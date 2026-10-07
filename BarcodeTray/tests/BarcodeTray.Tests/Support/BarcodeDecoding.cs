using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace BarcodeTray.Tests.Support;

/// <summary>
/// Décodeur indépendant : on relit l'image produite par <see cref="BarcodeRenderer"/> avec ZXing.Net
/// (jamais livré avec l'application). Si ZXing retrouve le texte, un lecteur de code-barres le retrouvera aussi.
/// </summary>
internal static class BarcodeDecoding
{
    /// <summary>Image en octets B,G,R (ordre mémoire de Format24bppRgb), lignes consécutives sans remplissage.</summary>
    internal sealed class Raster
    {
        internal Raster(byte[] bgr24, int width, int height)
        {
            Bgr24 = bgr24;
            Width = width;
            Height = height;
        }

        internal byte[] Bgr24 { get; }

        internal int Width { get; }

        internal int Height { get; }

        /// <summary>Luminance approximative (0 = noir, 255 = blanc) du pixel (x, y).</summary>
        internal int Luma(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return (Bgr24[i] + Bgr24[i + 1] + Bgr24[i + 2]) / 3;
        }

        internal bool IsPureBlackOrWhite(int x, int y)
        {
            int i = (y * Width + x) * 3;
            byte b = Bgr24[i], g = Bgr24[i + 1], r = Bgr24[i + 2];
            return b == g && g == r && (b == 0 || b == 255);
        }
    }

    /// <summary>Copie un Bitmap dans un tableau d'octets BGR24 compact (via LockBits, quel que soit le format source).</summary>
    internal static Raster ToRaster(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 3;
        var packed = new byte[rowBytes * height];

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            // Stride : multiple de 4 octets (donc plus grand que rowBytes) ; copie ligne par ligne.
            for (int y = 0; y < height; y++)
            {
                IntPtr rowStart = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowStart, packed, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new Raster(packed, width, height);
    }

    internal static string? Decode(Bitmap bitmap) => Decode(ToRaster(bitmap));

    /// <summary>Texte lu par le lecteur Code 128 de ZXing, ou null si rien n'a pu être décodé.</summary>
    internal static string? Decode(Raster raster)
    {
        var source = new RGBLuminanceSource(
            raster.Bgr24, raster.Width, raster.Height, RGBLuminanceSource.BitmapFormat.BGR24);
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        var hints = new Dictionary<DecodeHintType, object> { { DecodeHintType.TRY_HARDER, true } };
        try
        {
            Result? result = new Code128Reader().decode(bitmap, hints);
            return result?.Text;
        }
        catch (ReaderException)
        {
            return null;
        }
    }
}
