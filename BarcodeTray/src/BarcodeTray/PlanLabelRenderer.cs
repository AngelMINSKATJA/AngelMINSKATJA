using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace BarcodeTray;

/// <summary>
/// Mesures de la mise en page d'une étiquette « plan de châssis » (renvoyées par
/// <see cref="PlanLabelRenderer.Render(string, string, AppSettings, int, int, RectangleF, out PlanLabelLayout)"/>).
/// Toutes les valeurs en points de l'image (origine = coin haut gauche du papier).
/// </summary>
internal sealed record PlanLabelLayout
{
    /// <summary>Nom de la police réellement utilisée (peut différer du réglage si elle est absente du poste).</summary>
    public string FontName { get; init; } = string.Empty;

    public int WidthPx { get; init; }

    public int HeightPx { get; init; }

    public int Dpi { get; init; }

    /// <summary>Taille demandée (réglages) et taille retenue après réduction éventuelle, en points typographiques.</summary>
    public float TypeRequestedPt { get; init; }

    public float TypePt { get; init; }

    public float PlanRequestedPt { get; init; }

    public float PlanPt { get; init; }

    /// <summary>Faux si même à la taille minimale le texte déborde de la zone imprimable (il sera alors rogné par le pilote).</summary>
    public bool TypeFits { get; init; }

    public bool PlanFits { get; init; }

    /// <summary>Encre (boîte noire du texte) du type et du numéro de plan, en points de l'image ; vide si rien n'est dessiné.</summary>
    public RectangleF TypeInkPx { get; init; }

    public RectangleF PlanInkPx { get; init; }

    /// <summary>Limites horizontales (zone imprimable moins la marge de sécurité) utilisées pour la réduction, en points.</summary>
    public double LeftLimitPx { get; init; }

    public double RightLimitPx { get; init; }

    public bool TypeShrunk => TypePt < TypeRequestedPt;

    public bool PlanShrunk => PlanPt < PlanRequestedPt;
}

/// <summary>
/// Dessin de l'étiquette « plan de châssis » : type en haut à gauche, numéro de plan centré en dessous.
/// </summary>
/// <remarks>
/// Les contours des lettres sont obtenus avec <see cref="GraphicsPath.AddString(string, FontFamily, int, float, PointF, StringFormat)"/>
/// (taille de l'em en POINTS DE L'IMAGE = pt x dpi / 72, mise en forme « typographique » sans marge) : la boîte de l'encre
/// est donc mesurée exactement, sans dépendre de la mise en page de Windows. Chaque ligne est ensuite déplacée pour que
/// son encre soit au bon endroit à gauche ou au centre, et pour que sa LIGNE DE BASE soit à l'ordonnée voulue : la
/// référence verticale est le bas de la lettre « H » (sans débord) de la même taille, de sorte que « 11111 » et « 20086 »
/// (chiffres ronds qui débordent un peu sous la base) ont exactement la même ligne de base.
/// Tout est exprimé en coordonnées de l'ÉTIQUETTE (origine = coin du papier), comme dans P-touch Editor : l'image de la
/// page est ensuite dessinée en (-zone imprimable X, -zone imprimable Y) et c'est le pilote qui rogne.
/// </remarks>
public static class PlanLabelRenderer
{
    /// <summary>Longueur maximale (après suppression des espaces aux extrémités) d'un champ de l'étiquette.</summary>
    public const int MaxFieldLength = 30;

    internal const string TypeFieldName = "Type de châssis";
    internal const string PlanFieldName = "N° de plan";

    /// <summary>
    /// Taille minimale (pt) en dessous de laquelle le texte n'est plus réduit (4 pt : 30 caractères, même les plus larges,
    /// tiennent alors dans la largeur du ruban ; un texte illisible vaut mieux qu'un texte rogné en silence).
    /// </summary>
    internal const float MinFontPt = 4f;

    /// <summary>Pas de réduction de la taille, en points typographiques.</summary>
    internal const float FontStepPt = 0.5f;

    /// <summary>Marge de sécurité, de chaque côté, à l'intérieur de la zone imprimable (mm).</summary>
    internal const double SafetyMm = 0.3;

    /// <summary>Marges supposées quand le pilote n'annonce aucune zone imprimable (mm, de chaque côté).</summary>
    internal const double FallbackMarginMm = 1.5;

