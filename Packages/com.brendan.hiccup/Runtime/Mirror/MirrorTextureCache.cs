using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hiccup.Mirror
{
    /// <summary>
    /// Exports textures, or sub-rectangles of them, as PNG data URLs the mirrored page can use as backgrounds.
    /// Unity tints a graphic by multiplying a color into the texture, which CSS cannot express for an
    /// arbitrary image, so the tint is baked into the export instead. Results are cached by texture, rectangle and
    /// tint; the tint is quantized to 128 levels per channel so a color transition does not produce a PNG per frame.
    /// </summary>
    internal sealed class MirrorTextureCache : IDisposable
    {
        private struct Key : IEquatable<Key>
        {
            public EntityId Texture;
            public int X, Y, W, H, Tint;
            public bool Equals(Key o) => Texture.Equals(o.Texture) && X == o.X && Y == o.Y && W == o.W && H == o.H && Tint == o.Tint;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => (((((Texture.GetHashCode() * 397) ^ X) * 397 ^ Y) * 397 ^ W) * 397 ^ H) * 397 ^ Tint;
        }

        private struct SlicedKey : IEquatable<SlicedKey>
        {
            public Key Source;
            public int OutW, OutH, L, B, R, T;
            public bool Fill;
            public bool Equals(SlicedKey o) => Source.Equals(o.Source) && OutW == o.OutW && OutH == o.OutH && L == o.L && B == o.B && R == o.R && T == o.T && Fill == o.Fill;
            public override bool Equals(object obj) => obj is SlicedKey k && Equals(k);
            public override int GetHashCode() => ((((((Source.GetHashCode() * 397 ^ OutW) * 397 ^ OutH) * 397 ^ L) * 397 ^ B) * 397 ^ R) * 397 ^ T) * 2 + (Fill ? 1 : 0);
        }

        private const int MaxEntries = 1024;   // beyond this the exports are dropped and rebuilt on demand

        private readonly Dictionary<Key, string> _urls = new Dictionary<Key, string>();
        private readonly Dictionary<SlicedKey, string> _sliced = new Dictionary<SlicedKey, string>();
        private readonly Dictionary<Key, Color32[]> _sources = new Dictionary<Key, Color32[]>();   // untinted pixels per texture rectangle
        private readonly List<Key> _deadKeys = new List<Key>();
        private readonly List<SlicedKey> _deadSliced = new List<SlicedKey>();
        private Texture2D _scratch;

        // WebGPU cannot read a render target back synchronously (Texture2D.ReadPixels logs an error and returns
        // nothing), so blit reads are issued as AsyncGPUReadback requests and picked up on a later sync pass. The
        // caller gets null until then and no background for the element; the next sync retries and finds the pixels.
        private struct PendingRead
        {
            public AsyncGPUReadbackRequest Request;
            public RenderTexture Target;   // kept alive until the readback completes
            public int W, H;
        }

        private readonly Dictionary<Key, PendingRead> _pending = new Dictionary<Key, PendingRead>();
        private bool _asyncErrorLogged;

        /// <summary>True where a synchronous readback is not available (WebGPU) and the async path is used instead.</summary>
        private static bool ReadsAsync => SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU && SystemInfo.supportsAsyncGPUReadback;
        // The last whole texture read for a sub-rectangle, kept for the rest of the sync pass so an atlas is
        // copied once for all of its sprites rather than once per sprite (see EndSync).
        private EntityId _fullId;
        private Color32[] _full;

        /// <summary>Number of PNGs exported so far, for the inspector.</summary>
        public int Count => _urls.Count + _sliced.Count;

        // 128 levels per channel: within one 8-bit step of the exact color, so a baked tint is indistinguishable from
        // a CSS color, while a color-tint transition still collapses onto a bounded set of PNGs.
        private const int TintLevels = 127;

        public static int QuantizeTint(Color c)
        {
            int r = Mathf.RoundToInt(Mathf.Clamp01(c.r) * TintLevels);
            int g = Mathf.RoundToInt(Mathf.Clamp01(c.g) * TintLevels);
            int b = Mathf.RoundToInt(Mathf.Clamp01(c.b) * TintLevels);
            return (r << 14) | (g << 7) | b;
        }

        private static Color TintFromKey(int q) =>
            new Color(((q >> 14) & TintLevels) / (float)TintLevels, ((q >> 7) & TintLevels) / (float)TintLevels, (q & TintLevels) / (float)TintLevels, 1f);

        /// <summary>Returns a data URL for <paramref name="rect"/> (texture pixels, origin bottom-left) of <paramref name="texture"/>, multiplied by <paramref name="tint"/>'s RGB.</summary>
        public string DataUrl(Texture texture, RectInt rect, Color tint)
        {
            if (texture == null)
                return null;
            int q = QuantizeTint(tint);
            var key = new Key { Texture = texture.GetEntityId(), X = rect.x, Y = rect.y, W = rect.width, H = rect.height, Tint = q };
            if (_urls.TryGetValue(key, out var url))
                return url;
            url = Export(texture, rect, TintFromKey(q));
            if (url != null)
            {
                Trim();
                _urls[key] = url;
            }
            return url;
        }

        /// <summary>
        /// A 9-sliced image composed at its final size, the way <c>Image.Type.Sliced</c> builds its mesh: borders in
        /// output pixels, the rest stretched. One image per (sprite, size, borders, tint), so the browser draws a single
        /// bitmap and there are no seams between slices — CSS <c>border-image</c> shows hairlines wherever a slice
        /// boundary lands on a fractional pixel. Borders are given in output pixels and are expected to fit.
        /// </summary>
        public string SlicedDataUrl(Texture texture, RectInt rect, Vector4 sourceBorder, int outW, int outH, int left, int bottom, int right, int top, bool fillCenter, Color tint)
        {
            if (texture == null)
                return null;
            outW = Mathf.Max(1, outW);
            outH = Mathf.Max(1, outH);
            int q = QuantizeTint(tint);
            var source = new Key { Texture = texture.GetEntityId(), X = rect.x, Y = rect.y, W = rect.width, H = rect.height, Tint = 0 };
            var key = new SlicedKey { Source = source, OutW = outW, OutH = outH, L = left, B = bottom, R = right, T = top, Fill = fillCenter };
            key.Source.Tint = q;
            if (_sliced.TryGetValue(key, out var url))
                return url;

            var src = Source(texture, rect, source);
            if (src == null)
                return null;
            int sw = Mathf.Max(1, rect.width), sh = Mathf.Max(1, rect.height);
            var px = Compose(src, sw, sh, outW, outH,
                left, bottom, right, top,
                Mathf.Clamp(Mathf.RoundToInt(sourceBorder.x), 0, sw), Mathf.Clamp(Mathf.RoundToInt(sourceBorder.y), 0, sh),
                Mathf.Clamp(Mathf.RoundToInt(sourceBorder.z), 0, sw), Mathf.Clamp(Mathf.RoundToInt(sourceBorder.w), 0, sh), fillCenter);
            Tint(px, TintFromKey(q));
            url = Encode(px, outW, outH, texture, TintFromKey(q));
            Trim();
            _sliced[key] = url;
            return url;
        }

        /// <summary>Drops every export of a texture so the next request re-reads it (RenderTextures that change).</summary>
        public void Invalidate(Texture texture)
        {
            if (texture == null)
                return;
            var id = texture.GetEntityId();
            _deadKeys.Clear();
            foreach (var k in _urls.Keys)
            {
                if (k.Texture.Equals(id))
                    _deadKeys.Add(k);
            }
            foreach (var k in _deadKeys)
                _urls.Remove(k);
            _deadKeys.Clear();
            foreach (var k in _sources.Keys)
            {
                if (k.Texture.Equals(id))
                    _deadKeys.Add(k);
            }
            foreach (var k in _deadKeys)
                _sources.Remove(k);
            _deadKeys.Clear();
            foreach (var k in _pending.Keys)
            {
                if (k.Texture.Equals(id))
                    _deadKeys.Add(k);
            }
            foreach (var k in _deadKeys)
                DropPending(k);
            _deadSliced.Clear();
            foreach (var k in _sliced.Keys)
            {
                if (k.Source.Texture.Equals(id))
                    _deadSliced.Add(k);
            }
            foreach (var k in _deadSliced)
                _sliced.Remove(k);
            if (_fullId.Equals(id))
                _full = null;
        }

        /// <summary>Call once the frame's exports are done: releases the whole-texture copy kept for sub-rectangle reads.</summary>
        public void EndSync() => _full = null;

        private void Trim()
        {
            if (_urls.Count + _sliced.Count < MaxEntries)
                return;
            // The PNG strings are what grows without bound (one per size and tint); the untinted sources are bounded
            // by the distinct texture rectangles and are the expensive part to rebuild, so they stay.
            _urls.Clear();
            _sliced.Clear();
        }

        /// <summary>The untinted pixels of a texture rectangle, bottom row first, read once and kept.</summary>
        private Color32[] Source(Texture texture, RectInt rect, Key source)
        {
            if (_sources.TryGetValue(source, out var px))
                return px;
            int w = Mathf.Max(1, rect.width), h = Mathf.Max(1, rect.height);
            if (_pending.TryGetValue(source, out var pending))
            {
                px = CompleteAsyncRead(source, pending);
            }
            else
            {
                px = ReadDirect(texture, rect, w, h);
                if (px == null)
                {
                    if (ReadsAsync)
                    {
                        BeginAsyncRead(source, texture, rect, w, h);
                        return null;
                    }
                    px = ReadThroughBlit(texture, rect, w, h);
                }
            }
            if (px == null)
                return null;
            if (texture is Texture2D t2 && t2.format == TextureFormat.Alpha8)
                NormalizeAlphaOnly(px);
            if (!(texture is RenderTexture))
                _sources[source] = px;
            return px;
        }

        /// <summary>
        /// An alpha-only texture (icons, font glyphs) reads back as black, gray or white with the coverage in the
        /// alpha or the color channels, depending on the graphics API and on whether it came from the CPU or a
        /// blit. Unity draws it as its tint color, so the pixels become white with the coverage as alpha.
        /// </summary>
        private static void NormalizeAlphaOnly(Color32[] px)
        {
            bool alphaVaries = false;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a != 255)
                {
                    alphaVaries = true;
                    break;
                }
            }
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                byte a = alphaVaries ? p.a : (byte)Mathf.Max(p.r, Mathf.Max(p.g, p.b));
                px[i] = new Color32(255, 255, 255, a);
            }
        }

        private static readonly byte[] s_tintLut = new byte[3 * 256];

        private static void Tint(Color32[] px, Color tint)
        {
            if (tint == Color.white)
                return;
            // One lookup per channel instead of a float multiply and a round per channel per pixel.
            var lut = s_tintLut;
            for (int i = 0; i < 256; i++)
            {
                lut[i] = (byte)Mathf.RoundToInt(i * tint.r);
                lut[256 + i] = (byte)Mathf.RoundToInt(i * tint.g);
                lut[512 + i] = (byte)Mathf.RoundToInt(i * tint.b);
            }
            for (int i = 0; i < px.Length; i++)
            {
                px[i].r = lut[px[i].r];
                px[i].g = lut[256 + px[i].g];
                px[i].b = lut[512 + px[i].b];
            }
        }

        // ---- 9-slice composition

        private static Color32[] Compose(Color32[] src, int sw, int sh, int ow, int oh, int dl, int db, int dr, int dt, int sl, int sb, int sr, int st, bool fill)
        {
            var dst = new Color32[ow * oh];
            var clear = new Color32(0, 0, 0, 0);
            // The horizontal mapping is the same on every row, so it is computed once per column rather than per pixel.
            var xRegion = new int[ow];
            var xs = new float[ow];
            var xLo = new int[ow];
            var xHi = new int[ow];
            for (int x = 0; x < ow; x++)
                xRegion[x] = Map(x, ow, dl, dr, sw, sl, sr, out xs[x], out xLo[x], out xHi[x]);
            for (int y = 0; y < oh; y++)
            {
                int region = Map(y, oh, db, dt, sh, sb, st, out float sy, out int ylo, out int yhi);
                int row = y * ow;
                bool hollow = !fill && region == 1;
                for (int x = 0; x < ow; x++)
                {
                    if (hollow && xRegion[x] == 1)
                    {
                        dst[row + x] = clear;
                        continue;
                    }
                    dst[row + x] = Sample(src, sw, xs[x], sy, xLo[x], xHi[x], ylo, yhi);
                }
            }
            return dst;
        }

        /// <summary>
        /// Maps an output coordinate to a continuous source coordinate on one axis. Returns 0 for the low border,
        /// 1 for the stretched middle, 2 for the high border, and the source index range the sample may touch so
        /// filtering never bleeds across a slice boundary.
        /// </summary>
        private static int Map(int d, int outLen, int dLow, int dHigh, int srcLen, int sLow, int sHigh, out float s, out int lo, out int hi)
        {
            if (dLow > 0 && d < dLow)
            {
                lo = 0; hi = Mathf.Max(0, sLow - 1);
                s = (d + 0.5f) * (sLow / (float)dLow);
                return 0;
            }
            if (dHigh > 0 && d >= outLen - dHigh)
            {
                lo = Mathf.Max(0, srcLen - sHigh); hi = srcLen - 1;
                s = (srcLen - sHigh) + (d - (outLen - dHigh) + 0.5f) * (sHigh / (float)dHigh);
                return 2;
            }
            int midOut = Mathf.Max(1, outLen - dLow - dHigh);
            int midSrc = Mathf.Max(1, srcLen - sLow - sHigh);
            lo = Mathf.Min(sLow, srcLen - 1); hi = Mathf.Max(lo, srcLen - sHigh - 1);
            s = sLow + (d - dLow + 0.5f) * (midSrc / (float)midOut);
            return 1;
        }

        /// <summary>Bilinear sample at a pixel-center coordinate, clamped to the slice's own pixels.</summary>
        private static Color32 Sample(Color32[] src, int sw, float sx, float sy, int xlo, int xhi, int ylo, int yhi)
        {
            float fx = sx - 0.5f, fy = sy - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), xlo, xhi), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), ylo, yhi);
            int x1 = Mathf.Clamp(x0 + 1, xlo, xhi), y1 = Mathf.Clamp(y0 + 1, ylo, yhi);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            Color32 a = src[y0 * sw + x0], b = src[y0 * sw + x1], c = src[y1 * sw + x0], d = src[y1 * sw + x1];
            return new Color32(
                Lerp(a.r, b.r, c.r, d.r, tx, ty), Lerp(a.g, b.g, c.g, d.g, tx, ty),
                Lerp(a.b, b.b, c.b, d.b, tx, ty), Lerp(a.a, b.a, c.a, d.a, tx, ty));
        }

        private static byte Lerp(byte a, byte b, byte c, byte d, float tx, float ty)
        {
            float top = a + (b - a) * tx, bottom = c + (d - c) * tx;
            return (byte)Mathf.RoundToInt(top + (bottom - top) * ty);
        }

        /// <summary>When set, every export is also written there as a numbered PNG, to check what the page receives.</summary>
        public string DumpDirectory;
        private int _dumped;

        private string Export(Texture texture, RectInt rect, Color tint)
        {
            int w = Mathf.Max(1, rect.width), h = Mathf.Max(1, rect.height);
            var source = new Key { Texture = texture.GetEntityId(), X = rect.x, Y = rect.y, W = rect.width, H = rect.height, Tint = 0 };
            var px = Source(texture, rect, source);
            if (px == null)
                return null;
            if (tint != Color.white)
            {
                px = (Color32[])px.Clone();   // the cached source stays untinted
                Tint(px, tint);
            }
            return Encode(px, w, h, texture, tint);
        }

        private string Encode(Color32[] px, int w, int h, Texture texture, Color tint)
        {
            Scratch(w, h).SetPixels32(px);
            var png = _scratch.EncodeToPNG();
            Dump(png, texture, tint);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }

        /// <summary>Readable textures (everything created at runtime, and imported ones with Read/Write on) are copied on the CPU: no render target, no color-space round trip.</summary>
        private Color32[] ReadDirect(Texture texture, RectInt rect, int w, int h)
        {
            if (!(texture is Texture2D t2) || !t2.isReadable)
                return null;
            try
            {
                bool whole = rect.x == 0 && rect.y == 0 && w == t2.width && h == t2.height;
                if (t2.format == TextureFormat.RGBA32)
                {
                    // Same layout as Color32, so the texture's own memory can be read without a decoded copy.
                    var data = t2.GetPixelData<Color32>(0);
                    if (whole)
                        return data.ToArray();
                    return CopyRect(data, t2.width, t2.height, rect, w, h);
                }
                var all = FullPixels(t2);
                if (whole)
                    return all;
                return CopyRect(all, t2.width, t2.height, rect, w, h);
            }
            catch (UnityException) { return null; }   // a format GetPixels32 cannot decode; the blit below can sample it
        }

        /// <summary>The decoded pixels of a whole texture, kept for the rest of the sync so every sprite of an atlas does not decode it again.</summary>
        private Color32[] FullPixels(Texture2D t2)
        {
            var id = t2.GetEntityId();
            if (_full == null || !_fullId.Equals(id))
            {
                _full = t2.GetPixels32();
                _fullId = id;
            }
            return _full;
        }

        private static Color32[] CopyRect(Color32[] all, int tw, int th, RectInt rect, int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Mathf.Clamp(rect.y + y, 0, th - 1);
                for (int x = 0; x < w; x++)
                    px[y * w + x] = all[sy * tw + Mathf.Clamp(rect.x + x, 0, tw - 1)];
            }
            return px;
        }

        private static Color32[] CopyRect(NativeArray<Color32> all, int tw, int th, RectInt rect, int w, int h)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Mathf.Clamp(rect.y + y, 0, th - 1);
                for (int x = 0; x < w; x++)
                    px[y * w + x] = all[sy * tw + Mathf.Clamp(rect.x + x, 0, tw - 1)];
            }
            return px;
        }

        /// <summary>Non-readable or compressed textures go through the GPU: blit the sub-rectangle into an sRGB target and read it back.</summary>
        private Color32[] ReadThroughBlit(Texture texture, RectInt rect, int w, int h)
        {
            var rt = BlitRect(texture, rect, w, h);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var scratch = Scratch(w, h);
                scratch.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                return scratch.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Blits the sub-rectangle into a temporary sRGB target, one texture pixel per pixel. The caller releases it.</summary>
        private static RenderTexture BlitRect(Texture texture, RectInt rect, int w, int h)
        {
            var desc = new RenderTextureDescriptor(w, h, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, 0) { sRGB = true, msaaSamples = 1, useMipMap = false };
            var rt = RenderTexture.GetTemporary(desc);
            var scale = new Vector2((float)w / texture.width, (float)h / texture.height);
            var offset = new Vector2((float)rect.x / texture.width, (float)rect.y / texture.height);
            Graphics.Blit(texture, rt, scale, offset);
            return rt;
        }

        /// <summary>Blits and queues a readback; the pixels arrive on a later sync pass through <see cref="CompleteAsyncRead"/>.</summary>
        private void BeginAsyncRead(Key source, Texture texture, RectInt rect, int w, int h)
        {
            var rt = BlitRect(texture, rect, w, h);
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            _pending[source] = new PendingRead { Request = request, Target = rt, W = w, H = h };
        }

        /// <summary>Null while the readback is in flight. On an error the rectangle is marked unreadable so it is not requested every frame.</summary>
        private Color32[] CompleteAsyncRead(Key source, PendingRead pending)
        {
            if (!pending.Request.done)
                return null;
            Color32[] px = null;
            if (pending.Request.hasError)
            {
                if (!_asyncErrorLogged)
                {
                    _asyncErrorLogged = true;
                    Debug.LogWarning("[Hiccup] Reading a texture back from the GPU failed; the element keeps no background image.");
                }
                _sources[source] = null;   // TryGetValue then yields null without another request
            }
            else
            {
                var data = pending.Request.GetData<Color32>();
                px = data.Length == pending.W * pending.H ? data.ToArray() : null;
            }
            DropPending(source);
            return px;
        }

        private void DropPending(Key source)
        {
            if (!_pending.TryGetValue(source, out var pending))
                return;
            _pending.Remove(source);
            if (pending.Target != null)
                RenderTexture.ReleaseTemporary(pending.Target);
        }

        private Texture2D Scratch(int w, int h)
        {
            if (_scratch == null)
                _scratch = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Hiccup mirror export", hideFlags = HideFlags.HideAndDontSave };
            else if (_scratch.width != w || _scratch.height != h)
                _scratch.Reinitialize(w, h);   // resizes in place rather than replacing the object per distinct size
            return _scratch;
        }

        private void Dump(byte[] png, Texture texture, Color tint)
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            if (string.IsNullOrEmpty(DumpDirectory))
                return;
            try
            {
                System.IO.Directory.CreateDirectory(DumpDirectory);
                var name = $"{_dumped++:000}_{texture.name}_{ColorUtility.ToHtmlStringRGB(tint)}.png";
                foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(DumpDirectory, name), png);
            }
            catch (Exception e) { Debug.LogWarning("[Hiccup] Could not dump a mirror texture export: " + e.Message); }
#endif
        }

        public void Dispose()
        {
            _urls.Clear();
            _sliced.Clear();
            _sources.Clear();
            _full = null;
            foreach (var p in _pending.Values)
            {
                if (p.Target != null)
                    RenderTexture.ReleaseTemporary(p.Target);
            }
            _pending.Clear();
            if (_scratch != null)
            {
                UnityEngine.Object.Destroy(_scratch);
                _scratch = null;
            }
        }
    }
}
