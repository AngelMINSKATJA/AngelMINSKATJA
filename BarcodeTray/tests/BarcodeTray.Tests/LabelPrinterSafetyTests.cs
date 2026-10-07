using System;
using System.Drawing;
using System.Linq;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Comportements de LabelPrinter qui ne nécessitent aucune imprimante réelle. IMPORTANT : on n'imprime JAMAIS
/// vers une vraie imprimante ici (par exemple « Microsoft Print to PDF » ouvrirait une boîte « Enregistrer sous »).
/// </summary>
public class LabelPrinterSafetyTests
{
    private const string NoSuchPrinter = "Imprimante-qui-n-existe-pas-0123456789";

    [Fact]
    public void GetPrinters_NeverThrows_AndReturnsNoBlankNames()
    {
        var printers = LabelPrinter.GetPrinters();

        Assert.NotNull(printers);
        Assert.DoesNotContain(printers, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void Print_ToAMissingPrinter_FailsWithAFriendlyInvalidOperationException()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("PRINT-TEST", new AppSettings());

        var ex = Assert.Throws<InvalidOperationException>(() => LabelPrinter.Print(bitmap, NoSuchPrinter, new AppSettings()));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        // Message destiné à l'utilisateur : en français, pas une trace technique.
        Assert.DoesNotContain("   at ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Print_WithoutPrinterName_FailsWithAFriendlyInvalidOperationException()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("PRINT-TEST", new AppSettings());

        Assert.Throws<InvalidOperationException>(() => LabelPrinter.Print(bitmap, "", new AppSettings()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(NoSuchPrinter)]
    public void BuildDiagnostics_NeverThrows_AndReturnsMultilineText(string? printerName)
    {
        string text = LabelPrinter.BuildDiagnostics(printerName, new AppSettings { PaperName = "62mm x 29mm" });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(text.Count(c => c == '\n') >= 5, "Le diagnostic devrait comporter plusieurs lignes.");
        Assert.Contains("62", text, StringComparison.Ordinal);            // largeur du ruban dans les réglages
        Assert.Contains("62mm x 29mm", text, StringComparison.Ordinal);   // réglage PaperName rappelé
    }

    [Fact]
    public void BuildDiagnostics_AcceptsNullSettings()
    {
        string text = LabelPrinter.BuildDiagnostics(null, null!);

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
