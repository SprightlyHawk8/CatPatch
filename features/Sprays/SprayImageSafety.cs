using System;
using System.IO;

namespace CatPatchspace
{
    // Bounded structural preflight, deliberately independent of native image decoders.
    // This is not an antivirus or proof that an engine decoder has no vulnerabilities.
    internal static class SprayImageSafety
    {
        public const int MaxBytes = 20_000_000; // Decimal MB: never accept more than the requested 20 MB.
        public const int MaxDimension = 4095;
        public const int MaxGifFrames = 256;
        public const long MaxAnimationBytes = 64L * 1024 * 1024;
        private const int MaxBlocks = 100000;

        internal readonly struct Info
        {
            public readonly int Width, Height, Frames;
            public long FrameBytes => (long)Width * Height * 4;
            public Info(int width, int height, int frames) { Width = width; Height = height; Frames = frames; }
        }

        public static bool TryValidate(byte[] bytes, out Info info, out string error)
        {
            info = default;
            error = "";
            try { info = Validate(bytes); return true; }
            catch (InvalidDataException ex) { error = ex.Message; return false; }
        }

        public static Info Validate(byte[] b)
        {
            Require(b != null && b.Length > 0 && b.Length <= MaxBytes, "Sprays must be at most 20 MB.");
            if (b.Length >= 8 && b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71 &&
                b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10) return Png(b);
            if (b.Length >= 6 && b[0] == 71 && b[1] == 73 && b[2] == 70 && b[3] == 56 &&
                (b[4] == 55 || b[4] == 57) && b[5] == 97) return Gif(b);
            if (b.Length >= 2 && b[0] == 255 && b[1] == 216) return Jpeg(b);
            throw new InvalidDataException("Only PNG, JPEG and GIF image contents are accepted.");
        }

        public static byte[] ReadFile(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(input.Length > 0 && input.Length <= MaxBytes, "Sprays must be at most 20 MB.");
            using var reader = new BinaryReader(input);
            var bytes = reader.ReadBytes((int)input.Length);
            Require(bytes.Length == input.Length, "Incomplete spray file.");
            Validate(bytes);
            return bytes;
        }

        private static Info Dimensions(int width, int height, int frames = 1)
        {
            Require(width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension,
                "Spray width and height must each be below 4096 pixels.");
            Require(frames > 0 && frames <= MaxGifFrames && (long)width * height * 4 * frames <= MaxAnimationBytes,
                "GIF exceeds 256 frames or the 64 MiB decoded animation budget.");
            return new Info(width, height, frames);
        }

        private static Info Png(byte[] b)
        {
            int p = 8, blocks = 0;
            bool header = false, data = false, endedData = false, palette = false;
            int color = -1;
            Info info = default;
            while (p < b.Length)
            {
                Require(++blocks <= MaxBlocks, "Too many PNG chunks.");
                Need(b, p, 12);
                uint length = Big(b, p);
                Require(length <= (uint)(b.Length - p - 12), "Truncated PNG chunk.");
                int n = (int)length, t = p + 4, d = p + 8;
                for (int i = 0; i < 4; i++) Require((b[t + i] >= 65 && b[t + i] <= 90) ||
                    (b[t + i] >= 97 && b[t + i] <= 122), "Invalid PNG chunk type.");
                string type = System.Text.Encoding.ASCII.GetString(b, t, 4);
                Require(header || type == "IHDR", "PNG must start with IHDR.");
                Require(Crc(b, t, n + 4) == Big(b, d + n), "PNG checksum mismatch.");
                if (type == "IHDR")
                {
                    Require(!header && n == 13, "Invalid PNG header.");
                    uint width = Big(b, d), height = Big(b, d + 4);
                    Require(width <= MaxDimension && height <= MaxDimension, "PNG dimensions exceed the below-4K limit.");
                    info = Dimensions((int)width, (int)height);
                    int depth = b[d + 8]; color = b[d + 9];
                    bool depthOk = color == 0 ? depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16 :
                        color == 3 ? depth == 1 || depth == 2 || depth == 4 || depth == 8 :
                        (color == 2 || color == 4 || color == 6) && (depth == 8 || depth == 16);
                    Require(depthOk && b[d + 10] == 0 && b[d + 11] == 0 && b[d + 12] <= 1, "Unsupported PNG header.");
                    header = true;
                }
                else if (type == "PLTE")
                {
                    Require(!data && !palette && n > 0 && n <= 768 && n % 3 == 0, "Invalid PNG palette.");
                    palette = true;
                }
                else if (type == "IDAT")
                {
                    Require(!endedData && (color != 3 || palette), "Invalid PNG data order.");
                    data = true;
                }
                else if (type == "IEND")
                {
                    Require(data && n == 0 && d + n + 4 == b.Length, "Invalid PNG ending or appended data.");
                    return info;
                }
                else
                {
                    Require(type != "acTL" && type != "fcTL" && type != "fdAT", "Animated PNG is unsupported; use GIF.");
                    // Compressed metadata is not needed for sprays and can inflate outside the pixel budget.
                    Require(type != "iCCP" && type != "zTXt" && type != "iTXt", "Compressed PNG metadata is unsupported.");
                    Require((b[t] & 32) != 0, "Unsupported critical PNG chunk.");
                    if (data) endedData = true;
                }
                p += n + 12;
            }
            throw new InvalidDataException("PNG is missing IEND.");
        }

