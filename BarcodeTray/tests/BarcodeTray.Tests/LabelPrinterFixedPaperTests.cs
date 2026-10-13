using System;
using System.Collections.Generic;
using System.Drawing;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Impression des étiquettes en série (62 x 40 mm, longueur imposée) : décisions et géométrie PURES, sans imprimante.
/// Les valeurs viennent du relevé réel d'une Brother QL-800 (DIAG) : formulaire « 62mm » id 259, zone imprimable
/// (6, 11,67, 232 x ...) en centièmes de pouce, soit environ 3 mm non imprimables en haut et en bas.
/// IMPORTANT : on n'imprime JAMAIS vers une vraie imprimante ici.
/// </summary>
public class LabelPrinterFixedPaperTests
{
    private const int Tape62 = 244;    // 62 mm en centièmes de pouce
    private const int Len40 = 157;     // 40 mm = 39,9 mm en centièmes de pouce
    private const float TopMargin = 11.67f;
    private const float BottomMargin = 12f;
    private const string NoSuchPrinter = "Imprimante-qui-n-existe-pas-0123456789";

    // Extrait fidèle du diagnostic réel (nom, largeur, hauteur, identifiant dmPaperSize).
    private static List<PaperInfo> RealQl800Forms() => new()
    {
        new("17mm x 54mm", 67, 212, 269),
        new("29mm x 90mm", 114, 354, 271),
        new("62mm x 29mm", 244, 114, 274),
        new("62mm x 100mm", 244, 393, 275),
        new("29mm", 114, 354, 258),
        new("62mm", 244, 354, 259),
        new("62mm x 2", 476, 354, 279),
        new("Taille définie par l'utilisateur", 114, 354, 256),
    };

    private static PaperChoice Choose(string? paperName = null) => LabelPrinter.ChoosePaper(RealQl800Forms(), 62, paperName);

    private static IReadOnlyList<PaperCandidate> Candidates(PaperChoice choice, int height = Len40) =>
        LabelPrinter.BuildPaperCandidates(choice, RealQl800Forms(), Tape62, height);

    /// <summary>Zone imprimable d'un pilote qui honore la page demandée : marges haute 11,67 et basse 12.</summary>
    private static RectangleF Honoured(int pageHeight) => new(6f, TopMargin, 232f, pageHeight - TopMargin - BottomMargin);

    /// <summary>Zone annoncée quand le pilote a repris sa page par défaut 29 mm x 90 mm (le bogue de la première version).</summary>
    private static RectangleF Default29mm() => new(6f, TopMargin, 102f, 330.33f);

    private static List<PaperProbe> Run(IReadOnlyList<PaperCandidate> candidates, Func<PaperCandidate, int, RectangleF> driver, List<(int Raw, int Height)>? calls = null)
    {
        return LabelPrinter.ProbeFixedCandidates(candidates, c => h =>
        {
            calls?.Add((c.RawKind, h));
            return driver(c, h);
        }, Tape62);
    }

    // ---------------------------------------------------------------- ComputeFullPageDestination

    [Fact]
    public void Destination_RealQl800_PutsTheImageOnThePaperCorner_AtItsPhysicalSize()
    {
        // 62 x 40 mm à 300 dpi = 732 x 471 points ; zone imprimable (6, 11,67) : 18 et 35 points du coin du papier.
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, Honoured(Len40), 300f, 300f);

