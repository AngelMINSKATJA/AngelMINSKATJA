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
        Assert.True(s.CheckPaper);
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
            CheckPaper = false,
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
                     "FontName", "FontSizePt", "LabelWidthMm", "MinLabelLengthMm", "PaperName", "CheckPrinterStatus", "CheckPaper",
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
        Assert.Equal(expected.CheckPaper, actual.CheckPaper);
        Assert.Equal(expected.BatchLabelLengthMm, actual.BatchLabelLengthMm);
        Assert.Equal(expected.BatchFontName, actual.BatchFontName);
        Assert.Equal(expected.BatchTypeFontPt, actual.BatchTypeFontPt);
        Assert.Equal(expected.BatchPlanFontPt, actual.BatchPlanFontPt);
        Assert.Equal(expected.BatchTypeLeftMm, actual.BatchTypeLeftMm);
        Assert.Equal(expected.BatchTypeBaselineMm, actual.BatchTypeBaselineMm);
        Assert.Equal(expected.BatchPlanBaselineMm, actual.BatchPlanBaselineMm);
        Assert.Equal(expected.BatchConfirm, actual.BatchConfirm);
    }

    private static void AssertBatchUsable(AppSettings s)
    {
        Assert.InRange(s.BatchLabelLengthMm, 10, 300);
        Assert.False(string.IsNullOrWhiteSpace(s.BatchFontName));
        foreach (float value in new[]
                 {
                     s.BatchTypeFontPt, s.BatchPlanFontPt, s.BatchTypeLeftMm, s.BatchTypeBaselineMm, s.BatchPlanBaselineMm,
                 })
        {
            Assert.False(float.IsNaN(value) || float.IsInfinity(value), "valeur non finie : " + value);
        }

        Assert.InRange(s.BatchTypeFontPt, 4f, 200f);
        Assert.InRange(s.BatchPlanFontPt, 4f, 300f);
        Assert.InRange(s.BatchTypeLeftMm, 0f, 60f);
        Assert.InRange(s.BatchTypeBaselineMm, 1f, 300f);
        Assert.InRange(s.BatchPlanBaselineMm, 1f, 300f);
    }

    private static readonly string[] BatchKeys =
    {
        "BatchLabelLengthMm", "BatchFontName", "BatchTypeFontPt", "BatchPlanFontPt", "BatchTypeLeftMm",
        "BatchTypeBaselineMm", "BatchPlanBaselineMm", "BatchConfirm",
    };

    // ---------------------------------------------------------------- étiquettes en série (clés Batch*)

    [Fact]
    public void BatchDefaults_MatchTheSpecification()
    {
        var s = new AppSettings();

        Assert.Equal(40, s.BatchLabelLengthMm);
        Assert.Equal("Arial", s.BatchFontName);
        Assert.Equal(23f, s.BatchTypeFontPt);
        Assert.Equal(61f, s.BatchPlanFontPt);
        Assert.Equal(1.9f, s.BatchTypeLeftMm);
        Assert.Equal(10.6f, s.BatchTypeBaselineMm);
        Assert.Equal(32.0f, s.BatchPlanBaselineMm);
        Assert.True(s.BatchConfirm);
    }

    [Fact]
    public void BatchDefaults_SurviveValidate_Unchanged()
    {
        var s = new AppSettings();

        s.Validate();
        s.Validate();

        AssertEqualSettings(new AppSettings(), s);
    }

    [Fact]
    public void BatchKeys_SaveThenLoad_RoundTripsEveryValue()
    {
        using var sandbox = new SettingsSandbox();
        var original = new AppSettings
        {
            FirstRunDone = true,
            BatchLabelLengthMm = 29,
            BatchFontName = "Segoe UI",
            BatchTypeFontPt = 24.5f,
            BatchPlanFontPt = 62.25f,
            BatchTypeLeftMm = 2.5f,
            BatchTypeBaselineMm = 11.2f,
            BatchPlanBaselineMm = 33.75f,
            BatchConfirm = false,
        };

        original.Save();

        AppSettings loaded = AppSettings.Load();
        AssertEqualSettings(original, loaded);
        Assert.False(loaded.BatchConfirm);
        Assert.Equal(62.25f, loaded.BatchPlanFontPt);
    }

    [Fact]
    public void BatchKeys_AreWrittenToTheJsonFile_UnderTheirDocumentedNames()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.DeleteSettingsFile();

        new AppSettings().Save();

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8));
        string[] keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        foreach (string expected in BatchKeys)
        {
            Assert.Contains(expected, keys);
        }

        Assert.Equal(40, doc.RootElement.GetProperty("BatchLabelLengthMm").GetInt32());
        Assert.Equal("Arial", doc.RootElement.GetProperty("BatchFontName").GetString());
        Assert.Equal(23.0, doc.RootElement.GetProperty("BatchTypeFontPt").GetDouble(), 3);
        Assert.Equal(61.0, doc.RootElement.GetProperty("BatchPlanFontPt").GetDouble(), 3);
        Assert.Equal(1.9, doc.RootElement.GetProperty("BatchTypeLeftMm").GetDouble(), 3);
        Assert.Equal(10.6, doc.RootElement.GetProperty("BatchTypeBaselineMm").GetDouble(), 3);
        Assert.Equal(32.0, doc.RootElement.GetProperty("BatchPlanBaselineMm").GetDouble(), 3);
        Assert.True(doc.RootElement.GetProperty("BatchConfirm").GetBoolean());
    }

    [Fact]
    public void Load_SettingsFileWrittenByThePreviousVersion_GetsBatchDefaults_AndKeepsItsOwnValues()
    {
        using var sandbox = new SettingsSandbox();
        // Fichier de la version précédente : aucune clé Batch*.
        sandbox.WriteRaw(
            "{\n"
            + "  \"PrinterName\": \"Brother QL-800 (Copie 1)\",\n"
            + "  \"FirstRunDone\": true,\n"
            + "  \"Dpi\": 300,\n"
            + "  \"ModulePx\": 3,\n"
            + "  \"MaxWidthPx\": 696,\n"
            + "  \"BarHeightPx\": 150,\n"
            + "  \"FontName\": \"Arial\",\n"
            + "  \"FontSizePt\": 11,\n"
            + "  \"LabelWidthMm\": 62,\n"
            + "  \"MinLabelLengthMm\": 15,\n"
            + "  \"PaperName\": null,\n"
            + "  \"CheckPrinterStatus\": true,\n"
            + "  \"CheckPaper\": true\n"
            + "}\n");

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.LoadFailed);
        Assert.True(loaded.FirstRunDone);
        Assert.Equal("Brother QL-800 (Copie 1)", loaded.PrinterName);
        Assert.Equal(300, loaded.Dpi);
        Assert.Equal(40, loaded.BatchLabelLengthMm);
        Assert.Equal("Arial", loaded.BatchFontName);
        Assert.Equal(23f, loaded.BatchTypeFontPt);
        Assert.Equal(61f, loaded.BatchPlanFontPt);
        Assert.Equal(1.9f, loaded.BatchTypeLeftMm);
        Assert.Equal(10.6f, loaded.BatchTypeBaselineMm);
        Assert.Equal(32.0f, loaded.BatchPlanBaselineMm);
        Assert.True(loaded.BatchConfirm);
    }

    [Fact]
    public void Save_AfterLoadingAFileWithoutBatchKeys_AddsThem_WithoutTouchingTheOtherValues()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"PrinterName\": \"Ma QL-800\", \"FirstRunDone\": true, \"Dpi\": 203, \"PaperName\": \"62mm\" }");
        AppSettings loaded = AppSettings.Load();

        loaded.Save();

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8));
        string[] keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        foreach (string expected in BatchKeys)
        {
            Assert.Contains(expected, keys);
        }

        AppSettings reloaded = AppSettings.Load();
        Assert.Equal("Ma QL-800", reloaded.PrinterName);
        Assert.Equal(203, reloaded.Dpi);
        Assert.Equal("62mm", reloaded.PaperName);
        Assert.Equal(40, reloaded.BatchLabelLengthMm);
        Assert.False(reloaded.LoadFailed);
    }

    [Fact]
    public void Load_OnlyBatchKeysPresent_AppliesThemAndKeepsTheRestAtDefaults()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"BatchLabelLengthMm\": 50, \"BatchPlanFontPt\": 55.5, \"BatchConfirm\": false }");

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.LoadFailed);
        Assert.Equal(50, loaded.BatchLabelLengthMm);
        Assert.Equal(55.5f, loaded.BatchPlanFontPt);
        Assert.False(loaded.BatchConfirm);
        Assert.Equal(23f, loaded.BatchTypeFontPt);   // le reste garde ses valeurs par défaut
        Assert.Equal(300, loaded.Dpi);
    }

    [Fact]
    public void Load_BatchKeys_AreCaseInsensitive_AndAcceptNumbersWrittenAsText()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"batchlabellengthmm\": \"45\", \"BATCHTYPEFONTPT\": \"20.5\", \"batchFontName\": \"Calibri\" }");

        AppSettings loaded = AppSettings.Load();

        Assert.False(loaded.LoadFailed);
        Assert.Equal(45, loaded.BatchLabelLengthMm);
        Assert.Equal(20.5f, loaded.BatchTypeFontPt);
        Assert.Equal("Calibri", loaded.BatchFontName);
    }

    [Theory]
    [InlineData(-5, 10)]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(40, 40)]
    [InlineData(62, 62)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(100000, 300)]
    [InlineData(int.MinValue, 10)]
    [InlineData(int.MaxValue, 300)]
    public void Validate_ClampsBatchLabelLengthMm(int input, int expected)
    {
        var s = new AppSettings { BatchLabelLengthMm = input };

        s.Validate();

        Assert.Equal(expected, s.BatchLabelLengthMm);
    }

    [Theory]
    [InlineData(-1f, 4f)]
    [InlineData(0f, 4f)]
    [InlineData(3.9f, 4f)]
    [InlineData(4f, 4f)]
    [InlineData(23f, 23f)]
    [InlineData(200f, 200f)]
    [InlineData(200.1f, 200f)]
    [InlineData(100000f, 200f)]
    public void Validate_ClampsBatchTypeFontPt(float input, float expected)
    {
        var s = new AppSettings { BatchTypeFontPt = input };

        s.Validate();

        Assert.Equal(expected, s.BatchTypeFontPt);
    }

    [Theory]
    [InlineData(-1f, 4f)]
    [InlineData(0f, 4f)]
    [InlineData(4f, 4f)]
    [InlineData(61f, 61f)]
    [InlineData(300f, 300f)]
    [InlineData(300.5f, 300f)]
    [InlineData(100000f, 300f)]
    public void Validate_ClampsBatchPlanFontPt(float input, float expected)
    {
        var s = new AppSettings { BatchPlanFontPt = input };

        s.Validate();

        Assert.Equal(expected, s.BatchPlanFontPt);
    }

    [Theory]
    [InlineData(-3f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(1.9f, 1.9f)]
    [InlineData(60f, 60f)]
    [InlineData(60.5f, 60f)]
    [InlineData(5000f, 60f)]
    public void Validate_ClampsBatchTypeLeftMm(float input, float expected)
    {
        var s = new AppSettings { BatchTypeLeftMm = input };

        s.Validate();

        Assert.Equal(expected, s.BatchTypeLeftMm);
    }

    [Theory]
    [InlineData(-3f, 1f)]
    [InlineData(0f, 1f)]
    [InlineData(1f, 1f)]
    [InlineData(10.6f, 10.6f)]
    [InlineData(300f, 300f)]
    [InlineData(301f, 300f)]
    [InlineData(1e9f, 300f)]
    public void Validate_ClampsBatchBaselines(float input, float expected)
    {
        var s = new AppSettings { BatchTypeBaselineMm = input, BatchPlanBaselineMm = input };

        s.Validate();

        Assert.Equal(expected, s.BatchTypeBaselineMm);
        Assert.Equal(expected, s.BatchPlanBaselineMm);
    }

    [Fact]
    public void Validate_NonFiniteBatchValues_FallBackToTheDefaults()
    {
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var s = new AppSettings
            {
                BatchTypeFontPt = bad,
                BatchPlanFontPt = bad,
                BatchTypeLeftMm = bad,
                BatchTypeBaselineMm = bad,
                BatchPlanBaselineMm = bad,
            };

            s.Validate();

            AssertBatchUsable(s);
            Assert.Equal(23f, s.BatchTypeFontPt);
            Assert.Equal(61f, s.BatchPlanFontPt);
            Assert.Equal(1.9f, s.BatchTypeLeftMm);
            Assert.Equal(10.6f, s.BatchTypeBaselineMm);
            Assert.Equal(32.0f, s.BatchPlanBaselineMm);
        }
    }

    [Fact]
    public void Save_WithNonFiniteBatchValues_WritesValidJson_AndReloadsTheDefaults()
    {
        using var sandbox = new SettingsSandbox();
        var s = new AppSettings
        {
            FirstRunDone = true,
            BatchPlanFontPt = float.NaN,
            BatchTypeFontPt = float.PositiveInfinity,
            BatchPlanBaselineMm = float.NegativeInfinity,
        };

        s.Save();   // NaN / Infinity ne sont pas du JSON valide : Validate les remplace avant l'écriture

        Assert.True(File.Exists(sandbox.SettingsPath));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(sandbox.SettingsPath, Encoding.UTF8));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        AppSettings loaded = AppSettings.Load();
        Assert.False(loaded.LoadFailed);
        Assert.Equal(61f, loaded.BatchPlanFontPt);
        Assert.Equal(23f, loaded.BatchTypeFontPt);
        Assert.Equal(32.0f, loaded.BatchPlanBaselineMm);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"abc\"")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    public void Load_NonNumericOrNonFiniteBatchValueInTheFile_NeverThrows_AndStaysUsable(string literal)
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"Dpi\": 203, \"BatchTypeFontPt\": " + literal + ", \"BatchPlanFontPt\": " + literal
                         + ", \"BatchTypeLeftMm\": " + literal + ", \"BatchTypeBaselineMm\": " + literal
                         + ", \"BatchPlanBaselineMm\": " + literal + " }");

        AppSettings loaded = AppSettings.Load();

        AssertBatchUsable(loaded);
        Assert.Equal(203, loaded.Dpi);   // une valeur illisible ne fait pas perdre les autres réglages
    }

    [Fact]
    public void Load_AbsurdBatchValuesInTheFile_AreClamped()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"BatchLabelLengthMm\": 99999, \"BatchTypeFontPt\": -5, \"BatchPlanFontPt\": 100000, "
                         + "\"BatchTypeLeftMm\": -10, \"BatchTypeBaselineMm\": 0, \"BatchPlanBaselineMm\": 1e9, \"BatchFontName\": \"   \" }");

        AppSettings loaded = AppSettings.Load();

        AssertBatchUsable(loaded);
        Assert.Equal(300, loaded.BatchLabelLengthMm);
        Assert.Equal(4f, loaded.BatchTypeFontPt);
        Assert.Equal(300f, loaded.BatchPlanFontPt);
        Assert.Equal(0f, loaded.BatchTypeLeftMm);
        Assert.Equal(1f, loaded.BatchTypeBaselineMm);
        Assert.Equal(300f, loaded.BatchPlanBaselineMm);
        Assert.Equal("Arial", loaded.BatchFontName);
    }

    [Fact]
    public void Load_WrongTypeForBatchConfirm_KeepsTheDefault_AndTheOtherSettings()
    {
        using var sandbox = new SettingsSandbox();
        sandbox.WriteRaw("{ \"BatchConfirm\": \"oui\", \"BatchLabelLengthMm\": 55, \"PrinterName\": \"QL\" }");

        AppSettings loaded = AppSettings.Load();

        Assert.True(loaded.LoadFailed);          // valeur illisible signalée, comme pour les autres clés
        Assert.True(loaded.BatchConfirm);        // valeur par défaut conservée
        Assert.Equal(55, loaded.BatchLabelLengthMm);
        Assert.Equal("QL", loaded.PrinterName);
    }

    [Theory]
    [InlineData(null, "Arial")]
    [InlineData("", "Arial")]
    [InlineData("   ", "Arial")]
    [InlineData("Arial", "Arial")]
    [InlineData("  Calibri  ", "Calibri")]
    [InlineData("Segoe UI Semibold", "Segoe UI Semibold")]
    public void Validate_BatchFontName_IsTrimmed_AndNeverBlank(string? input, string expected)
    {
        var s = new AppSettings { BatchFontName = input! };

        s.Validate();

        Assert.Equal(expected, s.BatchFontName);
    }

    [Fact]
    public void Validate_KeepsReasonableCustomBatchValues()
    {
        var s = new AppSettings
        {
            BatchLabelLengthMm = 29,
            BatchFontName = "Calibri",
            BatchTypeFontPt = 18f,
            BatchPlanFontPt = 48f,
            BatchTypeLeftMm = 3f,
            BatchTypeBaselineMm = 9f,
            BatchPlanBaselineMm = 24.5f,
            BatchConfirm = false,
        };

        s.Validate();

        Assert.Equal(29, s.BatchLabelLengthMm);
        Assert.Equal("Calibri", s.BatchFontName);
        Assert.Equal(18f, s.BatchTypeFontPt);
        Assert.Equal(48f, s.BatchPlanFontPt);
        Assert.Equal(3f, s.BatchTypeLeftMm);
        Assert.Equal(9f, s.BatchTypeBaselineMm);
        Assert.Equal(24.5f, s.BatchPlanBaselineMm);
        Assert.False(s.BatchConfirm);
    }

    [Fact]
    public void Clone_CopiesTheBatchKeys_AndIsIndependent()
    {
        var original = new AppSettings { BatchLabelLengthMm = 33, BatchFontName = "Calibri", BatchPlanFontPt = 50f, BatchConfirm = false };

        AppSettings copy = original.Clone();
        copy.BatchLabelLengthMm = 99;
        copy.BatchFontName = "Verdana";
        copy.BatchConfirm = true;

        Assert.Equal(33, original.BatchLabelLengthMm);
        Assert.Equal("Calibri", original.BatchFontName);
        Assert.False(original.BatchConfirm);
        Assert.Equal(50f, original.BatchPlanFontPt);
        Assert.Equal(50f, copy.BatchPlanFontPt);
    }

    [Fact]
    public void BatchKeys_AreSettableProperties_SoTheJsonLoaderKnowsThem()
    {
        // Le chargeur ne connaît que les propriétés publiques lisibles ET modifiables : une clé Batch* privée ou en
        // lecture seule serait silencieusement ignorée dans settings.json.
        foreach (string key in BatchKeys)
        {
            System.Reflection.PropertyInfo? property = typeof(AppSettings).GetProperty(key);
            Assert.NotNull(property);
            Assert.True(property!.CanRead && property.CanWrite, key);
        }
    }
}
