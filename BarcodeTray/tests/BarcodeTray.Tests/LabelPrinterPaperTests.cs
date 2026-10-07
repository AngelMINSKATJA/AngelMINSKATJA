using System;
using System.Collections.Generic;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>Choix du format de papier parmi ceux du pilote Brother QL-800 (logique pure, sans imprimante).</summary>
public class LabelPrinterPaperTests
{
    // Quelques entrées typiques du pilote QL-800 (largeur et hauteur en centièmes de pouce ;
    // 62 mm = 244, 29 mm = 114, 38 mm = 150, 12 mm = 47, 102 mm = 402).
    private static readonly PaperInfo Continuous62 = new("62mm", 244, 0);
    private static readonly PaperInfo Label62x29 = new("62mm x 29mm", 244, 114);
    private static readonly PaperInfo Label62x100 = new("62mm x 100mm", 244, 394);
    private static readonly PaperInfo RedBlack62 = new("62mm(Red/Black)", 244, 0);
    private static readonly PaperInfo Continuous29 = new("29mm", 114, 0);
    private static readonly PaperInfo Continuous38 = new("38mm", 150, 0);
    private static readonly PaperInfo Continuous12 = new("12mm", 47, 0);
    private static readonly PaperInfo A4 = new("A4", 827, 1169);

    private static List<PaperInfo> DriverList() => new()
    {
        Continuous29, Continuous38, Label62x29, RedBlack62, Label62x100, Continuous62, Continuous12, A4,
    };

    // ---------------------------------------------------------------- rouleau continu 62 mm

