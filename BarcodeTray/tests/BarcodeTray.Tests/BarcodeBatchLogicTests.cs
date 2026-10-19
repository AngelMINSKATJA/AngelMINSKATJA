using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Code128;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Logique de la fenêtre « Codes-barres en série » (BarcodeBatchLogic) et cache de miniatures (BarcodeBatchLruCache) :
/// analyse des lignes sans dessiner d'image, résumé, états, textes des messages, première ligne invalide, glisser-déposer,
/// bilans de série (avec le déroulement réel de BatchLogic.Run). Tests purs : ni interface ni imprimante ni GDI+, ils tournent
/// aussi sous Linux. Le test de la fenêtre elle-même est dans BarcodeBatchFormSmokeTests (Windows).
/// </summary>
public class BarcodeBatchLogicTests
{
    // ------------------------------------------------------------------------------------------------
    // Outils
    // ------------------------------------------------------------------------------------------------

    private const int Preferred = 3;
    private const int MaxWidth = 696;

    private static BarcodeRowAnalysis A(string? text, int preferred = Preferred, int max = MaxWidth)
        => BarcodeBatchLogic.AnalyzeRow(text, preferred, max);

    private static List<BarcodeRowAnalysis> All(params string?[] texts)
        => BarcodeBatchLogic.AnalyzeAll(texts, new AppSettings());

    /// <summary>26 caractères : barres de 2 points ; 27 à 58 caractères : 1 point (fines) ; au-delà : trop large pour le ruban.</summary>
    private static string Letters(int count) => new('A', count);

    private static ListReadResult Read(IReadOnlyList<string> values, IReadOnlyList<string>? warnings = null, string? error = null, string source = "presse-papiers", string? skippedTitle = null)
        => new(values, warnings ?? Array.Empty<string>(), error, source, skippedTitle);

    private sealed class Fake : IDisposable
    {
        internal int DisposeCount;

        internal bool Disposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }

    // ------------------------------------------------------------------------------------------------
    // Analyse d'une ligne (sans image)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Analyze_AShortValidText_IsOk_AndCountsTheRealSymbols()
    {
        BarcodeRowAnalysis a = A("  ABC-123  ");

        Assert.Equal("ABC-123", a.Text);
        Assert.Null(a.Error);
        Assert.True(a.IsValid);
        Assert.Equal(Code128Encoder.Encode("ABC-123").Symbols.Count, a.SymbolCount);
        Assert.Equal(BarcodeRenderer.ChooseModulePx(a.SymbolCount, Preferred, MaxWidth), a.ModulePx);
        Assert.Equal(3, a.ModulePx);
        Assert.False(a.ThinBars);
        Assert.False(a.Scaled);
        Assert.Null(a.Hint);
        Assert.Null(a.Message);
        Assert.Equal(BarcodeRowSeverity.Ok, a.Severity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Analyze_AnEmptyText_IsInvalid_WithTheSingleWindowMessage(string? text)
    {
        BarcodeRowAnalysis a = A(text);

        Assert.False(a.IsValid);
        Assert.Equal(BarcodeRenderer.ValidateText(text), a.Error);
        Assert.Equal("Saisissez un texte.", a.Error);
        Assert.Equal(string.Empty, a.Text);
        Assert.Equal(0, a.SymbolCount);
        Assert.Equal(BarcodeRowSeverity.Invalid, a.Severity);
        Assert.Equal(a.Error, a.Message);
    }

    [Theory]
    [InlineData("Réf 12")]
    [InlineData("A\tB")]
    [InlineData("A\u00A0B")]
    [InlineData("Prix 5€")]
    [InlineData("ligne1\nligne2")]
    [InlineData("A\u0001B")]
    [InlineData("日本")]
    public void Analyze_ACharacterOutsideAscii_IsInvalid_WithTheValidateTextMessage(string text)
    {
        BarcodeRowAnalysis a = A(text);

        Assert.False(a.IsValid);
        Assert.Equal(BarcodeRenderer.ValidateText(text.Trim()), a.Error);
        Assert.Contains("Code 128", a.Error ?? string.Empty);
    }

    [Fact]
    public void Analyze_AnAccentedCharacter_NamesTheCharacter()
    {
        BarcodeRowAnalysis a = A("Réf");

        Assert.Contains("« é »", a.Error);
    }

    [Fact]
    public void Analyze_TheLengthLimitIsTheOneOfTheSingleWindow()
    {
        Assert.Equal(200, BarcodeBatchLogic.MaxTextLength);
        Assert.True(A(Letters(200)).IsValid);

        BarcodeRowAnalysis tooLong = A(Letters(201));

        Assert.False(tooLong.IsValid);
        Assert.Equal("Texte trop long (201 caractères) : 200 caractères au maximum.", tooLong.Error);
    }

    [Fact]
    public void Analyze_BarsThinnerThanTheirLimit_AreFlaggedOrange_ButStayPrintable()
    {
        BarcodeRowAnalysis fits2 = A(Letters(26));
        BarcodeRowAnalysis thin = A(Letters(27));

        Assert.Equal(2, fits2.ModulePx);
        Assert.False(fits2.ThinBars);
        Assert.Equal(BarcodeRowSeverity.Ok, fits2.Severity);

        Assert.True(thin.IsValid);
        Assert.Equal(1, thin.ModulePx);
        Assert.True(thin.ThinBars);
        Assert.False(thin.Scaled);
        Assert.Equal(BarcodeRowSeverity.Warning, thin.Severity);
        Assert.Equal(BarcodeRenderer.ThinBarsWarning(1, Preferred), thin.Hint);
        Assert.Contains("barres très fines", thin.Hint);
    }

    [Fact]
    public void Analyze_ATextWiderThanTheTape_EvenWithOnePointBars_IsFlaggedScaled()
    {
        BarcodeRowAnalysis fits = A(Letters(58));   // 695 modules de large : tient à 1 point
        BarcodeRowAnalysis scaled = A(Letters(70)); // 827 modules : plus large que le ruban (696 points)

        Assert.True(fits.ThinBars);
        Assert.False(fits.Scaled);

        Assert.True(scaled.IsValid);
        Assert.True(scaled.Scaled);
        Assert.Equal(1, scaled.ModulePx);
        Assert.Contains("réduit", scaled.Hint);
        Assert.Equal(BarcodeRowSeverity.Warning, scaled.Severity);
    }

    [Fact]
    public void Analyze_AFavouriteBarWidthOfOnePoint_IsNeverReportedAsThin()
    {
        BarcodeRowAnalysis a = A(Letters(40), preferred: 1);

        Assert.True(a.IsValid);
        Assert.Equal(1, a.ModulePx);
        Assert.False(a.ThinBars);
        Assert.Equal(BarcodeRowSeverity.Ok, a.Severity);
    }

    [Fact]
    public void Analyze_FollowsTheSameRulesAsTheRenderer_ForManyLengths()
    {
        // La largeur minimale du code-barres (modules x largeur retenue) est celle que BarcodeRenderer dessine.
        for (int n = 1; n <= 120; n++)
        {
            string text = Letters(n);
            BarcodeRowAnalysis a = A(text);
            int symbols = Code128Encoder.Encode(text).Symbols.Count;
            int module = BarcodeRenderer.ChooseModulePx(symbols, Preferred, MaxWidth);
            long minWidth = (11L * (symbols - 1) + 13 + 2L * BarcodeRenderer.QuietZoneModulesUsed) * module;

            Assert.Equal(symbols, a.SymbolCount);
            Assert.Equal(module, a.ModulePx);
            Assert.Equal(minWidth > MaxWidth * 1.02, a.Scaled);
            Assert.Equal(BarcodeRenderer.ThinBarsWarning(module, Preferred) != null, a.ThinBars);
        }
    }

    [Fact]
    public void ModuleLimits_AreTheValidatedSettings()
    {
        Assert.Equal((3, 696), BarcodeBatchLogic.ModuleLimits(null));
        Assert.Equal((3, 696), BarcodeBatchLogic.ModuleLimits(new AppSettings()));

        (int preferred, int max) = BarcodeBatchLogic.ModuleLimits(new AppSettings { ModulePx = 2, MaxWidthPx = 500 });
        Assert.Equal(2, preferred);
        Assert.Equal(500, max);

        (int clamped, _) = BarcodeBatchLogic.ModuleLimits(new AppSettings { ModulePx = 999 });
        Assert.InRange(clamped, 1, 10);
    }

    [Fact]
    public void ModuleLimits_DoNotModifyTheSettingsObject()
    {
        var settings = new AppSettings { ModulePx = 999 };

        _ = BarcodeBatchLogic.ModuleLimits(settings);

        Assert.Equal(999, settings.ModulePx);
    }

    [Fact]
    public void AnalyzeAll_KeepsTheOrder_AndTreatsNullAsEmpty()
    {
        List<BarcodeRowAnalysis> rows = All("A1", null, "B2", "é");

        Assert.Equal(4, rows.Count);
        Assert.Equal("A1", rows[0].Text);
        Assert.False(rows[1].IsValid);
        Assert.Equal("B2", rows[2].Text);
        Assert.False(rows[3].IsValid);
    }

    [Fact]
    public void Analyze_NeverThrows_OnGarbage()
    {
        var random = new Random(12345);
        for (int i = 0; i < 2000; i++)
        {
            int length = random.Next(0, 260);
            var chars = new char[length];
            for (int j = 0; j < length; j++)
            {
                chars[j] = random.Next(5) switch
                {
                    0 => (char)random.Next(0, 0x20),
                    1 => (char)random.Next(0x7F, 0x3000),
                    2 => (char)random.Next(0xD800, 0xE000), // substituts isolés
                    _ => (char)random.Next(0x20, 0x7F),
                };
            }

            string text = new(chars);
            BarcodeRowAnalysis a = A(text);

            Assert.Equal(text.Trim(), a.Text);
            if (a.IsValid)
            {
                Assert.Null(BarcodeRenderer.ValidateText(a.Text));
                Assert.InRange(a.Text.Length, 1, BarcodeBatchLogic.MaxTextLength);
                Assert.True(a.SymbolCount >= 4);
                Assert.True(a.ModulePx >= 1);
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(a.Error));
            }
        }
    }

    [Fact]
    public void Analyze_FiveThousandRows_IsFast()
    {
        string[] texts = Enumerable.Range(0, 5000).Select(i => "ART-" + (100000 + i).ToString("D7")).ToArray();

        var watch = Stopwatch.StartNew();
        List<BarcodeRowAnalysis> rows = BarcodeBatchLogic.AnalyzeAll(texts, new AppSettings());
        string summary = BarcodeBatchLogic.BuildSummary(rows, 2);
        watch.Stop();

        Assert.Equal(5000, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid));
        Assert.StartsWith("5000 codes-barres x 2 copies = 10000 impressions — tous valides", summary);
        Assert.True(watch.ElapsedMilliseconds < 2000, "L'analyse de 5000 lignes a pris " + watch.ElapsedMilliseconds + " ms.");
    }

