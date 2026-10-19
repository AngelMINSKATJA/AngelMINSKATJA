using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Nom du logiciel (« POG's Industries Labels v3 ») et noms TECHNIQUES qui ne doivent pas bouger : dossier des réglages,
/// dossier du journal, valeur de démarrage automatique du registre. Tests purs (aucun accès au registre ni à GDI+).
/// Les noms du mutex / de l'événement et les informations de version de l'exécutable sont vérifiés dans
/// AppIdentityTests (Windows seulement : il lui faut l'assemblage réel de l'application).
/// </summary>
public class AppInfoTests
{
    // ---------------------------------------------------------------- constantes

    [Fact]
    public void TheNamesAreExactlyWhatTheUserAskedFor()
    {
        Assert.Equal("POG's Industries Labels", AppInfo.ProductName);
        Assert.Equal(3, AppInfo.MajorVersion);
        Assert.Equal("POG's Industries Labels v3", AppInfo.DisplayName);
        Assert.Equal("POGs Industries Labels v3", AppInfo.FileBaseName);
    }

    [Fact]
    public void DisplayName_IsTheProductNameFollowedByTheMajorVersion()
    {
        // DisplayName est une constante : le « 3 » y est écrit en dur, ce test le garde cohérent avec MajorVersion.
        Assert.Equal(AppInfo.DisplayName, AppInfo.ProductName + " v" + AppInfo.MajorVersion);
    }

    [Fact]
    public void TheApostropheIsThePlainAsciiOne_AsTheUserTypedIt()
    {
        Assert.Equal(1, AppInfo.ProductName.Split('\'').Length - 1);
        Assert.Equal('\'', AppInfo.ProductName[3]);
        Assert.DoesNotContain('\u2019', AppInfo.DisplayName);   // pas d'apostrophe typographique
        Assert.DoesNotContain('\u2018', AppInfo.DisplayName);
    }

    [Fact]
    public void FileBaseName_IsTheDisplayNameWithoutTheApostrophe()
    {
        Assert.Equal(AppInfo.FileBaseName, AppInfo.DisplayName.Replace("'", string.Empty));
        Assert.DoesNotContain('\'', AppInfo.FileBaseName);
    }

    [Fact]
    public void FileBaseName_IsAValidWindowsFileName()
    {
        // Path.GetInvalidFileNameChars() est plus court sous Linux : on liste les interdits de Windows.
        foreach (char forbidden in "\\/:*?\"<>|")
        {
            Assert.DoesNotContain(forbidden, AppInfo.FileBaseName);
        }

        Assert.All(AppInfo.FileBaseName, c => Assert.True(c >= ' '));
        Assert.Equal(AppInfo.FileBaseName, AppInfo.FileBaseName.Trim());
        Assert.False(AppInfo.FileBaseName.EndsWith('.'));
        Assert.True(AppInfo.FileBaseName.Length + ".exe".Length < 100);
    }

    // ---------------------------------------------------------------- titres de fenêtres

    [Theory]
    [InlineData("Codes-barres en série", "Codes-barres en série \u2014 POG's Industries Labels v3")]
    [InlineData("Étiquettes en série — plans de châssis", "Étiquettes en série — plans de châssis \u2014 POG's Industries Labels v3")]
    [InlineData("X", "X \u2014 POG's Industries Labels v3")]
    public void WindowTitle_IsTheFunctionThenAnEmDashThenTheDisplayName(string function, string expected)
    {
        Assert.Equal(expected, AppInfo.WindowTitle(function));
        Assert.Equal(function + " \u2014 " + AppInfo.DisplayName, AppInfo.WindowTitle(function));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WindowTitle_WithoutAFunction_IsJustTheDisplayName(string function)
    {
        Assert.Equal(AppInfo.DisplayName, AppInfo.WindowTitle(function));
    }

    [Fact]
    public void TheBarcodeSeriesWindowTitle_FollowsTheNewName()
    {
        Assert.Equal("Codes-barres en série \u2014 POG's Industries Labels v3", BarcodeBatchLogic.AppTitle);
    }

    [Fact]
    public void NoTitleKeepsTheOldIdentity()
    {
        foreach (string title in new[] { AppInfo.DisplayName, AppInfo.WindowTitle("x"), BarcodeBatchLogic.AppTitle })
        {
            Assert.DoesNotContain("Code-barres Code 128", title, StringComparison.Ordinal);
            Assert.DoesNotContain("BarcodeTray", title, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- impression

    [Fact]
    public void ThePrintQueueDocumentName_IsTheDisplayName()
    {
        FieldInfo? field = typeof(LabelPrinter).GetField("DocumentName", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(AppInfo.DisplayName, field!.GetRawConstantValue());
    }

    [Fact]
    public void TheDiagnosticHeader_NamesThePrintingSoftwareByItsNewName()
    {
        string text = LabelPrinter.BuildDiagnostics(null, new AppSettings());

        string firstLine = text.Split('\n')[0].TrimEnd('\r');
        Assert.Equal("=== Diagnostic d'impression - POG's Industries Labels v3 ===", firstLine);
        Assert.DoesNotContain("Code-barres Code 128 ===", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- noms techniques : INCHANGÉS

    [Fact]
    public void TheAutostartValueNameIsStillBarcodeTray()
    {
        // Même nom de valeur => au premier lancement de la nouvelle version, EnsurePathCurrent réécrit le chemin de l'ancienne
        // entrée (aucun doublon dans Démarrage, le choix « désactivé » du Gestionnaire des tâches est conservé).
        Assert.Equal("BarcodeTray", StartupRegistration.ValueName);
    }

    [Fact]
    public void TheSettingsFolderIsStillBarcodeTray()
    {
        Assert.Equal("BarcodeTray", Path.GetFileName(AppSettings.DirectoryPath));
        Assert.Equal("settings.json", Path.GetFileName(AppSettings.FilePath));
        Assert.Equal(AppSettings.DirectoryPath, Path.GetDirectoryName(AppSettings.FilePath));
    }

    [Fact]
    public void TheLogFolderAndFileAreStillBarcodeTray()
    {
        Assert.Equal("BarcodeTray", Path.GetFileName(Log.DirectoryPath));
        Assert.Equal("barcodetray.log", Path.GetFileName(Log.FilePath));
        Assert.Equal(Log.DirectoryPath, Path.GetDirectoryName(Log.FilePath));
    }

    [Fact]
    public void TheTechnicalFolderNamesDoNotFollowTheDisplayName()
    {
        foreach (string name in new[]
                 {
                     Path.GetFileName(AppSettings.DirectoryPath),
                     Path.GetFileName(Log.DirectoryPath),
                     StartupRegistration.ValueName,
                 })
        {
            Assert.DoesNotContain("POG", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Labels", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- ligne de commande du démarrage automatique

    [Theory]
    [InlineData(@"C:\Outils\POGs Industries Labels v3.exe")]
    [InlineData(@"C:\Users\Marie Dupont\Documents\POG's Industries\POGs Industries Labels v3.exe")]
    [InlineData(@"D:\Programmes (x86)\Étiquettes\POGs Industries Labels v3.exe")]
    public void TheAutostartCommand_QuotesThePath_SoSpacesAreSafe(string exePath)
    {
        string command = StartupRegistration.FormatCommand(exePath);

        Assert.Equal("\"" + exePath + "\" --tray", command);
        Assert.StartsWith("\"" + exePath + "\"", command, StringComparison.Ordinal);
        Assert.EndsWith("POGs Industries Labels v3.exe\" --tray", command, StringComparison.Ordinal);

        // Relecture comme le fait Windows : le premier argument est ce qui est entre guillemets.
        int close = command.IndexOf('"', 1);
        Assert.Equal(exePath, command.Substring(1, close - 1));
        Assert.Equal(" --tray", command.Substring(close + 1));
    }

    [Fact]
    public void TheAutostartCommand_ForTheNewFileName_IsNotTheOldOne()
    {
        string command = StartupRegistration.FormatCommand(@"C:\Outils\" + AppInfo.FileBaseName + ".exe");

        Assert.Contains("POGs Industries Labels v3.exe", command, StringComparison.Ordinal);
        Assert.DoesNotContain("BarcodeTray.exe", command, StringComparison.OrdinalIgnoreCase);
    }
}
