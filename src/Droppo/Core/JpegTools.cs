using System;
using System.IO;
using System.Text;

namespace Droppo.Core;

/// <summary>
/// Byte-level JPEG helpers. No re-encoding, so stripping metadata is lossless.
/// </summary>
public static class JpegTools
{
    public readonly record struct JpegInfo(int Width, int Height, int Components);

    public static bool IsJpeg(byte[] data) => data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8;

    /// <summary>Reads the frame size from the SOF marker (what a PDF needs to place the image).</summary>
    public static JpegInfo ReadInfo(byte[] data)
    {
        if (!IsJpeg(data)) throw new InvalidDataException("Not a JPEG file.");
        int i = 2;
        while (i + 4 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }
            byte marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }            // fill byte
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xD9) break;

            int len = (data[i + 2] << 8) | data[i + 3];
            bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof && i + 9 < data.Length)
            {
                int h = (data[i + 5] << 8) | data[i + 6];
                int w = (data[i + 7] << 8) | data[i + 8];
                int comps = data[i + 9];
                return new JpegInfo(w, h, comps);
            }
            i += 2 + len;
        }
        throw new InvalidDataException("JPEG has no frame header.");
    }

    /// <summary>
    /// Removes EXIF (GPS, camera, dates), XMP, IPTC and comments. Keeps the colour profile and
    /// keeps the rotation flag (rebuilt as a tiny EXIF block) so the photo doesn't turn sideways.
    /// </summary>
    public static byte[] StripMetadata(byte[] data)
    {
        if (!IsJpeg(data)) throw new InvalidDataException("Not a JPEG file.");

        using var output = new MemoryStream(data.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        int orientation = 1;
        bool exifWritten = false;
        int i = 2;

        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF) throw new InvalidDataException("Corrupt JPEG segment.");
            byte marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }

            if (marker == 0xDA || marker == 0xD9)
            {
                // Start of scan: compressed image data follows, copy everything as-is.
                if (!exifWritten && orientation != 1) { WriteOrientationExif(output, orientation); exifWritten = true; }
                output.Write(data, i, data.Length - i);
                return output.ToArray();
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { output.Write(data, i, 2); i += 2; continue; }

            int len = (data[i + 2] << 8) | data[i + 3];
            int segEnd = i + 2 + len;
            if (segEnd > data.Length) throw new InvalidDataException("Truncated JPEG segment.");

            bool keep;
            if (marker == 0xE0) keep = true;                                           // APP0 JFIF
            else if (marker == 0xE1) { orientation = ReadOrientation(data, i + 4, len - 2, orientation); keep = false; } // EXIF / XMP
            else if (marker == 0xE2) keep = StartsWith(data, i + 4, "ICC_PROFILE\0");  // colour profile only
            else if (marker == 0xEE) keep = true;                                      // APP14 Adobe, affects colours
            else if (marker >= 0xE3 && marker <= 0xEF) keep = false;                   // other APPn incl. IPTC
            else if (marker == 0xFE) keep = false;                                     // comment
            else keep = true;                                                          // tables, frame header etc

            // Put the rebuilt orientation block before the first non-APP0 segment
            if (keep && marker != 0xE0 && !exifWritten && orientation != 1)
            {
                WriteOrientationExif(output, orientation);
                exifWritten = true;
            }
            if (keep) output.Write(data, i, segEnd - i);
            i = segEnd;
        }
        return output.ToArray();
    }

    private static bool StartsWith(byte[] data, int offset, string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        if (offset + bytes.Length > data.Length) return false;
        for (int k = 0; k < bytes.Length; k++)
            if (data[offset + k] != bytes[k]) return false;
        return true;
    }

    /// <summary>Finds tag 0x0112 in IFD0 of an EXIF APP1 payload. Returns fallback if absent.</summary>
    internal static int ReadOrientation(byte[] data, int start, int length, int fallback)
    {
        try
        {
            if (length < 14 || !StartsWith(data, start, "Exif\0\0")) return fallback;
            int tiff = start + 6;
            int end = start + length;
            bool little = data[tiff] == (byte)'I';
            int U16(int p) => little ? data[p] | (data[p + 1] << 8) : (data[p] << 8) | data[p + 1];
            int U32(int p) => little
                ? data[p] | (data[p + 1] << 8) | (data[p + 2] << 16) | (data[p + 3] << 24)
                : (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];

            int ifd = tiff + U32(tiff + 4);
            if (ifd + 2 > end) return fallback;
            int count = U16(ifd);
            for (int e = 0; e < count; e++)
            {
                int entry = ifd + 2 + e * 12;
                if (entry + 12 > end) break;
                if (U16(entry) == 0x0112)
                {
                    int value = U16(entry + 8);
                    return value is >= 1 and <= 8 ? value : fallback;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            // weird EXIF, ignore it
        }
        return fallback;
    }

    private static void WriteOrientationExif(Stream s, int orientation)
    {
        // "Exif\0\0" + big-endian TIFF header + IFD0 with a single Orientation entry
        byte[] payload =
        {
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,
            0, 1,                               // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1,       // tag 0x0112, type SHORT, count 1
            0, (byte)orientation, 0, 0,         // value
            0, 0, 0, 0,                         // no next IFD
        };
        int len = payload.Length + 2;
        s.WriteByte(0xFF);
        s.WriteByte(0xE1);
        s.WriteByte((byte)(len >> 8));
        s.WriteByte((byte)(len & 0xFF));
        s.Write(payload, 0, payload.Length);
    }
}
