using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Étiquettes « plan de châssis » : logique pure (tourne partout, y compris sous Linux).
/// </summary>
public class PlanLabelRendererPureTests
{
    // ---------------------------------------------------------------- ValidateField

    public static TheoryData<string> PrintableValues => new()
    {
        "HB1",
        "22062",
        "  HB1  ",
        "HB1\t\r\n",                  // les blancs de début et de fin (tabulation, retours) sont retirés avant contrôle
        "HB1\u00A0",                  // espace insécable finale (copier-coller Excel) : retirée aussi
        "A\u00A0B",                   // espace insécable au milieu : dessinée comme une espace
        "Châssis é",
        "Ünïcôdé ç œ",
        "VF1 bis",
        "2020Q7E 22061",
        "N° 12",
        "a&b",                        // « & » n'est pas un raccourci clavier ici
        "A\U0001F600",                // paire de substitution valide (symbole)
        "e\u0301",                    // lettre + accent combinant
        new string('1', 30),                  // exactement 30 caractères
        "  " + new string('1', 30) + "  ",    // 30 après suppression des espaces
    };

    [Theory]
    [MemberData(nameof(PrintableValues))]
    public void ValidateField_AcceptsPrintableText(string value)
    {
        Assert.Null(PlanLabelRenderer.ValidateField(value, "N° de plan"));
    }

    public static TheoryData<string?, string> UnprintableValues => new()
    {
        { null, "est vide" },
        { "", "est vide" },
        { "   ", "est vide" },
        { "\u00A0\u00A0", "est vide" },
        { "\t\r\n", "est vide" },
        { new string('1', 31), "trop long" },
        { new string('W', 200), "trop long" },
        { "A\tB", "tabulation" },
        { "A\r\nB", "retour à la ligne" },
        { "A\nB", "retour à la ligne" },
        { "A\u0007B", "caractère de contrôle" },
        { "A\u007FB", "caractère de contrôle" },
        { "A\u0085B", "caractère de contrôle" },
        { "A\u200BB", "caractère invisible" },        // espace de largeur nulle
        { "\uFEFFA1", "caractère invisible" },        // BOM en tête (pas un blanc : n'est pas retiré par Trim)
        { "A\u2028B", "séparateur de ligne" },
        { "A\u2029B", "séparateur de ligne" },
        { "A\uD83DB", "caractère incomplet" },        // substitut haut isolé
        { "A\uDE00", "caractère incomplet" },         // substitut bas isolé
        { "A\uE000", "usage privé" },
        { "A\u0378", "caractère inconnu" },           // point de code non attribué
    };

    [Theory]
    [MemberData(nameof(UnprintableValues))]
    public void ValidateField_RejectsUnprintableText_WithFrenchMessageNamingTheField(string? value, string expectedFragment)
    {
        string? message = PlanLabelRenderer.ValidateField(value, "Type de châssis");

        Assert.NotNull(message);
        Assert.Contains("« Type de châssis »", message);
        Assert.Contains(expectedFragment, message);
    }

    [Fact]
    public void ValidateField_ReportsLengthAndLimit()
    {
        string? message = PlanLabelRenderer.ValidateField(new string('7', 45), "N° de plan");

        Assert.NotNull(message);
        Assert.Contains("45", message);
        Assert.Contains("30", message);
    }

    [Fact]
    public void ValidateField_ControlMessageGivesTheUnicodeCodePoint()
    {
        string? message = PlanLabelRenderer.ValidateField("A\u0007B", "N° de plan");

        Assert.NotNull(message);
        Assert.Contains("U+0007", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateField_WithoutFieldName_UsesAGenericOne(string? fieldName)
    {
        string? message = PlanLabelRenderer.ValidateField("", fieldName!);

        Assert.NotNull(message);
        Assert.Contains("« champ »", message);
    }

    [Fact]
    public void ValidateField_NeverThrows_OnRandomText()
    {
        var random = new Random(20240607);
        var buffer = new char[40];
        for (int round = 0; round < 3000; round++)
        {
            int length = random.Next(0, buffer.Length + 1);
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (char)random.Next(0, 0x10000);
            }

            string? message = PlanLabelRenderer.ValidateField(new string(buffer, 0, length), "x");
            if (message != null)
            {
                Assert.StartsWith("Le champ « x »", message);
            }
        }
    }

    // ---------------------------------------------------------------- Render : arguments refusés avant tout dessin

    [Fact]
    public void Render_RejectsEmptyType_WithTheValidationMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanLabelRenderer.Render("  ", "22062", new AppSettings(), 244, 157, RectangleF.Empty));

