using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SysMonCmdPal;

public sealed partial class SparklineChart
{
    public byte[] ToPng()
    {
        float[] points;
        lock (_lock)
        {
            if (_history.Count < 2) return CreateEmptyPng();
            points = new float[_history.Count];
            _history.CopyTo(points, 0);
        }

        byte[] px = new byte[Width * Height * 4];
        Render(px, Width, Height, points);

        using var ms = new MemoryStream();
        WritePngFile(ms, Width, Height, px);
        return ms.ToArray();
    }

    private static byte[] CreateEmptyPng()
    {
        using var ms = new MemoryStream();
        WritePngFile(ms, 1, 1, [0, 0, 0, 0]);
        return ms.ToArray();
    }

    private static void WritePngFile(Stream s, int w, int h, byte[] rgba)
    {
        s.Write([137, 80, 78, 71, 13, 10, 26, 10], 0, 8);
        using (var ms = new MemoryStream())
        {
            WriteBE(ms, (uint)w); WriteBE(ms, (uint)h);
            ms.WriteByte(8); ms.WriteByte(6); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
            WriteChunk(s, "IHDR", ms.ToArray());
        }

        byte[] raw = new byte[1 + h * (1 + w * 4)];
        int pos = 0;
        for (int y = 0; y < h; y++)
        {
            raw[pos++] = 0; // filter type: None
            Array.Copy(rgba, y * w * 4, raw, pos, w * 4);
            pos += w * 4;
        }

        using var cms = new MemoryStream();
        // ZLibStream produces proper zlib format (2-byte header + deflate + 4-byte Adler32)
        // which PNG IDAT requires. DeflateStream produces raw deflate without the wrapper,
        // causing PNG decoders to fail or hang.
        using (var zlib = new ZLibStream(cms, CompressionLevel.Optimal, true))
            zlib.Write(raw, 0, pos);
        WriteChunk(s, "IDAT", cms.ToArray());
        WriteChunk(s, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        WriteBE(s, (uint)data.Length);
        byte[] tb = Encoding.ASCII.GetBytes(type);
        s.Write(tb, 0, 4); s.Write(data, 0, data.Length);
        WriteBE(s, Crc32(tb, data));
    }

    private static void WriteBE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
