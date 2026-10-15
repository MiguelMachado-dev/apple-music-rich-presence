using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AppleMusicPresence.Bench;

/// <summary>Saves what a window shows as a PNG (PrintWindow with full content, no image libraries).</summary>
internal static class WindowCapture
{
    private const uint RenderFullContent = 2;

    public static void SavePng(nint window, string path)
    {
        if (!Native.GetWindowRect(window, out var rect)) throw new InvalidOperationException("GetWindowRect failed.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var screen = Native.GetDC(0);
        var memory = Native.CreateCompatibleDC(screen);
        var bitmap = Native.CreateCompatibleBitmap(screen, width, height);
        var previous = Native.SelectObject(memory, bitmap);
        try
        {
            if (!Native.PrintWindow(window, memory, RenderFullContent)) throw new InvalidOperationException("PrintWindow failed.");
            Native.SelectObject(memory, previous);
            var header = new Native.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            if (Native.GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) != height)
                throw new InvalidOperationException("GetDIBits failed.");
            WritePng(path, width, height, pixels);
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
    }

    private static void WritePng(string path, int width, int height, byte[] bgra)
    {
        var rows = new byte[height * (1 + width * 3)];
        for (int y = 0, o = 0; y < height; y++)
        {
            rows[o++] = 0;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                rows[o++] = bgra[i + 2];
                rows[o++] = bgra[i + 1];
                rows[o++] = bgra[i];
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(rows);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream file, string type, byte[] data)
    {
        var number = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        file.Write(number);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        file.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(body));
        file.Write(number);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
