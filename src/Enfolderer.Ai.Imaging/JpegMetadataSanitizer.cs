using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Enfolderer.Ai.Imaging;

/// <summary>Removes private JPEG metadata without re-encoding pixels or changing display orientation.</summary>
public static class JpegMetadataSanitizer
{
    public static byte[] StripPrivateMetadata(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("Expected a JPEG image.");
        using var image = Image.Load(jpeg);
        var orientation = image.Metadata.ExifProfile?.GetValue(ExifTag.Orientation)?.Value ?? 1;
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        if (orientation is >= 2 and <= 8)
        {
            // Minimal little-endian EXIF IFD: orientation only, with no GPS or camera identifiers.
            byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
                73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0,
                (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
            output.Write(exif);
        }
        var position = 2;
        while (position < jpeg.Length)
        {
            var start = position;
            if (jpeg[position++] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            while (position < jpeg.Length && jpeg[position] == 0xff) position++;
            if (position >= jpeg.Length) throw new InvalidDataException("Truncated JPEG marker.");
            var marker = jpeg[position++];
            if (marker is 0xda or 0xd9)
            {
                output.Write(jpeg, start, jpeg.Length - start);
                return output.ToArray();
            }
            if (marker is 0x01 or >= 0xd0 and <= 0xd7)
            {
                output.Write(jpeg, start, position - start);
                continue;
            }
            if (position + 2 > jpeg.Length) throw new InvalidDataException("Truncated JPEG segment.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(position, 2));
            if (length < 2 || position + length > jpeg.Length)
                throw new InvalidDataException("Invalid JPEG segment length.");
            // APP1 = EXIF/XMP, APP13 = IPTC, COM = arbitrary comments. Keep ICC and compressed pixels.
            if (marker is not (0xe1 or 0xed or 0xfe))
                output.Write(jpeg, start, position + length - start);
            position += length;
        }
        throw new InvalidDataException("JPEG contains no scan or end marker.");
    }
}