        Assert.Equal(PlanLabelRenderer.ValidateField("  ", "Type de châssis"), ex.Message);
        Assert.Contains("Type de châssis", ex.Message);
    }

    [Fact]
    public void Render_RejectsEmptyOrTabbedPlan_WithTheValidationMessage()
    {
        var empty = Assert.Throws<ArgumentException>(
            () => PlanLabelRenderer.Render("HB1", "", new AppSettings(), 244, 157, RectangleF.Empty));
        var tabbed = Assert.Throws<ArgumentException>(
            () => PlanLabelRenderer.Render("HB1", "22\t062", new AppSettings(), 244, 157, RectangleF.Empty));

        Assert.Contains("N° de plan", empty.Message);
        Assert.Contains("tabulation", tabbed.Message);
    }

    [Theory]
    [InlineData(0, 157)]
    [InlineData(244, 0)]
    [InlineData(-1, 157)]
    [InlineData(244, -5)]
    [InlineData(int.MaxValue, 157)]
    [InlineData(244, int.MaxValue)]
    public void Render_RejectsAbsurdPageSizes(int widthUnits, int heightUnits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), widthUnits, heightUnits, RectangleF.Empty));
    }

    // ---------------------------------------------------------------- taille de la page en points

    [Theory]
    [InlineData(244, 300, 732)]      // 62 mm
    [InlineData(157, 300, 471)]      // 40 mm : 157,48 centièmes de pouce, arrondis à 157
    [InlineData(244, 203, 495)]      // 495,32
    [InlineData(157, 203, 319)]      // 318,71
    [InlineData(244, 600, 1464)]
    [InlineData(157, 600, 942)]
    [InlineData(354, 300, 1062)]     // formulaire « 62mm » du pilote (90 mm)
    [InlineData(100, 300, 300)]
    [InlineData(1, 72, 1)]           // 0,72 -> 1 : jamais zéro
    [InlineData(1, 100, 1)]
    [InlineData(3, 150, 5)]          // 4,5 -> 5 (arrondi loin de zéro)
    public void PageSizePx_IsUnitsTimesDpiOver100_Rounded(int units, int dpi, int expected)
    {
        Assert.Equal(expected, PlanLabelRenderer.PageSizePx(units, dpi));
    }

    [Fact]
    public void PageSizePx_NeverReturnsLessThanOne_OrOverflows()
    {
        Assert.Equal(1, PlanLabelRenderer.PageSizePx(0, 300));
        Assert.Equal(1, PlanLabelRenderer.PageSizePx(-50, 300));
        Assert.Equal(int.MaxValue, PlanLabelRenderer.PageSizePx(int.MaxValue, 1200));
    }

    [Theory]
    [InlineData(25.4, 300, 300.0)]
    [InlineData(1.9, 300, 22.44094)]
    [InlineData(10.6, 300, 125.19685)]
    [InlineData(32.0, 300, 377.95276)]
    [InlineData(62.0, 300, 732.28346)]
    [InlineData(10.0, 600, 236.22047)]
    [InlineData(0.0, 300, 0.0)]
    public void MmToPx_ConvertsMillimetresAtTheGivenResolution(double mm, int dpi, double expected)
    {
        Assert.Equal(expected, PlanLabelRenderer.MmToPx(mm, dpi), 3);
    }

    // ---------------------------------------------------------------- limites horizontales

    private const double Safety300 = 0.3 * 300 / 25.4;          // 3,5433 points
    private const double Fallback300 = 1.5 * 300 / 25.4;        // 17,7165 points

    [Fact]
    public void ResolveHorizontalLimits_UsesThePrintableAreaMinusTheSafetyMargin()
    {
        (double left, double right) = PlanLabelRenderer.ResolveHorizontalLimits(new RectangleF(6f, 11.67f, 232f, 134f), 732, 300);

        Assert.Equal(18.0 + Safety300, left, 3);        // 6/100 pouce = 18 points
        Assert.Equal(714.0 - Safety300, right, 3);      // (6 + 232)/100 pouce = 714 points
    }

    [Fact]
    public void ResolveHorizontalLimits_ScalesWithResolution()
    {
        (double left, double right) = PlanLabelRenderer.ResolveHorizontalLimits(new RectangleF(6f, 11.67f, 232f, 134f), 1464, 600);

        double safety = 0.3 * 600 / 25.4;
        Assert.Equal(36.0 + safety, left, 3);
        Assert.Equal(1428.0 - safety, right, 3);
    }

    public static TheoryData<float, float, float, float> UnusableAreas => new()
    {
        { 0f, 0f, 0f, 0f },
        { 6f, 11.67f, 0f, 134f },
        { 6f, 11.67f, 232f, 0f },
        { 6f, 11.67f, -232f, 134f },
        { float.NaN, 0f, 232f, 134f },
        { 6f, float.NaN, 232f, 134f },
        { 6f, 0f, float.PositiveInfinity, 134f },
        { 6f, 0f, 232f, float.NaN },
        { 400f, 0f, 20f, 100f },           // entièrement à droite de la page (244)
        { -300f, 0f, 100f, 100f },         // entièrement à gauche
    };

    [Theory]
    [MemberData(nameof(UnusableAreas))]
    public void ResolveHorizontalLimits_FallsBackToFixedMargins_WhenTheAreaIsUnusable(float x, float y, float w, float h)
    {
        (double left, double right) = PlanLabelRenderer.ResolveHorizontalLimits(new RectangleF(x, y, w, h), 732, 300);

        Assert.Equal(Fallback300 + Safety300, left, 3);
        Assert.Equal(732 - Fallback300 - Safety300, right, 3);
    }

    [Fact]
    public void ResolveHorizontalLimits_ClampsAnAreaWiderThanThePage()
    {
        (double left, double right) = PlanLabelRenderer.ResolveHorizontalLimits(new RectangleF(-10f, 0f, 1000f, 100f), 732, 300);

        Assert.Equal(Safety300, left, 3);
        Assert.Equal(732 - Safety300, right, 3);
    }

    [Theory]
    [InlineData(5, 300)]
    [InlineData(20, 300)]
    [InlineData(1, 1200)]
    public void ResolveHorizontalLimits_NeverReturnsRightBelowLeft(int pageWidthPx, int dpi)
    {
        (double left, double right) = PlanLabelRenderer.ResolveHorizontalLimits(RectangleF.Empty, pageWidthPx, dpi);

        Assert.True(right >= left);
    }

    [Theory]
    [InlineData(366.0, 20.0, 712.0, 692.0)]     // symétrique : toute la largeur
    [InlineData(366.0, 20.0, 600.0, 468.0)]     // limité par la droite : 2 x (600 - 366)
    [InlineData(366.0, 300.0, 712.0, 132.0)]    // limité par la gauche : 2 x (366 - 300)
    [InlineData(366.0, 400.0, 712.0, 0.0)]      // centre hors limites
    [InlineData(366.0, 20.0, 300.0, 0.0)]
    public void CenteredCapacity_IsTwiceTheDistanceToTheNearestLimit(double center, double left, double right, double expected)
    {
        Assert.Equal(expected, PlanLabelRenderer.CenteredCapacity(center, left, right), 6);
    }

    [Theory]
    [InlineData(366.0, 21.5, 710.5, 366.0)]      // cas normal : le centre de la page
    [InlineData(366.0, 21.5, 320.5, 171.0)]      // zone imprimable entièrement à gauche du centre : son milieu
    [InlineData(366.0, 400.0, 700.0, 550.0)]     // entièrement à droite
    [InlineData(366.0, 366.0, 700.0, 533.0)]     // centre pile sur la limite : aucune place de chaque côté
    [InlineData(366.0, 20.0, 366.0, 193.0)]
    public void ResolveCenter_IsThePageCentre_UnlessItIsOutsideTheLimits(double pageCenter, double left, double right, double expected)
    {
        Assert.Equal(expected, PlanLabelRenderer.ResolveCenter(pageCenter, left, right), 6);
    }

    // ---------------------------------------------------------------- réduction de la taille

    [Fact]
    public void FitPointSize_KeepsTheRequestedSize_WhenItFits()
    {
        int calls = 0;

        float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 1000, p => { calls++; return 4.0 * p; }, out bool fits);

        Assert.Equal(61f, pt);
        Assert.True(fits);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(244.0, 61.0)]       // 4 x 61 = 244 : tient exactement
    [InlineData(243.9, 60.5)]
    [InlineData(200.0, 50.0)]
    [InlineData(201.0, 50.0)]       // 50,25 n'est pas une taille permise : 50,0
    [InlineData(201.9, 50.0)]
    [InlineData(202.0, 50.5)]
    [InlineData(100.0, 25.0)]
    [InlineData(24.0, 6.0)]         // exactement le plancher
    public void FitPointSize_PicksTheLargestHalfPointStepThatFits(double available, double expected)
    {
        float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, available, p => 4.0 * p, out bool fits);

        Assert.Equal(expected, pt, 4);
        Assert.True(fits);
    }

    [Fact]
    public void FitPointSize_ResultIsMaximal_ForManyAvailableWidths()
    {
        for (double available = 25.0; available <= 244.0; available += 1.37)
        {
            float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, available, p => 4.0 * p, out bool fits);

            Assert.True(fits);
            Assert.True(4.0 * pt <= available, "doit tenir : " + pt + " pour " + available);
            Assert.True(pt >= 61f || 4.0 * (pt + 0.5f) > available, "doit être maximal : " + pt + " pour " + available);
            Assert.Equal(0.0, (pt * 2.0) % 1.0, 6);
        }
    }

    [Fact]
    public void FitPointSize_StopsAtTheFloor_AndSaysItDoesNotFit()
    {
        int calls = 0;

        float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 10.0, p => { calls++; return 4.0 * p; }, out bool fits);

        Assert.Equal(6f, pt);
        Assert.False(fits);
        Assert.Equal(111, calls);       // 61 ; 60,5 ; ... ; 6 : (61 - 6) / 0,5 + 1 essais
    }

    [Fact]
    public void FitPointSize_StartingBelowTheFloor_NeverGoesAboveTheRequestedSize()
    {
        float pt = PlanLabelRenderer.FitPointSize(5f, 6f, 0.5f, 1.0, p => 4.0 * p, out bool fits);

        Assert.Equal(5f, pt);
        Assert.False(fits);
    }

    [Fact]
    public void FitPointSize_WithAStartThatIsNotOnTheGrid_StepsDownFromIt_ThenEndsOnTheFloor()
    {
        // 23,3 -> 22,8 -> ... : la grille part de la taille demandée.
        float pt = PlanLabelRenderer.FitPointSize(23.3f, 6f, 0.5f, 20.0, p => p, out bool fits);

        Assert.True(fits);
        Assert.Equal(19.8f, pt, 3);

        // Plancher hors grille : il est quand même essayé en dernier.
        float last = PlanLabelRenderer.FitPointSize(10.3f, 6f, 0.5f, 6.0, p => p, out bool lastFits);

        Assert.True(lastFits);
        Assert.Equal(6f, last, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void FitPointSize_InvalidStep_UsesHalfAPoint(float step)
    {
        float pt = PlanLabelRenderer.FitPointSize(10f, 6f, step, 8.1, p => p, out bool fits);

        Assert.True(fits);
        Assert.Equal(8.0f, pt, 3);
    }

    [Fact]
    public void FitPointSize_WithANonFiniteMeasure_TerminatesAtTheFloor()
    {
        float nan = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 100.0, _ => double.NaN, out bool nanFits);
        float inf = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 100.0, _ => double.PositiveInfinity, out bool infFits);

        Assert.Equal(6f, nan);
        Assert.False(nanFits);
        Assert.Equal(6f, inf);
        Assert.False(infFits);
    }

    [Fact]
    public void FitPointSize_WithANegativeAvailableWidth_ShrinksToTheFloor()
    {
        float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, -5.0, p => 4.0 * p, out bool fits);

        Assert.Equal(6f, pt);
        Assert.False(fits);
    }

    [Fact]
    public void FitPointSize_NullMeasure_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 100.0, null!, out _));
    }

    [Fact]
    public void FitPointSize_WithAnAffineMeasure_StillPicksTheLargestFittingStep()
    {
        // largeur = 3,5 x pt + 10 (encre = em + une constante) : disponible 150 -> pt <= 40
        float pt = PlanLabelRenderer.FitPointSize(61f, 6f, 0.5f, 150.0, p => 3.5 * p + 10.0, out bool fits);

        Assert.True(fits);
        Assert.Equal(40.0f, pt, 3);
    }
}

