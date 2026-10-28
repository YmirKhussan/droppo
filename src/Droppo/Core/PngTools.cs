using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Droppo.Core;

public static class PngTools
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // Text, EXIF and timestamp chunks. Everything that draws the picture stays untouched.
    private static readonly HashSet<string> Drop = new() { "tEXt", "zTXt", "iTXt", "eXIf", "tIME" };

    public static bool IsPng(byte[] data)
    {
        if (data.Length < Signature.Length) return false;
        for (int k = 0; k < Signature.Length; k++)
            if (data[k] != Signature[k]) return false;
        return true;
    }

    /// <summary>Width and height from the PNG header.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        var head = new byte[24];
        using (var f = File.OpenRead(path)) f.ReadExactly(head, 0, 24);
        if (!IsPng(head)) throw new InvalidDataException("Not a PNG file.");
        int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
        return (w, h);
    }

    /// <summary>Lossless: copies every chunk except metadata ones, CRCs unchanged.</summary>
    public static byte[] StripMetadata(byte[] data)
    {
        if (!IsPng(data)) throw new InvalidDataException("Not a PNG file.");
        using var output = new MemoryStream(data.Length);
        output.Write(Signature, 0, Signature.Length);

        int i = Signature.Length;
        while (i + 12 <= data.Length)
        {
            int len = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
            if (len < 0 || i + 12 + len > data.Length) throw new InvalidDataException("Corrupt PNG chunk.");
            string type = Encoding.ASCII.GetString(data, i + 4, 4);
            int total = 12 + len;
            if (!Drop.Contains(type)) output.Write(data, i, total);
            i += total;
            if (type == "IEND") break;
        }
        return output.ToArray();
    }
}
