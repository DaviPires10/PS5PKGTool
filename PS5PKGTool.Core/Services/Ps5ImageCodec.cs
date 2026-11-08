using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using PS5PKGTool.Core.Models;
using SkiaSharp;

namespace PS5PKGTool.Core.Services;

/// <summary>
/// DDS/BC and PNG codecs used by the details panes and thumbnails. BC textures are decoded with
/// BCnEncoder (MIT/Unlicense) and PNGs are encoded/decoded with SkiaSharp (MIT), so the engine has no
/// ImageSharp dependency and no license-gated or vulnerable image package.
/// </summary>
public static class Ps5ImageCodec
{
    public static byte[] DecodeDdsToPng(Stream ddsStream)
    {
        ArgumentNullException.ThrowIfNull(ddsStream);
        if (!ddsStream.CanRead) throw new ArgumentException("The DDS stream must be readable.", nameof(ddsStream));
        Memory2D<ColorRgba32> pixels = new BcDecoder().Decode2D(ddsStream);
        using SKBitmap bitmap = CreateBitmap(pixels);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Decodes a DDS/BC texture to raw RGBA pixels. The UI builds a bitmap straight from these bytes,
    /// avoiding the intermediate PNG encode/decode a re-encoded PNG would require.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) DecodeDdsToRgba(Stream ddsStream)
    {
        ArgumentNullException.ThrowIfNull(ddsStream);
        if (!ddsStream.CanRead) throw new ArgumentException("The DDS stream must be readable.", nameof(ddsStream));
        Memory2D<ColorRgba32> pixels = new BcDecoder().Decode2D(ddsStream);
        byte[] rgba = new byte[checked(pixels.Width * pixels.Height * 4)];
        for (int y = 0; y < pixels.Height; y++)
        {
            ReadOnlySpan<ColorRgba32> row = pixels.Span.GetRowSpan(y);
            MemoryMarshal.AsBytes(row).CopyTo(rgba.AsSpan(y * pixels.Width * 4, pixels.Width * 4));
        }
        return (rgba, pixels.Width, pixels.Height);
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
            using SKBitmap? source = SKBitmap.Decode(pngBytes);
            if (source is null) return null;
            using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
                SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(target))
            {
                canvas.Clear(SKColors.Transparent);
                using SKImage image = SKImage.FromBitmap(source);
                canvas.DrawImage(image, new SKRect(0, 0, width, height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            return Ps5ImageData.FromRgba(target.Bytes, width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static SKBitmap CreateBitmap(Memory2D<ColorRgba32> pixels)
    {
        var bitmap = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        using SKPixmap pixmap = bitmap.PeekPixels();
        Span<byte> destination = pixmap.GetPixelSpan();
        for (int y = 0; y < pixels.Height; y++)
        {
            ReadOnlySpan<ColorRgba32> row = pixels.Span.GetRowSpan(y);
            MemoryMarshal.AsBytes(row).CopyTo(destination.Slice(y * pixmap.RowBytes, pixels.Width * 4));
        }
        return bitmap;
    }
}