    [Fact]
    public void Picks62mmContinuous_AmongSimilarlyNamedEntries()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, null);

        Assert.Equal("62mm", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
        Assert.False(choice.UseDriverSizeAsIs);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Fact]
    public void ContinuousChoice_DoesNotDependOnTheOrderOfTheList()
    {
        var shuffles = new List<List<PaperInfo>>
        {
            new() { Continuous62, Label62x29, RedBlack62, Continuous29 },
            new() { Label62x29, RedBlack62, Continuous29, Continuous62 },
            new() { RedBlack62, Label62x29, Continuous62 },
            new() { Label62x100, Label62x29, RedBlack62, A4, Continuous62 },
        };

        foreach (List<PaperInfo> list in shuffles)
        {
            PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

            Assert.Equal("62mm", choice.Name);
            Assert.False(choice.UseDriverSizeAsIs);
        }
    }

    [Theory]
    [InlineData("62mm")]
    [InlineData("62MM")]
    [InlineData("62 mm")]
    [InlineData("62 Mm")]
    [InlineData(" 62mm ")]
    [InlineData("62mm ")]
    public void ContinuousName_IsMatchedIgnoringCaseAndSpaces(string driverName)
    {
        var list = new List<PaperInfo> { Label62x29, RedBlack62, new(driverName, 244, 0), Continuous29 };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        Assert.Equal(driverName, choice.Name);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Fact]
    public void ExactContinuousName_BeatsOtherNamesStartingWith62mm()
    {
        var list = new List<PaperInfo> { new("62mm continu", 244, 0), Continuous62 };

        Assert.Equal("62mm", LabelPrinter.ChoosePaper(list, 62, null).Name);
    }

    [Fact]
    public void NameStartingWith62mm_WithoutXOrParenthesis_IsPreferredOverDieCutLabels()
    {
        var list = new List<PaperInfo> { Label62x29, RedBlack62, new("62mm continu", 244, 0), Continuous29 };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        Assert.Equal("62mm continu", choice.Name);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Fact]
    public void DieCutAndRedBlackEntries_AreNeverPreferredWhenAContinuousEntryExists()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(
            new List<PaperInfo> { Label62x29, Label62x100, RedBlack62, Continuous62 }, 62, null);

        Assert.DoesNotContain("x", choice.Name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("(", choice.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrower29mmTape_PicksThe29mmEntry()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 29, null);

        Assert.Equal("29mm", choice.Name);
        Assert.Equal(114, choice.WidthHundredthsInch);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    // ---------------------------------------------------------------- tolérance sur la largeur

    [Fact]
    public void WithoutNamedEntry_FallsBackToAnyEntryOfTheRightWidth_PreferringTheShortest()
    {
        var list = new List<PaperInfo> { A4, new("Rouleau B", 244, 500), new("Rouleau A", 244, 100), Continuous29 };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        Assert.Equal("Rouleau A", choice.Name);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Theory]
    [InlineData(241, true)]     // 244 - 3
    [InlineData(242, true)]
    [InlineData(244, true)]
    [InlineData(246, true)]
    [InlineData(247, true)]     // 244 + 3
    [InlineData(248, false)]
    [InlineData(240, false)]
    [InlineData(300, false)]
    public void WidthTolerance_IsThreeHundredthsOfAnInch(int driverWidth, bool shouldMatch)
    {
        var list = new List<PaperInfo> { A4, new("Papier perso", driverWidth, 0) };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        if (shouldMatch)
        {
            Assert.Equal("Papier perso", choice.Name);
            Assert.False(choice.UseDriverSizeAsIs);
            Assert.InRange(choice.WidthHundredthsInch, 241, 247);
        }
        else
        {
            Assert.Equal("Custom", choice.Name);
            Assert.Equal(244, choice.WidthHundredthsInch);
        }
    }

    // ---------------------------------------------------------------- repli : format personnalisé

    [Fact]
    public void NoPaperSizeAtAll_FallsBackToCustom()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(new List<PaperInfo>(), 62, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
        Assert.False(choice.UseDriverSizeAsIs);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Fact]
    public void OnlyUnrelatedSizes_FallBackToCustom()
    {
        var list = new List<PaperInfo> { A4, new("Letter", 850, 1100), Continuous29, Continuous12 };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Fact]
    public void OnlyACustomEntryOfAnotherWidth_FallsBackToOurOwnCustomSize()
    {
        var list = new List<PaperInfo> { new("Custom", 827, 1169) };

        PaperChoice choice = LabelPrinter.ChoosePaper(list, 62, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
        Assert.False(choice.UseDriverSizeAsIs);
    }

    [Fact]
    public void CustomFallbackUsesTheConfiguredTapeWidth()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(new List<PaperInfo> { A4 }, 29, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(114, choice.WidthHundredthsInch);
    }

    [Fact]
    public void NullList_IsTreatedAsEmpty()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(null!, 62, null);

        Assert.Equal("Custom", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
    }

    // ---------------------------------------------------------------- réglage PaperName (étiquettes prédécoupées)

    [Fact]
    public void PaperNameOverride_ThatMatches_IsUsedAsIs()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, "62mm x 29mm");

        Assert.Equal("62mm x 29mm", choice.Name);
        Assert.True(choice.UseDriverSizeAsIs);
        Assert.Equal(244, choice.WidthHundredthsInch);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Theory]
    [InlineData("62MM X 29MM")]
    [InlineData("62mm x 29mm ")]
    [InlineData("  62Mm X 29mM")]
    public void PaperNameOverride_IsMatchedIgnoringCase(string wanted)
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, wanted);

        Assert.True(choice.UseDriverSizeAsIs);
        Assert.Equal("62mm x 29mm", choice.Name, ignoreCase: true);
    }

    [Fact]
    public void PaperNameOverride_WinsOverTheContinuousEntry()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, "62mm(Red/Black)");

        Assert.True(choice.UseDriverSizeAsIs);
        Assert.Equal("62mm(Red/Black)", choice.Name);
    }

    [Theory]
    [InlineData("N'existe pas")]
    [InlineData("62mm x")]            // pas de correspondance partielle
    [InlineData("62mm x 29mm x")]
    public void PaperNameOverride_ThatDoesNotMatch_IsIgnored(string wanted)
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, wanted);

        Assert.False(choice.UseDriverSizeAsIs);
        Assert.Equal("62mm", choice.Name);
        Assert.False(string.IsNullOrWhiteSpace(choice.Reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPaperNameOverride_MeansAutomatic(string? wanted)
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(DriverList(), 62, wanted);

        Assert.False(choice.UseDriverSizeAsIs);
        Assert.Equal("62mm", choice.Name);
    }

    [Fact]
    public void PaperNameOverride_WithEmptyDriverList_FallsBackToCustom()
    {
        PaperChoice choice = LabelPrinter.ChoosePaper(new List<PaperInfo>(), 62, "62mm x 29mm");

        Assert.False(choice.UseDriverSizeAsIs);
        Assert.Equal("Custom", choice.Name);
        Assert.Equal(244, choice.WidthHundredthsInch);
    }

    // ---------------------------------------------------------------- conversions

    [Theory]
    [InlineData(62, 244)]
    [InlineData(29, 114)]
    [InlineData(38, 150)]
    [InlineData(25.4, 100)]
    [InlineData(15, 59)]
    public void MillimetresToHundredthsOfAnInch(double mm, int expectedUnits)
    {
        Assert.Equal(expectedUnits, LabelPrinter.MmToUnits(mm));
    }
}
