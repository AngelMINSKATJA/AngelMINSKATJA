using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Logique de la fenêtre « Étiquettes en série » (BatchLogic) : résumé, textes, validation des lignes, choix des lignes,
/// état des boutons et déroulement d'une série (arrêt, échec, délai, pause, reprise). Tests purs : ni interface ni imprimante,
/// ils tournent aussi sous Linux. Le test de la fenêtre elle-même est dans BatchFormSmokeTests (Windows).
/// </summary>
public class BatchFormTests
{
    // ------------------------------------------------------------------------------------------------
    // Outils
    // ------------------------------------------------------------------------------------------------

    private static LabelItem I(string type, string plan) => new(type, plan);

    private static List<LabelItem> Items(params (string Type, string Plan)[] items)
        => items.Select(i => new LabelItem(i.Type, i.Plan)).ToList();

    private static List<LabelItem> Many(string type, int count, int firstPlan = 22000)
        => Enumerable.Range(0, count).Select(i => new LabelItem(type, (firstPlan + i).ToString())).ToList();

    private static int[] Rows(int count) => Enumerable.Range(0, count).ToArray();

    // ------------------------------------------------------------------------------------------------
    // Résumé sous la liste
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Summary_MatchesTheExampleOfTheSpecification()
    {
        var items = new List<LabelItem>();
        items.AddRange(Many("HB1", 44));
        items.AddRange(Many("HB2", 19, 22110));
        items.AddRange(Many("VF1", 12, 22148));
        items.AddRange(Many("VF2", 13, 22168));

        Assert.Equal("88 étiquettes — HB1 : 44, HB2 : 19, VF1 : 12, VF2 : 13", BatchLogic.BuildSummary(items));
    }

    [Fact]
    public void Summary_OfAnEmptyList_ExplainsHowToStart()
    {
        Assert.Equal(BatchLogic.NoLabelsText, BatchLogic.BuildSummary(new List<LabelItem>()));
        Assert.Contains("Excel", BatchLogic.NoLabelsText);
    }

    [Fact]
    public void Summary_OfOneLabel_IsSingular()
    {
        Assert.Equal("1 étiquette — HB1 : 1", BatchLogic.BuildSummary(Items(("HB1", "22061"))));
    }

    [Fact]
    public void Summary_ListsTypesInOrderOfFirstAppearance_AndGroupsThemIgnoringCase()
    {
        string text = BatchLogic.BuildSummary(Items(("VF1", "1"), ("hb1", "2"), ("VF1 ", "3"), ("HB1", "4"), ("HB2", "5")));

        Assert.Equal("5 étiquettes — VF1 : 2, hb1 : 2, HB2 : 1", text);
    }

    [Fact]
    public void Summary_NamesAnEmptyType()
    {
        string text = BatchLogic.BuildSummary(Items(("", "22061"), ("  ", "22062"), ("HB1", "22063")));

        Assert.Equal("3 étiquettes — " + BatchLogic.EmptyTypeName + " : 2, HB1 : 1", text);
    }

    [Fact]
    public void Summary_WithSeveralCopies_ShowsTheNumberOfPrints()
    {
        string text = BatchLogic.BuildSummary(Items(("HB1", "22061"), ("HB1", "22062"), ("HB2", "22063")), copies: 2);

        Assert.Equal("3 étiquettes x 2 copies = 6 impressions — HB1 : 2, HB2 : 1", text);
    }

    [Theory]
    [InlineData(0, "1 étiquette — HB1 : 1")]
    [InlineData(-5, "1 étiquette — HB1 : 1")]
    [InlineData(1, "1 étiquette — HB1 : 1")]
    [InlineData(99, "1 étiquette x 99 copies = 99 impressions — HB1 : 1")]
    [InlineData(1000, "1 étiquette x 99 copies = 99 impressions — HB1 : 1")]
    public void Summary_ClampsTheNumberOfCopies(int copies, string expected)
    {
        Assert.Equal(expected, BatchLogic.BuildSummary(Items(("HB1", "22061")), copies));
    }

    [Fact]
    public void Summary_WithManyTypes_ListsTheFirstOnesAndCountsTheOthers()
    {
        List<LabelItem> items = Enumerable.Range(1, 15).Select(i => I("T" + i, "100" + i)).ToList();

        string text = BatchLogic.BuildSummary(items);

        Assert.StartsWith("15 étiquettes — T1 : 1, T2 : 1,", text);
        Assert.Contains("T12 : 1", text);
        Assert.DoesNotContain("T13", text);
        Assert.EndsWith(", … (3 autres types)", text);
    }

    [Fact]
    public void Summary_OfAParsedExcelTable_CountsOneLabelPerCross()
    {
        PlanTableParseResult parsed = PlanTableParser.Parse(
            "Plan SE\tHB1 (20)\tHBA (20)\tHB2 (8)\r\n"
            + "2020Q7E 22061\tX\t\t\r\n"
            + "2020Q7E 22120\tX\t\t\r\n"
            + "2020Q7E 22120\t\t\tX\r\n"
            + "2020Q7E 22110\t\t\tX\r\n");

        Assert.Null(parsed.Error);
        Assert.Equal("4 étiquettes — HB1 : 2, HB2 : 2", BatchLogic.BuildSummary(parsed.Items));
    }

    // ------------------------------------------------------------------------------------------------
    // Texte de la colonne « État » et de la progression
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Pending", 0, 1, "")]
    [InlineData("Printing", 0, 1, "en cours…")]
    [InlineData("Done", 0, 1, "✔ imprimée")]
    [InlineData("Done", 3, 3, "✔ imprimée")]
    [InlineData("Failed", 0, 1, "✖ échec")]
    [InlineData("Failed", 0, 3, "✖ échec")]
    [InlineData("Failed", 1, 3, "✖ échec (1/3 copies)")]
    [InlineData("Uncertain", 0, 1, "? à vérifier")]
    [InlineData("Uncertain", 2, 3, "? à vérifier (2/3 copies)")]
    [InlineData("Partial", 0, 1, "arrêtée")]
    [InlineData("Partial", 2, 3, "arrêtée (2/3 copies)")]
    public void StateText_IsWhatTheGridShows(string stateName, int copiesDone, int copies, string expected)
    {
        Assert.Equal(expected, BatchLogic.StateText(Enum.Parse<RowState>(stateName), copiesDone, copies));
    }

    [Fact]
    public void StateText_OfAnUncertainRow_StartsWithAQuestionMark()
    {
        Assert.StartsWith("?", BatchLogic.StateText(RowState.Uncertain));
    }

    [Fact]
    public void ProgressText_NamesTheLabelBeingPrinted()
    {
        Assert.Equal("Étiquette 12 / 88 (HB1 22072)…", BatchLogic.BuildProgressText(12, 88, 1, 1, "HB1", "22072"));
    }

