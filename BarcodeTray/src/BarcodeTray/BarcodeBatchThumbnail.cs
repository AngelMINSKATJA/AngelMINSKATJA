using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BarcodeTray;

/// <summary>
/// Miniature d'un code-barres pour la colonne « Code-barres » de la fenêtre « Codes-barres en série » : le VRAI code-barres, tel
/// que <see cref="BarcodeRenderer"/> le dessinerait pour l'impression, réduit avec une interpolation de haute qualité pour tenir
/// dans la cellule. L'image imprimée n'est jamais cette miniature.
/// </summary>
internal static class BarcodeBatchThumbnail
{
    /// <summary>
    /// Dessine <paramref name="text"/> (déjà validé) puis le réduit pour tenir dans <paramref name="maxWidth"/> x <paramref name="maxHeight"/>
    /// (proportions gardées, jamais agrandi). L'appelant libère (Dispose) le Bitmap rendu. Lève <see cref="ArgumentException"/>
    /// (message français) si le texte n'est pas un code-barres valide.
    /// </summary>
    internal static Bitmap Create(string text, AppSettings settings, int maxWidth, int maxHeight)
    {
        using Bitmap full = BarcodeRenderer.Render(text, settings, out _);
        (int width, int height) = BarcodeBatchLogic.FitSize(full.Width, full.Height, maxWidth, maxHeight);

        var thumb = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            thumb.SetResolution(96f, 96f);
            using Graphics g = Graphics.FromImage(thumb);
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // TileFlipXY : sans cela, la réduction laisse un liseré gris sur les bords de l'image.
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(
                full,
                new Rectangle(0, 0, width, height),
                0,
                0,
                full.Width,
                full.Height,
                GraphicsUnit.Pixel,
                attributes);
            return thumb;
        }
        catch
        {
            thumb.Dispose();
            throw;
        }
    }
}