    // ------------------------------------------------------------------------------------------------
    // Doublons, décomptes, résumé
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Duplicates_AreTheRowsWhoseTextAppearedAbove_ExactlyAndTrimmed()
    {
        Assert.Equal(0, BarcodeBatchLogic.CountDuplicates(All("A", "B", "C")));
        Assert.Equal(2, BarcodeBatchLogic.CountDuplicates(All("A", "B", "A", "A")));
        Assert.Equal(1, BarcodeBatchLogic.CountDuplicates(All("ABC", " ABC ")));      // les espaces de bord ne comptent pas
        Assert.Equal(0, BarcodeBatchLogic.CountDuplicates(All("abc", "ABC")));        // un code-barres est sensible à la casse
        Assert.Equal(0, BarcodeBatchLogic.CountDuplicates(All("", "  ", null)));      // les lignes vides ne sont pas des doublons
        Assert.Equal(1, BarcodeBatchLogic.CountDuplicates(All("é", "é")));            // une ligne invalide peut être un doublon
    }

    [Fact]
    public void Stats_CountEachKindOnce_AScaledRowIsNotAlsoThin()
    {
        List<BarcodeRowAnalysis> rows = All("OK1", "", Letters(27), Letters(70), "OK1", Letters(27));

        BarcodeListStats stats = BarcodeBatchLogic.ComputeStats(rows);

        Assert.Equal(6, stats.Total);
        Assert.Equal(1, stats.Invalid);
        Assert.Equal(2, stats.Thin);
        Assert.Equal(1, stats.Scaled);
        Assert.Equal(2, stats.Duplicates); // "OK1" et la deuxième ligne de 27 lettres
        Assert.Equal(3, stats.Warnings);
    }

    [Fact]
    public void Summary_OfAnEmptyList_ExplainsHowToStart()
    {
        Assert.Equal(BarcodeBatchLogic.NoCodesText, BarcodeBatchLogic.BuildSummary(new List<BarcodeRowAnalysis>()));
        Assert.Contains("Excel", BarcodeBatchLogic.NoCodesText);
        Assert.Contains("fichier", BarcodeBatchLogic.NoCodesText);
    }

    [Fact]
    public void Summary_OfValidRows_SaysSo()
    {
        Assert.Equal("3 codes-barres — tous valides", BarcodeBatchLogic.BuildSummary(All("A1", "B2", "C3")));
        Assert.Equal("1 code-barres — valide", BarcodeBatchLogic.BuildSummary(All("A1")));
    }

    [Fact]
    public void Summary_ListsEveryKindOfRemark()
    {
        List<BarcodeRowAnalysis> rows = All("ABC", "", Letters(27), Letters(70), "ABC");

        Assert.Equal(
            "5 codes-barres — 1 invalide, 1 à barres fines, 1 réduit pour tenir, 1 doublon",
            BarcodeBatchLogic.BuildSummary(rows));
    }

    [Fact]
    public void Summary_UsesThePlural_WhenThereAreSeveral()
    {
        List<BarcodeRowAnalysis> rows = All("", "é", Letters(27), Letters(28), Letters(70), Letters(71), "X", "X", "X", "X");

        string summary = BarcodeBatchLogic.BuildSummary(rows);

        Assert.Equal("10 codes-barres — 2 invalides, 2 à barres fines, 2 réduits pour tenir, 3 doublons", summary);
    }

    [Theory]
    [InlineData(0, "2 codes-barres — tous valides")]
    [InlineData(-3, "2 codes-barres — tous valides")]
    [InlineData(1, "2 codes-barres — tous valides")]
    [InlineData(3, "2 codes-barres x 3 copies = 6 impressions — tous valides")]
    [InlineData(1000, "2 codes-barres x 99 copies = 198 impressions — tous valides")]
    public void Summary_ClampsAndShowsTheNumberOfCopies(int copies, string expected)
    {
        Assert.Equal(expected, BarcodeBatchLogic.BuildSummary(All("A1", "B2"), copies));
    }

    [Fact]
    public void SummaryTone_IsRedForInvalid_OrangeForRemarks_NeutralOtherwise()
    {
        Assert.Equal(BatchTone.Info, BarcodeBatchLogic.SummaryTone(new List<BarcodeRowAnalysis>()));
        Assert.Equal(BatchTone.Info, BarcodeBatchLogic.SummaryTone(All("A1", "B2")));
        Assert.Equal(BatchTone.Warning, BarcodeBatchLogic.SummaryTone(All("A1", "A1")));
        Assert.Equal(BatchTone.Warning, BarcodeBatchLogic.SummaryTone(All("A1", Letters(27))));
        Assert.Equal(BatchTone.Error, BarcodeBatchLogic.SummaryTone(All("A1", Letters(27), "")));
    }

    // ------------------------------------------------------------------------------------------------
    // Colonne « État »
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void StateColumn_ShowsThePrintStateWhenThereIsOne_TheRemarkOtherwise()
    {
        BarcodeRowAnalysis ok = A("A1");
        BarcodeRowAnalysis bad = A("");
        BarcodeRowAnalysis thin = A(Letters(27));
        BarcodeRowAnalysis scaled = A(Letters(70));

        Assert.Equal(string.Empty, BarcodeBatchLogic.StateColumnText(RowState.Pending, 0, 1, ok));
        Assert.Equal(string.Empty, BarcodeBatchLogic.StateColumnText(RowState.Pending, 0, 1, null));
        Assert.Equal("✖ invalide", BarcodeBatchLogic.StateColumnText(RowState.Pending, 0, 1, bad));
        Assert.Equal("⚠ barres fines", BarcodeBatchLogic.StateColumnText(RowState.Pending, 0, 1, thin));
        Assert.Equal("⚠ réduit", BarcodeBatchLogic.StateColumnText(RowState.Pending, 0, 1, scaled));

        // Dès qu'il y a un état d'impression, c'est lui qui est montré (mêmes mots que la fenêtre des étiquettes de plans).
        Assert.Equal(BatchLogic.StateText(RowState.Done), BarcodeBatchLogic.StateColumnText(RowState.Done, 1, 1, thin));
        Assert.Equal("✔ imprimée", BarcodeBatchLogic.StateColumnText(RowState.Done, 1, 1, ok));
        Assert.Equal("en cours…", BarcodeBatchLogic.StateColumnText(RowState.Printing, 0, 1, ok));
        Assert.Equal("✖ échec (2/3 copies)", BarcodeBatchLogic.StateColumnText(RowState.Failed, 2, 3, ok));
        Assert.Equal("? à vérifier", BarcodeBatchLogic.StateColumnText(RowState.Uncertain, 0, 1, ok));
        Assert.Equal("arrêtée (1/2 copies)", BarcodeBatchLogic.StateColumnText(RowState.Partial, 1, 2, ok));
    }

