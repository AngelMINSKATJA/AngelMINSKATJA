using System;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Contrôle de l'état de la file d'impression (printing-7, robustness-1) et détection d'un travail annulé par le
/// pilote (printing-3). Aucune impression réelle n'est lancée.
/// </summary>
public class PrinterStatusTests
{
    private const string Printer = "Brother QL-800";

    // Valeurs de winspool.h écrites en dur : elles ne doivent pas dépendre des constantes du programme.
    private const uint Paused = 0x1;
    private const uint Error = 0x2;
    private const uint PaperJam = 0x8;
    private const uint PaperOut = 0x10;
    private const uint Offline = 0x80;
    private const uint Printing = 0x400;
    private const uint NotAvailable = 0x1000;
    private const uint UserIntervention = 0x100000;
    private const uint OutOfMemory = 0x200000;
    private const uint DoorOpen = 0x400000;
    private const uint ServerOffline = 0x2000000;
    private const uint WorkOfflineAttribute = 0x400;

    // ---------------------------------------------------------------- DescribeProblem

    [Theory]
    [InlineData(0u, 0u)]                       // prête
    [InlineData(Printing, 0u)]                 // impression en cours : normal
    [InlineData(0x200u, 0u)]                   // BUSY
    [InlineData(0x4000u, 0u)]                  // PROCESSING
    [InlineData(Error, 0u)]                    // erreur générique : peut être périmée, ne bloque pas
    [InlineData(UserIntervention, 0u)]
    [InlineData(OutOfMemory, 0u)]              // 0x200000 n'est PAS « port indisponible »
    [InlineData(0u, 0x4u)]                     // DEFAULT
    [InlineData(0u, 0x40u | 0x800u)]           // LOCAL + ENABLE_BIDI
    public void DescribeProblem_ReadyOrHarmlessStates_DoNotBlock(uint status, uint attributes)
    {
        Assert.Null(PrinterStatus.DescribeProblem(Printer, status, attributes));
    }

    [Fact]
    public void DescribeProblem_TheSameBitMeansDifferentThingsInStatusAndAttributes()
    {
        // 0x400 = PRINTING dans Status (normal) mais WORK_OFFLINE dans Attributes (bloquant).
        Assert.Null(PrinterStatus.DescribeProblem(Printer, Printing, 0));
        Assert.NotNull(PrinterStatus.DescribeProblem(Printer, 0, WorkOfflineAttribute));
    }

    [Theory]
    [InlineData(Offline, 0u)]
    [InlineData(NotAvailable, 0u)]
    [InlineData(ServerOffline, 0u)]
    [InlineData(0u, WorkOfflineAttribute)]
    [InlineData(Offline | Printing, WorkOfflineAttribute)]
    public void DescribeProblem_OfflinePrinter_IsRefusedWithAClearFrenchMessage(uint status, uint attributes)
    {
        string? message = PrinterStatus.DescribeProblem(Printer, status, attributes);

        Assert.NotNull(message);
        Assert.Contains("hors connexion", message, StringComparison.Ordinal);
        Assert.Contains("« " + Printer + " »", message, StringComparison.Ordinal);
        Assert.Contains("Rien n'a été envoyé", message, StringComparison.Ordinal);
        Assert.Contains("CheckPrinterStatus", message, StringComparison.Ordinal); // la sortie de secours est indiquée
    }

    [Theory]
    [InlineData(Paused, "pause")]
    [InlineData(DoorOpen, "capot")]
    [InlineData(PaperOut, "ruban")]
    [InlineData(PaperJam, "bourrage")]
    public void DescribeProblem_OtherBlockingStates_NameTheProblem(uint status, string expectedWord)
    {
        string? message = PrinterStatus.DescribeProblem(Printer, status, 0);

        Assert.NotNull(message);
        Assert.Contains(expectedWord, message, StringComparison.Ordinal);
        Assert.Contains("Rien n'a été envoyé", message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- DescribeFlags

    [Fact]
    public void DescribeFlags_NamesTheActiveFlags()
    {
        Assert.Contains("prête", PrinterStatus.DescribeFlags(0, 0), StringComparison.Ordinal);

        string text = PrinterStatus.DescribeFlags(Offline | DoorOpen, WorkOfflineAttribute);

        Assert.Contains("OFFLINE", text, StringComparison.Ordinal);
        Assert.Contains("DOOR_OPEN", text, StringComparison.Ordinal);
        Assert.Contains("WORK_OFFLINE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PAPER_OUT", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- TryRead (spouleur réel, sans rien imprimer)

    [Fact]
    public void TryRead_UnknownOrBlankPrinter_ReturnsNullAndNeverThrows()
    {
        Assert.Null(PrinterStatus.TryRead("Imprimante-qui-n-existe-pas-0123456789"));
        Assert.Null(PrinterStatus.TryRead(""));
        Assert.Null(PrinterStatus.TryRead("   "));
    }

    [Fact]
    public void TryRead_InstalledPrinters_NeverThrows()
    {
        foreach (string name in LabelPrinter.GetPrinters())
        {
            PrinterStatus.Snapshot? snapshot = PrinterStatus.TryRead(name); // null admis (pilote sans état)
            if (snapshot != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(PrinterStatus.DescribeFlags(snapshot.Value.Status, snapshot.Value.Attributes)));
            }
        }
    }

    [Fact]
    public void CheckPrinterStatus_CanBeSwitchedOffInTheSettings_AndPrintingStillFailsFriendlyOnAMissingPrinter()
    {
        using System.Drawing.Bitmap bitmap = BarcodeRenderer.Render("PRINT-TEST", new AppSettings());
        var settings = new AppSettings { CheckPrinterStatus = false };

        var ex = Assert.Throws<InvalidOperationException>(
            () => LabelPrinter.Print(bitmap, "Imprimante-qui-n-existe-pas-0123456789", settings));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    // ---------------------------------------------------------------- travail annulé par le pilote (printing-3)

    [Fact]
    public void DescribeNoPageProblem_NothingPrinted_ReportsACancelledJob()
    {
        string? message = LabelPrinter.DescribeNoPageProblem(pagePrinted: false, pageError: null);

        Assert.NotNull(message);
        Assert.Contains("annulée", message, StringComparison.Ordinal);
        Assert.Contains("aucune page", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNoPageProblem_APagePrinted_IsFine()
    {
        Assert.Null(LabelPrinter.DescribeNoPageProblem(pagePrinted: true, pageError: null));
    }

    [Fact]
    public void DescribeNoPageProblem_DrawingError_IsReportedElsewhere()
    {
        // Une erreur de dessin a son propre message (« Impossible de dessiner l'étiquette ») : pas de doublon.
        Assert.Null(LabelPrinter.DescribeNoPageProblem(pagePrinted: false, pageError: new InvalidOperationException("boum")));
    }
}