    [Fact]
    public void ProgressText_WithSeveralCopies_ShowsTheCopyNumber()
    {
        Assert.Equal(
            "Étiquette 2 / 5 (VF2 22170), copie 3 / 4…",
            BatchLogic.BuildProgressText(2, 5, 3, 4, " VF2 ", " 22170 "));
    }

    // ------------------------------------------------------------------------------------------------
    // Validation des lignes
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void FindInvalidRow_OfAValidList_IsNull()
    {
        List<LabelItem> items = Items(("HB1", "22061"), ("VF2", "22170"), ("Type spécial", "A-12/B"));

        Assert.Null(BatchLogic.FindInvalidRow(items, Rows(items.Count)));
    }

    [Fact]
    public void FindInvalidRow_ReportsTheFirstBadRow_WithItsNumberOnScreen()
    {
        List<LabelItem> items = Items(("HB1", "22061"), ("HB1", "22062"), ("HB2", ""), ("", "22064"));

        RowProblem? problem = BatchLogic.FindInvalidRow(items, Rows(items.Count));

        Assert.NotNull(problem);
        Assert.Equal(2, problem!.Row);
        Assert.Equal(LabelField.Plan, problem.Field);
        Assert.StartsWith("Ligne 3 : ", problem.Message);
        Assert.Contains("N° de plan", problem.Message);
        Assert.Contains("vide", problem.Message);
    }