    // Un contour de lettre est aplati avec une tolérance de 0,05 point avant d'être mesuré : la boîte est alors exacte
    // (à 0,05 point près) même pour les courbes, alors que GetBounds() sur des courbes de Bézier peut inclure leurs
    // points de contrôle.
    private const float FlatnessPx = 0.05f;

    private const int MaxPageUnits = 20000;          // 200 pouces
    private const long MaxPagePixels = 40_000_000;   // 120 Mo en 24 bits : largement plus que 62 x 40 mm à 1200 dpi (5,5 M)
    private const int DarkThreshold = 128;

    private static int _fontWarned;

    // ------------------------------------------------------------------ validation

    /// <summary>Message d'erreur français si le champ ne peut pas être imprimé (vide, trop long, caractères de contrôle), sinon null.</summary>
    public static string? ValidateField(string? value, string fieldName)
    {
        string name = string.IsNullOrWhiteSpace(fieldName) ? "champ" : fieldName.Trim();
        string t = (value ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return "Le champ « " + name + " » est vide.";
        }

        if (t.Length > MaxFieldLength)
        {
            return "Le champ « " + name + " » est trop long (" + t.Length.ToString(CultureInfo.InvariantCulture)
                   + " caractères, " + MaxFieldLength.ToString(CultureInfo.InvariantCulture) + " au maximum).";
        }

        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            string? problem;
            if (char.IsHighSurrogate(c) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
            {
                problem = DescribeUnprintable(CharUnicodeInfo.GetUnicodeCategory(t, i), char.ConvertToUtf32(c, t[i + 1]));
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                problem = DescribeUnprintable(UnicodeCategory.Surrogate, c);
            }
            else
            {
                problem = DescribeUnprintable(char.GetUnicodeCategory(c), c);
            }

            if (problem != null)
            {
                return "Le champ « " + name + " » contient un caractère non imprimable (" + problem + ").";
            }
        }

        return null;
    }

