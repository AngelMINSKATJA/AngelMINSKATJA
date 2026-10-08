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

    // ---------------------------------------------------------------- marges non imprimables du pilote

    [Fact]
    public void NextPageHeight_AddsTheDriversUnprintableMargins()
    {
        // Page de 76 -> zone imprimable de 52,33 : le pilote retire 23,67 (environ 3 mm en haut et en bas).
        var area = new RectangleF(6f, 11.67f, 232f, 52.33f);

        int next = LabelPrinter.NextPageHeight(76, area, 75.7, 59);

        Assert.Equal(101, next); // ceil(75,7 + 23,67) + 1
        // Avec cette hauteur, la zone imprimable (101 - 23,67 = 77,33) contient l'image.
        Assert.True(101 - 23.67 >= 75.7);
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
        Assert.Contains("CheckPaper", message);
    }

    [Fact]
    public void DescribeProbe_MentionsFormId_Area_AndVerdict()
    {
        string line = LabelPrinter.DescribeProbe(Probe("A", 259, 330.33f, ProbeVerdict.WidthRejected));

        Assert.Contains("id 259", line);
        Assert.Contains("REFUSÉ", line);
        Assert.Contains("232", line);
    }
}