    [Fact]
    public void FindInvalidRow_ChecksTheTypeBeforeThePlan_OfTheSameRow()
    {
        RowProblem? problem = BatchLogic.FindInvalidRow(Items(("", "")), new[] { 0 });

        Assert.NotNull(problem);
        Assert.Equal(LabelField.Type, problem!.Field);
        Assert.Contains("Type de châssis", problem.Message);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FindInvalidRow_RejectsBlankFields(string blank)
    {
        Assert.NotNull(BatchLogic.FindInvalidRow(Items(("HB1", blank)), new[] { 0 }));
        Assert.NotNull(BatchLogic.FindInvalidRow(Items((blank, "22061")), new[] { 0 }));
    }

    [Fact]
    public void FindInvalidRow_RejectsATooLongPlan()
    {
        RowProblem? problem = BatchLogic.FindInvalidRow(Items(("HB1", new string('9', 31))), new[] { 0 });

        Assert.NotNull(problem);
        Assert.Equal(LabelField.Plan, problem!.Field);
        Assert.Contains("trop long", problem.Message);
        Assert.Null(BatchLogic.FindInvalidRow(Items(("HB1", new string('9', 30))), new[] { 0 }));
    }

    [Fact]
    public void FindInvalidRow_RejectsControlCharacters()
    {
        RowProblem? problem = BatchLogic.FindInvalidRow(Items(("HB1", "22\u000761")), new[] { 0 });

        Assert.NotNull(problem);
        Assert.Equal(LabelField.Plan, problem!.Field);
    }

    [Fact]
    public void FindInvalidRow_OnlyLooksAtTheRowsToPrint()
    {
        List<LabelItem> items = Items(("", "22061"), ("HB1", "22062"), ("HB1", "22063"));

        Assert.Null(BatchLogic.FindInvalidRow(items, new[] { 1, 2 }));
        Assert.Equal(0, BatchLogic.FindInvalidRow(items, new[] { 2, 0 })!.Row);
        Assert.Null(BatchLogic.FindInvalidRow(items, new[] { -1, 3, 99 }));
        Assert.Null(BatchLogic.FindInvalidRow(items, Array.Empty<int>()));
    }

    // ------------------------------------------------------------------------------------------------
    // Choix des lignes
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ChooseRows_All_IsEveryRow()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, BatchLogic.ChooseRows(PrintMode.All, 4, new[] { 2 }));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.All, 0, Array.Empty<int>()));
    }

    [Fact]
    public void ChooseRows_Selection_IsSortedWithoutDuplicates_AndIgnoresUnknownRows()
    {
        Assert.Equal(new[] { 1, 3, 4 }, BatchLogic.ChooseRows(PrintMode.Selection, 5, new[] { 4, 1, 3, 1, 9, -2 }));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.Selection, 5, Array.Empty<int>()));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.Selection, 0, new[] { 0 }));
    }

    [Fact]
    public void ChooseRows_FromSelection_GoesFromTheFirstSelectedRowToTheEnd()
    {
        Assert.Equal(new[] { 2, 3, 4 }, BatchLogic.ChooseRows(PrintMode.FromSelection, 5, new[] { 4, 2 }));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, BatchLogic.ChooseRows(PrintMode.FromSelection, 5, new[] { 0 }));
        Assert.Equal(new[] { 4 }, BatchLogic.ChooseRows(PrintMode.FromSelection, 5, new[] { 4 }));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.FromSelection, 5, Array.Empty<int>()));
        Assert.Empty(BatchLogic.ChooseRows(PrintMode.FromSelection, 5, new[] { 7 }));
    }

    [Fact]
    public void NothingToPrintMessage_DependsOnTheMode()
    {
        Assert.Contains("collez", BatchLogic.NothingToPrintMessage(PrintMode.All));
        Assert.Contains("Sélectionnez", BatchLogic.NothingToPrintMessage(PrintMode.Selection));
        Assert.Contains("Sélectionnez", BatchLogic.NothingToPrintMessage(PrintMode.FromSelection));
    }

    [Fact]
    public void NextRowToPrint_IsTheFirstRowNotYetDone()
    {
        var states = new[] { RowState.Done, RowState.Done, RowState.Failed, RowState.Pending };

        Assert.Equal(2, BatchLogic.NextRowToPrint(states));
        Assert.Equal(3, BatchLogic.NextRowToPrint(states, 3));
        Assert.Null(BatchLogic.NextRowToPrint(new[] { RowState.Done, RowState.Done }));
        Assert.Null(BatchLogic.NextRowToPrint(Array.Empty<RowState>()));
        Assert.Equal(1, BatchLogic.NextRowToPrint(new[] { RowState.Done, RowState.Uncertain }, -4));
    }

    // ------------------------------------------------------------------------------------------------
    // Boutons
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Buttons_OfAnEmptyList_OnlyAllowPasting()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(0, 0, false, false, false);

        Assert.True(s.Paste);
        Assert.False(s.Clear);
        Assert.False(s.PrintAll);
        Assert.False(s.PrintSelection);
        Assert.False(s.PrintFromSelection);
        Assert.False(s.Stop);
        Assert.True(s.Copies && s.Printer && s.Confirm && s.GridEditable);
    }

    [Fact]
    public void Buttons_WithRowsButNoSelection_AllowPrintAllOnly()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(88, 0, false, false, false);

        Assert.True(s.PrintAll);
        Assert.True(s.Clear);
        Assert.False(s.PrintSelection);
        Assert.False(s.PrintFromSelection);
    }

    [Fact]
    public void Buttons_WithASelection_AllowEveryPrintCommand()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(88, 3, false, false, false);

        Assert.True(s.PrintAll && s.PrintSelection && s.PrintFromSelection);
        Assert.False(s.Stop);
    }

    [Fact]
    public void Buttons_WhilePrinting_OnlyStopIsAvailable()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(88, 3, true, false, false);

        Assert.True(s.Stop);
        Assert.False(s.Paste || s.Clear || s.Copies || s.Printer || s.Confirm);
        Assert.False(s.PrintAll || s.PrintSelection || s.PrintFromSelection);
        Assert.False(s.GridEditable);
    }

    [Fact]
    public void Buttons_OnceStopIsRequested_StopIsGrayedOut()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(88, 3, true, true, false);

        Assert.False(s.Stop);
        Assert.False(s.PrintAll);
    }

    [Fact]
    public void Buttons_WhileAnEarlierSendIsStillPending_NothingCanBePrinted_ButTheListCanBePrepared()
    {
        BatchButtonStates s = BatchLogic.ComputeButtons(88, 3, false, false, true);

        Assert.False(s.PrintAll || s.PrintSelection || s.PrintFromSelection);
        Assert.False(s.Stop);
        Assert.True(s.Paste && s.Clear && s.GridEditable);
    }

    // ------------------------------------------------------------------------------------------------
    // Confirmation, collage
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(88, 1, "Imprimer 88 étiquettes sur « Brother QL-800 » ?")]
    [InlineData(2, 1, "Imprimer 2 étiquettes sur « Brother QL-800 » ?")]
    [InlineData(1, 3, "Imprimer 1 étiquette en 3 copies sur « Brother QL-800 » ?")]
    [InlineData(88, 2, "Imprimer 88 étiquettes en 2 copies (176 impressions au total) sur « Brother QL-800 » ?")]
    public void ConfirmText_NamesTheCountAndThePrinter(int labels, int copies, string expected)
    {
        Assert.Equal(expected, BatchLogic.BuildConfirmText(labels, copies, "Brother QL-800"));
    }

    [Fact]
    public void PasteStatus_OfAnError_IsTheParserMessage()
    {
        PlanTableParseResult result = PlanTableParser.Parse("");

        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result);

        Assert.Equal(BatchTone.Error, tone);
        Assert.Equal(result.Error, text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void PasteStatus_OfAGoodTable_IsGreenAndCountsTheLabels()
    {
        PlanTableParseResult result = PlanTableParser.Parse("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n");

        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result);

        Assert.Equal(BatchTone.Success, tone);
        Assert.StartsWith("2 étiquettes lues dans le tableau.", text);
        Assert.Contains("Imprimer tout", text);
    }

    [Fact]
    public void PasteStatus_WithWarnings_IsOrangeAndShowsThem()
    {
        PlanTableParseResult result = PlanTableParser.Parse(
            "Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\r\n");

        Assert.NotEmpty(result.Warnings);
        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.StartsWith("1 étiquette lue dans le tableau.", text);
        foreach (string warning in result.Warnings)
        {
            Assert.Contains(warning.Trim(), text);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Message final
    // ------------------------------------------------------------------------------------------------

    private static BatchOutcome Outcome(
        BatchEnd end, int rowsDone, int sheetsDone, int totalRows, int copies = 1, int? resume = null, int resumeCopies = 0,
        string? error = null, Task? orphan = null)
        => new(end, rowsDone, sheetsDone, totalRows, totalRows * copies, resume, resumeCopies, error, orphan);

    [Fact]
    public void Message_WhenCompleted_IsGreen()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Completed, 88, 88, 88), Many("HB1", 88), 1, "Brother QL-800", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchTone.Success, tone);
        Assert.Equal("✔ 88 étiquettes envoyées à « Brother QL-800 ».", text);
    }

    [Fact]
    public void Message_WhenCompletedWithCopies_MentionsThePrints()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Completed, 88, 176, 88, copies: 2), Many("HB1", 88), 2, "QL", 60_000, null, out _);

        Assert.Equal("✔ 88 étiquettes envoyées (176 impressions) à « QL ».", text);
    }

    [Fact]
    public void Message_WhenOneLabelIsCompleted_IsSingular()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Completed, 1, 1, 1), Many("HB1", 1), 1, "QL", 60_000, null, out _);

        Assert.Equal("✔ 1 étiquette envoyée à « QL ».", text);
    }

    [Fact]
    public void Message_WhenStopped_SaysHowToResume()
    {
        List<LabelItem> items = Many("HB1", 88);

        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Stopped, 12, 12, 88, resume: 12), items, 1, "QL", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.StartsWith("Impression arrêtée : 12 sur 88 étiquettes envoyées à « QL ».", text);
        Assert.Contains("sélectionnez la ligne 13 et cliquez sur « Imprimer à partir de la sélection »", text);
    }

    [Fact]
    public void Message_WhenStoppedBetweenCopies_WarnsAboutTheDuplicate()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Stopped, 2, 8, 5, copies: 3, resume: 2, resumeCopies: 2), Many("HB1", 5), 3, "QL", 60_000, null, out _);

        Assert.Contains("2 copies sur 3", text);
        Assert.Contains("ligne 3", text);
    }

    [Fact]
    public void Message_WhenFailed_NamesTheRowAndTheReason()
    {
        List<LabelItem> items = Items(("HB1", "22061"), ("HB1", "22062"), ("HB2", "22063"));

        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Failed, 2, 2, 3, resume: 2, error: "L'imprimante est hors connexion"),
            items, 1, "QL", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchTone.Error, tone);
        Assert.StartsWith("Échec à la ligne 3 (HB2 22063) : L'imprimante est hors connexion.", text);
        Assert.Contains("2 sur 3 étiquettes envoyées", text);
        Assert.Contains("sélectionnez la ligne 3 et cliquez sur « Imprimer à partir de la sélection »", text);
    }

    [Fact]
    public void Message_WhenFailed_DoesNotDoubleTheFinalPeriod()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Failed, 0, 0, 3, resume: 0, error: "Rien n'a été envoyé."), Many("HB1", 3), 1, "QL", 60_000, null, out _);

        Assert.Contains("Rien n'a été envoyé. 0 sur 3", text);
        Assert.DoesNotContain("..", text);
    }

    [Fact]
    public void Message_WhenTimedOut_TellsToCheckThePrintQueue()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.TimedOut, 4, 4, 10, resume: 4), Many("VF1", 10), 1, "QL", 60_000, null, out BatchTone tone);

        Assert.Equal(BatchTone.Error, tone);
        Assert.Contains("Windows n'a pas confirmé l'envoi", text);
        Assert.Contains("ligne 5", text);
        Assert.Contains("60 s", text);
        Assert.Contains("file d'impression", text);
        Assert.Contains("« ? »", text);
        Assert.Contains("ou la suivante si l'étiquette est sortie", text);
    }

    [Fact]
    public void Message_WhenPaused_ShowsTheNoteAndHowToContinue()
    {
        string text = BatchLogic.BuildOutcomeMessage(
            Outcome(BatchEnd.Paused, 1, 1, 10, resume: 1), Many("HB1", 10), 1, "QL", 60_000, "Le pilote impose 90 mm.", out BatchTone tone);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.Contains("Le pilote impose 90 mm.", text);
        Assert.Contains("sélectionnez la ligne 2", text);
    }

    [Fact]
    public void DescribeRow_NamesTheRowAsShownOnScreen()
    {
        List<LabelItem> items = Items(("HB1", " 22061 "));

        Assert.Equal("ligne 1 (HB1 22061)", BatchLogic.DescribeRow(items, 0));
        Assert.Equal("ligne 8", BatchLogic.DescribeRow(items, 7));
    }

    // ------------------------------------------------------------------------------------------------
    // Aperçu
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void PreviewPrintableArea_UsesTheMeasuredQl800Margins()
    {
        RectangleF area = BatchLogic.PreviewPrintableArea(244, 157);

        Assert.Equal(6f, area.X, 2);
        Assert.Equal(11.67f, area.Y, 2);
        Assert.Equal(232f, area.Width, 2);
        Assert.Equal(133.33f, area.Height, 2);
        Assert.True(area.Right <= 244f && area.Bottom <= 157f);
    }

    [Fact]
    public void PreviewPrintableArea_OfATinyPage_StaysPositive()
    {
        RectangleF area = BatchLogic.PreviewPrintableArea(5, 10);

        Assert.True(area.Width >= 1f && area.Height >= 1f);
    }

    // ------------------------------------------------------------------------------------------------
    // Étiquettes déjà imprimées : « Imprimer tout » après un arrêt, un échec ou la pause ne doit jamais doubler en silence
    // ------------------------------------------------------------------------------------------------

    private static RowState[] States(int total, params int[] done)
    {
        var states = new RowState[total];
        foreach (int d in done)
        {
            states[d] = RowState.Done;
        }

        return states;
    }

    [Fact]
    public void CountDone_CountsOnlyTheRowsToPrintThatAreAlreadyDone()
    {
        RowState[] states = States(10, 0, 1, 2, 3, 8);
        states[4] = RowState.Failed;
        states[5] = RowState.Uncertain;
        states[6] = RowState.Partial;

        Assert.Equal(5, BatchLogic.CountDone(states, Rows(10)));
        Assert.Equal(2, BatchLogic.CountDone(states, new[] { 2, 3, 4, 5, 6 }));
        Assert.Equal(0, BatchLogic.CountDone(states, new[] { 4, 5, 6, 7 }));
        Assert.Equal(1, BatchLogic.CountDone(states, new[] { 8, 99, -1 })); // lignes inconnues ignorées
        Assert.Equal(0, BatchLogic.CountDone(states, Array.Empty<int>()));
    }

    [Fact]
    public void ExcludeDone_KeepsThePendingFailedAndUncertainRows_InOrder()
    {
        // Échec à la ligne 41 sur 88 (indice 40) : « Imprimer tout » ne doit refaire que 41 à 88.
        RowState[] states = States(88, Enumerable.Range(0, 40).ToArray());
        states[40] = RowState.Failed;

        IReadOnlyList<int> rest = BatchLogic.ExcludeDone(states, Rows(88));

        Assert.Equal(Enumerable.Range(40, 48), rest);
        Assert.Equal(40, rest[0]);
    }

    [Fact]
    public void ExcludeDone_OfASelection_KeepsItsOrderAndLeavesNothingWhenAllAreDone()
    {
        RowState[] states = States(6, 1, 3);

        Assert.Equal(new[] { 0, 2, 4 }, BatchLogic.ExcludeDone(states, new[] { 0, 1, 2, 3, 4 }));
        Assert.Empty(BatchLogic.ExcludeDone(states, new[] { 1, 3 }));
        Assert.Empty(BatchLogic.ExcludeDone(states, Array.Empty<int>()));
    }

    [Fact]
    public void ReprintText_WithoutAnyDoneRow_IsTheOrdinaryConfirmation()
    {
        Assert.Equal(
            BatchLogic.BuildConfirmText(88, 1, "Brother QL-800"),
            BatchLogic.BuildReprintText(88, 0, 1, "Brother QL-800"));
    }

    [Fact]
    public void ReprintText_ExplainsTheThreeAnswers_AndCountsBothGroups()
    {
        // Le scénario du défaut : 40 étiquettes déjà sorties, « Imprimer tout » sur 88.
        string text = BatchLogic.BuildReprintText(88, 40, 1, "Brother QL-800");

        Assert.StartsWith("Imprimer 88 étiquettes sur « Brother QL-800 » ?", text);
        Assert.Contains("40 étiquettes sont déjà marquées « ✔ imprimée »", text);
        Assert.Contains("Oui : tout imprimer (les 40 déjà imprimées sortiront en double)", text);
        Assert.Contains("Non : imprimer seulement les 48 étiquettes pas encore imprimées", text);
        Assert.Contains("Annuler : ne rien imprimer", text);
    }

    [Fact]
    public void ReprintText_UsesTheSingularWhenOneRowIsDone()
    {
        string text = BatchLogic.BuildReprintText(5, 1, 1, "QL");

        Assert.Contains("1 étiquette est déjà marquée « ✔ imprimée »", text);
        Assert.Contains("celle déjà imprimée sortira en double", text);

        string oneLeft = BatchLogic.BuildReprintText(5, 4, 1, "QL");
        Assert.Contains("Non : imprimer seulement l'étiquette pas encore imprimée", oneLeft);
    }

    [Fact]
    public void ReprintText_WhenEverythingIsAlreadyDone_AsksASingleYesNoQuestion()
    {
        string many = BatchLogic.BuildReprintText(3, 3, 1, "QL");
        Assert.Contains("Ces 3 étiquettes sont déjà marquées « ✔ imprimée »", many);
        Assert.Contains("en double", many);
        Assert.EndsWith("Imprimer 3 étiquettes sur « QL » ?", many);
        Assert.DoesNotContain("Oui :", many); // pas de choix « seulement les restantes » : il n'y en a aucune
        Assert.DoesNotContain("Non :", many);

        string one = BatchLogic.BuildReprintText(1, 1, 1, "QL");
        Assert.StartsWith("Cette étiquette est déjà marquée « ✔ imprimée »", one);
    }

    [Fact]
    public void ReprintText_KeepsTheCopiesOfTheConfirmation()
    {
        string text = BatchLogic.BuildReprintText(88, 40, 2, "QL");

        Assert.StartsWith("Imprimer 88 étiquettes en 2 copies (176 impressions au total) sur « QL » ?", text);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(200)]
    public void ReprintText_ClampsAbsurdCounts(int done)
    {
        string text = BatchLogic.BuildReprintText(4, done, 1, "QL");

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("Imprimer 4 étiquettes sur « QL » ?", text);
    }

    // ------------------------------------------------------------------------------------------------
    // Texte et longueur réelle de la page (PaperName d'une étiquette plus courte que BatchLabelLengthMm)
    // ------------------------------------------------------------------------------------------------

    private const int Dpi300 = 300;

    private static float Px(double mm) => (float)(mm * Dpi300 / 25.4);

    /// <summary>Encre du type (4,8 à 10,6 mm) et du numéro (16,4 à 32,2 mm) telle que la mesure le moteur de dessin à 300 dpi.</summary>
    private static (RectangleF Type, RectangleF Plan) DefaultInk()
        => (new RectangleF(Px(1.9), Px(4.8), Px(14), Px(5.8)), new RectangleF(Px(1.9), Px(16.4), Px(58.3), Px(15.8)));

    [Fact]
    public void TextFit_OfTheDefaultLabel_IsAccepted()
    {
        (RectangleF type, RectangleF plan) = DefaultInk();

        // 62 x 40 mm, zone imprimable du pilote 6 / 11,67 / 232 / 134 (bas à 145,67 / 100 pouce = 37 mm).
        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 157, new RectangleF(6f, 11.67f, 232f, 133.33f)));
    }

    [Fact]
    public void TextFit_OnAPageShorterThanTheText_IsRefused_WithTheNumbersAndWhatToChange()
    {
        // PaperName = « 62mm x 29mm » : page de 114 / 100 pouce (29 mm), bas imprimable à ~25,9 mm, texte jusqu'à 32,2 mm.
        (RectangleF type, RectangleF plan) = DefaultInk();

        string? message = BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 114, new RectangleF(6f, 11.67f, 232f, 90.33f));

        Assert.NotNull(message);
        Assert.Contains("s'arrête à 25,9 mm", message);
        Assert.Contains("étiquette de 29 mm", message);
        Assert.Contains("jusqu'à 32,2 mm", message);
        Assert.Contains("Rien n'a été envoyé", message);
        Assert.Contains("PaperName", message);
        Assert.Contains("BatchLabelLengthMm", message);
        Assert.Contains("BatchPlanBaselineMm", message);
    }

    [Fact]
    public void TextFit_WithoutAReadablePrintableArea_OnlyTheWholePageCounts()
    {
        (RectangleF type, RectangleF plan) = DefaultInk();

        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 157, RectangleF.Empty));
        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 157, new RectangleF(float.NaN, 0f, 232f, 100f)));
        Assert.NotNull(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 114, RectangleF.Empty)); // 29 mm < 32,2 mm
    }

    [Fact]
    public void TextFit_ToleratesAFractionOfAMillimetre_ButNotMore()
    {
        (RectangleF type, RectangleF plan) = DefaultInk();
        // Zone imprimable s'arrêtant à 31,95 mm (texte jusqu'à 32,2 mm) : 0,25 mm de débord -> toléré.
        float almost = (float)(31.95 / 25.4 * 100.0);
        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 157, new RectangleF(6f, 0f, 232f, almost)));

        // 1 mm de débord -> refusé.
        float tooShort = (float)(31.2 / 25.4 * 100.0);
        Assert.NotNull(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 157, new RectangleF(6f, 0f, 232f, tooShort)));
    }

    [Fact]
    public void TextFit_UsesTheLowerInkOfTheTwoLines()
    {
        RectangleF type = new(Px(1.9), Px(4.8), Px(14), Px(5.8));
        RectangleF plan = RectangleF.Empty;
        var area = new RectangleF(6f, 11.67f, 232f, 90.33f); // s'arrête à 25,9 mm

        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, Dpi300, 114, area)); // le type finit à 10,6 mm
        Assert.NotNull(BatchLogic.CheckTextFitsPageHeight(type, new RectangleF(Px(1.9), Px(16.4), Px(58.3), Px(15.8)), Dpi300, 114, area));
    }

    [Theory]
    [InlineData(0, 157)]
    [InlineData(-300, 157)]
    [InlineData(300, 0)]
    [InlineData(300, -5)]
    public void TextFit_WithAbsurdDimensions_JudgesNothing(int dpi, int pageHeightUnits)
    {
        (RectangleF type, RectangleF plan) = DefaultInk();

        Assert.Null(BatchLogic.CheckTextFitsPageHeight(type, plan, dpi, pageHeightUnits, new RectangleF(6f, 11.67f, 232f, 90f)));
    }

    [Fact]
    public void TextFit_NeverThrows_OnNonFiniteInk()
    {
        var weird = new RectangleF(float.NaN, float.NaN, float.PositiveInfinity, float.NaN);

        Assert.Null(BatchLogic.CheckTextFitsPageHeight(weird, weird, Dpi300, 157, new RectangleF(6f, 11.67f, 232f, 133f)));
    }

    // ------------------------------------------------------------------------------------------------
    // Envoi (ou préparation du papier) resté sans réponse : messages
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void PrepareTimeout_SaysNothingWasSent_WithoutContradictingItself()
    {
        string text = BatchLogic.BuildPrepareTimeoutMessage("Brother QL-800", 60_000);

        Assert.Contains("n'a pas répondu à la préparation du papier en 60 s", text);
        Assert.Contains("Rien n'a été envoyé", text);
        Assert.Contains("Attendez sa réponse", text);
        Assert.DoesNotContain("envoi précédent", text);   // rien n'a été envoyé : pas de « envoi précédent »
        Assert.DoesNotContain("file d'impression", text);
    }

    [Fact]
    public void LateBlockedMessage_DependsOnWhatIsStillRunning()
    {
        string send = BatchLogic.BuildLateBlockedMessage(labelSent: true);
        string prepare = BatchLogic.BuildLateBlockedMessage(labelSent: false);

        Assert.Equal(BatchLogic.LateSendBlockedMessage, send);
        Assert.Contains("file d'impression", send);
        Assert.Equal(BatchLogic.LatePrepareBlockedMessage, prepare);
        Assert.Contains("rien n'a été envoyé", prepare);
        Assert.DoesNotContain("file d'impression", prepare);
    }

    [Fact]
    public void LateFinished_OfALabel_AnnouncesALabelAndTheQuestionMarkRow()
    {
        string ok = BatchLogic.BuildLateFinishedMessage(true, "QL", null, out BatchTone tone);
        Assert.Equal(BatchTone.Info, tone);
        Assert.Contains("l'étiquette doit sortir", ok);
        Assert.Contains("« ? »", ok);

        string failed = BatchLogic.BuildLateFinishedMessage(true, "QL", "Papier absent.", out tone);
        Assert.Equal(BatchTone.Error, tone);
        Assert.Equal("L'envoi en attente a échoué : Papier absent.", failed);
    }

    [Fact]
    public void LateFinished_OfThePaperPreparation_NeverPromisesALabel()
    {
        string ok = BatchLogic.BuildLateFinishedMessage(false, "QL", null, out BatchTone tone);
        Assert.Equal(BatchTone.Info, tone);
        Assert.Contains("rien n'est parti", ok);
        Assert.Contains("relancer l'impression", ok);
        Assert.DoesNotContain("doit sortir", ok);
        Assert.DoesNotContain("« ? »", ok);

        string failed = BatchLogic.BuildLateFinishedMessage(false, "QL", "Imprimante introuvable", out tone);
        Assert.Equal(BatchTone.Error, tone);
        Assert.Contains("a fini par échouer : Imprimante introuvable.", failed);
        Assert.EndsWith("Rien n'a été envoyé.", failed);
        Assert.DoesNotContain("L'envoi en attente", failed);
    }

    [Fact]
    public void PasteStatus_WhilePrintingIsBlocked_DoesNotInviteToClickAGrayedOutButton()
    {
        PlanTableParseResult result = PlanTableParser.Parse("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n");

        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result, BatchLogic.LatePrepareBlockedMessage);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.StartsWith("2 étiquettes lues dans le tableau.", text);
        Assert.Contains(BatchLogic.LatePrepareBlockedMessage, text);
        Assert.DoesNotContain("cliquez sur « Imprimer tout »", text);
    }

    [Fact]
    public void PasteStatus_WhilePrintingIsBlocked_PutsTheNoticeBeforeTheWarnings()
    {
        PlanTableParseResult result = PlanTableParser.Parse("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\r\n");
        Assert.NotEmpty(result.Warnings);

        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result, BatchLogic.LateSendBlockedMessage);

        Assert.Equal(BatchTone.Warning, tone);
        Assert.True(text.IndexOf(BatchLogic.LateSendBlockedMessage, StringComparison.Ordinal) < text.IndexOf("Remarques :", StringComparison.Ordinal));
        foreach (string warning in result.Warnings)
        {
            Assert.Contains(warning.Trim(), text);
        }
    }

    [Fact]
    public void PasteStatus_AnErrorStaysRed_EvenWhilePrintingIsBlocked()
    {
        PlanTableParseResult result = PlanTableParser.Parse("");

        (string text, BatchTone tone) = BatchLogic.BuildPasteStatus(result, BatchLogic.LateSendBlockedMessage);

        Assert.Equal(BatchTone.Error, tone);
        Assert.Equal(result.Error, text);
    }

    // ------------------------------------------------------------------------------------------------
    // Déroulement d'une série
    // ------------------------------------------------------------------------------------------------

    private sealed class Recorder
    {
        public readonly List<(int Row, int Copy)> Printed = new();
        public readonly List<BatchEvent> Events = new();
        private int _running;
        public int MaxConcurrent;
        public int Calls;

        public Task Print(int row, int copy)
        {
            Interlocked.Increment(ref Calls);
            Printed.Add((row, copy));
            return Task.CompletedTask;
        }

        public void Enter() => MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running));

        public void Exit() => Interlocked.Decrement(ref _running);

        public void Report(BatchEvent e) => Events.Add(e);
    }

    private static Task<BatchOutcome> Run(
        IReadOnlyList<int> rows, int copies, Recorder r, Func<int, int, Task>? print = null, Func<bool>? stop = null,
        int timeoutMs = 5000, int pauseAfterRows = 0)
        => BatchLogic.Run(rows, copies, print ?? r.Print, stop ?? (() => false), r.Report, timeoutMs, pauseAfterRows);

    [Fact]
    public async Task Run_SendsEveryRowInOrder_AndReportsEachOne()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(new[] { 0, 1, 2 }, 1, r);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(3, o.RowsDone);
        Assert.Equal(3, o.SheetsDone);
        Assert.Equal(3, o.TotalRows);
        Assert.Equal(3, o.TotalSheets);
        Assert.Null(o.ResumeRow);
        Assert.Null(o.Error);
        Assert.Null(o.Orphan);
        Assert.Equal(new[] { (0, 1), (1, 1), (2, 1) }, r.Printed);

        Assert.Equal(6, r.Events.Count);
        Assert.Equal(
            new[] { RowState.Printing, RowState.Done, RowState.Printing, RowState.Done, RowState.Printing, RowState.Done },
            r.Events.Select(e => e.State));
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, r.Events.Select(e => e.Row));
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, r.Events.Select(e => e.Position));
        Assert.All(r.Events, e => Assert.Equal(3, e.TotalRows));
        Assert.Equal(new[] { 0, 1, 1, 2, 2, 3 }, r.Events.Select(e => e.SheetsDone));
    }

    [Fact]
    public async Task Run_WithCopies_SendsOneJobPerCopy_RowByRow()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(new[] { 4, 7 }, 3, r);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(new[] { (4, 1), (4, 2), (4, 3), (7, 1), (7, 2), (7, 3) }, r.Printed);
        Assert.Equal(2, o.RowsDone);
        Assert.Equal(6, o.SheetsDone);
        Assert.Equal(6, o.TotalSheets);

        BatchEvent firstDone = r.Events.First(e => e.State == RowState.Done);
        Assert.Equal(4, firstDone.Row);
        Assert.Equal(3, firstDone.CopiesDone);
        Assert.Equal(3, firstDone.SheetsDone);
        Assert.All(r.Events.Where(e => e.State == RowState.Printing), e => Assert.Equal(e.Copy - 1, e.CopiesDone));
    }

    [Fact]
    public async Task Run_NeverHasTwoJobsAtOnce()
    {
        var r = new Recorder();
        Func<int, int, Task> slow = async (row, copy) =>
        {
            r.Enter();
            await Task.Delay(5);
            await r.Print(row, copy);
            r.Exit();
        };

        await Run(Rows(8), 2, r, slow);

        Assert.Equal(16, r.Printed.Count);
        Assert.Equal(1, r.MaxConcurrent);
    }

    [Fact]
    public async Task Run_WithNoRow_CompletesImmediately()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(Array.Empty<int>(), 2, r);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(0, o.SheetsDone);
        Assert.Empty(r.Printed);
        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task Run_ClampsTheNumberOfCopies()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(new[] { 0 }, 1000, r);

        Assert.Equal(99, o.SheetsDone);
        Assert.Equal(99, r.Printed.Count);
        BatchOutcome none = await Run(new[] { 0 }, 0, new Recorder());
        Assert.Equal(1, none.SheetsDone);
    }

    [Fact]
    public async Task Run_StoppedBetweenLabels_KeepsWhatWasSent_AndTellsWhereToResume()
    {
        var r = new Recorder();
        bool stop = false;
        Func<int, int, Task> print = (row, copy) =>
        {
            if (row == 1)
            {
                stop = true; // « Arrêter » pendant l'envoi de la 2e étiquette : elle se termine, la 3e ne part pas
            }

            return r.Print(row, copy);
        };

        BatchOutcome o = await Run(Rows(6), 1, r, print, () => stop);

        Assert.Equal(BatchEnd.Stopped, o.End);
        Assert.Equal(new[] { (0, 1), (1, 1) }, r.Printed);
        Assert.Equal(2, o.RowsDone);
        Assert.Equal(2, o.SheetsDone);
        Assert.Equal(2, o.ResumeRow);
        Assert.Equal(0, o.ResumeCopiesDone);
        Assert.Null(o.Orphan);
        Assert.DoesNotContain(r.Events, e => e.Row >= 2);
        Assert.Equal(RowState.Done, r.Events.Last().State);
    }

    [Fact]
    public async Task Run_StoppedBeforeTheFirstSheet_PrintsNothing()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(new[] { 3, 4 }, 1, r, stop: () => true);

        Assert.Equal(BatchEnd.Stopped, o.End);
        Assert.Empty(r.Printed);
        Assert.Empty(r.Events);
        Assert.Equal(3, o.ResumeRow);
        Assert.Equal(0, o.RowsDone);
    }

    [Fact]
    public async Task Run_StoppedBetweenCopies_MarksTheRowAsPartial()
    {
        var r = new Recorder();
        bool stop = false;
        Func<int, int, Task> print = (row, copy) =>
        {
            if (row == 5 && copy == 2)
            {
                stop = true;
            }

            return r.Print(row, copy);
        };

        BatchOutcome o = await Run(new[] { 5, 6 }, 3, r, print, () => stop);

        Assert.Equal(BatchEnd.Stopped, o.End);
        Assert.Equal(new[] { (5, 1), (5, 2) }, r.Printed);
        Assert.Equal(0, o.RowsDone);
        Assert.Equal(2, o.SheetsDone);
        Assert.Equal(5, o.ResumeRow);
        Assert.Equal(2, o.ResumeCopiesDone);

        BatchEvent last = r.Events.Last();
        Assert.Equal(RowState.Partial, last.State);
        Assert.Equal(5, last.Row);
        Assert.Equal(2, last.CopiesDone);
    }

    [Fact]
    public async Task Run_StopRequestedDuringTheLastSheet_IsTooLate_AndTheSeriesIsComplete()
    {
        var r = new Recorder();
        bool stop = false;
        Func<int, int, Task> print = (row, copy) =>
        {
            if (row == 2)
            {
                stop = true;
            }

            return r.Print(row, copy);
        };

        BatchOutcome o = await Run(Rows(3), 1, r, print, () => stop);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(3, o.RowsDone);
    }

    [Fact]
    public async Task Run_StopsAtTheFirstFailure_AndNeverRetriesOrContinues()
    {
        var r = new Recorder();
        Func<int, int, Task> print = (row, copy) =>
            row == 2
                ? Task.FromException(new InvalidOperationException("L'imprimante est hors connexion."))
                : r.Print(row, copy);

        BatchOutcome o = await Run(Rows(5), 1, r, print);

        Assert.Equal(BatchEnd.Failed, o.End);
        Assert.Equal(new[] { (0, 1), (1, 1) }, r.Printed);
        Assert.Equal(2, o.RowsDone);
        Assert.Equal(2, o.ResumeRow);
        Assert.Equal(0, o.ResumeCopiesDone);
        Assert.Equal("L'imprimante est hors connexion.", o.Error);
        Assert.Null(o.Orphan);

        BatchEvent last = r.Events.Last();
        Assert.Equal(RowState.Failed, last.State);
        Assert.Equal(2, last.Row);
        Assert.DoesNotContain(r.Events, e => e.Row > 2);
    }

    [Fact]
    public async Task Run_WhenTheSendThrowsSynchronously_ItIsAFailureToo()
    {
        var r = new Recorder();
        Func<int, int, Task> print = (row, copy) =>
        {
            if (row == 1)
            {
                throw new InvalidOperationException("Papier absent.");
            }

            return r.Print(row, copy);
        };

        BatchOutcome o = await Run(Rows(3), 1, r, print);

        Assert.Equal(BatchEnd.Failed, o.End);
        Assert.Equal("Papier absent.", o.Error);
        Assert.Equal(1, o.ResumeRow);
        Assert.Single(r.Printed);
    }

    [Fact]
    public async Task Run_FailureOnTheSecondCopy_RemembersHowManyCopiesWereSent()
    {
        var r = new Recorder();
        Func<int, int, Task> print = (row, copy) =>
            row == 1 && copy == 2 ? Task.FromException(new InvalidOperationException("Capot ouvert.")) : r.Print(row, copy);

        BatchOutcome o = await Run(Rows(3), 3, r, print);

        Assert.Equal(BatchEnd.Failed, o.End);
        Assert.Equal(1, o.RowsDone);
        Assert.Equal(4, o.SheetsDone);
        Assert.Equal(1, o.ResumeRow);
        Assert.Equal(1, o.ResumeCopiesDone);
        BatchEvent last = r.Events.Last();
        Assert.Equal(RowState.Failed, last.State);
        Assert.Equal(1, last.CopiesDone);
        Assert.Equal(3, last.Copies);
    }

    [Fact]
    public async Task Run_AFailureThatIsNotAFrenchMessage_IsLabelledUnexpected()
    {
        var r = new Recorder();
        Func<int, int, Task> print = (row, copy) => Task.FromException(new NullReferenceException("Object reference not set"));

        BatchOutcome o = await Run(Rows(2), 1, r, print);

        Assert.Equal(BatchEnd.Failed, o.End);
        Assert.Equal("Erreur inattendue : Object reference not set", o.Error);
    }

    [Fact]
    public async Task Run_WhenWindowsDoesNotAnswerInTime_StopsAndHandsBackTheRunningTask()
    {
        var r = new Recorder();
        var never = new TaskCompletionSource();
        Func<int, int, Task> print = (row, copy) => row == 1 ? never.Task : r.Print(row, copy);

        BatchOutcome o = await Run(Rows(4), 1, r, print, timeoutMs: 80);

        Assert.Equal(BatchEnd.TimedOut, o.End);
        Assert.Equal(1, o.RowsDone);
        Assert.Equal(1, o.ResumeRow);
        Assert.Equal(0, o.ResumeCopiesDone);
        Assert.Same(never.Task, o.Orphan);
        Assert.False(o.Orphan!.IsCompleted);
        Assert.Equal(RowState.Uncertain, r.Events.Last().State);
        Assert.Equal(1, r.Events.Last().Row);

        int callsAtTimeout = r.Calls;
        never.SetResult(); // la réponse arrive après coup : la série ne reprend PAS toute seule
        await Task.Delay(100);
        Assert.Equal(callsAtTimeout, r.Calls);
        Assert.Equal(new[] { (0, 1) }, r.Printed);
    }

    [Fact]
    public async Task Run_TheTimeoutIsPerSend_NotForTheWholeSeries()
    {
        var r = new Recorder();
        Func<int, int, Task> print = async (row, copy) =>
        {
            await Task.Delay(30);
            await r.Print(row, copy);
        };

        BatchOutcome o = await Run(Rows(8), 1, r, print, timeoutMs: 400);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(8, o.SheetsDone);
    }

    [Fact]
    public async Task Run_WithoutTimeout_WaitsForAnySlowSend()
    {
        var r = new Recorder();
        Func<int, int, Task> print = async (row, copy) =>
        {
            await Task.Delay(120);
            await r.Print(row, copy);
        };

        BatchOutcome o = await Run(Rows(2), 1, r, print, timeoutMs: 0);

        Assert.Equal(BatchEnd.Completed, o.End);
    }

    [Fact]
    public async Task Run_PausesAfterTheFirstRow_WhenAsked()
    {
        var r = new Recorder();

        BatchOutcome o = await Run(new[] { 10, 11, 12 }, 2, r, pauseAfterRows: 1);

        Assert.Equal(BatchEnd.Paused, o.End);
        Assert.Equal(new[] { (10, 1), (10, 2) }, r.Printed);
        Assert.Equal(1, o.RowsDone);
        Assert.Equal(2, o.SheetsDone);
        Assert.Equal(11, o.ResumeRow);
        Assert.Equal(0, o.ResumeCopiesDone);
        Assert.Equal(RowState.Done, r.Events.Last().State);
    }

    [Fact]
    public async Task Run_DoesNotPause_WhenThereIsNothingAfter_OrWhenNotAsked()
    {
        Assert.Equal(BatchEnd.Completed, (await Run(new[] { 0 }, 1, new Recorder(), pauseAfterRows: 1)).End);
        Assert.Equal(BatchEnd.Completed, (await Run(Rows(3), 1, new Recorder(), pauseAfterRows: 0)).End);
        Assert.Equal(BatchEnd.Completed, (await Run(Rows(2), 1, new Recorder(), pauseAfterRows: 2)).End);
        Assert.Equal(BatchEnd.Paused, (await Run(Rows(3), 1, new Recorder(), pauseAfterRows: 2)).End);
    }

    [Fact]
    public async Task Run_ADisplayFailure_NeverInterruptsThePrinting()
    {
        var r = new Recorder();

        BatchOutcome o = await BatchLogic.Run(
            Rows(3), 1, r.Print, () => false, _ => throw new InvalidOperationException("fenêtre fermée"), 5000);

        Assert.Equal(BatchEnd.Completed, o.End);
        Assert.Equal(3, r.Printed.Count);
    }

    [Fact]
    public async Task Run_Resuming_PicksUpWhereTheFailureWas()
    {
        // Scénario complet : échec sur la ligne 3 (indice 2), puis reprise « à partir de la sélection ».
        var r = new Recorder();
        bool printerOffline = true;
        Func<int, int, Task> print = (row, copy) =>
            row == 2 && printerOffline ? Task.FromException(new InvalidOperationException("Hors connexion.")) : r.Print(row, copy);

        BatchOutcome first = await Run(BatchLogic.ChooseRows(PrintMode.All, 5, Array.Empty<int>()), 1, r, print);
        Assert.Equal(BatchEnd.Failed, first.End);
        Assert.Equal(2, first.ResumeRow);

        printerOffline = false;
        IReadOnlyList<int> rest = BatchLogic.ChooseRows(PrintMode.FromSelection, 5, new[] { first.ResumeRow!.Value });
        BatchOutcome second = await Run(rest, 1, r, print);

        Assert.Equal(BatchEnd.Completed, second.End);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, r.Printed.Select(p => p.Row)); // chaque étiquette exactement une fois
    }

    [Fact]
    public async Task Run_WhateverTheFailurePosition_NothingAfterItIsPrinted_AndNothingIsPrintedTwice()
    {
        var random = new Random(20240607);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            int total = random.Next(1, 12);
            int copies = random.Next(1, 4);
            int failRow = random.Next(0, total);
            int failCopy = random.Next(1, copies + 1);
            var r = new Recorder();
            Func<int, int, Task> print = (row, copy) =>
                row == failRow && copy == failCopy ? Task.FromException(new InvalidOperationException("x")) : r.Print(row, copy);

            BatchOutcome o = await Run(Rows(total), copies, r, print);

            Assert.Equal(BatchEnd.Failed, o.End);
            Assert.Equal(failRow, o.ResumeRow);
            Assert.Equal(failCopy - 1, o.ResumeCopiesDone);
            Assert.Equal(failRow * copies + failCopy - 1, r.Printed.Count);
            Assert.Equal(r.Printed.Count, r.Printed.Distinct().Count());
            Assert.Equal(o.SheetsDone, r.Printed.Count);
            Assert.All(r.Printed, p => Assert.True(p.Row <= failRow));
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Messages d'erreur
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void DescribeError_KeepsTheFrenchMessageOfThePrinterCode()
    {
        Assert.Equal("Papier absent.", BatchLogic.DescribeError(new InvalidOperationException("  Papier absent. ")));
        Assert.Equal("Le champ est vide.", BatchLogic.DescribeError(new ArgumentException("Le champ est vide.")));
    }

    [Fact]
    public void DescribeError_LabelsEverythingElseAsUnexpected_AndUnwrapsAggregates()
    {
        Assert.Equal("Erreur inattendue : boum", BatchLogic.DescribeError(new IOException("boum")));
        Assert.Equal("Erreur inattendue : boum", BatchLogic.DescribeError(new AggregateException(new IOException("boum"))));
        Assert.Equal("Erreur inattendue : NullReferenceException", BatchLogic.DescribeError(new NullReferenceException(string.Empty)));
    }

    private sealed class IOException : Exception
    {
        public IOException(string message) : base(message)
        {
        }
    }
}