    /// <summary>Décrit (français, avec le code Unicode) un caractère qu'on ne peut pas dessiner, ou null s'il est imprimable.</summary>
    private static string? DescribeUnprintable(UnicodeCategory category, int codePoint)
    {
        string? what;
        switch (category)
        {
            case UnicodeCategory.Control:
                what = codePoint switch
                {
                    '\t' => "tabulation",
                    '\r' or '\n' => "retour à la ligne",
                    _ => "caractère de contrôle",
                };
                break;
            case UnicodeCategory.Format:
                what = "caractère invisible";
                break;
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
                what = "séparateur de ligne";
                break;
            case UnicodeCategory.Surrogate:
                what = "caractère incomplet";
                break;
            case UnicodeCategory.PrivateUse:
                what = "caractère à usage privé";
                break;
            case UnicodeCategory.OtherNotAssigned:
                what = "caractère inconnu";
                break;
            default:
                return null;
        }

        return what + ", U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ calculs purs (testables sans GDI+)

    /// <summary>Taille en points de l'image d'une longueur en centièmes de pouce : units x dpi / 100, arrondi, au moins 1.</summary>
    internal static int PageSizePx(int units, int dpi)
    {
        double px = Math.Round(units * (double)dpi / 100.0, MidpointRounding.AwayFromZero);
        return (int)Math.Max(1.0, Math.Min(px, int.MaxValue));
    }

    /// <summary>Millimètres vers points de l'image à la résolution donnée.</summary>
    internal static double MmToPx(double mm, int dpi) => mm * dpi / 25.4;

    /// <summary>
    /// Limites horizontales (gauche, droite), en points de l'image, entre lesquelles toute l'encre doit rester :
    /// la zone imprimable annoncée par le pilote (centièmes de pouce, origine = coin du papier) bornée à la page, moins
    /// <see cref="SafetyMm"/> de chaque côté. Zone vide, non finie ou hors de la page : marges de
    /// <see cref="FallbackMarginMm"/> de chaque côté de la page. Toujours <c>Right &gt;= Left</c>.
    /// </summary>
    internal static (double Left, double Right) ResolveHorizontalLimits(RectangleF printableAreaUnits, int pageWidthPx, int dpi)
    {
        double left = 0;
        double right = 0;
        bool usable = IsFinite(printableAreaUnits.X) && IsFinite(printableAreaUnits.Y)
                      && IsFinite(printableAreaUnits.Width) && IsFinite(printableAreaUnits.Height)
                      && printableAreaUnits.Width > 0 && printableAreaUnits.Height > 0;
        if (usable)
        {
            double perUnit = dpi / 100.0;
            left = Math.Max(0.0, printableAreaUnits.X * perUnit);
            right = Math.Min(pageWidthPx, ((double)printableAreaUnits.X + printableAreaUnits.Width) * perUnit);
            usable = right > left;
        }

        if (!usable)
        {
            double margin = MmToPx(FallbackMarginMm, dpi);
            left = margin;
            right = pageWidthPx - margin;
        }

        double safety = MmToPx(SafetyMm, dpi);
        left += safety;
        right -= safety;
        if (right < left)
        {
            right = left; // page ou zone minuscule : plus aucune place, le texte sera réduit au minimum
        }

        return (left, right);
    }

    /// <summary>
    /// Centre horizontal du numéro de plan : celui de la page, sauf s'il n'est pas à l'intérieur des limites (zone imprimable
    /// annoncée entièrement d'un côté de la page) : on centre alors sur le milieu des limites pour que le texte reste visible.
    /// </summary>
    internal static double ResolveCenter(double pageCenterPx, double leftLimitPx, double rightLimitPx)
    {
        return pageCenterPx > leftLimitPx && pageCenterPx < rightLimitPx ? pageCenterPx : (leftLimitPx + rightLimitPx) / 2.0;
    }

    /// <summary>Largeur disponible pour une encre centrée en <paramref name="centerPx"/> entre deux limites (0 si le centre est hors limites).</summary>
    internal static double CenteredCapacity(double centerPx, double leftLimitPx, double rightLimitPx)
    {
        return Math.Max(0.0, 2.0 * Math.Min(centerPx - leftLimitPx, rightLimitPx - centerPx));
    }

    /// <summary>
    /// Plus grande taille (pas de <paramref name="stepPt"/>, à partir de <paramref name="startPt"/>) dont la largeur d'encre
    /// mesurée par <paramref name="measureInkWidthPx"/> tient dans <paramref name="availablePx"/>. Si rien ne tient, la
    /// taille minimale (<paramref name="minPt"/>, ou <paramref name="startPt"/> si elle est plus petite) est renvoyée avec
    /// <paramref name="fits"/> faux. La largeur mesurée est supposée croissante avec la taille.
    /// </summary>
    internal static float FitPointSize(float startPt, float minPt, float stepPt, double availablePx,
        Func<float, double> measureInkWidthPx, out bool fits)
    {
        if (measureInkWidthPx == null)
        {
            throw new ArgumentNullException(nameof(measureInkWidthPx));
        }

        float step = stepPt > 0f ? stepPt : FontStepPt;
        float floor = Math.Min(minPt, startPt);
        float pt = startPt;
        while (true)
        {
            double width = measureInkWidthPx(pt);
            if (width <= availablePx)
            {
                fits = true;
                return pt;
            }

            if (pt <= floor)
            {
                fits = false;
                return pt;
            }

            pt = Math.Max(pt - step, floor);
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    // ------------------------------------------------------------------ dessin

    /// <summary>
    /// Image de la PAGE entière, en coordonnées de l'étiquette (origine = coin haut gauche du papier), à <c>settings.Dpi</c> :
    /// largeur = pageWidthUnits x Dpi / 100 points, hauteur = pageHeightUnits x Dpi / 100 points (centièmes de pouce).
    /// <paramref name="printableAreaUnits"/> est la zone imprimable annoncée par le pilote (X, Y, largeur, hauteur en centièmes
    /// de pouce, origine = coin du papier) : le texte est réduit s'il ne tient pas dans sa largeur. Fond blanc, 24 bits.
    /// L'appelant libère le Bitmap.
    /// </summary>
    /// <exception cref="ArgumentException">Un champ est vide, trop long ou contient un caractère non imprimable (message français).</exception>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions de page nulles, négatives ou démesurées.</exception>
    public static Bitmap Render(string type, string plan, AppSettings settings, int pageWidthUnits, int pageHeightUnits, RectangleF printableAreaUnits)
        => Render(type, plan, settings, pageWidthUnits, pageHeightUnits, printableAreaUnits, out _);

    /// <summary>Comme <see cref="Render(string, string, AppSettings, int, int, RectangleF)"/>, et renvoie aussi les mesures de la mise en page.</summary>
    internal static Bitmap Render(string type, string plan, AppSettings settings, int pageWidthUnits, int pageHeightUnits,
        RectangleF printableAreaUnits, out PlanLabelLayout layout)
    {
        string? error = ValidateField(type, TypeFieldName) ?? ValidateField(plan, PlanFieldName);
        if (error != null)
        {
            throw new ArgumentException(error); // sans nom de paramètre : le message est montré tel quel à l'utilisateur
        }

        if (pageWidthUnits <= 0 || pageWidthUnits > MaxPageUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidthUnits), pageWidthUnits, "Largeur de page invalide.");
        }

        if (pageHeightUnits <= 0 || pageHeightUnits > MaxPageUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(pageHeightUnits), pageHeightUnits, "Hauteur de page invalide.");
        }

        string typeText = type.Trim();
        string planText = plan.Trim();

        AppSettings s = (settings ?? new AppSettings()).Clone();
        s.Validate();

        int dpi = s.Dpi;
        int widthPx = PageSizePx(pageWidthUnits, dpi);
        int heightPx = PageSizePx(pageHeightUnits, dpi);
        if ((long)widthPx * heightPx > MaxPagePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidthUnits), "La page demandée est trop grande.");
        }

        (double leftLimit, double rightLimit) = ResolveHorizontalLimits(printableAreaUnits, widthPx, dpi);
        double typeLeftPx = Math.Max(MmToPx(s.BatchTypeLeftMm, dpi), leftLimit);
        double typeBaselinePx = MmToPx(s.BatchTypeBaselineMm, dpi);
        double planBaselinePx = MmToPx(s.BatchPlanBaselineMm, dpi);
        double centerPx = ResolveCenter(widthPx / 2.0, leftLimit, rightLimit);

        using FontFamily family = ResolveFontFamily(s.BatchFontName);
        using StringFormat format = CreateFormat();

        // 1) Taille retenue : la plus grande (pas de 0,5 pt) dont l'encre mesurée tient dans la zone imprimable.
        float typePt = FitPointSize(s.BatchTypeFontPt, MinFontPt, FontStepPt, rightLimit - typeLeftPx,
            pt => MeasureInkWidth(typeText, family, format, EmPx(pt, dpi)), out bool typeFits);
        float planPt = FitPointSize(s.BatchPlanFontPt, MinFontPt, FontStepPt, CenteredCapacity(centerPx, leftLimit, rightLimit),
            pt => MeasureInkWidth(planText, family, format, EmPx(pt, dpi)), out bool planFits);

        // 2) Dessin.
        var bitmap = new Bitmap(widthPx, heightPx, PixelFormat.Format24bppRgb);
        try
        {
            bitmap.SetResolution(dpi, dpi);
            RectangleF typeInk;
            RectangleF planInk;
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.PageUnit = GraphicsUnit.Pixel;
                g.ResetTransform();
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half; // coordonnées = bords de points : placement exact même lissé
                g.Clear(Color.White);

                using var black = new SolidBrush(Color.Black);
                typeInk = DrawLine(g, black, typeText, family, format, EmPx(typePt, dpi), typeBaselinePx, typeLeftPx, centered: false);
                planInk = DrawLine(g, black, planText, family, format, EmPx(planPt, dpi), planBaselinePx, centerPx, centered: true);
            }

            layout = new PlanLabelLayout
            {
                FontName = family.Name,
                WidthPx = widthPx,
                HeightPx = heightPx,
                Dpi = dpi,
                TypeRequestedPt = s.BatchTypeFontPt,
                TypePt = typePt,
                PlanRequestedPt = s.BatchPlanFontPt,
                PlanPt = planPt,
                TypeFits = typeFits,
                PlanFits = planFits,
                TypeInkPx = typeInk,
                PlanInkPx = planInk,
                LeftLimitPx = leftLimit,
                RightLimitPx = rightLimit,
            };
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Dessine une ligne de texte noire : encre alignée à gauche sur <paramref name="anchorX"/> (ou centrée sur lui si
    /// <paramref name="centered"/>) et ligne de base sur <paramref name="baselineY"/>. Renvoie la boîte de l'encre posée
    /// (vide si le texte n'a aucun contour visible).
    /// </summary>
    private static RectangleF DrawLine(Graphics g, Brush brush, string text, FontFamily family, StringFormat format,
        float emPx, double baselineY, double anchorX, bool centered)
    {
        using GraphicsPath path = BuildPath(text, family, emPx, format);
        RectangleF ink = MeasureInk(path);
        if (ink.IsEmpty)
        {
            return RectangleF.Empty;
        }

        double dx = anchorX - (centered ? ink.Left + ink.Width / 2.0 : ink.Left);
        double dy = baselineY - BaselineOffset(family, format, emPx);

        using (var move = new Matrix())
        {
            move.Translate((float)dx, (float)dy);
            path.Transform(move);
        }

        g.FillPath(brush, path);
        return new RectangleF((float)(ink.X + dx), (float)(ink.Y + dy), ink.Width, ink.Height);
    }

    /// <summary>
    /// Distance entre l'ordonnée d'origine du texte et sa ligne de base : bas de la lettre « H » (sans débord), mesuré sur
    /// son contour. Repli sur l'ascendante de la police si le contour est vide.
    /// </summary>
    private static double BaselineOffset(FontFamily family, StringFormat format, float emPx)
    {
        using GraphicsPath reference = BuildPath("H", family, emPx, format);
        RectangleF box = MeasureInk(reference);
        if (!box.IsEmpty)
        {
            return box.Bottom;
        }

        int emHeight = family.GetEmHeight(FontStyle.Regular);
        return emHeight > 0 ? family.GetCellAscent(FontStyle.Regular) * (double)emPx / emHeight : emPx;
    }

    private static double MeasureInkWidth(string text, FontFamily family, StringFormat format, float emPx)
    {
        using GraphicsPath path = BuildPath(text, family, emPx, format);
        RectangleF ink = MeasureInk(path);
        return ink.IsEmpty ? 0.0 : ink.Width;
    }

    private static float EmPx(float pt, int dpi) => pt * dpi / 72f;

    private static GraphicsPath BuildPath(string text, FontFamily family, float emPx, StringFormat format)
    {
        var path = new GraphicsPath();
        try
        {
            path.AddString(text, family, (int)FontStyle.Regular, emPx, new PointF(0f, 0f), format);
            return path;
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    /// <summary>Boîte exacte de l'encre d'un contour (courbes aplaties sur une copie) ; vide si le contour n'a aucun point.</summary>
    private static RectangleF MeasureInk(GraphicsPath path)
    {
        if (path.PointCount == 0)
        {
            return RectangleF.Empty;
        }

        using var flat = (GraphicsPath)path.Clone();
        using var identity = new Matrix();
        flat.Flatten(identity, FlatnessPx);
        return flat.PointCount == 0 ? RectangleF.Empty : flat.GetBounds();
    }

    /// <summary>
    /// Mise en forme « typographique » : pas de marge autour du texte, une seule ligne, rien n'est rogné, le caractère
    /// « &amp; » reste un « &amp; ».
    /// </summary>
    private static StringFormat CreateFormat()
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None,
        };
        format.FormatFlags = (format.FormatFlags & ~StringFormatFlags.LineLimit)
                             | StringFormatFlags.NoWrap
                             | StringFormatFlags.NoClip;
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
                // police absente : repli ci-dessous
            }

            if (Interlocked.Exchange(ref _fontWarned, 1) == 0)
            {
                Log.Warn("Police « " + name + " » introuvable : police sans empattement par défaut pour les étiquettes en série.");
            }
        }

        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
    }

    // ------------------------------------------------------------------ contrôle de l'image

    /// <summary>
    /// Plus petit rectangle (en points) contenant tous les points « sombres » (luminosité moyenne &lt; <paramref name="threshold"/>)
    /// de l'image ; <see cref="Rectangle.Empty"/> si elle est entièrement claire. Sert à l'auto-test.
    /// </summary>
    internal static Rectangle FindInkBounds(Bitmap bitmap, int threshold = DarkThreshold)
    {
        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;
        int limit = threshold * 3;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    int sum = row[3 * x] + row[3 * x + 1] + row[3 * x + 2];
                    if (sum < limit)
                    {
                        if (x < minX)
                        {
                            minX = x;
                        }

                        if (x > maxX)
                        {
                            maxX = x;
                        }

                        if (y < minY)
                        {
                            minY = y;
                        }

                        maxY = y;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