        private static Info Gif(byte[] b)
        {
            Need(b, 0, 13);
            int width = Little(b, 6), height = Little(b, 8), frames = 0, blocks = 0, p = 13;
            Dimensions(width, height);
            if ((b[10] & 128) != 0) Skip(b, ref p, 3 * (2 << (b[10] & 7)));
            while (p < b.Length)
            {
                Require(++blocks <= MaxBlocks, "Too many GIF blocks.");
                int kind = b[p++];
                if (kind == 0x3b)
                {
                    Require(frames > 0 && p == b.Length, "Invalid GIF ending or appended data.");
                    return Dimensions(width, height, frames);
                }
                if (kind == 0x21)
                {
                    Need(b, p, 1); int label = b[p++];
                    Require(label == 0xf9 || label == 0xfe || label == 0xff, "Unsupported GIF extension.");
                    if (label == 0xf9)
                    {
                        Need(b, p, 6); Require(b[p] == 4 && b[p + 5] == 0, "Invalid GIF control extension."); p += 6;
                    }
                    else
                    {
                        if (label == 0xff) { Need(b, p, 1); Require(b[p] == 11, "Invalid GIF application block."); }
                        SubBlocks(b, ref p, ref blocks);
                    }
                    continue;
                }
                Require(kind == 0x2c, "Invalid GIF block.");
                Need(b, p, 9);
                int x = Little(b, p), y = Little(b, p + 2), w = Little(b, p + 4), h = Little(b, p + 6);
                Require(w > 0 && h > 0 && x + w <= width && y + h <= height, "GIF frame exceeds its canvas.");
                int packed = b[p + 8]; p += 9;
                if ((packed & 128) != 0) Skip(b, ref p, 3 * (2 << (packed & 7)));
                Need(b, p, 1); Require(b[p] >= 2 && b[p] <= 8, "Invalid GIF LZW code size."); p++;
                int start = p; SubBlocks(b, ref p, ref blocks);
                Require(p > start + 1, "Empty GIF frame.");
                Dimensions(width, height, ++frames);
            }
            throw new InvalidDataException("GIF is missing its trailer.");
        }

        private static Info Jpeg(byte[] b)
        {
            int p = 2, blocks = 0; bool scan = false, header = false;
            Info info = default;
            while (p < b.Length)
            {
                Require(++blocks <= MaxBlocks, "Too many JPEG markers.");
                Need(b, p, 2); Require(b[p++] == 255, "Invalid JPEG marker.");
                while (p < b.Length && b[p] == 255) p++;
                Need(b, p, 1); int marker = b[p++];
                if (marker == 0xd9)
                {
                    Require(header && scan && p == b.Length, "Invalid JPEG ending or appended data."); return info;
                }
                Require(marker != 0 && marker != 0xd8 && marker != 1 && (marker < 0xd0 || marker > 0xd7), "Unexpected JPEG marker.");
                Require(marker == 0xc0 || marker == 0xc2 || marker == 0xc4 || marker == 0xdb ||
                    marker == 0xdd || marker == 0xda || marker == 0xfe || (marker >= 0xe0 && marker <= 0xef), "Unsupported JPEG coding marker.");
                Need(b, p, 2); int n = (b[p] << 8) | b[p + 1]; Require(n >= 2, "Invalid JPEG segment."); Need(b, p, n);
                if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
                {
                    Require(!header && (marker == 0xc0 || marker == 0xc2) && n >= 8 && b[p + 2] == 8, "Unsupported JPEG frame.");
                    int components = b[p + 7];
                    Require((components == 1 || components == 3) && n == 8 + components * 3, "Invalid JPEG components.");
                    info = Dimensions((b[p + 5] << 8) | b[p + 6], (b[p + 3] << 8) | b[p + 4]); header = true;
                }
                if (marker == 0xda)
                {
                    Require(header && n >= 6, "JPEG scan without a valid frame.");
                    int components = b[p + 2]; Require(components >= 1 && components <= 3 && n == 6 + components * 2, "Invalid JPEG scan.");
                    scan = true; p += n;
                    while (p < b.Length)
                    {
                        if (b[p] != 255) { p++; continue; }
                        Need(b, p, 2);
                        int next = p + 1; while (next < b.Length && b[next] == 255) next++;
                        Need(b, next, 1);
                        if (b[next] == 0 || (b[next] >= 0xd0 && b[next] <= 0xd7)) { p = next + 1; continue; }
                        break;
                    }
                }
                else p += n;
            }
            throw new InvalidDataException("JPEG is missing EOI.");
        }

        private static void SubBlocks(byte[] b, ref int p, ref int blocks)
        {
            while (true)
            {
                Require(++blocks <= MaxBlocks, "Too many image data blocks.");
                Need(b, p, 1); int n = b[p++]; if (n == 0) return; Skip(b, ref p, n);
            }
        }
        private static void Skip(byte[] b, ref int p, int n) { Need(b, p, n); p += n; }
        private static void Need(byte[] b, int p, int n) => Require(p >= 0 && n >= 0 && p <= b.Length - n, "Truncated image data.");
        private static int Little(byte[] b, int p) => b[p] | b[p + 1] << 8;
        private static uint Big(byte[] b, int p) => (uint)b[p] << 24 | (uint)b[p + 1] << 16 | (uint)b[p + 2] << 8 | b[p + 3];
        private static readonly uint[] CrcTable = CreateCrcTable();
        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++) { uint v = i; for (int j = 0; j < 8; j++) v = (v & 1) != 0 ? 0xedb88320 ^ (v >> 1) : v >> 1; table[i] = v; }
            return table;
        }
        private static uint Crc(byte[] b, int p, int n)
        {
            uint value = uint.MaxValue;
            for (int i = 0; i < n; i++) value = CrcTable[(value ^ b[p + i]) & 255] ^ (value >> 8);
            return value ^ uint.MaxValue;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
