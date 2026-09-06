using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Hiccup
{
    /// <summary>Encoding used when a Unity texture is sent to the page with <see cref="HtmlDocument.SetImage(string, Texture, HtmlImageFormat, int)"/>.</summary>
    public enum HtmlImageFormat
    {
        /// <summary>Lossless, keeps alpha. The default.</summary>
        Png = 0,
        /// <summary>Smaller and faster to encode, no alpha. Good for live camera feeds.</summary>
        Jpeg = 1
    }

    /// <summary>
    /// Turns a texture rectangle into PNG or JPEG bytes on the CPU. Readable textures are copied directly; anything
    /// else (RenderTextures, non-readable imports, compressed formats) goes through a blit and a readback.
    /// </summary>
    internal static class HtmlImageEncoder
    {
        private static Texture2D s_scratch;

        public static string MimeType(HtmlImageFormat format) => format == HtmlImageFormat.Jpeg ? "image/jpeg" : "image/png";

        /// <summary>Encodes <paramref name="rect"/> of <paramref name="texture"/> (pixels, bottom-left origin). Null if the texture is null or has no size.</summary>
        public static byte[] Encode(Texture texture, RectInt rect, HtmlImageFormat format, int quality)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return null;

            // Clamp to the texture, as a sprite rect may be a pixel past the edge after rounding.
            int x = Mathf.Clamp(rect.x, 0, texture.width - 1);
            int y = Mathf.Clamp(rect.y, 0, texture.height - 1);
            int w = Mathf.Clamp(rect.width, 1, texture.width - x);
            int h = Mathf.Clamp(rect.height, 1, texture.height - y);
            rect = new RectInt(x, y, w, h);

            var scratch = Scratch(w, h);
            if (!ReadDirect(texture, rect, scratch))
                ReadThroughBlit(texture, rect, scratch);
            return format == HtmlImageFormat.Jpeg
                ? scratch.EncodeToJPG(Mathf.Clamp(quality, 1, 100))
                : scratch.EncodeToPNG();
        }

        /// <summary>Readable, uncompressed textures are copied on the CPU: no render target, no color-space round trip.</summary>
        private static bool ReadDirect(Texture texture, RectInt rect, Texture2D scratch)
        {
            if (!(texture is Texture2D t2) || !t2.isReadable)
                return false;
            try
            {
                scratch.SetPixels(t2.GetPixels(rect.x, rect.y, rect.width, rect.height));
                return true;
            }
            catch (Exception)
            {
                return false;   // a format GetPixels cannot decode; the blit below can sample it
            }
        }

        private static void ReadThroughBlit(Texture texture, RectInt rect, Texture2D scratch)
        {
            int w = scratch.width, h = scratch.height;
            var desc = new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, 0) { sRGB = true, msaaSamples = 1, useMipMap = false };
            var rt = RenderTexture.GetTemporary(desc);
            var previous = RenderTexture.active;
            try
            {
                var scale = new Vector2((float)w / texture.width, (float)h / texture.height);
                var offset = new Vector2((float)rect.x / texture.width, (float)rect.y / texture.height);
                Graphics.Blit(texture, rt, scale, offset);
                RenderTexture.active = rt;
                scratch.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Texture2D Scratch(int w, int h)
        {
            if (s_scratch == null)
                s_scratch = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Hiccup image export", hideFlags = HideFlags.HideAndDontSave };
            else if (s_scratch.width != w || s_scratch.height != h)
                s_scratch.Reinitialize(w, h);
            return s_scratch;
        }
    }
}
