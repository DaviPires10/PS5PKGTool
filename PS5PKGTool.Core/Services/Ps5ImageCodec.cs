using ImageMagick;
using PS5PKGTool.Core.Models;

namespace PS5PKGTool.Core.Services;

/// <summary>
/// DDS/BC and PNG codecs used by the details panes and thumbnails. Both formats are decoded and
/// encoded with ImageMagick (Magick.NET), so the engine has a single image dependency instead of a
/// separate BC decoder and PNG codec.
/// </summary>
public static class Ps5ImageCodec
{
    public static byte[] DecodeDdsToPng(Stream ddsStream)
    {
        ArgumentNullException.ThrowIfNull(ddsStream);
        if (!ddsStream.CanRead) throw new ArgumentException("The DDS stream must be readable.", nameof(ddsStream));
        using var image = new MagickImage(ddsStream);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    /// <summary>
    /// Decodes a DDS/BC texture to raw RGBA pixels. The UI builds a bitmap straight from these bytes,
    /// avoiding the intermediate PNG encode/decode a re-encoded PNG would require.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) DecodeDdsToRgba(Stream ddsStream)
    {
        ArgumentNullException.ThrowIfNull(ddsStream);
        if (!ddsStream.CanRead) throw new ArgumentException("The DDS stream must be readable.", nameof(ddsStream));
        using var image = new MagickImage(ddsStream);
        return ToRgba(image);
    }

    /// <summary>
    /// Decodes a PNG and resizes it to <paramref name="width"/> x <paramref name="height"/> raw RGBA.
    /// Used to pre-build small grid thumbnails (for example trophy icons) on the background thread so
    /// the UI only has to blit the pixels. Returns null when the bytes are not a decodable image.
    /// </summary>
    public static Ps5ImageData? DecodePngToRgba(byte[] pngBytes, int width, int height)
    {
        if (pngBytes is null || pngBytes.Length == 0 || width <= 0 || height <= 0) return null;
        try
        {
            using var image = new MagickImage(pngBytes);
            image.BackgroundColor = MagickColors.Transparent;
            image.Alpha(AlphaAction.Set);
            image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
            (byte[] rgba, int actualWidth, int actualHeight) = ToRgba(image);
            return Ps5ImageData.FromRgba(rgba, actualWidth, actualHeight);
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or
                                   InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static (byte[] Rgba, int Width, int Height) ToRgba(MagickImage image)
    {
        image.Alpha(AlphaAction.Set);
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        byte[] rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("ImageMagick did not return the decoded pixels.");
        if (rgba.Length != checked(width * height * 4))
            throw new InvalidOperationException("ImageMagick returned an unexpected pixel buffer size.");
        return (rgba, width, height);
    }
}
