using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace Code128.Tests.Support;

/// <summary>
/// Independent decoder oracle: renders encoded modules as a grayscale raster (exactly what a
/// printer/renderer would produce: quiet zones + integer module scale) and decodes it with
/// ZXing.Net's Code 128 reader ONLY.
/// </summary>
internal static class ZXingOracle
{
    internal const int DefaultScale = 3;
    internal const int DefaultQuietModules = 10;
    internal const int DefaultHeightPx = 48;

    internal readonly record struct Raster(byte[] Gray8, int Width, int Height);

    internal static Raster Rasterize(
        IReadOnlyList<bool> modules,
        int scale = DefaultScale,
        int quietModules = DefaultQuietModules,
        int heightPx = DefaultHeightPx)
    {
        int widthModules = modules.Count + 2 * quietModules;
        int width = widthModules * scale;
        var row = new byte[width];
        Array.Fill(row, (byte)255);                       // white background / quiet zones
        for (int m = 0; m < modules.Count; m++)
        {
            if (modules[m])
            {
                int x0 = (quietModules + m) * scale;
                for (int dx = 0; dx < scale; dx++)
                {
                    row[x0 + dx] = 0;                      // black bar
                }
            }
        }
        var pixels = new byte[width * heightPx];
        for (int y = 0; y < heightPx; y++)
        {
            Buffer.BlockCopy(row, 0, pixels, y * width, width);
        }
        return new Raster(pixels, width, heightPx);
    }

    /// <summary>Decodes a raster with the ZXing Code 128 reader; null when nothing was decoded.</summary>
    internal static string? Decode(Raster raster)
    {
        var source = new RGBLuminanceSource(
            raster.Gray8, raster.Width, raster.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        var reader = new Code128Reader();
        try
        {
            Result? result = reader.decode(bitmap);
            return result?.Text;
        }
        catch (ReaderException)
        {
            return null;
        }
    }

    /// <summary>Encode -> rasterize -> ZXing Code128Reader. Returns the decoded text or null.</summary>
    internal static string? RoundTrip(
        Code128Barcode barcode,
        int scale = DefaultScale,
        int quietModules = DefaultQuietModules)
        => Decode(Rasterize(barcode.Modules, scale, quietModules));
}