    [Fact]
    public void StateTone_FollowsTheStateThenTheRemark()
    {
        BarcodeRowAnalysis ok = A("A1");
        BarcodeRowAnalysis bad = A("");
        BarcodeRowAnalysis thin = A(Letters(27));

        Assert.Equal(BatchTone.Success, BarcodeBatchLogic.StateTone(RowState.Done, bad));
        Assert.Equal(BatchTone.Error, BarcodeBatchLogic.StateTone(RowState.Failed, ok));
        Assert.Equal(BatchTone.Warning, BarcodeBatchLogic.StateTone(RowState.Uncertain, ok));
        Assert.Equal(BatchTone.Warning, BarcodeBatchLogic.StateTone(RowState.Partial, ok));
        Assert.Equal(BatchTone.Info, BarcodeBatchLogic.StateTone(RowState.Printing, ok));
        Assert.Equal(BatchTone.Info, BarcodeBatchLogic.StateTone(RowState.Pending, ok));
        Assert.Equal(BatchTone.Info, BarcodeBatchLogic.StateTone(RowState.Pending, null));
        Assert.Equal(BatchTone.Error, BarcodeBatchLogic.StateTone(RowState.Pending, bad));
        Assert.Equal(BatchTone.Warning, BarcodeBatchLogic.StateTone(RowState.Pending, thin));
    }

    // ------------------------------------------------------------------------------------------------
    // Lignes à imprimer
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void FindInvalidRow_ReturnsTheFirstInvalidRow_InTheOrderOfTheRowsToPrint()
    {
        List<BarcodeRowAnalysis> rows = All("OK1", "é", "", "OK2");

        BarcodeRowProblem? first = BarcodeBatchLogic.FindInvalidRow(rows, new[] { 0, 1, 2, 3 });
        Assert.NotNull(first);
        Assert.Equal(1, first!.Row);
        Assert.Equal("Ligne 2 : " + BarcodeRenderer.ValidateText("é"), first.Message);

        BarcodeRowProblem? second = BarcodeBatchLogic.FindInvalidRow(rows, new[] { 2, 3 });
        Assert.Equal(2, second!.Row);
        Assert.Equal("Ligne 3 : Saisissez un texte.", second.Message);

        Assert.Equal(2, BarcodeBatchLogic.FindInvalidRow(rows, new[] { 3, 2, 1 })!.Row); // l'ordre donné prime
    }

    [Fact]
    public void FindInvalidRow_IgnoresValidRowsAndRowsOutOfRange()
    {
        List<BarcodeRowAnalysis> rows = All("OK1", "é", "OK2");

        Assert.Null(BarcodeBatchLogic.FindInvalidRow(rows, new[] { 0, 2 }));
        Assert.Null(BarcodeBatchLogic.FindInvalidRow(rows, new[] { -1, 7, 99 }));
        Assert.Null(BarcodeBatchLogic.FindInvalidRow(rows, Array.Empty<int>()));
        Assert.Null(BarcodeBatchLogic.FindInvalidRow(new List<BarcodeRowAnalysis>(), new[] { 0 }));
    }

    [Fact]
    public void FindInvalidRow_AWarningRowIsPrintable()
    {
        List<BarcodeRowAnalysis> rows = All(Letters(27), Letters(70));

        Assert.Null(BarcodeBatchLogic.FindInvalidRow(rows, new[] { 0, 1 }));
    }

    [Fact]
    public void NothingToPrint_MessagesAreSpecificToTheMode()
    {
        Assert.Equal("Aucun code-barres à imprimer : collez ou ouvrez d'abord une liste.", BarcodeBatchLogic.NothingToPrintMessage(PrintMode.All));
        Assert.Equal("Sélectionnez d'abord une ou plusieurs lignes dans la liste.", BarcodeBatchLogic.NothingToPrintMessage(PrintMode.Selection));
        Assert.Equal("Sélectionnez d'abord une ou plusieurs lignes dans la liste.", BarcodeBatchLogic.NothingToPrintMessage(PrintMode.FromSelection));
    }