        Assert.Equal(-6f, dest.X, 3);
        Assert.Equal(-35f / 3f, dest.Y, 3);   // 35 points = 11,667 centièmes de pouce (11,67 arrondi au point)
        Assert.Equal(244f, dest.Width, 3);
        Assert.Equal(157f, dest.Height, 3);
    }

    [Fact]
    public void Destination_OffsetsAreWholeDevicePrinterDots()
    {
        foreach (float dpi in new[] { 203f, 300f, 600f, 720f })
        {
            for (float off = 0f; off <= 20f; off += 0.37f)
            {
                var area = new RectangleF(off, off * 1.9f, 232f, 100f);

                RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, area, dpi, dpi);

                double dotsX = dest.X * dpi / 100.0;
                double dotsY = dest.Y * dpi / 100.0;
                Assert.True(Math.Abs(dotsX - Math.Round(dotsX)) < 1e-3, $"X {dest.X} pas sur un point à {dpi} dpi");
                Assert.True(Math.Abs(dotsY - Math.Round(dotsY)) < 1e-3, $"Y {dest.Y} pas sur un point à {dpi} dpi");
                // Le coin de l'image tombe sur le coin du papier à un demi-point près (aucune dérive).
                Assert.True(Math.Abs(dest.X + area.X) <= 0.5 * 100.0 / dpi + 1e-3, $"dérive X {dest.X + area.X} à {dpi} dpi");
                Assert.True(Math.Abs(dest.Y + area.Y) <= 0.5 * 100.0 / dpi + 1e-3, $"dérive Y {dest.Y + area.Y} à {dpi} dpi");
            }
        }
    }

    [Fact]
    public void Destination_SizeIsAlwaysThePhysicalImageSize_NeverScaled()
    {
        // Image bien plus grande que la zone imprimable (232 x 134) : elle garde sa taille, le pilote rogne.
        RectangleF big = LabelPrinter.ComputeFullPageDestination(1500, 2400, 300f, 300f, Honoured(Len40), 300f, 300f);
        Assert.Equal(500f, big.Width, 2);
        Assert.Equal(800f, big.Height, 2);

        // Plus petite que la zone : elle n'est pas agrandie ni centrée (calée sur le coin du papier).
        RectangleF small = LabelPrinter.ComputeFullPageDestination(300, 150, 300f, 300f, Honoured(Len40), 300f, 300f);
        Assert.Equal(100f, small.Width, 2);
        Assert.Equal(50f, small.Height, 2);
        Assert.Equal(-6f, small.X, 3);

        // La résolution de l'image fixe sa taille physique : 600 dpi = moitié moins de centièmes de pouce.
        RectangleF dpi600 = LabelPrinter.ComputeFullPageDestination(732, 471, 600f, 600f, Honoured(Len40), 600f, 600f);
        Assert.Equal(122f, dpi600.Width, 2);
        Assert.Equal(78.5f, dpi600.Height, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-300f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Destination_UnusableImageDpi_FallsBackTo300(float imageDpi)
    {
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, imageDpi, imageDpi, Honoured(Len40), 300f, 300f);

        Assert.Equal(244f, dest.Width, 2);
        Assert.Equal(157f, dest.Height, 2);
    }

    [Fact]
    public void Destination_UnreadablePrintableArea_DoesNotShiftTheImage()
    {
        foreach (RectangleF area in new[]
                 {
                     RectangleF.Empty,
                     new RectangleF(float.NaN, float.NaN, 232f, 100f),
                     new RectangleF(float.PositiveInfinity, float.NegativeInfinity, 232f, 100f),
                     new RectangleF(-5f, -7f, 232f, 100f),   // jamais négatif en vrai : traité comme 0
                 })
        {
            RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, area, 300f, 300f);

            Assert.Equal(0f, dest.X);
            Assert.Equal(0f, dest.Y);
            Assert.Equal(244f, dest.Width, 2);
            Assert.Equal(157f, dest.Height, 2);
        }
    }

    [Fact]
    public void Destination_ZeroOffset_IsPositiveZero()
    {
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, new RectangleF(0f, 0f, 244f, 157f), 300f, 300f);

        Assert.Equal(0, BitConverter.SingleToInt32Bits(dest.X));   // pas de -0
        Assert.Equal(0, BitConverter.SingleToInt32Bits(dest.Y));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void Destination_UnknownDeviceResolution_KeepsExactOffsets(float deviceDpi)
    {
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, Honoured(Len40), deviceDpi, deviceDpi);

        Assert.Equal(-6f, dest.X, 4);
        Assert.Equal(-TopMargin, dest.Y, 4);
        Assert.Equal(244f, dest.Width, 4);
        Assert.Equal(157f, dest.Height, 4);
    }

    [Fact]
    public void Destination_300x600Printer_AlignsEachAxisOnItsOwnDots()
    {
        // QL-800 en mode 300 x 600 : 11,67 centièmes de pouce = 70,02 points verticaux de 1/600 pouce -> 70.
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(732, 471, 300f, 300f, Honoured(Len40), 300f, 600f);

        Assert.Equal(-6f, dest.X, 3);
        Assert.Equal(-70f / 6f, dest.Y, 3);
        Assert.Equal(244f, dest.Width, 3);
        Assert.Equal(157f, dest.Height, 3);
    }

    [Fact]
    public void Destination_NeverCollapsesToZeroSize()
    {
        RectangleF dest = LabelPrinter.ComputeFullPageDestination(0, 0, 300f, 300f, Honoured(Len40), 300f, 300f);

        Assert.True(dest.Width > 0 && dest.Height > 0);
    }

    // ---------------------------------------------------------------- longueur demandée

    [Theory]
    [InlineData(40.0, 157)]
    [InlineData(62.0, 244)]
    [InlineData(10.0, 39)]
    [InlineData(29.0, 114)]
    [InlineData(90.0, 354)]
    [InlineData(1016.0, 4000)]   // maximum des QL
    public void FixedPageHeight_ConvertsMillimetersToHundredthsOfAnInch(double mm, int expected)
    {
        Assert.True(LabelPrinter.TryGetFixedPageHeight(mm, out int units, out string? error));
        Assert.Equal(expected, units);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    [InlineData(9.9)]
    [InlineData(1016.5)]
    [InlineData(1e9)]
    public void FixedPageHeight_AbsurdLengths_AreRefusedInFrench(double mm)
    {
        Assert.False(LabelPrinter.TryGetFixedPageHeight(mm, out int units, out string? error));
        Assert.Equal(0, units);
        Assert.NotNull(error);
        Assert.Contains("BatchLabelLengthMm", error, StringComparison.Ordinal);
        Assert.Contains("mm", error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- longueur respectée par le pilote

    [Theory]
    [InlineData(6f, 11.67f, 232f, 133.33f, 157, true)]    // QL-800 réel : bas à 145 (marge basse 12)
    [InlineData(6f, 11.67f, 232f, 145.33f, 157, true)]    // pilote sans marge basse : bas à 157
    [InlineData(6f, 0f, 232f, 159f, 157, true)]           // exactement la tolérance (+2)
    [InlineData(6f, 0f, 232f, 159.5f, 157, false)]        // juste au-delà
    [InlineData(6f, 11.67f, 232f, 330.33f, 157, false)]   // le pilote impose sa page de 90 mm
    [InlineData(6f, 0f, 232f, 127f, 157, true)]           // 30 de marge basse : limite
    [InlineData(6f, 0f, 232f, 126.5f, 157, false)]        // page raccourcie par le pilote
    [InlineData(6f, 11.67f, 232f, 90.33f, 157, false)]    // le pilote a repris le format 62 x 29 mm (114) : bas rogné
    [InlineData(6f, 11.67f, 232f, 50f, 157, false)]
    public void IsLengthHonored_ComparesTheBottomOfThePrintableAreaWithThePageLength(
        float x, float y, float w, float h, int pageHeight, bool expected)
    {
        Assert.Equal(expected, LabelPrinter.IsLengthHonored(new RectangleF(x, y, w, h), pageHeight));
    }

    [Fact]
    public void IsLengthHonored_UnreadableArea_CannotBeJudged_SoItIsNotAccused()
    {
        Assert.True(LabelPrinter.IsLengthHonored(RectangleF.Empty, Len40));
        Assert.True(LabelPrinter.IsLengthHonored(new RectangleF(float.NaN, 0f, 232f, 100f), Len40));
        Assert.Equal(LabelPrinter.FixedLengthCheck.Unknown, LabelPrinter.CheckFixedLength(RectangleF.Empty, Len40));
    }

    [Fact]
    public void CheckFixedLength_DistinguishesTooLongFromTooShort()
    {
        Assert.Equal(LabelPrinter.FixedLengthCheck.Honored, LabelPrinter.CheckFixedLength(Honoured(Len40), Len40));
        Assert.Equal(LabelPrinter.FixedLengthCheck.TooLong, LabelPrinter.CheckFixedLength(Honoured(354), Len40));
        Assert.Equal(LabelPrinter.FixedLengthCheck.TooShort, LabelPrinter.CheckFixedLength(Honoured(114), Len40));
    }

    // ---------------------------------------------------------------- essai des candidats (faux pilote)

    [Fact]
    public void Probe_DriverThatHonoursTheLength_AcceptsTheFirstCandidateWithoutGrowingThePage()
    {
        var calls = new List<(int Raw, int Height)>();

        List<PaperProbe> probes = Run(Candidates(Choose()), (_, h) => Honoured(h), calls);

        Assert.All(probes, p => Assert.Equal(ProbeVerdict.Ok, p.Verdict));
        // Une seule mesure par candidat : la page n'est JAMAIS rallongée (la longueur est imposée).
        Assert.Equal(probes.Count, calls.Count);
        Assert.Equal((259, Len40), calls[0]);
        Assert.Equal(Len40, probes[0].PageHeightUnits);
        Assert.Equal(Len40, probes[1].PageHeightUnits);
    }

    [Fact]
    public void Probe_AShortPrintableArea_IsStillAccepted_BecauseTheLengthIsImposed()
    {
        // 20 centièmes de pouce de zone imprimable : une étiquette normale serait refusée « hauteur insuffisante »,
        // ici seule la largeur compte (sinon la page serait rallongée et la longueur demandée perdue).
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, _) => new RectangleF(6f, 11.67f, 232f, 20f));

        Assert.All(probes, p => Assert.Equal(ProbeVerdict.Ok, p.Verdict));
        Assert.Equal(Len40, probes[0].PageHeightUnits);
    }

    [Fact]
    public void Probe_DriverThatIgnoresTheFormId_IsRejectedOnWidth()
    {
        // Le bogue réel de la première version : id 259 / 256 ignorés, page 29 x 90 reprise.
        List<PaperProbe> probes = Run(Candidates(Choose()), (c, h) => c.RawKind == 0 ? Honoured(h) : Default29mm());

        Assert.Equal(ProbeVerdict.WidthRejected, probes[0].Verdict);
        Assert.Equal(ProbeVerdict.WidthRejected, probes[1].Verdict);
        Assert.Equal(ProbeVerdict.WidthRejected, probes[2].Verdict);
        Assert.Equal(ProbeVerdict.Ok, probes[3].Verdict);   // l'ancienne méthode sans identifiant
    }

    // ---------------------------------------------------------------- décision du papier

    [Fact]
    public void Decide_RealQl800_PicksTheDriverForm_WithTheRequestedLength()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, h) => Honoured(h));

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, formImposed: false)!;

        Assert.NotNull(paper);
        Assert.Equal(259, paper.Candidate.RawKind);
        Assert.Equal("62mm", paper.Candidate.Name);
        Assert.Equal(Tape62, paper.PageWidthUnits);
        Assert.Equal(Len40, paper.PageHeightUnits);
        Assert.True(paper.LengthHonored);
        Assert.Equal(6f, paper.PrintableArea.X);
        Assert.Equal(TopMargin, paper.PrintableArea.Y);
        Assert.Contains("« 62mm »", paper.Description, StringComparison.Ordinal);
        Assert.Contains("id 259", paper.Description, StringComparison.Ordinal);
        Assert.Contains("62 x 40 mm", paper.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("ATTENTION", paper.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_TheChosenPaperSizeCarriesTheDriverFormId_SoTheDriverDoesNotIgnoreIt()
    {
        // Leçon du diagnostic réel : un PaperSize sans identifiant de formulaire (RawKind) est IGNORÉ par le pilote Brother.
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, h) => Honoured(h));
        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        System.Drawing.Printing.PaperSize size = LabelPrinter.CreatePaperSize(paper.Candidate, paper.PageHeightUnits);

        Assert.Equal(259, size.RawKind);
        Assert.Equal(Tape62, size.Width);
        Assert.Equal(Len40, size.Height);
    }

    [Fact]
    public void Decide_PrefersTheCandidateThatHonoursTheLength_OverOneWhereTheDriverImposesItsOwn()
    {
        // Le formulaire « 62mm » impose ses 90 mm quoi qu'on demande ; le formulaire « utilisateur » respecte les 40 mm.
        List<PaperProbe> probes = Run(Candidates(Choose()), (c, h) => c.RawKind == 259 ? Honoured(354) : Honoured(h));

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        Assert.Equal(LabelPrinter.DmPaperUser, paper.Candidate.RawKind);
        Assert.Equal(Len40, paper.PageHeightUnits);
        Assert.True(paper.LengthHonored);
    }

    [Fact]
    public void Decide_WhenEverythingImposesTheDriversLength_KeepsTheFirstCandidate_AndWarnsClearly()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, _) => Honoured(354));

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        Assert.Equal(259, paper.Candidate.RawKind);          // égalité : le premier
        Assert.Equal(Len40, paper.PageHeightUnits);          // on demande toujours 40 mm
        Assert.False(paper.LengthHonored);
        Assert.Contains("ATTENTION", paper.Description, StringComparison.Ordinal);
        Assert.Contains("90 mm", paper.Description, StringComparison.Ordinal);   // longueur imposée estimée
        Assert.Contains("40 mm", paper.Description, StringComparison.Ordinal);   // longueur demandée
        Assert.Contains("plus longues", paper.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_WhenOnlyTheDriversOwnFormIsAccepted_UsesItsLength_AndSaysSo()
    {
        // Ni le formulaire à longueur demandée, ni le formulaire utilisateur, ni la méthode sans identifiant ne passent.
        List<PaperProbe> probes = Run(Candidates(Choose()), (c, h) => c.FixedLength && c.RawKind == 259 ? Honoured(c.HeightHundredthsInch) : Default29mm());

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        Assert.Equal(259, paper.Candidate.RawKind);
        Assert.True(paper.Candidate.FixedLength);
        Assert.Equal(354, paper.PageHeightUnits);     // les étiquettes sont dessinées sur la page de 90 mm réellement utilisée
        Assert.False(paper.LengthHonored);
        Assert.Contains("90 mm", paper.Description, StringComparison.Ordinal);
        Assert.Contains("40 mm", paper.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_NothingAccepted_ReturnsNull()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, _) => Default29mm());

        Assert.Null(LabelPrinter.DecideFixedPaper(probes, Len40, false));
        Assert.Null(LabelPrinter.DecideFixedPaper(new List<PaperProbe>(), Len40, false));
        // Le message d'échec existant sert aussi aux étiquettes en série.
        Assert.Contains("Rien n'a été envoyé", LabelPrinter.NoUsablePaperMessage("QL-800", probes, 62), StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_UnreadablePrintableArea_StillWorks_WithoutGrowingThePage_AndSaysSo()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, _) => RectangleF.Empty);

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        Assert.NotNull(paper);
        Assert.Equal(Len40, paper.PageHeightUnits);   // pas de marge forfaitaire ajoutée : la longueur est imposée
        Assert.True(paper.LengthHonored);             // rien ne prouve le contraire
        Assert.Contains("illisible", paper.Description, StringComparison.Ordinal);
        Assert.False(LabelPrinter.IsUsableArea(paper.PrintableArea));
    }

    [Fact]
    public void Decide_ADriverThatThrowsOnTheFirstCandidate_FallsBackToTheNext()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (c, h) =>
            c.RawKind == 259 && !c.FixedLength ? throw new InvalidOperationException("pilote muet") : Honoured(h));

        Assert.Equal(ProbeVerdict.Error, probes[0].Verdict);
        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;
        Assert.Equal(LabelPrinter.DmPaperUser, paper.Candidate.RawKind);
        Assert.True(paper.LengthHonored);
    }

    [Fact]
    public void Decide_PrintableAreaCutShort_IsFlagged_BecauseTheBottomOfTheLabelWouldBeClipped()
    {
        var probes = new List<PaperProbe>
        {
            new(Candidates(Choose())[0], Len40, new RectangleF(6f, TopMargin, 232f, 50f), ProbeVerdict.Ok),
        };

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false)!;

        Assert.False(paper.LengthHonored);
        Assert.Contains("rogné", paper.Description, StringComparison.Ordinal);
        Assert.Contains("ATTENTION", paper.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_ImposedForm_UsesTheDriversOwnLength_AndIgnoresBatchLabelLengthMm()
    {
        PaperChoice choice = Choose("62mm x 100mm");
        IReadOnlyList<PaperCandidate> candidates = Candidates(choice);
        PaperCandidate only = Assert.Single(candidates);
        Assert.Equal(393, only.HeightHundredthsInch);
        List<PaperProbe> probes = Run(candidates, (_, h) => Honoured(h));

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, formImposed: true)!;

        Assert.Equal(275, paper.Candidate.RawKind);
        Assert.Equal(393, paper.PageHeightUnits);
        Assert.True(paper.LengthHonored);   // la longueur attendue est celle du formulaire, pas celle demandée
        Assert.Contains("PaperName", paper.Description, StringComparison.Ordinal);
        Assert.Contains("BatchLabelLengthMm", paper.Description, StringComparison.Ordinal);
        Assert.Contains("100 mm", paper.Description, StringComparison.Ordinal);

        // Sans l'indication « imposé », la même mesure passerait pour une longueur imposée par le pilote.
        Assert.False(LabelPrinter.DecideFixedPaper(probes, Len40, formImposed: false)!.LengthHonored);
    }

    [Fact]
    public void Decide_ImposedNarrowForm_IsJudgedOnItsOwnWidth()
    {
        // PaperName = « 29mm x 90mm » sur un rouleau de 29 mm : zone 102 x 330 normale, pas un refus de largeur.
        PaperChoice choice = Choose("29mm x 90mm");
        IReadOnlyList<PaperCandidate> candidates = Candidates(choice);
        int reference = LabelPrinter.ReferenceTapeUnits(choice, 62);
        List<PaperProbe> probes = LabelPrinter.ProbeFixedCandidates(candidates, cand => height => Default29mm(), reference);

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, formImposed: true)!;

        Assert.NotNull(paper);
        Assert.Equal(354, paper.PageHeightUnits);
        Assert.Equal(114, paper.PageWidthUnits);
        Assert.True(paper.LengthHonored);
    }

    [Fact]
    public void Decide_ExtraNote_IsAppendedToTheDescription()
    {
        List<PaperProbe> probes = Run(Candidates(Choose()), (_, h) => Honoured(h));

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(probes, Len40, false, "Vérification désactivée.")!;

        Assert.EndsWith("Vérification désactivée.", paper.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_WithoutVerification_FirstCandidateIsAppliedAsIs_EvenWhenWidthLooksWrong()
    {
        // CheckPaper = false : un seul « essai » fabriqué sans jugement, zone relue au mieux.
        PaperCandidate first = Candidates(Choose())[0];
        var probe = new PaperProbe(first, Len40, Default29mm(), ProbeVerdict.Ok);

        LabelPrinter.FixedPaper paper = LabelPrinter.DecideFixedPaper(new[] { probe }, Len40, false, "Sans contrôle.")!;

        Assert.Equal(259, paper.Candidate.RawKind);
        Assert.Equal(Len40, paper.PageHeightUnits);
        Assert.False(paper.LengthHonored);   // zone de 330 : l'alerte reste visible même sans vérification
    }

    // ---------------------------------------------------------------- messages et erreurs (sans imprimante)

    [Fact]
    public void ToFriendlyException_Win32Error_NamesThePrinter_AndKeepsTheInnerException()
    {
        var original = new System.ComponentModel.Win32Exception(5, "Accès refusé");

        InvalidOperationException ex = LabelPrinter.ToFriendlyException(original, "Brother QL-800", "Étiquette en série");

        Assert.Same(original, ex.InnerException);
        Assert.Contains("Windows a signalé", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Brother QL-800", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToFriendlyException_UnexpectedError_IsExplainedInFrench_WithoutAStackTrace()
    {
        var original = new ArgumentException("boum");

        InvalidOperationException ex = LabelPrinter.ToFriendlyException(original, "QL-800", "Étiquette en série");

        Assert.Same(original, ex.InnerException);
        Assert.Contains("boum", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Copier le diagnostic d'impression", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareFixedPaper_WithoutPrinter_FailsInFrench()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrepareFixedPaper("  ", new AppSettings(), 40));

        Assert.Equal("Aucune imprimante sélectionnée.", ex.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(5000.0)]
    public void PrepareFixedPaper_AbsurdLength_FailsBeforeTouchingTheDriver(double lengthMm)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrepareFixedPaper(NoSuchPrinter, new AppSettings(), lengthMm));

        Assert.Contains("BatchLabelLengthMm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareFixedPaper_MissingPrinter_FailsWithAFriendlyInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrepareFixedPaper(NoSuchPrinter, new AppSettings(), 40));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("   at ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareFixedPaper_AcceptsNullSettings()
    {
        // Ne lève pas NullReferenceException : les réglages absents valent les valeurs par défaut.
        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrepareFixedPaper(NoSuchPrinter, null!, 40));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void PrintFullPage_WithoutImage_ThrowsArgumentNull()
    {
        var paper = new LabelPrinter.FixedPaper(Candidates(Choose())[0], Tape62, Len40, Honoured(Len40), true, "test");

        Assert.Throws<ArgumentNullException>(() => LabelPrinter.PrintFullPage(null!, NoSuchPrinter, new AppSettings(), paper));
    }

    [Fact]
    public void PrintFullPage_ToAMissingPrinter_FailsWithAFriendlyInvalidOperationException_AndPrintsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // GDI+ (Bitmap) n'existe que sous Windows
        }

        var paper = new LabelPrinter.FixedPaper(Candidates(Choose())[0], Tape62, Len40, Honoured(Len40), true, "test");
        using var bitmap = new Bitmap(732, 471);
        bitmap.SetResolution(300f, 300f);

        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrintFullPage(bitmap, NoSuchPrinter, new AppSettings(), paper));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("   at ", ex.Message, StringComparison.Ordinal);

        var ex2 = Assert.Throws<InvalidOperationException>(() => LabelPrinter.PrintFullPage(bitmap, "", new AppSettings(), paper));
        Assert.Equal("Aucune imprimante sélectionnée.", ex2.Message);

        Assert.Throws<ArgumentNullException>(() => LabelPrinter.PrintFullPage(bitmap, NoSuchPrinter, new AppSettings(), null!));
    }

    [Fact]
    public void FixedPaper_KeepsEverythingItWasGiven()
    {
        PaperCandidate c = Candidates(Choose())[0];
        RectangleF area = Honoured(Len40);

        var paper = new LabelPrinter.FixedPaper(c, Tape62, Len40, area, false, "texte");

        Assert.Equal(c, paper.Candidate);
        Assert.Equal(Tape62, paper.PageWidthUnits);
        Assert.Equal(Len40, paper.PageHeightUnits);
        Assert.Equal(area, paper.PrintableArea);
        Assert.False(paper.LengthHonored);
        Assert.Equal("texte", paper.Description);
    }
}
