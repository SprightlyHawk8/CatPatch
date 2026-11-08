using System;
using System.IO;
using System.Linq;
using CatPatchspace;
using CatPatchspace.SprayFallback;

internal static class Program
{
    private static int _passed;
    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    private static void Check(bool value, string label)
    { if (!value) throw new Exception("FAILED: " + label); _passed++; Console.WriteLine("PASS " + label); }
    private static bool Valid(byte[] bytes) => SprayImageSafety.TryValidate(bytes, out _, out _);
    private static byte[] Canvas(int width, int height, int frames = 1)
    {
        using var output = new MemoryStream();
        byte[] header = Gif.Take(19).ToArray();
        header[6] = (byte)width; header[7] = (byte)(width >> 8);
        header[8] = (byte)height; header[9] = (byte)(height >> 8);
        output.Write(header);
        for (int i = 0; i < frames; i++) output.Write(Gif, 19, Gif.Length - 20);
        output.WriteByte(0x3b); return output.ToArray();
    }
    private static byte[] PaddedGif(int length)
    {
        var b = new byte[length]; Array.Copy(Gif, b, Gif.Length - 1);
        int p = Gif.Length - 1; b[p++] = 0x21; b[p++] = 0xfe;
        while (p < length - 2)
        {
            int remaining = length - 2 - p;
            int n = Math.Min(255, remaining - 1);
            if (remaining - n - 1 == 1) n--;
            b[p++] = (byte)n; p += n;
        }
        b[p++] = 0; b[p] = 0x3b; return b;
    }
    private static byte[] Png(int width, int height, string extra = null)
    {
        using var s = new MemoryStream();
        s.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        byte[] header = new byte[13]; Put(header, 0, (uint)width); Put(header, 4, (uint)height); header[8] = 8; header[9] = 6;
        Chunk(s, "IHDR", header);
        if (extra != null) Chunk(s, extra, Array.Empty<byte>());
        // A real 1x1 RGBA zlib stream. Large-dimension tests only exercise preflight.
        Chunk(s, "IDAT", Convert.FromBase64String("eJxjYGBgYAAAAAUAAdZ7Dak="));
        Chunk(s, "IEND", Array.Empty<byte>()); return s.ToArray();
    }
    private static void Put(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p+1] = (byte)(v >> 16); b[p+2] = (byte)(v >> 8); b[p+3] = (byte)v; }
    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] b = new byte[data.Length + 12]; Put(b, 0, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4); data.CopyTo(b, 8);
        uint crc = uint.MaxValue;
        for (int i = 4; i < b.Length - 4; i++) { crc ^= b[i]; for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1; }
        Put(b, b.Length - 4, crc ^ uint.MaxValue); s.Write(b);
    }
    private static byte[] Jpeg(int width, int height)
    {
        return new byte[] { 255,216,255,192,0,11,8,(byte)(height>>8),(byte)height,(byte)(width>>8),(byte)width,1,1,0x11,0,
            255,218,0,8,1,1,0,0,63,0,0x12,0xff,0,0x34,255,217 };
    }
    private static void Main()
    {
        Check(!Valid(null) && !Valid(Array.Empty<byte>()), "empty images rejected");
        Check(!Valid(new byte[] { 77, 90, 144, 0 }), "executable header rejected");
        Check(!Valid(System.Text.Encoding.UTF8.GetBytes("<svg onload='alert(1)'/>")), "active SVG content rejected");
        Check(Valid(Gif) && Valid(Png(1, 1)) && Valid(Jpeg(1, 1)), "supported image structures accepted");
        Check(Valid(Canvas(4095, 4095)) && Valid(Png(4095, 4095)) && Valid(Jpeg(4095, 4095)), "below-4K boundary accepted");
        Check(!Valid(Canvas(4096, 1)) && !Valid(Png(1, 4096)) && !Valid(Jpeg(4096, 1)), "4096-pixel dimensions rejected in every format");
        Check(!Valid(Canvas(0, 1)) && !Valid(Png(0, 1)) && !Valid(Jpeg(0, 1)), "zero dimensions rejected");
        Check(!Valid(Canvas(32767, 32767)), "prior enormous GIF header attack rejected");
        Check(Valid(Canvas(1, 1, 256)) && !Valid(Canvas(1, 1, 257)), "GIF frame-count boundary enforced");
        Check(Valid(Canvas(1024, 1024, 16)) && !Valid(Canvas(1024, 1024, 17)), "total decoded GIF memory boundary enforced");
        var outside = (byte[])Gif.Clone(); outside[32] = 2;
        Check(!Valid(outside), "GIF subframe cannot exceed canvas");
        var codeSize = (byte[])Gif.Clone(); codeSize[37] = 12;
        Check(!Valid(codeSize), "invalid GIF LZW size rejected");
        Check(!Valid(Gif.Concat(new byte[] { 77,90 }).ToArray()) && !Valid(Png(1,1).Concat(new byte[] { 0 }).ToArray())
            && !Valid(Jpeg(1,1).Concat(new byte[] { 0 }).ToArray()), "appended payloads rejected in every format");
        Check(!Valid(Png(1, 1, "acTL")) && !Valid(Png(1, 1, "zTXt")) && !Valid(Png(1, 1, "iCCP")), "unbudgeted PNG animation/compressed metadata rejected");
        var badCrc = Png(1, 1); badCrc[30] ^= 1;
        Check(!Valid(badCrc), "corrupt PNG checksum rejected");
        var badLength = Png(1, 1); Put(badLength, 8, uint.MaxValue);
        Check(!Valid(badLength), "overflowing PNG length rejected");
        var dnl = Jpeg(1, 1).ToList(); dnl.InsertRange(15, new byte[] {255,220,0,4,0xff,0xff});
        Check(!Valid(dnl.ToArray()), "JPEG cannot redefine height after the checked header");
        var unsupported = Jpeg(1,1); unsupported[3] = 0xc3;
        Check(!Valid(unsupported), "unsupported JPEG coding rejected");
        foreach (var sample in new[] { Gif, Png(1,1), Jpeg(1,1) })
            for (int i = 0; i < sample.Length; i++) if (Valid(sample.Take(i).ToArray())) throw new Exception("Truncated image accepted");
        Check(true, "all truncations of GIF/PNG/JPEG fixtures rejected without decoder calls");
        Check(Valid(PaddedGif(SprayImageSafety.MaxBytes)), "exactly 20,000,000 encoded bytes accepted");
        Check(!Valid(new byte[SprayImageSafety.MaxBytes + 1]), "20,000,001 encoded bytes rejected");
        var random = new Random(20261107);
        for (int i = 0; i < 3000; i++)
        {
            byte[] b = new byte[random.Next(2048)]; random.NextBytes(b);
            if (b.Length >= 8 && i % 3 == 0) Array.Copy(Png(1,1), b, 8);
            else if (b.Length >= 6 && i % 3 == 1) Array.Copy(Gif, b, 6);
            else if (b.Length >= 2) { b[0]=255; b[1]=216; }
            SprayImageSafety.TryValidate(b, out _, out _);
        }
        Check(true, "3000 deterministic malformed inputs fail closed without unexpected exceptions");
        var quota = new TransferRateLimit(40, 2);
        Check(quota.Take(20, 0) && quota.Take(20, 0) && !quota.Take(1, 0), "cumulative byte quota blocks repeated immediate offers");
        Check(quota.Take(20, 10) && !quota.Take(1, 10), "quota replenishes only with elapsed time");
        Check(!quota.Take(1, 5) && !quota.Take(-1, 5), "clock rollback and negative costs cannot grant credit");
        Console.WriteLine($"SPRAY_SAFETY_CHECKS_COMPLETE passed={_passed} failed=0");
    }
}
