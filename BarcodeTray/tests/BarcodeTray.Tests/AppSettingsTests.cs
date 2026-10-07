using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

public class AppSettingsTests
{
    // ---------------------------------------------------------------- valeurs par défaut

    [Fact]
    public void Defaults_MatchTheSpecification()
    {
        var s = new AppSettings();

        Assert.Null(s.PrinterName);
        Assert.False(s.FirstRunDone);
        Assert.Equal(300, s.Dpi);
        Assert.Equal(3, s.ModulePx);
        Assert.Equal(696, s.MaxWidthPx);
        Assert.Equal(150, s.BarHeightPx);
        Assert.Equal("Arial", s.FontName);
        Assert.Equal(11f, s.FontSizePt);
        Assert.Equal(62, s.LabelWidthMm);
        Assert.Equal(15, s.MinLabelLengthMm);
        Assert.Null(s.PaperName);
        Assert.True(s.CheckPrinterStatus);
        Assert.False(s.LoadFailed);
    }

    [Fact]
    public void Paths_AreUnderAppData_BarcodeTray()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "BarcodeTray"), AppSettings.DirectoryPath);
        Assert.Equal(Path.Combine(appData, "BarcodeTray", "settings.json"), AppSettings.FilePath);
    }

    // ---------------------------------------------------------------- lecture / écriture

    [Fact]
    public void Load_WithoutFile_ReturnsDefaults()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.DeleteSettingsFile();

        AppSettings loaded = AppSettings.Load();

        AssertEqualSettings(new AppSettings(), loaded);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryValue()
    {
        using var sandbox = new SettingsSandbox();
        var original = new AppSettings
        {
            PrinterName = "Brother QL-800 (copie 2) – étiquettes",
            FirstRunDone = true,
            Dpi = 600,
            ModulePx = 2,
            MaxWidthPx = 640,
            BarHeightPx = 120,
            FontName = "Segoe UI",
            FontSizePt = 9.5f,
            LabelWidthMm = 29,
            MinLabelLengthMm = 20,
            PaperName = "62mm x 29mm",
            CheckPrinterStatus = false,
        };

        original.Save();

        Assert.True(File.Exists(sandbox.SettingsPath));
        AppSettings loaded = AppSettings.Load();
        AssertEqualSettings(original, loaded);
    }

    [Fact]
    public void Save_WritesIndentedJsonWithTheDocumentedKeys()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.DeleteSettingsFile();

        new AppSettings { PrinterName = "Brother QL-800" }.Save();

        string json = File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8);
        using JsonDocument doc = JsonDocument.Parse(json); // JSON valide
        Assert.Contains("\n", json, StringComparison.Ordinal); // indenté
        string[] keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        foreach (string expected in new[]
                 {
                     "PrinterName", "FirstRunDone", "Dpi", "ModulePx", "MaxWidthPx", "BarHeightPx",
                     "FontName", "FontSizePt", "LabelWidthMm", "MinLabelLengthMm", "PaperName", "CheckPrinterStatus",
                 })
        {
            Assert.Contains(expected, keys);
        }
    }

    [Fact]
    public void Save_ReplacesAnExistingFile_AndLeavesNoTemporaryFile()
    {
        using var sandbox = new SettingsSandbox();
        new AppSettings { Dpi = 203 }.Save();
        new AppSettings { Dpi = 300, PrinterName = "Deuxième enregistrement" }.Save();

        AppSettings loaded = AppSettings.Load();

        Assert.Equal(300, loaded.Dpi);
        Assert.Equal("Deuxième enregistrement", loaded.PrinterName);
        Assert.DoesNotContain(sandbox.FilesInDirectory(), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_IgnoresUnknownProperties()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"Dpi\": 203, \"NouvelleOption\": 123, \"Imbrique\": { \"a\": [1,2,3] }, \"PrinterName\": \"Zebra\" }");

        AppSettings loaded = AppSettings.Load();

        Assert.Equal(203, loaded.Dpi);
        Assert.Equal("Zebra", loaded.PrinterName);
        Assert.Equal(3, loaded.ModulePx); // le reste garde ses valeurs par défaut
    }

    [Fact]
    public void Load_ToleratesMissingProperties()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"PrinterName\": \"Seule clé\" }");

        AppSettings loaded = AppSettings.Load();

        Assert.Equal("Seule clé", loaded.PrinterName);
        Assert.Equal(300, loaded.Dpi);
        Assert.Equal(696, loaded.MaxWidthPx);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("{ ceci n'est pas du JSON")]
    [InlineData("{\"Dpi\": 300,")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("\"juste du texte\"")]
    [InlineData("42")]
    [InlineData("<xml>pas du json</xml>")]
    [InlineData("{ \"Dpi\": \"abc\", \"ModulePx\": [1] }")]
    public void Load_CorruptContent_NeverThrows_AndReturnsUsableSettings(string content)
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw(content);

        AppSettings loaded = AppSettings.Load();

        AssertUsable(loaded);
    }

    [Fact]
    public void Load_BinaryGarbage_NeverThrows()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRawBytes(new byte[] { 0x00, 0xFF, 0xFE, 0x01, 0x7B, 0x00, 0x80, 0x81, 0xC3, 0x28, 0x00 });

        AppSettings loaded = AppSettings.Load();

        AssertUsable(loaded);
    }

    [Fact]
    public void Load_AcceptsAUtf8ByteOrderMark()
    {
        using var sandbox = new SettingsSandbox();
        byte[] json = new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("{ \"PrinterName\": \"Avec BOM\", \"Dpi\": 203 }")).ToArray();
        sandbox.WriteRawBytes(json);

        AppSettings loaded = AppSettings.Load();

        Assert.Equal("Avec BOM", loaded.PrinterName);
        Assert.Equal(203, loaded.Dpi);
    }

    [Fact]
    public void Save_AfterACorruptFile_OverwritesItWithValidJson()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ corrompu");
        AppSettings loaded = AppSettings.Load();
        loaded.PrinterName = "Réparée";

        loaded.Save();

        AppSettings reloaded = AppSettings.Load();
        Assert.Equal("Réparée", reloaded.PrinterName);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Load_ClampsAbsurdValuesFoundInTheFile()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"Dpi\": 99999, \"ModulePx\": 0, \"MaxWidthPx\": -5, \"BarHeightPx\": 0, \"FontSizePt\": 0 }");

        AppSettings loaded = AppSettings.Load();

        AssertUsable(loaded);
        Assert.Equal(1200, loaded.Dpi);
        Assert.Equal(1, loaded.ModulePx);
    }

    // ---------------------------------------------------------------- fichier présent mais illisible (robustness-3)

    [Fact]
    public void Load_MissingFile_IsAFirstRun_AndNotAFailure()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.DeleteSettingsFile();

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.FirstRunDone);
        Assert.False(loaded.LoadFailed);
    }

    [Theory]
    [InlineData("{ \"PrinterName\": \"Brother QL-800\" \"Dpi\": 300 }")]   // virgule oubliée
    [InlineData("{ \"PrinterName\": \"Brother QL-800")]                       // guillemet et accolade oubliés
    [InlineData("[1, 2, 3]")]
    [InlineData("\"FirstRunDone\"")]
    [InlineData("{ \"FirstRunDone\": \"true\", \"Dpi\": 203 }")]               // booléen écrit comme du texte
    [InlineData("{ \"PrinterName\": 42 }")]                                    // mauvais type
    public void Load_UnreadableFile_IsNotAFirstRun_AndIsFlagged(string content)
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw(content);

        AppSettings loaded = AppSettings.Load();

        // Sinon Program réactiverait le démarrage automatique (même décoché) et réécrirait le fichier.
        Assert.True(loaded.LoadFailed);
        Assert.True(loaded.FirstRunDone);
    }

    [Fact]
    public void Load_UnreadableFile_IsNotOverwrittenByLoading_AndALocalCopyIsKept()
    {
        using var sandbox = new SettingsSandbox();
        const string damaged = "{ \"PrinterName\": \"Ma QL-800\", \"PaperName\": \"62mm\" ";   // accolade finale oubliée
        sandbox.WriteRaw(damaged);

        AppSettings loaded = AppSettings.Load();

        Assert.True(loaded.LoadFailed);
        Assert.Equal(damaged, File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8)); // fichier intact
        Assert.Contains(sandbox.FilesInDirectory(), f => f.StartsWith("settings.json.invalide", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_ValidFileWithoutFirstRunDoneKey_StillCountsAsFirstRun()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"PrinterName\": \"Seule clé\" }");

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.LoadFailed);
        Assert.False(loaded.FirstRunDone);
    }

    [Fact]
    public void Load_ValidFile_IsNotFlagged()
    {
        using var sandbox = new SettingsSandbox();
        new AppSettings { FirstRunDone = true, PrinterName = "OK" }.Save();

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.LoadFailed);
        Assert.True(loaded.FirstRunDone);
    }

    [Fact]
    public void Load_SecondDamagedFile_DoesNotEraseTheFirstKeptCopy()
    {
        using var sandbox = new SettingsSandbox();
        const string first = "{ premier fichier abîmé";
        const string second = "{ second fichier abîmé";

        sandbox.WriteRaw(first);
        AppSettings.Load();
        sandbox.WriteRaw(second);
        AppSettings.Load();
        AppSettings.Load(); // même contenu qu'avant : pas de copie en plus

        string[] kept = sandbox.FilesInDirectory().Where(f => f.StartsWith("settings.json.invalide", StringComparison.Ordinal)).ToArray();
        string directory = Path.GetDirectoryName(sandbox.SettingsPath)!;
        string[] contents = kept.Select(f => File.ReadAllText(Path.Combine(directory, f), Encoding.UTF8)).ToArray();
        Assert.Equal(2, kept.Length);
        Assert.Contains(first, contents);
        Assert.Contains(second, contents);
    }

    [Fact]
    public void LoadFailed_IsNeverWrittenToTheJsonFile()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ casse");
        AppSettings loaded = AppSettings.Load();

        loaded.Save();

        string json = File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8);
        Assert.DoesNotContain("LoadFailed", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(AppSettings.Load().LoadFailed);
    }

    // ---------------------------------------------------------------- Validate

    [Theory]
    [InlineData(-5, 72)]
    [InlineData(0, 72)]
    [InlineData(1, 72)]
    [InlineData(71, 72)]
    [InlineData(72, 72)]
    [InlineData(203, 203)]
    [InlineData(300, 300)]
    [InlineData(600, 600)]
    [InlineData(1200, 1200)]
    [InlineData(1201, 1200)]
    [InlineData(100000, 1200)]
    public void Validate_ClampsDpi(int input, int expected)
    {
        var s = new AppSettings { Dpi = input };

        s.Validate();

        Assert.Equal(expected, s.Dpi);
    }

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(999, 10)]
    public void Validate_ClampsModulePx(int input, int expected)
    {
        var s = new AppSettings { ModulePx = input };

        s.Validate();

        Assert.Equal(expected, s.ModulePx);
    }

    [Fact]
    public void Validate_KeepsDefaultsUnchanged_AndIsIdempotent()
    {
        var s = new AppSettings();

        s.Validate();
        s.Validate();

        AssertEqualSettings(new AppSettings(), s);
    }

    [Fact]
    public void Validate_BringsAbsurdValuesBackToUsableOnes()
    {
        var s = new AppSettings
        {
            MaxWidthPx = -10,
            BarHeightPx = 0,
            FontName = "",
            FontSizePt = float.NaN,
            LabelWidthMm = 0,
            MinLabelLengthMm = -4,
        };

        s.Validate();

        AssertUsable(s);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(100000f)]
    public void Validate_FontSizeBecomesSane(float input)
    {
        var s = new AppSettings { FontSizePt = input };

        s.Validate();

        Assert.False(float.IsNaN(s.FontSizePt) || float.IsInfinity(s.FontSizePt));
        Assert.InRange(s.FontSizePt, 1f, 200f);
    }

    [Fact]
    public void Validate_ToleratesNullFontName()
    {
        var s = new AppSettings { FontName = null! };

        s.Validate();

        Assert.False(string.IsNullOrWhiteSpace(s.FontName));
    }

    [Fact]
    public void Validate_KeepsReasonableCustomValues()
    {
        var s = new AppSettings
        {
            ModulePx = 2,
            MaxWidthPx = 640,
            BarHeightPx = 120,
            FontName = "Segoe UI",
            FontSizePt = 9.5f,
            LabelWidthMm = 29,
            MinLabelLengthMm = 20,
        };

        s.Validate();

        Assert.Equal(2, s.ModulePx);
        Assert.Equal(640, s.MaxWidthPx);
        Assert.Equal(120, s.BarHeightPx);
        Assert.Equal("Segoe UI", s.FontName);
        Assert.Equal(9.5f, s.FontSizePt);
        Assert.Equal(29, s.LabelWidthMm);
        Assert.Equal(20, s.MinLabelLengthMm);
    }

    // ---------------------------------------------------------------- aides

    private static void AssertUsable(AppSettings s)
    {
        Assert.InRange(s.Dpi, 72, 1200);
        Assert.InRange(s.ModulePx, 1, 10);
        Assert.True(s.MaxWidthPx > 0, "MaxWidthPx doit être positif");
        Assert.True(s.BarHeightPx > 0, "BarHeightPx doit être positif");
        Assert.False(string.IsNullOrWhiteSpace(s.FontName));
        Assert.False(float.IsNaN(s.FontSizePt) || float.IsInfinity(s.FontSizePt));
        Assert.True(s.FontSizePt > 0f, "FontSizePt doit être positif");
        Assert.True(s.LabelWidthMm > 0, "LabelWidthMm doit être positif");
        Assert.True(s.MinLabelLengthMm >= 0, "MinLabelLengthMm ne doit pas être négatif");
    }

    private static void AssertEqualSettings(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.PrinterName, actual.PrinterName);
        Assert.Equal(expected.FirstRunDone, actual.FirstRunDone);
        Assert.Equal(expected.Dpi, actual.Dpi);
        Assert.Equal(expected.ModulePx, actual.ModulePx);
        Assert.Equal(expected.MaxWidthPx, actual.MaxWidthPx);
        Assert.Equal(expected.BarHeightPx, actual.BarHeightPx);
        Assert.Equal(expected.FontName, actual.FontName);
        Assert.Equal(expected.FontSizePt, actual.FontSizePt);
        Assert.Equal(expected.LabelWidthMm, actual.LabelWidthMm);
        Assert.Equal(expected.MinLabelLengthMm, actual.MinLabelLengthMm);
        Assert.Equal(expected.PaperName, actual.PaperName);
        Assert.Equal(expected.CheckPrinterStatus, actual.CheckPrinterStatus);
    }
}
