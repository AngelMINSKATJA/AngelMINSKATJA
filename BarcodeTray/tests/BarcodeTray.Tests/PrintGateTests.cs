using System;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Verrou d'impression commun aux fenêtres (PrintGate) : une seule fenêtre à la fois envoie des travaux à une imprimante.
/// Classe pure (aucune dépendance à Windows Forms) : ces tests tournent aussi sous Linux.
/// </summary>
public class PrintGateTests
{
    private static string UniquePrinter() => "Imprimante-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void TheFirstWindowGetsThePrinter_TheSecondIsToldWho()
    {
        string printer = UniquePrinter();
        var series = new object();
        var main = new object();
        try
        {
            Assert.True(PrintGate.TryAcquire(printer, series, "Codes-barres en série", out string? none));
            Assert.Null(none);

            Assert.False(PrintGate.TryAcquire(printer, main, AppInfo.DisplayName, out string? busy));
            Assert.Equal("Codes-barres en série", busy);
            Assert.False(PrintGate.IsHeldBy(main));
        }
        finally
        {
            PrintGate.Release(series);
            PrintGate.Release(main);
        }
    }

    [Fact]
    public void AfterTheRelease_AnotherWindowCanPrint()
    {
        string printer = UniquePrinter();
        var first = new object();
        var second = new object();
        try
        {
            Assert.True(PrintGate.TryAcquire(printer, first, "A", out _));
            PrintGate.Release(first);

            Assert.False(PrintGate.IsHeldBy(first));
            Assert.True(PrintGate.TryAcquire(printer, second, "B", out _));
        }
        finally
        {
            PrintGate.Release(first);
            PrintGate.Release(second);
        }
    }

    [Fact]
    public void TheSameWindowCanTakeItAgain_AndReleaseIsHarmlessWhenNothingIsHeld()
    {
        string printer = UniquePrinter();
        var window = new object();
        try
        {
            Assert.True(PrintGate.TryAcquire(printer, window, "A", out _));
            Assert.True(PrintGate.TryAcquire(printer, window, "A", out _));
            Assert.True(PrintGate.IsHeldBy(window));
        }
        finally
        {
            PrintGate.Release(window);
        }

        PrintGate.Release(window);
        PrintGate.Release(new object());
        Assert.False(PrintGate.IsHeldBy(window));
    }

    [Fact]
    public void TheGateIsPerPrinter_AndPrinterNamesIgnoreCaseAndOuterSpaces()
    {
        string a = UniquePrinter();
        string b = UniquePrinter();
        var first = new object();
        var second = new object();
        try
        {
            Assert.True(PrintGate.TryAcquire(a, first, "A", out _));

            Assert.True(PrintGate.TryAcquire(b, second, "B", out _));                              // une autre imprimante : libre
            Assert.False(PrintGate.TryAcquire("  " + a.ToUpperInvariant() + " ", second, "B", out string? busy));
            Assert.Equal("A", busy);
        }
        finally
        {
            PrintGate.Release(first);
            PrintGate.Release(second);
        }
    }

    [Fact]
    public void ReleaseGivesBackEveryPrinterOfTheWindow()
    {
        string a = UniquePrinter();
        string b = UniquePrinter();
        var window = new object();
        var other = new object();
        try
        {
            Assert.True(PrintGate.TryAcquire(a, window, "A", out _));
            Assert.True(PrintGate.TryAcquire(b, window, "A", out _));

            PrintGate.Release(window);

            Assert.True(PrintGate.TryAcquire(a, other, "B", out _));
            Assert.True(PrintGate.TryAcquire(b, other, "B", out _));
        }
        finally
        {
            PrintGate.Release(window);
            PrintGate.Release(other);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAPrinterName_TheGateDoesNotBlock(string? printer)
    {
        var first = new object();
        var second = new object();

        Assert.True(PrintGate.TryAcquire(printer, first, "A", out _));
        Assert.True(PrintGate.TryAcquire(printer, second, "B", out _));
        Assert.False(PrintGate.IsHeldBy(first));
    }

    [Fact]
    public void TheBusyMessage_NamesThePrinterAndTheWindow_AndExplainsWhy()
    {
        string message = PrintGate.BusyMessage("Brother QL-800", "Codes-barres en série");

        Assert.Contains("« Brother QL-800 »", message);
        Assert.Contains("la fenêtre « Codes-barres en série »", message);
        Assert.Contains("envoi que Windows n'a pas encore confirmé", message);
        Assert.Contains("mélanger les étiquettes", message);

        string anonymous = PrintGate.BusyMessage(null, null);
        Assert.StartsWith("L'imprimante est déjà utilisée par une autre fenêtre du programme", anonymous);
    }
}
