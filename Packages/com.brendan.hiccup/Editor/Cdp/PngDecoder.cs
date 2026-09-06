using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Hiccup.Editor.Cdp
{
    /// <summary>
    /// Decodes the PNGs Chrome's screencast produces into RGBA32 pixels, on any thread.
    /// </summary>
    /// <remarks>
    /// <c>Texture2D.LoadImage</c> can only run on the main thread, and a full-viewport frame costs it tens of
    /// milliseconds. Screencast frames are a narrow subset of PNG — 8-bit, RGB or RGBA, non-interlaced — so a small
    /// decoder covers them, and anything outside that subset is reported as unsupported so the caller can fall back.
    /// <para>
    /// Output rows are stored bottom-up, matching what <c>LoadImage</c> produces, so the rest of the frame
    /// pipeline does not care which decoder ran. One instance is meant to be used by one decode at a time; it keeps
    /// its scratch buffers between frames of the same size.
    /// </para>
    /// </remarks>
    internal sealed class PngDecoder
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private byte[] _filtered;                                  // inflated scanlines, each prefixed by its filter byte
        private readonly ChunkStream _idat = new ChunkStream();    // the IDAT payloads, read in place

        /// <summary>Bytes needed for a <paramref name="width"/> × <paramref name="height"/> RGBA32 image.</summary>
        public static int OutputSize(int width, int height) => width * height * 4;

        /// <summary>
        /// Decodes the first <paramref name="length"/> bytes of <paramref name="png"/> into <paramref name="rgba"/>,
        /// allocating or reallocating it when it is not exactly the right size. Returns false when the data is not
        /// a PNG this decoder handles.
        /// </summary>
        public bool TryDecode(byte[] png, int length, ref byte[] rgba, out int width, out int height)
        {
            width = height = 0;
            if (png == null || length < Signature.Length + 8)
                return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (png[i] != Signature[i])
                    return false;
            }

            int pos = Signature.Length;
            int colorType = -1;
            int channels = 0;
            _idat.Reset(png);

            while (pos + 8 <= length)
            {
                int chunkLength = ReadInt32(png, pos);
                if (chunkLength < 0 || pos + 12 + chunkLength > length)
                    return false;
                uint type = ReadUInt32(png, pos + 4);
                int data = pos + 8;

                switch (type)
                {
                    case 0x49484452: // IHDR
                        if (chunkLength != 13)
                            return false;
                        width = ReadInt32(png, data);
                        height = ReadInt32(png, data + 4);
                        int bitDepth = png[data + 8];
                        colorType = png[data + 9];
                        int interlace = png[data + 12];
                        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0)
                            return false;
                        if (colorType == 6)
                            channels = 4;
                        else if (colorType == 2)
                            channels = 3;
                        else
                            return false;
                        // A frame beyond this is not a viewport; refuse rather than allocate absurdly.
                        if ((long)width * height > 64L * 1024 * 1024)
                            return false;
                        break;

                    case 0x49444154: // IDAT
                        if (channels == 0)
                            return false;
                        _idat.Add(data, chunkLength);
                        break;

                    case 0x49454E44: // IEND
                        pos = length;
                        continue;
                }
                pos = data + chunkLength + 4;   // skip the CRC; Chrome's output is trusted and the cost is real
            }

            if (channels == 0 || _idat.Length < 2)
                return false;

            int stride = 1 + width * channels;
            long filteredSize = (long)stride * height;
            if (filteredSize > int.MaxValue)
                return false;
            if (_filtered == null || _filtered.Length < filteredSize)
                _filtered = new byte[filteredSize];

            if (!Inflate(_idat, _filtered, (int)filteredSize))
                return false;

            int outputSize = OutputSize(width, height);
            if (rgba == null || rgba.Length != outputSize)
                rgba = new byte[outputSize];

            Unfilter(_filtered, rgba, width, height, channels);
            return true;
        }

        /// <summary>Inflates a zlib stream, skipping the two-byte header and ignoring the Adler-32 trailer.</summary>
        private static bool Inflate(ChunkStream zlib, byte[] destination, int count)
        {
            zlib.Skip(2);
            try
            {
                using (var deflate = new DeflateStream(zlib, CompressionMode.Decompress, leaveOpen: true))
                {
                    int total = 0;
                    while (total < count)
                    {
                        int read = deflate.Read(destination, total, count - total);
                        if (read <= 0)
                            return false;
                        total += read;
                    }
                }
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reverses the per-scanline filters and writes RGBA rows bottom-up. Unfiltering happens in place in
        /// <paramref name="filtered"/>, because each row's prediction reads the reconstructed row above it.
        /// </summary>
        private static void Unfilter(byte[] filtered, byte[] rgba, int width, int height, int channels)
        {
            int rowBytes = width * channels;
            int stride = 1 + rowBytes;
            int bpp = channels;

            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                int filter = filtered[row];
                int cur = row + 1;
                int prev = cur - stride;   // only valid when y > 0

                switch (filter)
                {
                    case 0:
                        break;
                    case 1: // Sub
                        for (int i = bpp; i < rowBytes; i++)
                            filtered[cur + i] += filtered[cur + i - bpp];
                        break;
                    case 2: // Up
                        if (y > 0)
                        {
                            for (int i = 0; i < rowBytes; i++)
                                filtered[cur + i] += filtered[prev + i];
                        }
                        break;
                    case 3: // Average
                        if (y == 0)
                        {
                            for (int i = bpp; i < rowBytes; i++)
                                filtered[cur + i] += (byte)(filtered[cur + i - bpp] >> 1);
                        }
                        else
                        {
                            for (int i = 0; i < bpp; i++)
                                filtered[cur + i] += (byte)(filtered[prev + i] >> 1);
                            for (int i = bpp; i < rowBytes; i++)
                                filtered[cur + i] += (byte)((filtered[cur + i - bpp] + filtered[prev + i]) >> 1);
                        }
                        break;
                    case 4: // Paeth
                        if (y == 0)
                        {
                            for (int i = bpp; i < rowBytes; i++)
                                filtered[cur + i] += filtered[cur + i - bpp];
                        }
                        else
                        {
                            for (int i = 0; i < bpp; i++)
                                filtered[cur + i] += filtered[prev + i];
                            for (int i = bpp; i < rowBytes; i++)
                            {
                                int a = filtered[cur + i - bpp];
                                int b = filtered[prev + i];
                                int c = filtered[prev + i - bpp];
                                int p = a + b - c;
                                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                                filtered[cur + i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                            }
                        }
                        break;
                    default:
                        // An unknown filter type leaves the row as-is; the frame is still displayable.
                        break;
                }

                // PNG rows are top-down; LoadImage's layout (and the blit that expects it) is bottom-up.
                int outRow = (height - 1 - y) * width * 4;
                if (channels == 4)
                {
                    Buffer.BlockCopy(filtered, cur, rgba, outRow, rowBytes);
                }
                else
                {
                    for (int x = 0, src = cur, dst = outRow; x < width; x++, src += 3, dst += 4)
                    {
                        rgba[dst] = filtered[src];
                        rgba[dst + 1] = filtered[src + 1];
                        rgba[dst + 2] = filtered[src + 2];
                        rgba[dst + 3] = 255;
                    }
                }
            }
        }

        private static int ReadInt32(byte[] b, int i) => (int)ReadUInt32(b, i);

        private static uint ReadUInt32(byte[] b, int i)
            => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

        /// <summary>
        /// A read-only, forward-only view over the IDAT chunks where they sit in the PNG, so the compressed
        /// data is never copied out before it is inflated. Reused across frames.
        /// </summary>
        private sealed class ChunkStream : Stream
        {
            private byte[] _source;
            private readonly List<int> _chunks = new List<int>();   // offset, length pairs
            private int _chunk;     // index into _chunks of the chunk being read
            private int _cursor;    // bytes of that chunk already consumed
            private long _length;

            public void Reset(byte[] source)
            {
                _source = source;
                _chunks.Clear();
                _chunk = 0;
                _cursor = 0;
                _length = 0;
            }

            public void Add(int offset, int length)
            {
                _chunks.Add(offset);
                _chunks.Add(length);
                _length += length;
            }

            public void Skip(int count)
            {
                while (count > 0 && _chunk < _chunks.Count)
                {
                    int available = _chunks[_chunk + 1] - _cursor;
                    if (available <= 0)
                    {
                        _chunk += 2;
                        _cursor = 0;
                        continue;
                    }
                    int n = Math.Min(available, count);
                    _cursor += n;
                    count -= n;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (count > 0 && _chunk < _chunks.Count)
                {
                    int available = _chunks[_chunk + 1] - _cursor;
                    if (available <= 0)
                    {
                        _chunk += 2;
                        _cursor = 0;
                        continue;
                    }
                    int n = Math.Min(available, count);
                    Buffer.BlockCopy(_source, _chunks[_chunk] + _cursor, buffer, offset, n);
                    _cursor += n;
                    offset += n;
                    count -= n;
                    total += n;
                }
                return total;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