/// <summary>
/// Étiquettes « plan de châssis » dessinées par GDI+ : tests réservés à Windows (ignorés ailleurs).
/// Les attentes viennent de la géométrie d'Arial (cap = 1467/2048 em, chiffres = 1466/2048 em, avance des chiffres = 1139/2048 em)
/// et de la capture P-touch : type en Arial 23 pt, bord gauche de l'encre à 1,9 mm, ligne de base à 10,6 mm ; numéro en
/// Arial 61 pt, encre centrée, ligne de base à 32,0 mm. Les positions sont lues sur l'IMAGE (balayage de l'encre), jamais
/// calculées avec les mêmes formules que le code testé.
/// </summary>
public class PlanLabelRendererTests
{
    private const int PageWidthUnits = 244;     // 62 mm
    private const int PageHeightUnits = 157;    // 40 mm
    private static readonly RectangleF Nominal = new(6f, 11.67f, 232f, 134f);   // zone imprimable du QL-800 sur du ruban de 62 mm

    /// <summary>Entre les deux lignes : le type se termine à 10,6 mm (jamais de jambage), le numéro commence vers 16,4 mm.</summary>
    private const double SplitMm = 13.5;

    // ---------------------------------------------------------------- aides

    private static void RequireGdi()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "GDI+ (Windows) requis.");
    }

    private static void RequireArial()
    {
        RequireGdi();
        bool arial;
        try
        {
            using var family = new FontFamily("Arial");
            arial = string.Equals(family.Name, "Arial", StringComparison.OrdinalIgnoreCase) && family.IsStyleAvailable(FontStyle.Regular);
        }
        catch (ArgumentException)
        {
            arial = false;
        }

        Skip.IfNot(arial, "Police Arial absente de ce poste.");
    }

    private readonly record struct Box(int Left, int Top, int Right, int Bottom, int Dpi)
    {
        public double LeftMm => Left * 25.4 / Dpi;

        public double RightMm => Right * 25.4 / Dpi;

        public double TopMm => Top * 25.4 / Dpi;

        public double BottomMm => Bottom * 25.4 / Dpi;

        public double WidthMm => (Right - Left) * 25.4 / Dpi;

        public double HeightMm => (Bottom - Top) * 25.4 / Dpi;

        /// <summary>Centre horizontal en points (bords de points : Right est exclusif).</summary>
        public double CenterPx => (Left + Right) / 2.0;
    }

    /// <summary>Copie de l'image en niveaux de gris (moyenne des trois composantes), ligne par ligne.</summary>
    private static byte[] ToGray(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var gray = new byte[width * height];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    gray[y * width + x] = (byte)((row[3 * x] + row[3 * x + 1] + row[3 * x + 2]) / 3);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return gray;
    }

    private static int MmToRow(double mm, int dpi, int height) => Math.Clamp((int)Math.Round(mm * dpi / 25.4), 0, height);

    /// <summary>Encre (points de luminosité moyenne &lt; 128) dans la bande de lignes [fromMm, toMm).</summary>
    private static Box FindInk(Bitmap bitmap, double fromMm, double toMm)
    {
        int dpi = (int)Math.Round(bitmap.HorizontalResolution);
        byte[] gray = ToGray(bitmap);
        int width = bitmap.Width;
        int y0 = MmToRow(fromMm, dpi, bitmap.Height);
        int y1 = MmToRow(toMm, dpi, bitmap.Height);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = y0; y < y1; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (gray[y * width + x] < 128)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.True(maxX >= 0, "aucune encre entre " + fromMm + " et " + toMm + " mm");
        return new Box(minX, minY, maxX + 1, maxY + 1, dpi);
    }

    private static Box TypeInk(Bitmap bitmap) => FindInk(bitmap, 0, SplitMm);

    private static Box PlanInk(Bitmap bitmap) => FindInk(bitmap, SplitMm, 100000);

    private static Bitmap RenderNominal(string type, string plan, AppSettings? settings = null)
        => PlanLabelRenderer.Render(type, plan, settings ?? new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal);

    private static void AssertNear(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            what + " : attendu " + expected.ToString("0.###", CultureInfo.InvariantCulture) + " ± "
            + tolerance.ToString("0.###", CultureInfo.InvariantCulture) + ", obtenu "
            + actual.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private const double PageWidthMm = 732 * 25.4 / 300;           // 61,98 mm : 244 centièmes de pouce à 300 dpi
    private const double PrintableLeftMm = 6 * 25.4 / 100;          // 1,524 mm : X de la zone imprimable du QL-800
    private const double PrintableRightMm = 238 * 25.4 / 100;       // 60,452 mm : X + largeur
    private const double InkToleranceMm = 0.15;                     // bord lissé : 0,5 point = 0,04 mm ; marge large

    /// <summary>L'encre est à l'intérieur de [gauche + 0,3 mm ; droite - 0,3 mm] (marge de sécurité), à la tolérance de lecture près.</summary>
    private static void AssertInsideLimits(Box ink, double leftMm, double rightMm)
    {
        Assert.True(ink.LeftMm >= leftMm + 0.3 - InkToleranceMm, "bord gauche " + ink.LeftMm + " pour une limite de " + (leftMm + 0.3));
        Assert.True(ink.RightMm <= rightMm - 0.3 + InkToleranceMm, "bord droit " + ink.RightMm + " pour une limite de " + (rightMm - 0.3));
    }

    private static int RunSelfTestLabel(params string[] args)
    {
        Type? program = typeof(AppSettings).Assembly.GetType("BarcodeTray.Program");
        Assert.NotNull(program);
        MethodInfo? method = program!.GetMethod("RunSelfTestLabel", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return (int)method!.Invoke(null, new object[] { args })!;
    }

    // ---------------------------------------------------------------- image : taille, résolution, fond

    [SkippableFact]
    public void Render_ReturnsAWhite24BitPageImageAt300Dpi_WithoutTransparency()
    {
        RequireGdi();

        using Bitmap bitmap = RenderNominal("HB1", "22062");

        Assert.Equal(732, bitmap.Width);
        Assert.Equal(471, bitmap.Height);
        Assert.Equal(PixelFormat.Format24bppRgb, bitmap.PixelFormat);
        Assert.False(Image.IsAlphaPixelFormat(bitmap.PixelFormat));
        Assert.Equal(300.0, bitmap.HorizontalResolution, 0.5);
        Assert.Equal(300.0, bitmap.VerticalResolution, 0.5);
    }

    [SkippableFact]
    public void Render_LeavesTheMarginsAndTheBottomOfThePageWhite()
    {
        RequireGdi();

        using Bitmap bitmap = RenderNominal("HB1", "22062");
        byte[] gray = ToGray(bitmap);

        int white = 0, dark = 0;
        foreach (byte v in gray)
        {
            if (v >= 250)
            {
                white++;
            }
            else if (v < 128)
            {
                dark++;
            }
        }

        Assert.True(dark > 5000, "il doit y avoir de l'encre : " + dark);
        Assert.True(white > gray.Length * 0.7, "le fond doit rester blanc : " + white);

        // Bandes qui ne contiennent aucun texte : haut (avant 4 mm), bas (après 35 mm), gauche (avant 1,5 mm), droite (après 60,5 mm).
        int dpi = 300;
        int topRows = MmToRow(4.0, dpi, bitmap.Height);
        int bottomRows = MmToRow(35.0, dpi, bitmap.Height);
        int leftCols = (int)(1.5 * dpi / 25.4);
        int rightCols = (int)(60.5 * dpi / 25.4);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bool inBlankBand = y < topRows || y >= bottomRows || x < leftCols || x >= rightCols;
                if (inBlankBand)
                {
                    Assert.True(gray[y * bitmap.Width + x] >= 250, "point non blanc en (" + x + ", " + y + ")");
                }
            }
        }
    }

    [SkippableFact]
    public void Render_DrawsOnlyShadesOfGrey()
    {
        RequireGdi();

        using Bitmap bitmap = RenderNominal("HB1", "22062");
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Assert.Equal(row[3 * x], row[3 * x + 1]);
                    Assert.Equal(row[3 * x + 1], row[3 * x + 2]);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [SkippableTheory]
    [InlineData(244, 157, 300, 732, 471)]
    [InlineData(244, 157, 203, 495, 319)]
    [InlineData(244, 157, 600, 1464, 942)]
    [InlineData(244, 354, 300, 732, 1062)]
    [InlineData(500, 120, 300, 1500, 360)]
    public void Render_PageSizeIsUnitsTimesDpiOver100(int widthUnits, int heightUnits, int dpi, int expectedWidth, int expectedHeight)
    {
        RequireGdi();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings { Dpi = dpi }, widthUnits, heightUnits, Nominal);

        Assert.Equal(expectedWidth, bitmap.Width);
        Assert.Equal(expectedHeight, bitmap.Height);
        Assert.Equal(dpi, bitmap.HorizontalResolution, 0.5);
        Assert.Equal(dpi, bitmap.VerticalResolution, 0.5);
    }

    [SkippableFact]
    public void Render_DoesNotModifyTheSettings()
    {
        RequireGdi();
        var settings = new AppSettings { Dpi = 9999, BatchPlanFontPt = 500f, BatchFontName = "  Arial  " };

        using Bitmap bitmap = RenderNominal("HB1", "22062", settings);

        Assert.Equal(9999, settings.Dpi);
        Assert.Equal(500f, settings.BatchPlanFontPt);
        Assert.Equal("  Arial  ", settings.BatchFontName);
        Assert.True(bitmap.Width > 0);
    }

    [SkippableFact]
    public void Render_TrimsBothFields_AndMatchesTheTrimmedRendering()
    {
        RequireGdi();

        using Bitmap padded = RenderNominal("  HB1\u00A0", " 22062\t");
        using Bitmap plain = RenderNominal("HB1", "22062");

        Assert.Equal(ToGray(plain), ToGray(padded));
    }

    [SkippableFact]
    public void Render_IsDeterministic()
    {
        RequireGdi();

        using Bitmap first = RenderNominal("VF2", "22181");
        using Bitmap second = RenderNominal("VF2", "22181");

        Assert.Equal(ToGray(first), ToGray(second));
    }

    // ---------------------------------------------------------------- type : en haut à gauche

    [SkippableTheory]
    [InlineData(203)]
    [InlineData(300)]
    [InlineData(600)]
    public void Type_InkStartsAt1p9mm_WithItsBaselineAt10p6mm_AndCapHeightOfArial23pt(int dpi)
    {
        RequireArial();

        using Bitmap bitmap = RenderNominal("HB1", "22062", new AppSettings { Dpi = dpi });
        Box ink = TypeInk(bitmap);

        AssertNear(1.9, ink.LeftMm, 0.25, "bord gauche de l'encre du type");
        AssertNear(10.6, ink.BottomMm, 0.25, "ligne de base du type (« HB1 » n'a pas de jambage)");
        // Arial : hauteur des capitales 1467/2048 em, em = 23 pt = 8,115 mm -> 5,81 mm (5,8 mm sur la capture P-touch).
        AssertNear(1467.0 / 2048.0 * 23.0 * 25.4 / 72.0, ink.HeightMm, 0.2, "hauteur des capitales");
    }

    [SkippableFact]
    public void Type_IsLeftAlignedOnTheInk_WhateverTheFirstLetter()
    {
        RequireArial();

        // Les approches latérales diffèrent d'une lettre à l'autre : c'est bien l'ENCRE qui est calée à 1,9 mm.
        foreach (string type in new[] { "HB1", "VF1", "AB", "Ij", "O2", "T", "J7", "Wa" })
        {
            using Bitmap bitmap = RenderNominal(type, "22062");
            Box ink = TypeInk(bitmap);

            AssertNear(1.9, ink.LeftMm, 0.25, "bord gauche de l'encre de « " + type + " »");
        }
    }

    [SkippableFact]
    public void TypeBaseline_IsTheSameForDifferentTypes()
    {
        RequireArial();

        var bottoms = new List<int>();
        foreach (string type in new[] { "HB1", "HBA", "HB2", "VF1", "VF2", "H", "B1", "E", "L7" })
        {
            using Bitmap bitmap = RenderNominal(type, "22062");
            bottoms.Add(TypeInk(bitmap).Bottom);
        }

        // Toutes ces chaînes ont le pied plat ou pointu à la ligne de base (jamais de débord) : même ligne, à 1 point près.
        Assert.True(bottoms.Max() - bottoms.Min() <= 1, "lignes de base : " + string.Join(", ", bottoms));
    }

    // ---------------------------------------------------------------- numéro de plan : centré, en dessous

    [SkippableFact]
    public void Plan_InkIsCentredOnThePage_AndFitsThePrintableWidth()
    {
        RequireArial();

        using Bitmap bitmap = RenderNominal("HB1", "22062");
        Box ink = PlanInk(bitmap);

        Assert.True(Math.Abs(ink.CenterPx - bitmap.Width / 2.0) <= 1.0, "centre " + ink.CenterPx + " pour " + bitmap.Width / 2.0);

        // Arial 61 pt : l'avance de 5 chiffres vaut 5 x 1139/2048 em = 59,84 mm ; l'encre en est plus étroite de ~1,5 mm
        // (approches des deux « 2 » extrêmes) : 58,3 mm sur la capture. La zone imprimable offre 58,93 - 0,6 = 58,33 mm.
        Assert.InRange(ink.WidthMm, 56.5, 58.45);
        AssertInsideLimits(ink, PrintableLeftMm, PrintableRightMm);
    }

    [SkippableFact]
    public void Plan_DefaultSizeIsKept_ForTheSampleNumberOfTheScreenshot()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);

        Assert.Equal("Arial", layout.FontName);
        Assert.Equal(23f, layout.TypePt);
        Assert.InRange(layout.PlanPt, 60f, 61f);     // au pire une réduction d'un point si l'encre dépassait de quelques centièmes de mm
        Assert.True(layout.TypeFits);
        Assert.True(layout.PlanFits);
        Assert.False(layout.TypeShrunk);
    }

    [SkippableFact]
    public void Plan_BaselineIs32mm_AndDigitHeightIsThatOfArial61pt()
    {
        RequireArial();

        // « 11111 » : chiffres à pied plat, donc le bas de l'encre EST la ligne de base.
        using Bitmap bitmap = RenderNominal("HB1", "11111");
        Box ink = PlanInk(bitmap);

        AssertNear(32.0, ink.BottomMm, 0.3, "ligne de base du numéro");
        // Arial : chiffres de 1466/2048 em ; em = 61 pt = 21,52 mm -> 15,40 mm.
        AssertNear(1466.0 / 2048.0 * 61.0 * 25.4 / 72.0, ink.HeightMm, 0.25, "hauteur des chiffres");
        Assert.True(Math.Abs(ink.CenterPx - bitmap.Width / 2.0) <= 1.0, "centre " + ink.CenterPx);
    }

    [SkippableFact]
    public void Plan_RoundDigitsOvershootTheBaselineByLessThanAFifthOfAMillimetre()
    {
        RequireArial();

        using Bitmap bitmap = RenderNominal("HB1", "22062");
        Box ink = PlanInk(bitmap);

        // « 0 » et « 6 » débordent d'environ 0,26 mm sous la ligne de base ; le sommet arrondi déborde aussi (~16,4 mm sur la capture).
        Assert.InRange(ink.BottomMm, 31.9, 32.6);
        Assert.InRange(ink.TopMm, 16.0, 16.9);
    }

    [SkippableFact]
    public void Baselines_AreStable_AcrossDifferentDigits()
    {
        RequireArial();

        // Chiffres à pied plat : 1, 2, 4, 7.
        var flat = new List<int>();
        foreach (string plan in new[] { "11111", "22222", "44444", "77777", "12471", "21147" })
        {
            using Bitmap bitmap = RenderNominal("HB1", plan);
            flat.Add(PlanInk(bitmap).Bottom);
        }

        Assert.True(flat.Max() - flat.Min() <= 1, "pieds plats : " + string.Join(", ", flat));
        int baseline = flat[0];

        // Chiffres ronds : jamais au-dessus de la ligne de base, et pas plus de 0,45 mm en dessous.
        foreach (string plan in new[] { "20086", "00000", "56983", "38900", "22062" })
        {
            using Bitmap bitmap = RenderNominal("HB1", plan);
            int bottom = PlanInk(bitmap).Bottom;
            double overshootMm = (bottom - baseline) * 25.4 / 300;

            Assert.True(bottom >= baseline - 1, plan + " : bas " + bottom + " pour " + baseline);
            Assert.True(overshootMm <= 0.45, plan + " : débord " + overshootMm + " mm");
        }
    }

    [SkippableFact]
    public void PlanBaseline_DoesNotMove_WhenTheNumberIsShrunk()
    {
        RequireArial();

        using Bitmap normal = RenderNominal("HB1", "22222");
        using Bitmap shrunk = PlanLabelRenderer.Render("HB1", "2222222", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);

        Assert.True(layout.PlanShrunk);
        Assert.True(Math.Abs(PlanInk(normal).Bottom - PlanInk(shrunk).Bottom) <= 1);
    }

    [SkippableFact]
    public void TypeAndPlan_DoNotTouchEachOther_ForTheRealCharacterSets()
    {
        RequireArial();

        foreach (string type in new[] { "HB1", "HBA", "HB2", "VF1", "VF2" })
        {
            foreach (string plan in new[] { "21969", "21970", "22061", "22104", "22162", "22181" })
            {
                using Bitmap bitmap = RenderNominal(type, plan);
                Box typeInk = TypeInk(bitmap);
                Box planInk = PlanInk(bitmap);

                Assert.True(planInk.TopMm - typeInk.BottomMm > 4.0, type + " " + plan + " : écart " + (planInk.TopMm - typeInk.BottomMm));
            }
        }
    }

    // ---------------------------------------------------------------- réduction pour tenir dans la zone imprimable

    [SkippableFact]
    public void Plan_With7Digits_IsShrunkToTheLargestHalfPointThatFits()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "2222222", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        // 7 chiffres : (7 x 1139/2048 - ~0,07) em doit tenir dans 58,33 mm -> em = 15,3 mm = 43,3 pt.
        Assert.True(layout.PlanShrunk);
        Assert.True(layout.PlanFits);
        Assert.InRange(layout.PlanPt, 42f, 44.5f);
        Assert.Equal(0.0, (layout.PlanPt * 2.0) % 1.0, 6);
        AssertInsideLimits(ink, PrintableLeftMm, PrintableRightMm);
        Assert.InRange(ink.WidthMm, 57.2, 58.45);       // presque toute la largeur disponible (un pas de 0,5 pt = 0,6 mm)
        Assert.True(Math.Abs(ink.CenterPx - bitmap.Width / 2.0) <= 1.0, "centre " + ink.CenterPx);
        AssertNear(32.0, ink.BottomMm, 0.3, "ligne de base du numéro réduit");
    }

    [SkippableFact]
    public void Plan_WithAVeryLongNumber_IsShrunkFurther_AndStillFits()
    {
        RequireArial();

        string plan = "2020Q7E 2206201234";    // 18 caractères
        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", plan, new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        Assert.True(layout.PlanFits);
        Assert.True(layout.PlanPt < 30f);
        AssertInsideLimits(ink, PrintableLeftMm, PrintableRightMm);
    }

    [SkippableFact]
    public void Plan_With30WideCharacters_IsShrunkToTheMinimum_InsteadOfBeingClipped()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", new string('W', 30), new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        // 30 « W » : 28 em de large -> 2,06 mm d'em = 5,8 pt (sous le plancher de 6 pt d'un barème plus timide, d'où le plancher de 4 pt).
        Assert.True(layout.PlanFits, "taille retenue : " + layout.PlanPt);
        Assert.InRange(layout.PlanPt, PlanLabelRenderer.MinFontPt, 6.5f);
        AssertInsideLimits(ink, PrintableLeftMm, PrintableRightMm);
    }

    [SkippableFact]
    public void Type_WithALongName_IsShrunkToFitTheRightLimit_ButStaysAtItsLeftPosition()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("ABCDEFGHIJKLMNOPRSTUVWXYZ", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);
        Box ink = TypeInk(bitmap);

        // 25 capitales : environ 17 em de large -> disponible 58,25 mm -> ~9 pt.
        Assert.True(layout.TypeShrunk);
        Assert.True(layout.TypeFits);
        Assert.InRange(layout.TypePt, 8f, 10.5f);
        AssertNear(1.9, ink.LeftMm, 0.25, "bord gauche de l'encre du type");
        Assert.True(ink.RightMm <= PrintableRightMm - 0.3 + InkToleranceMm, "bord droit " + ink.RightMm);
        AssertNear(10.6, ink.BottomMm, 0.25, "ligne de base du type réduit");
    }

    [SkippableFact]
    public void WideLabel_DoesNotShrinkASevenDigitNumber_AndCentresItOnThePage()
    {
        RequireArial();

        // Page de 127 mm (500 centièmes de pouce) : 7 chiffres (83 mm) y tiennent à 61 pt.
        var printable = new RectangleF(6f, 11.67f, 488f, 134f);
        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "2222222", new AppSettings(), 500, 157, printable, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        Assert.Equal(61f, layout.PlanPt);
        Assert.False(layout.PlanShrunk);
        Assert.Equal(1500, bitmap.Width);
        Assert.True(Math.Abs(ink.CenterPx - 750.0) <= 1.0, "centre " + ink.CenterPx);
        Assert.InRange(ink.WidthMm, 80.0, 84.0);
    }

    [SkippableFact]
    public void PrintableAreaWiderThanThePage_IsClampedToThePage()
    {
        RequireArial();

        var absurd = new RectangleF(-50f, -50f, 5000f, 5000f);
        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "2222222", new AppSettings(), PageWidthUnits, PageHeightUnits, absurd, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        // Largeur disponible = page (61,98 mm) - 0,6 mm : le numéro de 7 chiffres tient à ~45 pt, pas davantage.
        Assert.True(layout.PlanShrunk);
        Assert.InRange(layout.PlanPt, 44f, 47.5f);
        AssertInsideLimits(ink, 0.0, PageWidthMm);
    }

    [SkippableFact]
    public void EmptyPrintableArea_FallsBackTo1p5mmMargins()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "2222222", new AppSettings(), PageWidthUnits, PageHeightUnits, RectangleF.Empty, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        Assert.True(layout.PlanShrunk);
        Assert.InRange(layout.PlanPt, 42f, 45f);
        AssertInsideLimits(ink, 1.5, PageWidthMm - 1.5);
        Assert.True(Math.Abs(ink.CenterPx - bitmap.Width / 2.0) <= 1.0, "centre " + ink.CenterPx);
    }

    [SkippableTheory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFinitePrintableArea_IsTreatedAsEmpty(float bad)
    {
        RequireArial();

        using Bitmap viaEmpty = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, RectangleF.Empty);
        using Bitmap viaBad = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, new RectangleF(bad, 0f, 232f, 134f));

        Assert.Equal(ToGray(viaEmpty), ToGray(viaBad));
    }

    [SkippableFact]
    public void NarrowPrintableArea_ShrinksTheNumberAccordingly()
    {
        RequireArial();

        // Cas du bogue de la première version : le pilote annonce 102/100 pouce (25,9 mm) au lieu de 232.
        var narrow = new RectangleF(6f, 11.67f, 102f, 330f);
        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, narrow, out PlanLabelLayout layout);
        Box ink = PlanInk(bitmap);

        Assert.True(layout.PlanShrunk);
        AssertInsideLimits(ink, PrintableLeftMm, 108 * 25.4 / 100);
        Assert.True(Math.Abs(ink.CenterPx - 171.0) <= 1.0, "centre " + ink.CenterPx);   // milieu de la zone imprimable : la page n'a plus de centre utilisable
    }

    // ---------------------------------------------------------------- réglages de mise en page

    [SkippableFact]
    public void CustomSettings_MoveBothLines_AndResizeTheType()
    {
        RequireArial();
        var settings = new AppSettings
        {
            BatchTypeLeftMm = 5.0f,
            BatchTypeBaselineMm = 12.0f,
            BatchTypeFontPt = 30f,
            BatchPlanBaselineMm = 30.0f,
            BatchPlanFontPt = 40f,
        };

        using Bitmap typeBitmap = RenderNominal("HB1", "11111", settings);
        Box type = TypeInk(typeBitmap);
        Box plan = PlanInk(typeBitmap);

        AssertNear(5.0, type.LeftMm, 0.25, "bord gauche du type");
        AssertNear(12.0, type.BottomMm, 0.25, "ligne de base du type");
        AssertNear(1467.0 / 2048.0 * 30.0 * 25.4 / 72.0, type.HeightMm, 0.2, "hauteur des capitales à 30 pt");
        AssertNear(30.0, plan.BottomMm, 0.3, "ligne de base du numéro");
        AssertNear(1466.0 / 2048.0 * 40.0 * 25.4 / 72.0, plan.HeightMm, 0.25, "hauteur des chiffres à 40 pt");
    }

    [SkippableFact]
    public void TypeLeftBeyondThePrintableLimit_IsPulledInside()
    {
        RequireArial();
        var settings = new AppSettings { BatchTypeLeftMm = 0.0f };

        using Bitmap bitmap = RenderNominal("HB1", "22062", settings);
        Box ink = TypeInk(bitmap);

        // zone imprimable : 6/100 pouce = 1,52 mm, + 0,3 mm de sécurité
        Assert.True(ink.LeftMm >= PrintableLeftMm + 0.3 - InkToleranceMm, "bord gauche " + ink.LeftMm);
    }

    [SkippableFact]
    public void MissingFont_FallsBackToADefaultFont_WithTheSameBaselines()
    {
        RequireGdi();
        var settings = new AppSettings { BatchFontName = "Police Inexistante 12345" };

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "11111", settings, PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);

        Assert.False(string.IsNullOrWhiteSpace(layout.FontName));
        Assert.NotEqual("Police Inexistante 12345", layout.FontName);
        Box type = TypeInk(bitmap);
        Box plan = PlanInk(bitmap);
        AssertNear(1.9, type.LeftMm, 0.25, "bord gauche du type");
        AssertNear(10.6, type.BottomMm, 0.25, "ligne de base du type");
        AssertNear(32.0, plan.BottomMm, 0.3, "ligne de base du numéro");
        Assert.True(Math.Abs(plan.CenterPx - bitmap.Width / 2.0) <= 1.0);
    }

    [SkippableFact]
    public void Layout_ReportsTheInkAndTheLimitsOfTheDrawing()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);

        Assert.Equal(732, layout.WidthPx);
        Assert.Equal(471, layout.HeightPx);
        Assert.Equal(300, layout.Dpi);
        Assert.Equal(18.0 + 0.3 * 300 / 25.4, layout.LeftLimitPx, 3);
        Assert.Equal(714.0 - 0.3 * 300 / 25.4, layout.RightLimitPx, 3);

        // L'encre annoncée est celle qu'on retrouve sur l'image (bords lissés : 3 points de tolérance).
        Box type = TypeInk(bitmap);
        Box plan = PlanInk(bitmap);
        Assert.True(Math.Abs(layout.TypeInkPx.Left - type.Left) <= 3, layout.TypeInkPx + " / " + type);
        Assert.True(Math.Abs(layout.TypeInkPx.Right - type.Right) <= 3, layout.TypeInkPx + " / " + type);
        Assert.True(Math.Abs(layout.TypeInkPx.Top - type.Top) <= 3, layout.TypeInkPx + " / " + type);
        Assert.True(Math.Abs(layout.TypeInkPx.Bottom - type.Bottom) <= 3, layout.TypeInkPx + " / " + type);
        Assert.True(Math.Abs(layout.PlanInkPx.Left - plan.Left) <= 3, layout.PlanInkPx + " / " + plan);
        Assert.True(Math.Abs(layout.PlanInkPx.Right - plan.Right) <= 3, layout.PlanInkPx + " / " + plan);
        Assert.True(Math.Abs(layout.PlanInkPx.Top - plan.Top) <= 3, layout.PlanInkPx + " / " + plan);
        Assert.True(Math.Abs(layout.PlanInkPx.Bottom - plan.Bottom) <= 3, layout.PlanInkPx + " / " + plan);
    }

    // ---------------------------------------------------------------- contrôle de l'image (auto-test)

    [SkippableFact]
    public void FindInkBounds_FindsTheDarkPixels_AndReportsEmptyForABlankImage()
    {
        RequireGdi();

        using var blank = new Bitmap(40, 30, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(blank))
        {
            g.Clear(Color.White);
        }

        Assert.True(PlanLabelRenderer.FindInkBounds(blank).IsEmpty);

        using (Graphics g = Graphics.FromImage(blank))
        using (var black = new SolidBrush(Color.Black))
        using (var grey = new SolidBrush(Color.FromArgb(200, 200, 200)))
        {
            g.FillRectangle(black, 5, 7, 10, 4);       // x 5..14, y 7..10
            g.FillRectangle(grey, 30, 20, 5, 5);       // trop clair : ignoré
        }

        Assert.Equal(new Rectangle(5, 7, 10, 4), PlanLabelRenderer.FindInkBounds(blank));
    }

    [SkippableFact]
    public void SelfTestLabel_WritesTheSamplePng()
    {
        RequireGdi();
        string directory = Path.Combine(Path.GetTempPath(), "BarcodeTray-selftest-label-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(directory, "sous-dossier", "etiquette.png");

            int code = RunSelfTestLabel("--selftest-label", file);

            Assert.Equal(0, code);
            byte[] png = File.ReadAllBytes(file);
            Assert.True(png.Length > 100);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
            using var stream = new MemoryStream(png);
            using var image = new Bitmap(stream);
            Assert.Equal(732, image.Width);
            Assert.Equal(471, image.Height);
            Assert.False(PlanLabelRenderer.FindInkBounds(image).IsEmpty);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- contrôle de la hauteur de page (BatchLogic.CheckTextFitsPageHeight)

    [SkippableFact]
    public void TextFitCheck_AcceptsTheDefaultLabel_OnTheMeasuredInkOfTheRenderer()
    {
        RequireArial();

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, PageHeightUnits, Nominal, out PlanLabelLayout layout);

        Assert.Null(BatchLogic.CheckTextFitsPageHeight(layout.TypeInkPx, layout.PlanInkPx, layout.Dpi, PageHeightUnits, Nominal));
    }

    [SkippableFact]
    public void TextFitCheck_RefusesAPageOf29Mm_WhereTheNumberWouldBeCutOff()
    {
        RequireArial();
        // PaperName = « 62mm x 29mm » : le pilote impose 114 / 100 pouce de long, la zone imprimable s'arrête à ~26 mm.
        const int shortPage = 114;
        var printable = new RectangleF(6f, 11.67f, 232f, 90.33f);

        using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", new AppSettings(), PageWidthUnits, shortPage, printable, out PlanLabelLayout layout);
        string? message = BatchLogic.CheckTextFitsPageHeight(layout.TypeInkPx, layout.PlanInkPx, layout.Dpi, shortPage, printable);

        Assert.NotNull(message);
        Assert.Contains("le bas du texte serait coupé", message);
        Assert.Contains("jusqu'à 32", message); // l'encre du numéro descend jusqu'à la ligne de base (32 mm), un peu en dessous pour les chiffres ronds
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void SelfTestLabel_WithoutAnOutputPath_ReturnsExitCode2(int argumentCount)
    {
        RequireGdi();
        string[] args = argumentCount == 0 ? new[] { "--selftest-label" } : new[] { "--selftest-label", "   " };

        Assert.Equal(2, RunSelfTestLabel(args));
    }

    [SkippableFact]
    public void SelfTestLabel_WithAnUnwritableOutputPath_ReturnsExitCode2()
    {
        RequireGdi();
        string blocker = Path.Combine(Path.GetTempPath(), "BarcodeTray-selftest-label-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "ce fichier empêche de créer un dossier de ce nom");
        try
        {
            // Le « dossier » parent est en réalité un fichier : la création du dossier échoue.
            Assert.Equal(2, RunSelfTestLabel("--selftest-label", Path.Combine(blocker, "etiquette.png")));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
