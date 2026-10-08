using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Enfolderer.Ai.Imaging;

/// <summary>
/// A perceptual hash of a card face, and the distance between two of them.
/// <para>
/// This exists to answer the one question no amount of text can settle. A card whose name is
/// right, whose set is a real set and whose collector number is that set's real number for that
/// name is self-consistent in every field: nothing in the catalogue can object to it, because
/// every part of it is true of <em>some</em> card. Only the picture disagrees. That is exactly the
/// shape of an alternate-art reprint, which is the failure this scan keeps hitting, and it is why
/// the art has to be compared rather than reasoned about.
/// </para>
/// <para>
/// The hash is a difference hash: reduce to greyscale, resize to <see cref="Size"/>+1 by
/// <see cref="Size"/>, and record one bit per pixel for whether it is brighter than the pixel to
/// its right. The bits therefore describe where the image gets lighter and darker across the face
/// — its structure — and nothing about its absolute brightness or colour.
/// </para>
/// <para>
/// That is the point, because the two images being compared are never alike as files. One is a
/// clean scan from the catalogue; the other is a photograph through a sleeve, under whatever light
/// the room had, rectified out of a quadrilateral by <see cref="PerspectiveCropper"/> and encoded
/// as JPEG somewhere along the way. Exact comparison fails on every one of those; a difference
/// hash is unmoved by all of them, because none changes which parts of a picture are lighter than
/// their neighbours.
/// </para>
/// <para>
/// Greyscale rather than colour is deliberate and not merely a simplification. A foil card throws
/// colour everywhere — the holographic layer tints whole regions depending on the angle it is held
/// at — while leaving the light-and-dark structure of the art intact. Comparing colour would make
/// foils the worst case; comparing structure makes them merely a little noisier.
/// </para>
/// </summary>
public static class CardArtHash
{
    /// <summary>
    /// Width and height, in pixels, of the grid the image is reduced to. The hash is this squared
    /// in bits — 1024 for the default 32 — which is small enough to compare instantly and large
    /// enough to tell two illustrations apart rather than merely two layouts.
    /// </summary>
    public const int Size = 32;

    /// <summary>Number of bits in a hash, and so the largest possible distance between two.</summary>
    public const int BitCount = Size * Size;

    /// <summary>
    /// Hashes a whole card face.
    /// <para>
    /// The whole face, not the illustration box. Cropping out the art alone would compare more
    /// signal and less frame, but where the art box sits is a property of the frame, and the cards
    /// this exists to catch — borderless, showcase, full-art — are precisely the ones with no
    /// fixed art window. Finding the box would mean solving the layout problem first, and getting
    /// it wrong would compare a card's artwork against another card's border. The whole face is
    /// something both sides can produce without knowing anything about the frame.
    /// </para>
    /// </summary>
    public static ulong[] Compute(Stream image)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var loaded = Image.Load<Rgba32>(image);
        return Compute(loaded);
    }

    /// <summary>Hashes an image already in memory.</summary>
    public static ulong[] Compute(Image<Rgba32> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var reduced = image.Clone(context => context
            // One extra column: each bit compares a pixel with its right-hand neighbour, so Size
            // comparisons across a row need Size + 1 pixels.
            .Resize(Size + 1, Size)
            .Grayscale());

        var bits = new ulong[(BitCount + 63) / 64];
        var index = 0;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                // R alone: Grayscale has already put the same luminance in all three channels.
                if (reduced[x, y].R > reduced[x + 1, y].R)
                    bits[index / 64] |= 1UL << (index % 64);
                index++;
            }
        }

        return bits;
    }

    /// <summary>
    /// How many bits two hashes differ in: 0 for identical structure, and around
    /// <see cref="BitCount"/>/2 for two unrelated pictures, since independent bits agree half the
    /// time by chance.
    /// <para>
    /// That midpoint is worth holding on to, because it means a large distance carries no more
    /// information than a middling one. Two different illustrations sit near half the bit count
    /// and stay there; the signal is entirely in how far <em>below</em> that a candidate falls.
    /// </para>
    /// </summary>
    public static int Distance(ulong[] left, ulong[] right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length)
            throw new ArgumentException("Hashes must be the same length to be compared.", nameof(right));

        var distance = 0;
        for (var i = 0; i < left.Length; i++) distance += System.Numerics.BitOperations.PopCount(left[i] ^ right[i]);
        return distance;
    }

    /// <summary>
    /// <see cref="Distance"/> as a fraction of the bits, so a threshold can be written without
    /// knowing <see cref="Size"/>. 0 is identical, 0.5 is two unrelated pictures.
    /// </summary>
    public static double NormalisedDistance(ulong[] left, ulong[] right)
        => (double)Distance(left, right) / BitCount;
}
