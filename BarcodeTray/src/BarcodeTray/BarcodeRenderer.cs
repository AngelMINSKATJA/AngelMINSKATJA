using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using Code128;

namespace BarcodeTray;

/// <summary>
/// Dessine un code-barres Code 128 (barres + texte lisible dessous) dans une image 24 bits.
/// Les barres sont dessinées sur des frontières de pixels entières, sans lissage ni interpolation.
/// </summary>
public static class BarcodeRenderer
{
    /// <summary>
    /// Zone de silence dessinée de chaque côté, en modules (la norme exige au moins
    /// <see cref="Code128Encoder.QuietZoneModules"/> = 10 ; on prend 1 de plus par sécurité).
    /// </summary>
    internal const int QuietZoneModulesUsed = 11;

    /// <summary>
    /// En dessous de ce nombre de points par module (barre la plus fine), les barres sont si fines
    /// (1 point = 0,085 mm à 300 dpi) que beaucoup de lecteurs de bureau ne les lisent plus de façon fiable.
    /// </summary>
    internal const int MinReliableModulePx = 2;

    private const float MinFontPt = 6f;
    private const float FontStepPt = 0.5f;
    private const int MaxImageWidthPx = 30000;

    /// <summary>
    /// Vérifie le texte saisi. Renvoie null si tout est correct, sinon un message d'erreur en français.
    /// Règles : non vide après suppression des espaces en début et fin, uniquement ASCII imprimable (0x20 à 0x7E).
    /// </summary>
    public static string? ValidateText(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return "Saisissez un texte.";
        }

        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c >= 0x20 && c <= 0x7E)
            {
                continue;
            }

            return DescribeInvalidChar(t, i)
                   + " : Code 128 ne gère que l'ASCII (lettres sans accent, chiffres, ponctuation courante).";
        }

        return null;
    }

    /// <summary>
    /// Nombre de pixels par module (barre la plus fine) : le plus grand entier inférieur ou égal à
    /// <paramref name="preferredPx"/> tel que le code-barres complet, zones de silence comprises, tienne dans
    /// <paramref name="maxWidthPx"/> ; jamais moins de 1.
    /// <paramref name="symbolCount"/> = nombre total de symboles (départ, données, contrôle et arrêt compris),
    /// c'est-à-dire <c>Code128Barcode.Symbols.Count</c>.
    /// </summary>
    public static int ChooseModulePx(int symbolCount, int preferredPx, int maxWidthPx)
    {
        int preferred = Math.Max(1, preferredPx);
        long totalModules = TotalModules(symbolCount);
        long fit = maxWidthPx > 0 ? maxWidthPx / totalModules : 0;
        long chosen = Math.Min(preferred, fit);
        return (int)Math.Max(1, chosen);
    }

    /// <summary>
    /// Génère l'image. Le texte doit avoir passé <see cref="ValidateText"/> (il est rogné ici aussi).
    /// L'appelant doit libérer (Dispose) le Bitmap renvoyé.
    /// </summary>
    public static Bitmap Render(string text, AppSettings settings) => Render(text, settings, out _);

    /// <summary>
    /// Comme <see cref="Render(string, AppSettings)"/>, et renvoie aussi la largeur retenue pour la barre la plus
    /// fine (en points), afin que l'appelant puisse signaler des barres trop fines pour être lues sûrement.
    /// </summary>
    internal static Bitmap Render(string text, AppSettings settings, out int modulePx)
    {
        string t = (text ?? string.Empty).Trim();
        string? error = ValidateText(t);
        if (error != null)
        {
            throw new ArgumentException(error); // sans nom de paramètre : le message est montré tel quel à l'utilisateur
        }

        AppSettings s = (settings ?? new AppSettings()).Clone();
        s.Validate();

        Code128Barcode barcode = Code128Encoder.Encode(t);
        int barModules = barcode.Modules.Count;
        modulePx = ChooseModulePx(barcode.Symbols.Count, s.ModulePx, s.MaxWidthPx);
        int barsWidth = barModules * modulePx;
        int quietPx = QuietZoneModulesUsed * modulePx;
        long minWidthLong = (long)barsWidth + 2L * quietPx;
        if (minWidthLong > MaxImageWidthPx)
        {
            throw new ArgumentException("Le texte est trop long pour être converti en code-barres.");
        }

        int minWidth = (int)minWidthLong;
        // Le texte lisible peut déborder de la largeur des barres jusqu'à la largeur du ruban (MaxWidthPx) :
        // pour un texte long, les barres sont fines et étroites alors que le ruban offre encore de la place.
        int limitWidth = Math.Max(minWidth, s.MaxWidthPx);
        double scale = s.Dpi / 300.0;
        int margin = Math.Max(1, (int)Math.Round(8 * scale));
        int gap = margin;
        int barHeight = s.BarHeightPx;

        // 1) Mesure du texte (police, taille) sur une image jetable de même résolution.
        using FontFamily family = ResolveFontFamily(s.FontName);
        using StringFormat format = CreateTextFormat();
        float maxTextWidth = Math.Max(1, limitWidth - 2 * margin);

        Font? font = null;
        int lineHeight;
        double textWidth;
        try
        {
            using (var probe = new Bitmap(1, 1, PixelFormat.Format24bppRgb))
            {
                probe.SetResolution(s.Dpi, s.Dpi);
                using Graphics pg = Graphics.FromImage(probe);
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                float minPt = Math.Min(MinFontPt, s.FontSizePt);
                float pt = s.FontSizePt;
                while (true)
                {
                    font?.Dispose();
                    font = CreateFont(family, pt);
                    SizeF measured = pg.MeasureString(t, font, 100000, format);
                    textWidth = measured.Width;
                    if (measured.Width <= maxTextWidth || pt - FontStepPt < minPt)
                    {
                        break;
                    }

                    pt -= FontStepPt;
                }

                lineHeight = Math.Max(1, (int)Math.Ceiling(font!.GetHeight(pg)));
            }

            int width = ChooseCanvasWidth(barsWidth, quietPx, limitWidth, margin, textWidth);
            int height = margin + barHeight + gap + lineHeight + margin;

            // 2) Dessin. Les barres sont centrées : zones de silence gauche et droite strictement égales.
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            try
            {
                bitmap.SetResolution(s.Dpi, s.Dpi);
                using Graphics g = Graphics.FromImage(bitmap);
                g.Clear(Color.White);

                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.CompositingMode = CompositingMode.SourceOver;

                using (var black = new SolidBrush(Color.Black))
                {
                    DrawBars(g, black, barcode, (width - barsWidth) / 2, modulePx, margin, barHeight);

                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var textRect = new RectangleF(margin, margin + barHeight + gap, Math.Max(1, width - 2 * margin), lineHeight);
                    g.DrawString(t, font!, black, textRect, format);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            font?.Dispose();
        }
    }

    /// <summary>
    /// Largeur de l'image (pure) : celle des barres + zones de silence, élargie si le texte lisible est plus large,
    /// sans dépasser <paramref name="limitWidth"/>. L'écart avec la largeur des barres reste pair, de sorte que les
    /// barres sont centrées au point près (zones de silence gauche et droite identiques).
    /// </summary>
    /// <param name="barsWidth">Largeur des barres seules, sans zones de silence.</param>
    /// <param name="quietPx">Zone de silence minimale de chaque côté.</param>
    /// <param name="limitWidth">Largeur maximale permise pour le texte (jamais en dessous de la largeur minimale).</param>
    /// <param name="margin">Marge latérale du texte.</param>
    /// <param name="textWidth">Largeur mesurée du texte lisible.</param>
    internal static int ChooseCanvasWidth(int barsWidth, int quietPx, int limitWidth, int margin, double textWidth)
    {
        int minWidth = barsWidth + 2 * quietPx;
        int limit = Math.Max(minWidth, limitWidth);
        double needed = Math.Ceiling(textWidth) + 2.0 * margin;
        if (needed <= minWidth)
        {
            return minWidth;
        }

        long wantedPad = (long)Math.Ceiling((needed - barsWidth) / 2.0);
        int padMax = (limit - barsWidth) / 2; // au moins quietPx, car limit >= minWidth
        int pad = (int)Math.Min(wantedPad, padMax);
        return barsWidth + 2 * Math.Max(quietPx, pad);
    }

    /// <summary>
    /// Avertissement (en français) quand la barre la plus fine a dû être réduite à 1 point à cause de la longueur
    /// du texte : lecture peu fiable. Null si tout va bien, ou si 1 point était déjà le choix de l'utilisateur.
    /// </summary>
    internal static string? ThinBarsWarning(int chosenModulePx, int preferredModulePx)
    {
        if (chosenModulePx >= MinReliableModulePx || preferredModulePx <= chosenModulePx)
        {
            return null;
        }

        return "Attention : texte long, barres très fines (" + chosenModulePx + " point) — la lecture peut échouer. "
               + "Raccourcissez le texte.";
    }

    /// <summary>Encode l'image en PNG (la résolution est conservée dans le fichier PNG).</summary>
    public static byte[] ToPng(Bitmap bitmap)
    {
        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static long TotalModules(int symbolCount)
    {
        long symbols = Math.Max(1, symbolCount);
        // 11 modules par symbole, sauf l'arrêt qui en compte 13, plus les deux zones de silence.
        return 11 * (symbols - 1) + 13 + 2L * QuietZoneModulesUsed;
    }

    private static void DrawBars(Graphics g, Brush brush, Code128Barcode barcode, int left, int modulePx, int top, int barHeight)
    {
        var modules = barcode.Modules;
        int count = modules.Count;
        int i = 0;
        while (i < count)
        {
            if (!modules[i])
            {
                i++;
                continue;
            }

            int j = i;
            while (j < count && modules[j])
            {
                j++;
            }

            // Les modules noirs adjacents sont fusionnés en un seul rectangle (pas de coutures entre barres).
            g.FillRectangle(brush, left + i * modulePx, top, (j - i) * modulePx, barHeight);
            i = j;
        }
    }

    private static StringFormat CreateTextFormat()
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        format.FormatFlags = (format.FormatFlags & ~StringFormatFlags.LineLimit)
                             | StringFormatFlags.NoWrap
                             | StringFormatFlags.MeasureTrailingSpaces;
        return format;
    }

    private static FontFamily ResolveFontFamily(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular))
                {
                    return family;
                }

                family.Dispose();
            }
            catch (ArgumentException)
            {
                Log.Warn("Police « " + name + " » introuvable : police sans empattement par défaut.");
            }
        }

        return new FontFamily(GenericFontFamilies.SansSerif);
    }

    private static Font CreateFont(FontFamily family, float sizePt)
    {
        return new Font(family, sizePt, FontStyle.Regular, GraphicsUnit.Point);
    }

    /// <summary>Phrase (sans point final) qui nomme le premier caractère refusé, avec les bons accords.</summary>
    private static string DescribeInvalidChar(string text, int index)
    {
        char c = text[index];
        switch (c)
        {
            case '\t':
                return "Une tabulation n'est pas prise en charge";
            case '\r':
            case '\n':
                return "Un retour à la ligne n'est pas pris en charge";
        }

        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return "Le caractère « " + text.Substring(index, 2) + " » n'est pas pris en charge";
        }

        if (char.IsControl(c))
        {
            return "Le caractère de contrôle U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                   + " n'est pas pris en charge";
        }

        // Caractères invisibles : les citer entre guillemets ne montrerait rien à l'utilisateur.
        // Cas très fréquent après un copier-coller depuis Word ou Excel (« Réf : 12 345 »).
        if (c == '\u00A0') // espace insécable (U+00A0)
        {
            return "Une espace insécable (souvent issue d'un copier-coller depuis Word ou Excel) n'est pas prise en charge";
        }

        if (char.IsWhiteSpace(c))
        {
            return "Une espace spéciale (U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                   + ") n'est pas prise en charge";
        }

        if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
        {
            return "Un caractère invisible (U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                   + ") n'est pas pris en charge";
        }

        return "Le caractère « " + c + " » n'est pas pris en charge";
    }
}