    [Fact]
    public void TheRowChoiceAndTheButtonStates_AreTheOnesOfTheChassisWindow()
    {
        // Les helpers génériques de BatchLogic sont réutilisés tels quels : on vérifie ici le contrat dont la fenêtre dépend.
        Assert.Equal(new[] { 0, 1, 2, 3 }, BatchLogic.ChooseRows(PrintMode.All, 4, new[] { 2 }));
        Assert.Equal(new[] { 1, 3 }, BatchLogic.ChooseRows(PrintMode.Selection, 4, new[] { 3, 1, 3, 9, -1 }));
        Assert.Equal(new[] { 2, 3 }, BatchLogic.ChooseRows(PrintMode.FromSelection, 4, new[] { 3, 2 }));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.Selection, 4, Array.Empty<int>()));

        BatchButtonStates idle = BatchLogic.ComputeButtons(5, 2, printing: false, stopRequested: false, latePending: false);
        Assert.True(idle.Paste && idle.Clear && idle.PrintAll && idle.PrintSelection && idle.PrintFromSelection && idle.GridEditable);
        Assert.False(idle.Stop);

        BatchButtonStates busy = BatchLogic.ComputeButtons(5, 2, printing: true, stopRequested: false, latePending: false);
        Assert.False(busy.Paste || busy.Clear || busy.PrintAll || busy.PrintSelection || busy.GridEditable);
        Assert.True(busy.Stop);

        BatchButtonStates late = BatchLogic.ComputeButtons(5, 2, printing: false, stopRequested: false, latePending: true);
        Assert.True(late.Paste);
        Assert.False(late.PrintAll || late.PrintSelection || late.PrintFromSelection);
    }

    // ------------------------------------------------------------------------------------------------
    // Textes de l'impression
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ShortText_CutsLongTextsWithAnEllipsis_WithoutSplittingASurrogatePair()
    {
        Assert.Equal(string.Empty, BarcodeBatchLogic.ShortText(null));
        Assert.Equal("ABC", BarcodeBatchLogic.ShortText("  ABC  "));
        Assert.Equal(new string('A', 24), BarcodeBatchLogic.ShortText(new string('A', 24)));
        Assert.Equal(new string('A', 23) + "…", BarcodeBatchLogic.ShortText(new string('A', 25)));

        string withEmoji = new string('a', 22) + "😀" + "bbbb";
        Assert.Equal(new string('a', 22) + "…", BarcodeBatchLogic.ShortText(withEmoji));
    }

    [Fact]
    public void DescribeRow_NamesTheRowAndItsText()
    {
        Assert.Equal("ligne 3 (ABC)", BarcodeBatchLogic.DescribeRow(new[] { "X", "Y", "ABC" }, 2));
        Assert.Equal("ligne 11", BarcodeBatchLogic.DescribeRow(new[] { "X" }, 10));
        Assert.Equal("ligne 1 (" + new string('A', 23) + "…)", BarcodeBatchLogic.DescribeRow(new[] { new string('A', 40) }, 0));
    }

    [Fact]
    public void ProgressText_ShowsPositionTextAndCopies()
    {
        Assert.Equal("Code-barres 12 / 88 (ABC-123)…", BarcodeBatchLogic.BuildProgressText(12, 88, 1, 1, "ABC-123"));
        Assert.Equal("Code-barres 2 / 5 (X), copie 2 / 3…", BarcodeBatchLogic.BuildProgressText(2, 5, 2, 3, " X "));
        Assert.Equal("Code-barres 1 / 1 (" + new string('A', 23) + "…)…", BarcodeBatchLogic.BuildProgressText(1, 1, 1, 1, new string('A', 50)));
    }

    [Fact]
    public void ConfirmText_NamesTheCountThePrinterAndTheCopies()
    {
        Assert.Equal("Imprimer 88 codes-barres sur « Brother QL-800 » ?", BarcodeBatchLogic.BuildConfirmText(88, 1, "Brother QL-800"));
        Assert.Equal("Imprimer 1 code-barres sur « P » ?", BarcodeBatchLogic.BuildConfirmText(1, 1, "P"));
        Assert.Equal("Imprimer 1 code-barres en 2 copies sur « P » ?", BarcodeBatchLogic.BuildConfirmText(1, 2, "P"));
        Assert.Equal("Imprimer 3 codes-barres en 2 copies (6 impressions au total) sur « P » ?", BarcodeBatchLogic.BuildConfirmText(3, 2, "P"));
        Assert.Equal("Imprimer 2 codes-barres en 99 copies (198 impressions au total) sur « P » ?", BarcodeBatchLogic.BuildConfirmText(2, 500, "P"));
    }

    [Fact]
    public void ReprintText_WithNothingAlreadyPrinted_IsTheOrdinaryConfirmation()
    {
        Assert.Equal(BarcodeBatchLogic.BuildConfirmText(4, 2, "P"), BarcodeBatchLogic.BuildReprintText(4, 0, 2, "P"));
        Assert.Equal(BarcodeBatchLogic.BuildConfirmText(4, 2, "P"), BarcodeBatchLogic.BuildReprintText(4, -5, 2, "P"));
    }

    [Fact]
    public void ReprintText_WhenEverythingIsAlreadyPrinted_WarnsAboutDuplicates()
    {
        string several = BarcodeBatchLogic.BuildReprintText(3, 3, 1, "P");
        string one = BarcodeBatchLogic.BuildReprintText(1, 1, 1, "P");
        string tooMany = BarcodeBatchLogic.BuildReprintText(3, 9, 1, "P"); // plus que le total : ramené au total

        Assert.StartsWith("Ces 3 codes-barres sont déjà marqués « ✔ imprimée » : les imprimer de nouveau les fera sortir en double.", several);
        Assert.EndsWith(BarcodeBatchLogic.BuildConfirmText(3, 1, "P"), several);
        Assert.StartsWith("Ce code-barres est déjà marqué « ✔ imprimée » : l'imprimer de nouveau le fera sortir en double.", one);
        Assert.Equal(several, tooMany);
    }

    [Fact]
    public void ReprintText_WithSomeAlreadyPrinted_ExplainsTheThreeAnswers()
    {
        string text = BarcodeBatchLogic.BuildReprintText(5, 2, 1, "P");

        Assert.StartsWith(BarcodeBatchLogic.BuildConfirmText(5, 1, "P"), text);
        Assert.Contains("Attention : 2 codes-barres sont déjà marqués « ✔ imprimée ».", text);
        Assert.Contains("Oui : tout imprimer (les 2 déjà imprimés sortiront en double).", text);
        Assert.Contains("Non : imprimer seulement les 3 codes-barres pas encore imprimés.", text);
        Assert.EndsWith("Annuler : ne rien imprimer.", text);
    }

    [Fact]
    public void ReprintText_SingularForms()
    {
        string text = BarcodeBatchLogic.BuildReprintText(2, 1, 1, "P");

        Assert.Contains("Attention : 1 code-barres est déjà marqué « ✔ imprimée ».", text);
        Assert.Contains("Oui : tout imprimer (celui déjà imprimé sortira en double).", text);
        Assert.Contains("Non : imprimer seulement le code-barres pas encore imprimé.", text);
    }

    // ------------------------------------------------------------------------------------------------
    // Remarque sur la qualité (barres fines, code-barres réduit)
    // ------------------------------------------------------------------------------------------------

    private static BatchOutcome Outcome(int rowsDone, int totalRows = 5, BatchEnd end = BatchEnd.Completed, int? resumeRow = null, int resumeCopies = 0, string? error = null)
        => new(end, rowsDone, rowsDone, totalRows, totalRows, resumeRow, resumeCopies, error, null);

    [Fact]
    public void QualityNote_CountsOnlyThePrintedRows()
    {
        List<BarcodeRowAnalysis> rows = All("OK1", Letters(27), Letters(70), Letters(71), Letters(28));
        int[] order = { 0, 1, 2, 3, 4 };

        Assert.Equal(
            "Attention : 2 codes-barres ont des barres très fines et 2 ont été réduits pour tenir sur l'étiquette : vérifiez qu'ils se lisent bien.",
            BarcodeBatchLogic.BuildQualityNote(rows, order, Outcome(5)));
        Assert.Equal(
            "Attention : 1 code-barres a des barres très fines : vérifiez qu'il se lit bien.",
            BarcodeBatchLogic.BuildQualityNote(rows, order, Outcome(2)));
        Assert.Null(BarcodeBatchLogic.BuildQualityNote(rows, order, Outcome(1)));
        Assert.Null(BarcodeBatchLogic.BuildQualityNote(rows, order, Outcome(0)));
    }

    [Fact]
    public void QualityNote_Wordings()
    {
        List<BarcodeRowAnalysis> rows = All(Letters(27), Letters(70), Letters(71));
        int[] order = { 0, 1, 2 };

        Assert.Equal(
            "Attention : 1 code-barres a des barres très fines et 1 a été réduit pour tenir sur l'étiquette : vérifiez qu'ils se lisent bien.",
            BarcodeBatchLogic.BuildQualityNote(rows, order, Outcome(2, 3)));
        Assert.Equal(
            "Attention : 1 code-barres a été réduit pour tenir sur l'étiquette : vérifiez qu'il se lit bien.",
            BarcodeBatchLogic.BuildQualityNote(rows, new[] { 1 }, Outcome(1, 1)));
        Assert.Equal(
            "Attention : 2 codes-barres ont été réduits pour tenir sur l'étiquette : vérifiez qu'ils se lisent bien.",
            BarcodeBatchLogic.BuildQualityNote(rows, new[] { 1, 2 }, Outcome(2, 2)));
    }

    [Fact]
    public void QualityNote_FollowsTheRowsOfTheSeries_NotTheWholeList()
    {
        List<BarcodeRowAnalysis> rows = All(Letters(27), "OK1", "OK2", Letters(70));

        // Série limitée aux lignes 1 et 2 : les remarques des lignes 0 et 3 ne la concernent pas.
        Assert.Null(BarcodeBatchLogic.BuildQualityNote(rows, new[] { 1, 2 }, Outcome(2, 2)));
        Assert.Contains("réduit", BarcodeBatchLogic.BuildQualityNote(rows, new[] { 2, 3 }, Outcome(2, 2)));
    }

    // ------------------------------------------------------------------------------------------------
    // Bilan d'une série (avec le déroulement réel de BatchLogic.Run)
    // ------------------------------------------------------------------------------------------------

    private static Task<BatchOutcome> Run(
        int rowCount, int copies, Func<int, int, Task> print, Func<bool>? stop = null, int timeoutMs = 5000, List<BatchEvent>? events = null)
        => BatchLogic.Run(
            Enumerable.Range(0, rowCount).ToArray(), copies, print, stop ?? (() => false), e => events?.Add(e), timeoutMs, 0);

    private static readonly string[] Texts = { "A", "B", "C" };

    [Fact]
    public async Task Outcome_Completed()
    {
        BatchOutcome one = await Run(1, 1, (_, _) => Task.CompletedTask);
        BatchOutcome three = await Run(3, 1, (_, _) => Task.CompletedTask);
        BatchOutcome copies = await Run(3, 2, (_, _) => Task.CompletedTask);

        Assert.Equal("✔ 1 code-barres envoyé à « P ».", BarcodeBatchLogic.BuildOutcomeMessage(one, Texts, 1, "P", 60_000, null, out BatchTone t1));
        Assert.Equal(BatchTone.Success, t1);
        Assert.Equal("✔ 3 codes-barres envoyés à « P ».", BarcodeBatchLogic.BuildOutcomeMessage(three, Texts, 1, "P", 60_000, null, out _));
        Assert.Equal("✔ 3 codes-barres envoyés (6 impressions) à « P ».", BarcodeBatchLogic.BuildOutcomeMessage(copies, Texts, 2, "P", 60_000, null, out _));
    }

    [Fact]
    public async Task Outcome_Completed_WithAQualityNote_TurnsOrange()
    {
        BatchOutcome three = await Run(3, 1, (_, _) => Task.CompletedTask);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(three, Texts, 1, "P", 60_000, "Attention : 1 code-barres a été réduit.", out BatchTone tone);

        Assert.Equal("✔ 3 codes-barres envoyés à « P ». Attention : 1 code-barres a été réduit.", message);
        Assert.Equal(BatchTone.Warning, tone);
    }

    [Fact]
    public async Task Outcome_Stopped_TellsWhereToResume()
    {
        int sent = 0;
        BatchOutcome outcome = await Run(3, 1, (_, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, stop: () => sent >= 1);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchEnd.Stopped, outcome.End);
        Assert.Equal(BatchTone.Warning, tone);
        Assert.Equal(
            "Impression arrêtée : 1 sur 3 codes-barres envoyés à « P ». Pour reprendre, sélectionnez la ligne 2 et cliquez sur « Imprimer à partir de la sélection ».",
            message);
    }

    [Fact]
    public async Task Outcome_Stopped_AlsoMentionsTheQualityNote()
    {
        int sent = 0;
        BatchOutcome outcome = await Run(3, 1, (_, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, stop: () => sent >= 1);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, "Attention : 1 code-barres a été réduit.", out BatchTone tone);

        Assert.EndsWith("cliquez sur « Imprimer à partir de la sélection ». Attention : 1 code-barres a été réduit.", message);
        Assert.Equal(BatchTone.Warning, tone);
    }

    [Fact]
    public async Task Outcome_StoppedBetweenCopies_SaysHowManyCopiesLeft()
    {
        int sent = 0;
        BatchOutcome outcome = await Run(2, 3, (_, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, stop: () => sent >= 2);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 3, "P", 60_000, null, out _);

        Assert.Equal(BatchEnd.Stopped, outcome.End);
        Assert.Equal(0, outcome.ResumeRow);
        Assert.Equal(2, outcome.ResumeCopiesDone);
        Assert.Contains("sélectionnez la ligne 1", message);
        Assert.Contains("Cette ligne n'a reçu que 2 copies sur 3 : la reprise les imprimera toutes à nouveau.", message);
    }

    [Fact]
    public async Task Outcome_Failed_NamesTheRowTheReasonAndTheResumeInstruction()
    {
        BatchOutcome outcome = await Run(3, 1, (row, _) =>
            row == 1 ? Task.FromException(new InvalidOperationException("L'imprimante « P » est hors connexion")) : Task.CompletedTask);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchEnd.Failed, outcome.End);
        Assert.Equal(BatchTone.Error, tone);
        Assert.Equal(
            "Échec à la ligne 2 (B) : L'imprimante « P » est hors connexion. 1 sur 3 codes-barres envoyés. "
            + "Corrigez le problème, puis sélectionnez la ligne 2 et cliquez sur « Imprimer à partir de la sélection ».",
            message);
    }

    [Fact]
    public async Task Outcome_Failed_OnAnUnexpectedException_SaysSo()
    {
        BatchOutcome outcome = await Run(2, 1, (_, _) => Task.FromException(new IOException("disque plein")));

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out _);

        Assert.Contains("Échec à la ligne 1 (A) : Erreur inattendue : disque plein.", message);
    }

    [Fact]
    public async Task Outcome_TimedOut_WarnsAboutAPossibleDuplicate()
    {
        var never = new TaskCompletionSource<bool>();
        BatchOutcome outcome = await Run(3, 1, (row, _) => row == 1 ? never.Task : Task.CompletedTask, timeoutMs: 50);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchEnd.TimedOut, outcome.End);
        Assert.NotNull(outcome.Orphan);
        Assert.Equal(BatchTone.Error, tone);
        Assert.StartsWith("Windows n'a pas confirmé l'envoi de la ligne 2 (B) en 60 s…", message);
        Assert.Contains("la ligne est marquée « ? »", message);
        Assert.Contains("1 sur 3 codes-barres envoyés avant celui-ci", message);
        Assert.Contains("sélectionnez la ligne 2 (ou la suivante si l'étiquette est sortie)", message);
        never.SetResult(true);
    }

    [Fact]
    public async Task Outcome_TheEventsFeedTheStateColumn()
    {
        var events = new List<BatchEvent>();
        List<BarcodeRowAnalysis> rows = All("OK1", Letters(27));

        await Run(2, 2, (_, _) => Task.CompletedTask, events: events);

        // Par ligne : « en cours » à chaque copie, puis « imprimée » à la fin ; le texte de la colonne vient de la logique pure.
        Assert.Equal(6, events.Count);
        Assert.Equal(RowState.Printing, events[0].State);
        Assert.Equal(RowState.Done, events[2].State);
        Assert.Equal("en cours…", BarcodeBatchLogic.StateColumnText(events[0].State, events[0].CopiesDone, events[0].Copies, rows[events[0].Row]));
        Assert.Equal("✔ imprimée", BarcodeBatchLogic.StateColumnText(events[2].State, events[2].CopiesDone, events[2].Copies, rows[events[2].Row]));
        Assert.Equal(2, events[5].Position);
        Assert.Equal(4, events[5].TotalSheets);
    }

    // ------------------------------------------------------------------------------------------------
    // Message après le chargement de la liste
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void LoadStatus_OfNothing_IsAnError()
    {
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(null, null);

        Assert.Equal("La liste n'a pas pu être lue.", text);
        Assert.Equal(BatchTone.Error, tone);
    }

    [Fact]
    public void LoadStatus_OfAReaderError_IsTheReaderMessage_InRed()
    {
        ListReadResult result = Read(Array.Empty<string>(), error: "Le fichier « a.xls » est trop ancien : enregistrez-le en .xlsx / .docx.", source: "fichier « a.xls »");

        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);

        Assert.Equal("Le fichier « a.xls » est trop ancien : enregistrez-le en .xlsx / .docx.", text);
        Assert.Equal(BatchTone.Error, tone);
    }

    [Fact]
    public void LoadStatus_OfAnEmptyResultWithoutMessage_IsAnError()
    {
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(Read(Array.Empty<string>()), null);

        Assert.Equal("Aucune valeur trouvée dans la liste.", text);
        Assert.Equal(BatchTone.Error, tone);
    }

    [Fact]
    public void LoadStatus_OfAGoodList_InvitesToPrint_InGreen()
    {
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A", "B", "C" }), null);

        Assert.Equal("3 codes lus (presse-papiers). Vérifiez la liste, puis cliquez sur « Imprimer tous ».", text);
        Assert.Equal(BatchTone.Success, tone);
    }

    [Fact]
    public void LoadStatus_Singular_AndWithoutSource()
    {
        (string text, _) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A" }, source: "  "), null);

        Assert.StartsWith("1 code lu. Vérifiez", text);
    }

    [Fact]
    public void LoadStatus_WithReaderWarnings_IsOrange_AndShowsThem()
    {
        ListReadResult result = Read(new[] { "A", "B" }, new[] { "2 ligne(s) vide(s) ignorée(s).", "Seule la première feuille a été lue." }, source: "fichier « a.xlsx »");

        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);

        Assert.Equal(
            "2 codes lus (fichier « a.xlsx »). Remarques : 2 ligne(s) vide(s) ignorée(s). Seule la première feuille a été lue.",
            text);
        Assert.Equal(BatchTone.Warning, tone);
    }

    [Fact]
    public void LoadStatus_WithInvalidRows_SaysTheyBlockPrinting()
    {
        var oneInvalid = new BarcodeListStats(5, 1, 0, 0, 0);
        var severalInvalid = new BarcodeListStats(5, 3, 0, 0, 0);

        (string one, BatchTone toneOne) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A" }), oneInvalid);
        (string several, _) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A" }), severalInvalid);

        Assert.Contains("1 ligne est à corriger (en rouge) : elle bloquera l'impression.", one);
        Assert.Equal(BatchTone.Warning, toneOne);
        Assert.Contains("3 lignes sont à corriger (en rouge) : elles bloqueront l'impression.", several);
    }

    [Fact]
    public void LoadStatus_StatsWithoutInvalidRows_DoNotChangeTheMessage()
    {
        var clean = new BarcodeListStats(3, 0, 1, 1, 1);

        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A", "B", "C" }), clean);

        Assert.EndsWith("cliquez sur « Imprimer tous ».", text);
        Assert.Equal(BatchTone.Success, tone);
    }

    [Fact]
    public void LoadStatus_WhilePrintingIsBlocked_ExplainsWhy_InsteadOfInvitingToPrint()
    {
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A", "B" }), null, BatchLogic.LateSendBlockedMessage);

        Assert.Equal("2 codes lus (presse-papiers). " + BatchLogic.LateSendBlockedMessage, text);
        Assert.DoesNotContain("Imprimer tous", text);
        Assert.Equal(BatchTone.Warning, tone);

        (string withRemarks, _) = BarcodeBatchLogic.BuildLoadStatus(
            Read(new[] { "A" }, new[] { "Une remarque." }), null, BatchLogic.LateSendBlockedMessage);
        Assert.StartsWith("1 code lu (presse-papiers). " + BatchLogic.LateSendBlockedMessage + " Remarques : Une remarque.", withRemarks);
    }

    [Fact]
    public void FileFilter_IsNeverEmpty_AndHasPairs()
    {
        string filter = BarcodeBatchLogic.SafeFileFilter();

        Assert.False(string.IsNullOrWhiteSpace(filter));
        Assert.True(filter.Split('|').Length % 2 == 0, "Le filtre d'une boîte « Ouvrir » est une suite de paires « libellé|motif ».");
        Assert.Contains("*.xlsx", filter);
        Assert.Contains("*.docx", filter);
    }

    // ------------------------------------------------------------------------------------------------
    // Glisser-déposer
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Drop_OfNothing_DoesNothing()
    {
        Assert.Equal((null, null), BarcodeBatchLogic.ClassifyDrop(null));
        Assert.Equal((null, null), BarcodeBatchLogic.ClassifyDrop(Array.Empty<string>()));
        Assert.Equal((null, null), BarcodeBatchLogic.ClassifyDrop(new[] { "", "  " }));
    }

    [Fact]
    public void Drop_OfOneFile_IsLoaded()
    {
        (string? path, string? problem) = BarcodeBatchLogic.ClassifyDrop(new[] { @"C:\listes\codes.xlsx" }, _ => false);

        Assert.Equal(@"C:\listes\codes.xlsx", path);
        Assert.Null(problem);
    }

    [Fact]
    public void Drop_OfSeveralItems_AsksForOneFileAtATime()
    {
        (string? path, string? problem) = BarcodeBatchLogic.ClassifyDrop(new[] { "a.txt", "b.txt" }, _ => false);

        Assert.Null(path);
        Assert.Equal("Déposez un seul fichier à la fois (2 éléments déposés).", problem);
    }

    [Fact]
    public void Drop_OfAFolder_IsRefused()
    {
        (string? path, string? problem) = BarcodeBatchLogic.ClassifyDrop(new[] { "dossier" }, _ => true);

        Assert.Null(path);
        Assert.Equal("Déposez un fichier (.txt, .csv, .xlsx ou .docx), pas un dossier.", problem);
    }

    [Fact]
    public void Drop_WhenTheFolderTestFails_TreatsTheItemAsAFile()
    {
        (string? path, string? problem) = BarcodeBatchLogic.ClassifyDrop(new[] { "x.txt" }, _ => throw new UnauthorizedAccessException());

        Assert.Equal("x.txt", path);
        Assert.Null(problem);
    }

    [Fact]
    public void Drop_UsesTheFileSystem_ByDefault()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bbl-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "codes.txt");
        File.WriteAllText(file, "A");
        try
        {
            Assert.Equal((file, (string?)null), BarcodeBatchLogic.ClassifyDrop(new[] { file }));
            Assert.Null(BarcodeBatchLogic.ClassifyDrop(new[] { folder }).Path);
            Assert.NotNull(BarcodeBatchLogic.ClassifyDrop(new[] { folder }).Problem);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Taille des miniatures
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(696, 215, 272, 80, 259, 80)]     // un code-barres imprimé dans une cellule de 88 points de haut
    [InlineData(696, 215, 1000, 1000, 696, 215)] // jamais agrandi
    [InlineData(100, 50, 272, 80, 100, 50)]
    [InlineData(696, 215, 100, 500, 100, 31)]    // limité par la largeur
    [InlineData(30000, 100, 272, 80, 272, 1)]    // très large : au moins 1 point de haut
    [InlineData(0, 215, 272, 80, 1, 1)]
    [InlineData(696, -1, 272, 80, 1, 1)]
    [InlineData(696, 215, 0, 80, 1, 1)]
    [InlineData(696, 215, 272, -5, 1, 1)]
    public void FitSize_KeepsTheProportions_AndNeverEnlarges(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), BarcodeBatchLogic.FitSize(sw, sh, mw, mh));
    }

    [Fact]
    public void FitSize_NeverExceedsTheBox()
    {
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            int sw = random.Next(1, 30000);
            int sh = random.Next(1, 3000);
            int mw = random.Next(1, 1200);
            int mh = random.Next(1, 600);

            (int w, int h) = BarcodeBatchLogic.FitSize(sw, sh, mw, mh);

            Assert.InRange(w, 1, mw);
            Assert.InRange(h, 1, mh);
            Assert.True(w <= sw && h <= sh);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Cache LRU des miniatures
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Cache_RejectsACapacityBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarcodeBatchLruCache<string, Fake>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarcodeBatchLruCache<string, Fake>(-3));
    }

    [Fact]
    public void Cache_KeepsAtMostItsCapacity_AndDisposesTheOldest()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(3);
        var items = Enumerable.Range(0, 5).Select(_ => new Fake()).ToArray();

        for (int i = 0; i < 5; i++)
        {
            cache.Add("k" + i, items[i]);
        }

        Assert.Equal(3, cache.Count);
        Assert.Equal(3, cache.Capacity);
        Assert.False(cache.Contains("k0"));
        Assert.False(cache.Contains("k1"));
        Assert.True(cache.Contains("k4"));
        Assert.True(items[0].Disposed && items[1].Disposed);
        Assert.False(items[2].Disposed || items[3].Disposed || items[4].Disposed);
        Assert.Equal(2, cache.Evictions);
        Assert.Equal(new[] { "k4", "k3", "k2" }, cache.KeysByRecency());
    }

    [Fact]
    public void Cache_ReadingAnItem_MakesItTheMostRecent_SoItSurvives()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(3);
        var a = new Fake();
        var b = new Fake();
        var c = new Fake();
        var d = new Fake();
        cache.Add("a", a);
        cache.Add("b", b);
        cache.Add("c", c);

        Assert.True(cache.TryGet("a", out Fake? found));
        Assert.Same(a, found);
        cache.Add("d", d); // b était le plus ancien

        Assert.True(b.Disposed);
        Assert.False(a.Disposed);
        Assert.Equal(new[] { "d", "a", "c" }, cache.KeysByRecency());
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Cache_GetOrAdd_BuildsOnlyOnce_AndCountsHitsAndMisses()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(4);
        int built = 0;
        Fake Build(string key)
        {
            built++;
            return new Fake();
        }

        Fake first = cache.GetOrAdd("x", Build);
        Fake second = cache.GetOrAdd("x", Build);
        Fake other = cache.GetOrAdd("y", Build);

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, built);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void Cache_GetOrAdd_TheItemJustBuilt_IsNeverEvictedByItsOwnInsertion()
    {
        using var cache = new BarcodeBatchLruCache<int, Fake>(1);
        Fake first = cache.GetOrAdd(1, _ => new Fake());

        Fake second = cache.GetOrAdd(2, _ => new Fake());

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains(2));
    }

    [Fact]
    public void Cache_GetOrAdd_WhenTheFactoryFails_NothingIsCached()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(2);
        cache.Add("keep", new Fake());

        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd("boom", _ => throw new InvalidOperationException("échec")));

        Assert.Equal(1, cache.Count);
        Assert.False(cache.Contains("boom"));
        Assert.True(cache.Contains("keep"));
    }

    [Fact]
    public void Cache_AddingAnExistingKey_ReplacesAndDisposesTheOldValue()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(3);
        var old = new Fake();
        var fresh = new Fake();
        cache.Add("k", old);

        cache.Add("k", fresh);

        Assert.Equal(1, cache.Count);
        Assert.True(old.Disposed);
        Assert.False(fresh.Disposed);
        Assert.True(cache.TryGet("k", out Fake? now));
        Assert.Same(fresh, now);
    }

    [Fact]
    public void Cache_AddingTheSameInstanceAgain_DoesNotDisposeIt()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(3);
        var item = new Fake();
        cache.Add("k", item);

        cache.Add("k", item);

        Assert.Equal(1, cache.Count);
        Assert.False(item.Disposed);
    }

    [Fact]
    public void Cache_RemoveAndClear_DisposeEverythingExactlyOnce()
    {
        var cache = new BarcodeBatchLruCache<string, Fake>(5);
        var items = Enumerable.Range(0, 4).Select(_ => new Fake()).ToArray();
        for (int i = 0; i < 4; i++)
        {
            cache.Add("k" + i, items[i]);
        }

        Assert.True(cache.Remove("k1"));
        Assert.False(cache.Remove("k1"));
        Assert.False(cache.Remove("absent"));
        Assert.Equal(1, items[1].DisposeCount);
        Assert.Equal(3, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.All(items, item => Assert.Equal(1, item.DisposeCount));

        cache.Dispose();
        cache.Dispose(); // idempotent
        Assert.All(items, item => Assert.Equal(1, item.DisposeCount));
    }

    [Fact]
    public void Cache_AfterDispose_RefusesNewItems_ByDisposingThem()
    {
        var cache = new BarcodeBatchLruCache<string, Fake>(2);
        cache.Dispose();
        var late = new Fake();

        cache.Add("k", late);

        Assert.Equal(0, cache.Count);
        Assert.True(late.Disposed);
    }

    [Fact]
    public void Cache_ADisposeThatThrows_DoesNotBreakTheCache()
    {
        using var cache = new BarcodeBatchLruCache<string, BrokenDisposable>(1);
        cache.Add("a", new BrokenDisposable());

        cache.Add("b", new BrokenDisposable()); // évince « a », dont la libération échoue

        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains("b"));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    private sealed class BrokenDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("libération impossible");
    }

    [Fact]
    public void Cache_UsesTheGivenComparer()
    {
        using var cache = new BarcodeBatchLruCache<string, Fake>(3, StringComparer.OrdinalIgnoreCase);
        cache.Add("ABC", new Fake());

        Assert.True(cache.TryGet("abc", out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cache_WorksWithValuesThatAreNotDisposable()
    {
        using var cache = new BarcodeBatchLruCache<int, string>(2);
        cache.Add(1, "un");
        cache.Add(2, "deux");
        cache.Add(3, "trois");

        Assert.False(cache.Contains(1));
        Assert.True(cache.TryGet(3, out string? three));
        Assert.Equal("trois", three);
    }

    [Fact]
    public void Cache_UnderHeavyUse_NeverExceedsItsCapacity_AndLeaksNothing()
    {
        const int capacity = 96;
        using var cache = new BarcodeBatchLruCache<string, Fake>(capacity, StringComparer.Ordinal);
        var created = new List<Fake>();
        var random = new Random(99);

        for (int i = 0; i < 20_000; i++)
        {
            string key = "k" + random.Next(0, 400);
            cache.GetOrAdd(key, _ =>
            {
                var item = new Fake();
                created.Add(item);
                return item;
            });
            Assert.True(cache.Count <= capacity);
        }

        int alive = created.Count(f => !f.Disposed);
        Assert.Equal(cache.Count, alive);           // tout ce qui n'est plus dans le cache a été libéré
        Assert.All(created, f => Assert.True(f.DisposeCount <= 1));
    }

    [Fact]
    public void TheDefaultCacheCapacity_CoversTheVisibleRowsWithRoomToSpare()
    {
        // 88 points par ligne : même sur un écran 4K à 200 % (2160 points de haut) on ne voit pas plus de ~13 lignes.
        Assert.True(BarcodeBatchLogic.ThumbnailCacheCapacity >= 64);
        Assert.True(BarcodeBatchLogic.ThumbnailCacheCapacity <= 512, "Chaque miniature occupe de la mémoire : le cache doit rester modeste.");
    }

    // ------------------------------------------------------------------------------------------------
    // Intégration : une série complète, arrêt puis reprise
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullSeries_StopAndResume_PrintsEveryCodeOnce_InOrder()
    {
        string[] texts = Enumerable.Range(1, 6).Select(i => "CODE-" + i).ToArray();
        List<BarcodeRowAnalysis> analyses = BarcodeBatchLogic.AnalyzeAll(texts, new AppSettings());
        var printed = new List<string>();
        var states = Enumerable.Repeat(RowState.Pending, texts.Length).ToArray();

        void Apply(BatchEvent e) => states[e.Row] = e.State;
        Task Print(int row, int copy)
        {
            printed.Add(texts[row]);
            return Task.CompletedTask;
        }

        // Première série : arrêt demandé après le 3e code.
        IReadOnlyList<int> rows = BatchLogic.ChooseRows(PrintMode.All, texts.Length, Array.Empty<int>());
        Assert.Null(BarcodeBatchLogic.FindInvalidRow(analyses, rows));
        BatchOutcome first = await BatchLogic.Run(rows, 1, Print, () => printed.Count >= 3, Apply, 5000);

        Assert.Equal(BatchEnd.Stopped, first.End);
        Assert.Equal(new[] { "CODE-1", "CODE-2", "CODE-3" }, printed);
        Assert.Equal(3, BatchLogic.CountDone(states, rows));
        Assert.Equal(3, first.ResumeRow);

        // Reprise : « Imprimer à partir de la sélection » depuis la ligne 3, rien n'est refait.
        IReadOnlyList<int> rest = BatchLogic.ChooseRows(PrintMode.FromSelection, texts.Length, new[] { first.ResumeRow!.Value });
        BatchOutcome second = await BatchLogic.Run(rest, 1, Print, () => false, Apply, 5000);

        Assert.Equal(BatchEnd.Completed, second.End);
        Assert.Equal(texts, printed);
        Assert.All(states, s => Assert.Equal(RowState.Done, s));

        // « Imprimer tout » après coup : toutes les lignes sont déjà imprimées, la logique le détecte.
        Assert.Equal(6, BatchLogic.CountDone(states, rows));
        Assert.Empty(BatchLogic.ExcludeDone(states, rows));
    }

    // ------------------------------------------------------------------------------------------------
    // Avec le vrai lecteur de listes (ListFileReader)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRealReader_FeedsTheAnalysis_TheSummary_AndTheLoadStatus()
    {
        ListReadResult result = ListFileReader.ReadText("Réf\r\nAAA-1\r\n\r\nBBB-2\r\nRéf 3\r\nAAA-1\r\n", skipFirstLine: true);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "AAA-1", "BBB-2", "Réf 3", "AAA-1" }, result.Values);

        List<BarcodeRowAnalysis> rows = BarcodeBatchLogic.AnalyzeAll(result.Values.ToList(), new AppSettings());
        BarcodeListStats stats = BarcodeBatchLogic.ComputeStats(rows);
        (string status, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, stats);

        Assert.Equal(1, stats.Invalid);
        Assert.Equal(1, stats.Duplicates);
        Assert.Equal("4 codes-barres — 1 invalide, 1 doublon", BarcodeBatchLogic.BuildSummary(rows));
        Assert.StartsWith("4 codes lus (presse-papiers).", status);
        Assert.Contains("1 ligne est à corriger (en rouge)", status);
        Assert.Equal(BatchTone.Warning, tone);
        Assert.Equal(BarcodeRowSeverity.Invalid, rows[2].Severity);
    }

    [Fact]
    public void TheRealReader_ANormalPaste_GivesAGreenStatus()
    {
        ListReadResult result = ListFileReader.ReadText("ABC-1\r\nABC-2\r\nABC-3\r\n", skipFirstLine: false);
        List<BarcodeRowAnalysis> rows = BarcodeBatchLogic.AnalyzeAll(result.Values.ToList(), new AppSettings());

        (string status, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, BarcodeBatchLogic.ComputeStats(rows));

        Assert.Equal("3 codes lus (presse-papiers). Vérifiez la liste, puis cliquez sur « Imprimer tous ».", status);
        Assert.Equal(BatchTone.Success, tone);
    }

    [Fact]
    public void TheRealReader_AnEmptyPaste_GivesTheReaderError()
    {
        ListReadResult result = ListFileReader.ReadText("   \r\n  ", skipFirstLine: false);

        (string status, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);

        Assert.NotNull(result.Error);
        Assert.Equal(result.Error, status);
        Assert.Equal(BatchTone.Error, tone);
    }

    [Fact]
    public void TheRealReader_ATextFileOnDisk_IsLoadedWithItsNameInTheStatus()
    {
        string folder = Path.Combine(Path.GetTempPath(), "bbl-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "codes.txt");
        File.WriteAllText(file, "Référence\r\nAAA-1\r\nBBB-2\r\n", new System.Text.UTF8Encoding(false));
        try
        {
            (string? dropped, string? problem) = BarcodeBatchLogic.ClassifyDrop(new[] { file });
            Assert.Null(problem);
            ListReadResult result = ListFileReader.ReadFile(dropped!, skipFirstLine: true);
            (string status, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);

            Assert.Equal(new[] { "AAA-1", "BBB-2" }, result.Values);
            Assert.Equal("Référence", result.SkippedTitle);
            Assert.Equal("2 codes lus (fichier « codes.txt »). Titre ignoré : « Référence ». Vérifiez la liste, puis cliquez sur « Imprimer tous ».", status);
            Assert.Equal(BatchTone.Success, tone);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheRealReader_AMissingOrLegacyFile_IsAnErrorStatus_NeverAnException()
    {
        ListReadResult missing = ListFileReader.ReadFile(Path.Combine(Path.GetTempPath(), "bbl-absent-" + Guid.NewGuid().ToString("N") + ".txt"), false);
        ListReadResult legacy = ListFileReader.ReadFile(Path.Combine(Path.GetTempPath(), "ancien.xls"), false);

        foreach (ListReadResult result in new[] { missing, legacy })
        {
            (string status, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Equal(result.Error, status);
            Assert.Equal(BatchTone.Error, tone);
        }

        Assert.Contains(".xlsx", legacy.Error);
    }

    [Fact]
    public void TheRealReader_FiveThousandPastedLines_AreReadAndAnalysedFast()
    {
        string text = string.Join("\r\n", Enumerable.Range(0, 5200).Select(i => "REF-" + (10000 + i))) + "\r\n";

        var watch = Stopwatch.StartNew();
        ListReadResult result = ListFileReader.ReadText(text, skipFirstLine: false);
        List<BarcodeRowAnalysis> rows = BarcodeBatchLogic.AnalyzeAll(result.Values.ToList(), new AppSettings());
        (string status, _) = BarcodeBatchLogic.BuildLoadStatus(result, BarcodeBatchLogic.ComputeStats(rows));
        watch.Stop();

        Assert.Equal(ListFileReader.MaxValues, result.Values.Count);
        Assert.Equal(ListFileReader.MaxValues, rows.Count);
        Assert.Contains("5000", status);
        Assert.NotEmpty(result.Warnings);
        Assert.True(watch.ElapsedMilliseconds < 3000, "Lecture et analyse de 5000 lignes : " + watch.ElapsedMilliseconds + " ms.");
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : titre ignoré nommé dans le message de chargement
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void LoadStatus_NamesTheIgnoredTitle_AndStaysGreen()
    {
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(
            Read(new[] { "A", "B" }, source: "fichier « a.xlsx »", skippedTitle: "Référence"), null);

        Assert.Equal("2 codes lus (fichier « a.xlsx »). Titre ignoré : « Référence ». Vérifiez la liste, puis cliquez sur « Imprimer tous ».", text);
        Assert.Equal(BatchTone.Success, tone);
    }

    [Fact]
    public void LoadStatus_ShortensALongIgnoredTitle_AndKeepsItBeforeTheRemarks()
    {
        string title = "Référence interne du produit au catalogue 2024";
        (string text, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(
            Read(new[] { "A" }, new[] { "1 ligne(s) vide(s) ignorée(s)." }, skippedTitle: title), null);

        Assert.StartsWith("1 code lu (presse-papiers). Titre ignoré : « Référence interne du pr… ». Remarques : 1 ligne(s) vide(s) ignorée(s).", text);
        Assert.DoesNotContain("catalogue", text);
        Assert.Equal(BatchTone.Warning, tone);
    }

    [Fact]
    public void LoadStatus_WithoutAnIgnoredTitle_SaysNothingAboutIt()
    {
        (string text, _) = BarcodeBatchLogic.BuildLoadStatus(Read(new[] { "A" }, skippedTitle: "   "), null);

        Assert.DoesNotContain("Titre ignoré", text);
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : reprise après une série interrompue sur « Imprimer la sélection »
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void RemainingRows_StartAtTheResumeRowInTheOrderOfTheSeries()
    {
        var series = new[] { 2, 9, 26 };

        Assert.Equal(new[] { 9, 26 }, BarcodeBatchLogic.RemainingRows(series, 9));
        Assert.Equal(new[] { 2, 9, 26 }, BarcodeBatchLogic.RemainingRows(series, 2));
        Assert.Equal(new[] { 26 }, BarcodeBatchLogic.RemainingRows(series, 26));
        Assert.Equal(new[] { 5 }, BarcodeBatchLogic.RemainingRows(series, 5));        // ligne étrangère à la série : elle seule
        Assert.Equal(new[] { 0 }, BarcodeBatchLogic.RemainingRows(Array.Empty<int>(), 0));
    }

    [Fact]
    public async Task Outcome_StoppedAfterPrintSelection_ReSelectsTheSelectionInsteadOfPrintingTheWholeTail()
    {
        int sent = 0;
        BatchOutcome outcome = await Run(3, 1, (_, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, stop: () => sent >= 1);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, PrintMode.Selection, out BatchTone tone);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.Equal(
            "Impression arrêtée : 1 sur 3 codes-barres envoyés à « P ». Pour reprendre, cliquez sur « Imprimer la sélection » "
            + "(la ligne 2 et les suivantes de votre sélection sont déjà re-sélectionnées).",
            message);
        Assert.DoesNotContain("Imprimer à partir de la sélection", message);
    }

    [Fact]
    public async Task Outcome_FailedAfterPrintSelection_DoesNotSendTheUserToTheWholeTail()
    {
        BatchOutcome outcome = await Run(3, 1, (row, _) =>
            row == 1 ? Task.FromException(new InvalidOperationException("Plus de papier")) : Task.CompletedTask);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, PrintMode.Selection, out BatchTone tone);

        Assert.Equal(BatchTone.Error, tone);
        Assert.Contains("Corrigez le problème, puis cliquez sur « Imprimer la sélection » (la ligne 2 et les suivantes de votre sélection sont déjà re-sélectionnées).", message);
        Assert.DoesNotContain("Imprimer à partir de la sélection", message);
    }

    [Fact]
    public async Task Outcome_TimedOutAfterPrintSelection_TellsToUnselectTheRowIfItsLabelCameOut()
    {
        var never = new TaskCompletionSource<bool>();
        BatchOutcome outcome = await Run(3, 1, (row, _) => row == 1 ? never.Task : Task.CompletedTask, timeoutMs: 50);

        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, PrintMode.Selection, out _);

        Assert.Contains("cliquez sur « Imprimer la sélection » (la ligne 2 et les suivantes de votre sélection sont déjà re-sélectionnées ; "
                        + "désélectionnez la ligne 2 si son étiquette est sortie).", message);
        Assert.DoesNotContain("Imprimer à partir de la sélection", message);
        never.SetResult(true);
    }

    [Theory]
    [InlineData(false)]   // PrintMode.All
    [InlineData(true)]    // PrintMode.FromSelection
    public async Task Outcome_OtherModes_KeepTheFromSelectionInstruction(bool fromSelection)
    {
        PrintMode mode = fromSelection ? PrintMode.FromSelection : PrintMode.All;
        int sent = 0;
        BatchOutcome outcome = await Run(3, 1, (_, _) =>
        {
            sent++;
            return Task.CompletedTask;
        }, stop: () => sent >= 1);

        string withMode = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, mode, out _);
        string withoutMode = BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out _);

        Assert.Equal(withoutMode, withMode);
        Assert.Contains("sélectionnez la ligne 2 et cliquez sur « Imprimer à partir de la sélection »", withMode);
    }

    [Fact]
    public async Task Outcome_CompletedAfterPrintSelection_IsUnchanged()
    {
        BatchOutcome outcome = await Run(2, 1, (_, _) => Task.CompletedTask);

        Assert.Equal(
            BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, out _),
            BarcodeBatchLogic.BuildOutcomeMessage(outcome, Texts, 1, "P", 60_000, null, PrintMode.Selection, out _));
    }
}
