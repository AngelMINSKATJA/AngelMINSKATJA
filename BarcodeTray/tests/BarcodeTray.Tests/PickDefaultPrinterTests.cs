using System;
using System.Collections.Generic;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Présélection de l'imprimante : mémorisée (si toujours installée) puis « QL-800 », « Brother QL »,
/// « QL- » avec « Brother », imprimante par défaut de Windows, première de la liste, sinon rien.
/// </summary>
public class PickDefaultPrinterTests
{
    private static readonly string[] Office =
    {
        "Fax", "HP LaserJet Pro M404", "Microsoft Print to PDF", "OneNote (Desktop)",
    };

    [Fact]
    public void NoPrinters_ReturnsNull()
    {
        Assert.Null(LabelPrinter.PickDefaultPrinter(Array.Empty<string>(), null, null));
        Assert.Null(LabelPrinter.PickDefaultPrinter(Array.Empty<string>(), "Brother QL-800", "HP"));
        Assert.Null(LabelPrinter.PickDefaultPrinter(new List<string>(), "x", "y"));
    }

    [Fact]
    public void SavedPrinter_WinsEvenOverAQl800()
    {
        var printers = new[] { "Brother QL-800", "HP LaserJet Pro M404" };

        Assert.Equal("HP LaserJet Pro M404", LabelPrinter.PickDefaultPrinter(printers, "HP LaserJet Pro M404", "Brother QL-800"));
    }

    [Fact]
    public void SavedPrinter_IsMatchedIgnoringCase_AndReturnedAsInstalled()
    {
        var printers = new[] { "Brother QL-800", "HP LaserJet Pro M404" };

        Assert.Equal("HP LaserJet Pro M404", LabelPrinter.PickDefaultPrinter(printers, "hp laserjet pro m404", null));
    }

    [Fact]
    public void SavedPrinterNoLongerInstalled_FallsBackToTheQl800()
    {
        var printers = new[] { "Microsoft Print to PDF", "Brother QL-800", "Brother QL-820NWB" };

        Assert.Equal("Brother QL-800", LabelPrinter.PickDefaultPrinter(printers, "Imprimante débranchée", "Microsoft Print to PDF"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSavedName_IsIgnored(string? saved)
    {
        var printers = new[] { "HP LaserJet Pro M404", "Brother QL-800" };

        Assert.Equal("Brother QL-800", LabelPrinter.PickDefaultPrinter(printers, saved, "HP LaserJet Pro M404"));
    }

    [Fact]
    public void Ql800_IsPreferredWhateverItsPositionInTheList()
    {
        Assert.Equal("Brother QL-800", LabelPrinter.PickDefaultPrinter(
            new[] { "Brother QL-1110NWB", "Brother QL-820NWB", "Brother QL-800" }, null, "Brother QL-1110NWB"));
        Assert.Equal("Brother QL-800", LabelPrinter.PickDefaultPrinter(
            new[] { "Brother QL-800", "Brother QL-820NWB" }, null, null));
    }

    [Theory]
    [InlineData("brother ql-800")]
    [InlineData("BROTHER QL-800")]
    [InlineData("Brother QL-800 (Copie 1)")]
    [InlineData("\\\\SERVEUR\\Brother QL-800")]
    [InlineData("QL-800")]
    public void Ql800_IsRecognisedWhateverTheCaseOrDecoration(string name)
    {
        var printers = new[] { "Fax", "Microsoft Print to PDF", name };

        Assert.Equal(name, LabelPrinter.PickDefaultPrinter(printers, null, "Fax"));
    }

    [Fact]
    public void OtherBrotherQlModels_AreNextBest()
    {
        var printers = new[] { "HP LaserJet Pro M404", "Brother QL-1110NWB", "Brother QL-820NWB" };

        Assert.Equal("Brother QL-1110NWB", LabelPrinter.PickDefaultPrinter(printers, null, "HP LaserJet Pro M404"));
    }

    [Fact]
    public void QlDashWithBrother_IsRecognisedWhenTheNameIsUnusual()
    {
        var printers = new[] { "HP LaserJet Pro M404", "QL-700 (Brother)" };

        Assert.Equal("QL-700 (Brother)", LabelPrinter.PickDefaultPrinter(printers, null, "HP LaserJet Pro M404"));
    }

    [Fact]
    public void QlWithoutBrother_IsNotSpecial()
    {
        var printers = new[] { "HP LaserJet Pro M404", "QL-700" };

        Assert.Equal("HP LaserJet Pro M404", LabelPrinter.PickDefaultPrinter(printers, null, "HP LaserJet Pro M404"));
    }

    [Fact]
    public void WithoutAnyLabelPrinter_TheWindowsDefaultIsUsed()
    {
        Assert.Equal("HP LaserJet Pro M404", LabelPrinter.PickDefaultPrinter(Office, null, "HP LaserJet Pro M404"));
        Assert.Equal("HP LaserJet Pro M404", LabelPrinter.PickDefaultPrinter(Office, null, "hp laserjet pro m404"));
    }

    [Fact]
    public void DefaultNotInstalled_FallsBackToTheFirstPrinter()
    {
        Assert.Equal("Fax", LabelPrinter.PickDefaultPrinter(Office, null, "Imprimante fantôme"));
    }

    [Fact]
    public void NoDefaultAndNothingSaved_ReturnsTheFirstPrinter()
    {
        Assert.Equal("Fax", LabelPrinter.PickDefaultPrinter(Office, null, null));
        Assert.Equal("Fax", LabelPrinter.PickDefaultPrinter(Office, "", ""));
    }

    [Fact]
    public void SinglePrinter_IsReturned()
    {
        Assert.Equal("Seule", LabelPrinter.PickDefaultPrinter(new[] { "Seule" }, null, null));
    }
}
