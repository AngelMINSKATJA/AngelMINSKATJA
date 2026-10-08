using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Régression du premier essai réel sur une Brother QL-800 : le format « 62mm » était envoyé sans identifiant de
/// formulaire (dmPaperSize = 0), le pilote l'ignorait, gardait son format par défaut 29 mm x 90 mm (zone imprimable
/// 102 x 330 /100 pouce) et refusait d'imprimer sur un rouleau de 62 mm. Ces tests utilisent la liste de formats
/// RÉELLE relevée par « Copier le diagnostic d'impression » sur ce poste.
/// </summary>
public class LabelPrinterPaperProbeTests
{
    private const int Tape62 = 244; // 62 mm en centièmes de pouce

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
        new("Etiquette pour petite adresse", 244, 114, 319),
        new("Taille définie par l'utilisateur", 114, 354, 256),
    };

    private static PaperChoice Choose(string? paperName = null) => LabelPrinter.ChoosePaper(RealQl800Forms(), 62, paperName);

    // ---------------------------------------------------------------- identifiant de formulaire conservé

    [Fact]
    public void ChoosePaper_KeepsTheDriverFormId_Of62mmContinuous()
    {
        PaperChoice choice = Choose();

        Assert.Equal("62mm", choice.Name);
        Assert.Equal(259, choice.RawKind);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Fact]
    public void ChoosePaper_Override_KeepsTheDriverFormId()
    {
        PaperChoice choice = Choose("62mm x 29mm");

        Assert.True(choice.UseDriverSizeAsIs);
        Assert.Equal(274, choice.RawKind);
    }

    [Fact]
    public void ChoosePaper_WithoutADriverEntry_HasNoFormId()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(new List<PaperInfo> { new("A4", 827, 1169, 9) }, 62, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(0, choice.RawKind);
    }

    // ---------------------------------------------------------------- candidats

    [Fact]
    public void FirstCandidate_IsTheDriverForm62mm_WithTheRequestedLength()
    {
        IReadOnlyList<PaperCandidate> list = LabelPrinter.BuildPaperCandidates(Choose(), RealQl800Forms(), Tape62, 100);

        PaperCandidate first = list[0];
        Assert.Equal("62mm", first.Name);
        Assert.Equal(259, first.RawKind);
        Assert.Equal(Tape62, first.WidthHundredthsInch);
        Assert.Equal(100, first.HeightHundredthsInch);
        Assert.False(first.FixedLength);
    }

    [Fact]
    public void Candidates_ThenUserDefined_ThenDriverLength_ThenLegacy_InThatOrder()
    {
        IReadOnlyList<PaperCandidate> list = LabelPrinter.BuildPaperCandidates(Choose(), RealQl800Forms(), Tape62, 100);

        Assert.Equal(4, list.Count);

        Assert.Equal(LabelPrinter.DmPaperUser, list[1].RawKind);
        Assert.Equal("Taille définie par l'utilisateur", list[1].Name); // nom réel du formulaire 256 dans le pilote
        Assert.Equal(Tape62, list[1].WidthHundredthsInch);
        Assert.Equal(100, list[1].HeightHundredthsInch);

        Assert.Equal(259, list[2].RawKind);
        Assert.Equal(354, list[2].HeightHundredthsInch); // longueur nominale du formulaire
        Assert.True(list[2].FixedLength);

        Assert.Equal(0, list[3].RawKind); // ancienne méthode, en dernier recours (imprimantes virtuelles)
    }

    [Fact]
    public void Candidates_WithoutADriverForm_SkipTheFormCandidates()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(new List<PaperInfo> { new("A4", 827, 1169, 9) }, 62, null);

        IReadOnlyList<PaperCandidate> list = LabelPrinter.BuildPaperCandidates(choice, new List<PaperInfo>(), Tape62, 100);

        Assert.Equal(2, list.Count);
        Assert.Equal(LabelPrinter.DmPaperUser, list[0].RawKind);
        Assert.Equal("Custom", list[0].Name);
        Assert.Equal(0, list[1].RawKind);
    }

    [Fact]
    public void Candidates_ForAnImposedDieCutLabel_AreASingleFixedLengthDriverForm()
    {
        IReadOnlyList<PaperCandidate> list = LabelPrinter.BuildPaperCandidates(Choose("62mm x 100mm"), RealQl800Forms(), Tape62, 100);

        PaperCandidate only = Assert.Single(list);
        Assert.Equal(275, only.RawKind);
        Assert.Equal(393, only.HeightHundredthsInch); // longueur du pilote, pas celle de l'image
        Assert.True(only.FixedLength);
    }

    [Fact]
    public void CreatePaperSize_SetsTheFormId_AfterConstruction()
    {
        PaperCandidate c = LabelPrinter.BuildPaperCandidates(Choose(), RealQl800Forms(), Tape62, 100)[0];

        PaperSize size = LabelPrinter.CreatePaperSize(c, 123);

        Assert.Equal(259, size.RawKind); // c'est ce qui devient dmPaperSize dans le DEVMODE
        Assert.Equal("62mm", size.PaperName);
        Assert.Equal(Tape62, size.Width);
        Assert.Equal(123, size.Height);
        Assert.Equal(PaperKind.Custom, size.Kind);
    }

    [Fact]
    public void CreatePaperSize_LegacyCandidate_HasNoFormId()
    {
        var legacy = new PaperCandidate("ancien", "Custom", 0, Tape62, 100, false);

        Assert.Equal(0, LabelPrinter.CreatePaperSize(legacy, 100).RawKind);
    }

    // ---------------------------------------------------------------- jugement de la zone imprimable

    [Fact]
    public void Classify_TheRealDefault29mmPrintableArea_IsRejectedFor62mmTape()
    {
        // Mesuré sur le poste : le pilote répondait ceci alors qu'on lui demandait 62 mm.
        var area = new RectangleF(6f, 11.67f, 102f, 330.33f);

        Assert.Equal(ProbeVerdict.WidthRejected, LabelPrinter.ClassifyProbe(area, Tape62, 75.7));
    }

    [Fact]
    public void Classify_A62mmPrintableArea_IsOk_WhenTallEnough()
    {
        var area = new RectangleF(6f, 11.67f, 232f, 80f);

        Assert.Equal(ProbeVerdict.Ok, LabelPrinter.ClassifyProbe(area, Tape62, 75.7));
    }

    [Fact]
    public void Classify_TooShortPrintableHeight_IsHeightShort()
    {
        var area = new RectangleF(6f, 11.67f, 232f, 52.33f);

        Assert.Equal(ProbeVerdict.HeightShort, LabelPrinter.ClassifyProbe(area, Tape62, 75.7));
    }

    [Fact]
    public void Classify_EmptyArea_IsUnreadable()
    {
        Assert.Equal(ProbeVerdict.Unreadable, LabelPrinter.ClassifyProbe(RectangleF.Empty, Tape62, 75.7));
    }

    [Fact]
    public void Classify_NonFiniteArea_IsUnreadable_NeverOk()
    {
        // NaN / infini ne doivent jamais passer pour une zone valide (les comparaisons avec NaN sont toujours fausses).
        Assert.Equal(ProbeVerdict.Unreadable, LabelPrinter.ClassifyProbe(new RectangleF(0f, 0f, float.NaN, 80f), Tape62, 75.7));
        Assert.Equal(ProbeVerdict.Unreadable, LabelPrinter.ClassifyProbe(new RectangleF(0f, 0f, 232f, float.NaN), Tape62, 75.7));
        Assert.Equal(ProbeVerdict.Unreadable, LabelPrinter.ClassifyProbe(new RectangleF(0f, 0f, float.PositiveInfinity, 80f), Tape62, 75.7));
        Assert.Equal(ProbeVerdict.Unreadable, LabelPrinter.ClassifyProbe(new RectangleF(0f, 0f, 232f, float.PositiveInfinity), Tape62, 75.7));
    }

    // ---------------------------------------------------------------- marges non imprimables du pilote

    [Fact]
    public void NextPageHeight_AddsTheDriversUnprintableMargins()
    {
        // Page de 76 -> zone imprimable de 52,33 : le pilote retire 23,67 (environ 3 mm en haut et en bas).
        var area = new RectangleF(6f, 11.67f, 232f, 52.33f);

        int next = LabelPrinter.NextPageHeight(76, area, 75.7, 59);

        Assert.Equal(101, next); // ceil(75,7 + 23,67) + 1

        // Propriété réelle : un pilote qui retire toujours 23,67 donne, pour la page rallongée, une zone qui contient l'image.
        var grown = new RectangleF(6f, 11.67f, 232f, next - 23.67f);
        Assert.True(grown.Height >= 75.7f);
        Assert.Equal(ProbeVerdict.Ok, LabelPrinter.ClassifyProbe(grown, Tape62, 75.7));
    }

    [Fact]
    public void NextPageHeight_NeverShrinks_AndRespectsTheMinimum()
    {
        var plenty = new RectangleF(0f, 0f, 232f, 400f);

        Assert.Equal(300, LabelPrinter.NextPageHeight(300, plenty, 75.7, 59));
        Assert.Equal(300, LabelPrinter.NextPageHeight(100, new RectangleF(0f, 0f, 232f, 0.1f), 75.7, 300));
    }

    // ---------------------------------------------------------------- sélection

    private static PaperProbe Probe(string label, int raw, float areaHeight, ProbeVerdict verdict, int pageHeight = 100)
    {
        var c = new PaperCandidate(label, "62mm", raw, Tape62, pageHeight, false);
        return new PaperProbe(c, pageHeight, new RectangleF(6f, 11.67f, 232f, areaHeight), verdict);
    }

    [Fact]
    public void Select_PrefersTheFirstAcceptedProbe_OnATie()
    {
        var probes = new List<PaperProbe>
        {
            Probe("A", 259, 76.3f, ProbeVerdict.Ok),
            Probe("B", 256, 76.3f, ProbeVerdict.Ok),
        };

        Assert.Equal("A", LabelPrinter.SelectProbe(probes)!.Value.Candidate.Label);
    }

    [Fact]
    public void Select_PrefersTheShortestLabel_WhenTheDriverImposesALength()
    {
        // A est accepté mais le pilote impose 90 mm de long ; B respecte la longueur demandée : on évite le gaspillage.
        var probes = new List<PaperProbe>
        {
            Probe("A", 259, 330.33f, ProbeVerdict.Ok, 354),
            Probe("B", 256, 76.3f, ProbeVerdict.Ok),
        };

        Assert.Equal("B", LabelPrinter.SelectProbe(probes)!.Value.Candidate.Label);
    }

    [Fact]
    public void Select_IgnoresRejectedProbes()
    {
        var probes = new List<PaperProbe>
        {
            Probe("A", 0, 330.33f, ProbeVerdict.WidthRejected),
            Probe("B", 256, 20f, ProbeVerdict.HeightShort),
            Probe("C", 259, 76.3f, ProbeVerdict.Ok),
        };

        Assert.Equal("C", LabelPrinter.SelectProbe(probes)!.Value.Candidate.Label);
    }

    [Fact]
    public void Select_FallsBackToAnUnreadableArea_ButNeverToARejectedOne()
    {
        var unreadable = new List<PaperProbe>
        {
            Probe("A", 0, 0f, ProbeVerdict.WidthRejected),
            Probe("B", 259, 0f, ProbeVerdict.Unreadable),
        };
        Assert.Equal("B", LabelPrinter.SelectProbe(unreadable)!.Value.Candidate.Label);

        var allRejected = new List<PaperProbe>
        {
            Probe("A", 0, 330.33f, ProbeVerdict.WidthRejected),
            Probe("B", 256, 20f, ProbeVerdict.Error),
        };
        Assert.Null(LabelPrinter.SelectProbe(allRejected));
    }

    [Fact]
    public void NoUsablePaperMessage_NamesThePrinter_TheMeasuredWidth_AndTheEscapeHatch()
    {
        var probes = new List<PaperProbe> { Probe("A", 0, 330.33f, ProbeVerdict.WidthRejected) };
        // largeur mesurée 232 ; on veut le message : « 59 mm » (232 /100 pouce)
        string message = LabelPrinter.NoUsablePaperMessage("Brother QL-800 (Copie 1)", probes, 62);

        Assert.Contains("Brother QL-800 (Copie 1)", message);
        Assert.Contains("62 mm", message);
        Assert.Contains("59 mm", message);
        Assert.Contains("Rien n'a été envoyé", message);
        Assert.Contains("Copier le diagnostic", message);
        Assert.Contains("CheckPaper", message);
        Assert.DoesNotContain("trop haute", message);
        Assert.DoesNotContain("plus courte que l'étiquette", message);
    }

    [Fact]
    public void NoUsablePaperMessage_WhenOnlyTheHeightIsShort_BlamesTheHeight_NotTheFormat()
    {
        // Étiquette prédécoupée imposée (PaperName) plus courte que l'image : la largeur est bonne (232 = 59 mm).
        var probes = new List<PaperProbe> { Probe("A", 274, 66.33f, ProbeVerdict.HeightShort, 90) };

        string message = LabelPrinter.NoUsablePaperMessage("QL-800", probes, 62);

        Assert.Contains("hauteur imprimable (17 mm)", message);
        Assert.Contains("plus courte que l'étiquette", message);
        Assert.Contains("PaperName", message);
        Assert.Contains("BarHeightPx", message);
        Assert.Contains("Rien n'a été envoyé", message);
        Assert.Contains("Copier le diagnostic", message);
        Assert.DoesNotContain("n'a pas pris en compte", message);
        Assert.DoesNotContain("CheckPaper", message); // sans effet sur une hauteur insuffisante
    }

    [Fact]
    public void NoUsablePaperMessage_HeightShortAlongsideRejectedWidths_StillBlamesTheHeight()
    {
        // Cas courant sur rouleau continu : un essai a pris la largeur en compte mais reste trop court, les autres sont refusés.
        var probes = new List<PaperProbe>
        {
            Probe("A", 259, 50f, ProbeVerdict.HeightShort),
            Probe("B", 256, 330.33f, ProbeVerdict.WidthRejected),
            Probe("C", 0, 330.33f, ProbeVerdict.WidthRejected),
        };

        string message = LabelPrinter.NoUsablePaperMessage("QL-800", probes, 62);

        Assert.Contains("plus courte que l'étiquette", message);
        Assert.Contains("(13 mm)", message); // 50 /100 pouce, pas les 330 des essais refusés
    }

    [Fact]
    public void NoUsablePaperMessage_WithoutAnyReadableArea_OmitsTheMeasuredWidth()
    {
        var probes = new List<PaperProbe>
        {
            new(new PaperCandidate("A", "62mm", 259, Tape62, 100, false), 100, RectangleF.Empty, ProbeVerdict.Error, "Boom"),
        };

        string message = LabelPrinter.NoUsablePaperMessage("QL-800", probes, 62);

        Assert.DoesNotContain("zone imprimable", message);
        Assert.Contains("n'a pas pris en compte le format de 62 mm", message);
    }

    [Fact]
    public void NoUsablePaperMessage_FitsTheStatusLine_WhateverTheVerdict_AndThePrinterName()
    {
        // MainForm affiche « Échec de l'impression : » + message et coupe à 400 caractères (MaxStatusLength) :
        // la dernière phrase (échappatoire) ne doit jamais être rognée, même avec un nom de file démesuré.
        const string Prefix = "Échec de l'impression : ";
        const int Max = 400;

        var wide = new List<PaperProbe> { Probe("A", 259, 330.33f, ProbeVerdict.WidthRejected) };
        var tall = new List<PaperProbe> { Probe("A", 259, 66.33f, ProbeVerdict.HeightShort) };
        var none = new List<PaperProbe> { new(new PaperCandidate("A", "62mm", 259, Tape62, 100, false), 100, RectangleF.Empty, ProbeVerdict.Error, "Boom") };

        foreach (string name in new[] { "Brother QL-800 (Copie 1)", new string('X', 200) })
        {
            foreach (List<PaperProbe> probes in new[] { wide, tall, none })
            {
                string message = LabelPrinter.NoUsablePaperMessage(name, probes, 62);

                Assert.True((Prefix + message).Length <= Max, "message trop long : " + (Prefix + message).Length);
                Assert.EndsWith(".", message);
            }
        }

        // Le nom trop long est abrégé, pas supprimé.
        Assert.Contains(new string('X', 39) + "…", LabelPrinter.NoUsablePaperMessage(new string('X', 200), wide, 62));
    }

    [Fact]
    public void DescribeProbe_MentionsFormId_Area_AndVerdict()
    {
        string line = LabelPrinter.DescribeProbe(Probe("A", 259, 330.33f, ProbeVerdict.WidthRejected));

        Assert.Contains("id 259", line);
        Assert.Contains("REFUSÉ", line);
        Assert.Contains("232", line);
    }

    // ---------------------------------------------------------------- boucle d'essai avec un faux pilote

    private const double NaturalW = 224.0; // 672 px à 300 dpi, en centièmes de pouce
    private const double NaturalH = 75.67; // 227 px à 300 dpi
    private const int Min = 59;            // 15 mm

    private static PaperCandidate Continuous(int height = 76, bool fixedLength = false) =>
        new("A", "62mm", 259, Tape62, height, fixedLength);

    /// <summary>Faux pilote qui honore la longueur demandée mais retire toujours des marges non imprimables.</summary>
    private static Func<int, RectangleF> HonouringDriver(List<int> calls, double margins = 23.5) => h =>
    {
        calls.Add(h);
        return new RectangleF(6f, 11.67f, 232f, (float)(h - margins));
    };

    [Fact]
    public void ProbeCandidate_GrowsThePage_UntilTheDriversPrintableAreaContainsTheImage()
    {
        var calls = new List<int>();

        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(), HonouringDriver(calls), Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Ok, probe.Verdict);
        Assert.Equal(101, probe.PageHeightUnits);           // 76 + 23,5 de marges (+ 1)
        Assert.Equal(new List<int> { 76, 101 }, calls);     // un essai trop court, puis la page rallongée
        Assert.True(probe.Area.Height >= NaturalH);         // la zone imprimable obtenue contient l'image
    }

    [Fact]
    public void ProbeCandidate_AlreadyBigEnough_IsAcceptedWithoutGrowing()
    {
        var calls = new List<int>();

        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(120), HonouringDriver(calls), Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Ok, probe.Verdict);
        Assert.Equal(120, probe.PageHeightUnits);
        Assert.Single(calls);
    }

    [Fact]
    public void ProbeCandidate_DriverIgnoringTheLength_StopsAfterThreeAttempts_AndIsNeverSelected()
    {
        int calls = 0;
        Func<int, RectangleF> ignoring = _ =>
        {
            calls++;
            return new RectangleF(6f, 11.67f, 232f, 52.33f);
        };

        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(), ignoring, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.HeightShort, probe.Verdict);
        Assert.Equal(3, calls);
        Assert.Null(LabelPrinter.SelectProbe(new List<PaperProbe> { probe }));
    }

    [Fact]
    public void ProbeCandidate_FixedLength_IsMeasuredOnce_AndNeverGrown()
    {
        var calls = new List<int>();

        PaperProbe probe = LabelPrinter.ProbeCandidate(
            Continuous(90, fixedLength: true), HonouringDriver(calls), Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.HeightShort, probe.Verdict); // 90 - 23,5 = 66,5 < 75,67
        Assert.Equal(90, probe.PageHeightUnits);
        Assert.Single(calls);
    }

    [Fact]
    public void ProbeCandidate_NeverAsksForMoreThanTheMaximumLength()
    {
        var calls = new List<int>();

        // Image de 3990 : la page rallongée (3990 + 23,5 + 1) dépasserait 4000, on s'arrête sans la demander.
        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(3990), HonouringDriver(calls), Tape62, NaturalW, 3990.0, Min);

        Assert.Equal(ProbeVerdict.HeightShort, probe.Verdict);
        Assert.Equal(3990, probe.PageHeightUnits);
        Assert.Single(calls);
    }

    [Fact]
    public void ProbeCandidate_WidthRejected_IsReportedAsIs_WithoutGrowing()
    {
        var calls = new List<int>();
        Func<int, RectangleF> default29 = h =>
        {
            calls.Add(h);
            return new RectangleF(6f, 11.67f, 102f, 330.33f); // le pilote a repris son format 29 mm x 90 mm
        };

        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(), default29, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.WidthRejected, probe.Verdict);
        Assert.Equal(76, probe.PageHeightUnits);
        Assert.Single(calls);
    }

    [Fact]
    public void ProbeCandidate_UnreadableArea_GetsAFlatMarginAllowance()
    {
        // Sans zone lisible, on ne connaît pas les marges du pilote : la page doit quand même les absorber (>= 101).
        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(), _ => RectangleF.Empty, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Unreadable, probe.Verdict);
        Assert.True(probe.PageHeightUnits >= 101, "page " + probe.PageHeightUnits);
        Assert.Equal(76 + LabelPrinter.BlindMarginAllowanceUnits, probe.PageHeightUnits);

        // C'est bien cette hauteur qui est retenue par SelectProbe pour l'impression.
        Assert.Equal(probe.PageHeightUnits, LabelPrinter.SelectProbe(new List<PaperProbe> { probe })!.Value.PageHeightUnits);
    }

    [Fact]
    public void ProbeCandidate_UnreadableFixedLength_KeepsTheDriversLength()
    {
        PaperProbe probe = LabelPrinter.ProbeCandidate(
            Continuous(354, fixedLength: true), _ => RectangleF.Empty, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Unreadable, probe.Verdict);
        Assert.Equal(354, probe.PageHeightUnits);
    }

    [Fact]
    public void ProbeCandidate_NaNArea_IsUnreadable_NotAccepted()
    {
        var nan = new RectangleF(float.NaN, float.NaN, float.NaN, float.NaN);

        PaperProbe probe = LabelPrinter.ProbeCandidate(Continuous(), _ => nan, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Unreadable, probe.Verdict);
    }

    [Fact]
    public void ProbeCandidate_DriverException_BecomesAnErrorVerdict_WithTheDetail()
    {
        PaperProbe probe = LabelPrinter.ProbeCandidate(
            Continuous(), _ => throw new InvalidOperationException("pilote muet"), Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Error, probe.Verdict);
        Assert.Contains("InvalidOperationException", probe.Detail);
        Assert.Contains("pilote muet", probe.Detail);
        Assert.Null(LabelPrinter.SelectProbe(new List<PaperProbe> { probe }));
    }

    // ---------------------------------------------------------------- CheckPaper = false : la page absorbe quand même les marges

    [Fact]
    public void GrowPage_WithoutVerification_AddsTheDriversMargins_SoTheTextIsNotClipped()
    {
        var calls = new List<int>();

        int page = LabelPrinter.GrowPage(Continuous(), HonouringDriver(calls), NaturalW, NaturalH, Min);

        Assert.True(page >= 101, "page " + page); // 76 seul laissait 52,5 de zone imprimable : texte rogné, sans erreur
        Assert.Equal(101, page);
    }

    [Fact]
    public void GrowPage_UnreadableArea_UsesTheFlatAllowance()
    {
        int page = LabelPrinter.GrowPage(Continuous(), _ => RectangleF.Empty, NaturalW, NaturalH, Min);

        Assert.True(page >= 101, "page " + page);
    }

    [Fact]
    public void GrowPage_FixedLengthForm_IsLeftUntouched_WithoutAskingTheDriver()
    {
        int calls = 0;

        int page = LabelPrinter.GrowPage(Continuous(114, fixedLength: true), _ => { calls++; return RectangleF.Empty; }, NaturalW, NaturalH, Min);

        Assert.Equal(114, page);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void GrowPage_DriverThatAlreadyGivesEnough_KeepsTheRequestedLength()
    {
        // Pilote qui a repris sa page par défaut (zone 102 x 330) : rien à rallonger, la page reste celle demandée.
        int page = LabelPrinter.GrowPage(Continuous(), _ => new RectangleF(6f, 11.67f, 102f, 330.33f), NaturalW, NaturalH, Min);

        Assert.Equal(76, page);
    }

    [Fact]
    public void BlindPageHeight_IsImagePlusAllowance_WithinMinAndMax()
    {
        Assert.Equal(101, LabelPrinter.BlindPageHeight(75.67, Min));
        Assert.Equal(300, LabelPrinter.BlindPageHeight(10.0, 300));       // longueur minimale
        Assert.Equal(4000, LabelPrinter.BlindPageHeight(3990.0, Min));    // longueur maximale du QL
    }

    // ---------------------------------------------------------------- format imposé (PaperName) : largeur de référence du format

    [Fact]
    public void ReferenceTapeUnits_UsesTheImposedFormsOwnWidth_ElseTheTapeWidth()
    {
        Assert.Equal(114, LabelPrinter.ReferenceTapeUnits(Choose("29mm x 90mm"), 62));
        Assert.Equal(244, LabelPrinter.ReferenceTapeUnits(Choose("62mm x 29mm"), 62));
        Assert.Equal(Tape62, LabelPrinter.ReferenceTapeUnits(Choose(), 62));      // automatique : LabelWidthMm
        Assert.Equal(114, LabelPrinter.ReferenceTapeUnits(Choose(), 29));         // automatique : LabelWidthMm = 29
        Assert.Equal(Tape62, LabelPrinter.ReferenceTapeUnits(
            new PaperChoice("X", 0, true, "largeur inconnue", 300), 62));          // largeur du formulaire inconnue
    }

    [Fact]
    public void ImposedNarrowForm_IsAccepted_WhenTheDriverGivesItsOwnPrintableWidth()
    {
        // Rouleau de 29 mm, PaperName = « 29mm x 90mm », LabelWidthMm resté à 62 : zone mesurée 102 x 330 (diagnostic réel).
        PaperChoice choice = Choose("29mm x 90mm");
        PaperCandidate only = Assert.Single(LabelPrinter.BuildPaperCandidates(choice, RealQl800Forms(), Tape62, 76));
        Func<int, RectangleF> driver = _ => new RectangleF(6f, 11.67f, 102f, 330.33f);

        int reference = LabelPrinter.ReferenceTapeUnits(choice, 62);
        PaperProbe ok = LabelPrinter.ProbeCandidate(only, driver, reference, NaturalW, NaturalH, Min);
        PaperProbe oldBehaviour = LabelPrinter.ProbeCandidate(only, driver, Tape62, NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.Ok, ok.Verdict);
        Assert.Equal(ProbeVerdict.WidthRejected, oldBehaviour.Verdict); // ce que donnait la largeur LabelWidthMm
    }

    [Fact]
    public void ImposedWideForm_IsStillRejected_WhenTheDriverFallsBackToItsNarrowDefault()
    {
        PaperChoice choice = Choose("62mm x 29mm");
        PaperCandidate only = Assert.Single(LabelPrinter.BuildPaperCandidates(choice, RealQl800Forms(), Tape62, 76));

        PaperProbe probe = LabelPrinter.ProbeCandidate(
            only, _ => new RectangleF(6f, 11.67f, 102f, 330.33f), LabelPrinter.ReferenceTapeUnits(choice, 62), NaturalW, NaturalH, Min);

        Assert.Equal(ProbeVerdict.WidthRejected, probe.Verdict);
    }

    [Fact]
    public void TapeWidthMm_RoundTripsThroughHundredthsOfAnInch()
    {
        // PrintCore redérive les mm du message depuis la largeur de référence en centièmes de pouce.
        for (int mm = 10; mm <= 300; mm++)
        {
            Assert.Equal(mm, (int)Math.Round(LabelPrinter.UnitsToMm(LabelPrinter.MmToUnits(mm))));
        }
    }
}
