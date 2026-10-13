using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Analyse du tableau collé depuis Excel (PlanTableParser). Tests purs : ni System.Drawing, ni presse-papier, ils tournent aussi sous Linux.
/// </summary>
public class PlanTableParserTests
{
    // ------------------------------------------------------------------------------------------------
    // Outils
    // ------------------------------------------------------------------------------------------------

    private static string Table(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static PlanTableParseResult Ok(string text)
    {
        PlanTableParseResult result = PlanTableParser.Parse(text);
        Assert.True(result.Error is null, "Erreur inattendue : " + result.Error);
        return result;
    }

    private static PlanTableParseResult Failed(string? text)
    {
        PlanTableParseResult result = PlanTableParser.Parse(text);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Items);
        Assert.Empty(result.Types);
        Assert.Empty(result.Warnings);
        return result;
    }

    private static LabelItem I(string type, string plan) => new(type, plan);

    private static int Count(string text, char c) => text.Count(x => x == c);

    // ------------------------------------------------------------------------------------------------
    // Le vrai tableau (structure de la capture d'écran)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void FullTable_GivesOneLabelPerCross_InTableOrder()
    {
        PlanTableParseResult result = Ok(PlanTableFixture.BuildExcelText());

        Assert.True(result.Items.Count >= 80, "le tableau de référence doit avoir au moins 80 lignes");
        Assert.Equal(PlanTableFixture.ExpectedItems.Count, result.Items.Count);
        Assert.Equal(PlanTableFixture.ExpectedItems, result.Items);
    }

    [Fact]
    public void FullTable_TypesAreTheHeaderNames_WithoutTheCounts_AndKeepTheEmptyColumn()
    {
        PlanTableParseResult result = Ok(PlanTableFixture.BuildExcelText());

        Assert.Equal(PlanTableFixture.Types, result.Types);       // HB1, HBA, HB2, VF1, VF2 : HBA est vide mais fait partie de l'en-tête
        Assert.DoesNotContain(result.Items, i => i.Type.Contains('(') || i.Type.Contains(')'));
    }

    [Fact]
    public void FullTable_CountsPerType_MatchTheBlocks()
    {
        PlanTableParseResult result = Ok(PlanTableFixture.BuildExcelText());

        Dictionary<string, int> counts = result.Items.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(45, counts["HB1"]);       // 22061..22104 + 22120
        Assert.Equal(19, counts["HB2"]);
        Assert.Equal(12, counts["VF1"]);
        Assert.Equal(13, counts["VF2"]);
        Assert.False(counts.ContainsKey("HBA"));
        Assert.Equal(89, result.Items.Count);
    }

    [Fact]
    public void FullTable_PlanNumbersAreOnlyTheDigits_WithoutThePrefix()
    {
        PlanTableParseResult result = Ok(PlanTableFixture.BuildExcelText());

        foreach (LabelItem item in result.Items)
        {
            Assert.Matches("^[0-9]{5}$", item.Plan);
            Assert.DoesNotContain("2020Q7E", item.Plan);
            Assert.Equal(item.Plan.Trim(), item.Plan);
            Assert.Equal(item.Type.Trim(), item.Type);
        }
    }

    [Fact]
    public void FullTable_HasNeitherErrorNorWarning()
    {
        PlanTableParseResult result = PlanTableParser.Parse(PlanTableFixture.BuildExcelText());

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("22120", "HB1", "HB2")]
    [InlineData("22151", "HB2", "VF1")]
    [InlineData("22103", "HB1", "HB2")]
    public void SamePlanUnderTwoTypes_GivesTwoLabels_InTableOrder(string plan, string firstType, string secondType)
    {
        PlanTableParseResult result = Ok(PlanTableFixture.BuildExcelText());

        List<int> positions = Enumerable.Range(0, result.Items.Count).Where(k => result.Items[k].Plan == plan).ToList();

        Assert.Equal(2, positions.Count);
        Assert.Equal(firstType, result.Items[positions[0]].Type);
        Assert.Equal(secondType, result.Items[positions[1]].Type);
    }

    [Theory]
    [InlineData("crlf")]
    [InlineData("lf")]
    [InlineData("cr")]
    [InlineData("no-final-newline")]
    [InlineData("many-final-newlines")]
    [InlineData("no-trailing-tabs")]
    [InlineData("no-quirks")]
    [InlineData("plain-header")]
    [InlineData("bom")]
    [InlineData("one-leading-empty-row")]
    [InlineData("three-leading-empty-rows")]
    [InlineData("leading-empty-rows-without-tabs")]
    [InlineData("trailing-empty-rows")]
    [InlineData("everything")]
    public void FullTable_GivesTheSameLabels_WhateverTheClipboardFlavour(string flavour)
    {
        PlanTableFixture.Options options = flavour switch
        {
            "crlf" => new PlanTableFixture.Options(),
            "lf" => new PlanTableFixture.Options { NewLine = "\n" },
            "cr" => new PlanTableFixture.Options { NewLine = "\r" },
            "no-final-newline" => new PlanTableFixture.Options { FinalNewLine = false },
            "many-final-newlines" => new PlanTableFixture.Options { TrailingEmptyRows = 3 },
            "no-trailing-tabs" => new PlanTableFixture.Options { TrailingTabs = false },
            "no-quirks" => new PlanTableFixture.Options { Quirks = false },
            "plain-header" => new PlanTableFixture.Options { QuotedHeaderWithNewLine = false },
            "bom" => new PlanTableFixture.Options { Bom = true },
            "one-leading-empty-row" => new PlanTableFixture.Options { LeadingEmptyRows = 1 },
            "three-leading-empty-rows" => new PlanTableFixture.Options { LeadingEmptyRows = 3 },
            "leading-empty-rows-without-tabs" => new PlanTableFixture.Options { LeadingEmptyRows = 2, TrailingTabs = false },
            "trailing-empty-rows" => new PlanTableFixture.Options { TrailingEmptyRows = 2, FinalNewLine = false },
            "everything" => new PlanTableFixture.Options
            {
                NewLine = "\n", Bom = true, LeadingEmptyRows = 2, TrailingEmptyRows = 2, TrailingTabs = false, QuotedHeaderWithNewLine = true, Quirks = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(flavour)),
        };

        PlanTableParseResult result = PlanTableParser.Parse(PlanTableFixture.BuildExcelText(options));

        Assert.Null(result.Error);
        Assert.Equal(PlanTableFixture.ExpectedItems, result.Items);
        Assert.Equal(PlanTableFixture.Types, result.Types);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void FullTable_IsParsedTheSameWayEveryTime()
    {
        string text = PlanTableFixture.BuildExcelText();

        PlanTableParseResult a = PlanTableParser.Parse(text);
        PlanTableParseResult b = PlanTableParser.Parse(text);

        Assert.Equal(a.Items, b.Items);
        Assert.Equal(a.Types, b.Types);
        Assert.Equal(a.Warnings, b.Warnings);
    }

    [Fact]
    public void FullTable_PastedTwice_GivesTheLabelsTwice_WithADuplicateWarning()
    {
        // Le même bloc de lignes copié deux fois sous un seul en-tête : une étiquette par croix, les doublons sont signalés mais conservés.
        string rows = PlanTableFixture.BuildExcelText(new PlanTableFixture.Options { QuotedHeaderWithNewLine = false });
        rows = rows.Substring(rows.IndexOf("\r\n", StringComparison.Ordinal) + 2);       // sans la ligne d'en-tête
        string header = "Plan SE\tHB1\tHBA\tHB2\tVF1\tVF2\r\n";

        PlanTableParseResult result = Ok(header + rows + rows);

        Assert.Equal(PlanTableFixture.ExpectedItems.Count * 2, result.Items.Count);
        Assert.Contains(result.Warnings, w => w.Contains("en double", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------
    // Guillemets, espaces insécables, en-têtes
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void QuotedHeaderWithAnEmbeddedNewline_IsOneCell()
    {
        PlanTableParseResult result = Ok("Plan SE\t\"HB1\n(20)\"\t\"HBA\r\n(20)\"\tHB2 (8)\r\n2020Q7E 22061\tX\t\t\r\n2020Q7E 22062\t\t\tX\r\n");

        Assert.Equal(new[] { "HB1", "HBA", "HB2" }, result.Types);
        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062") }, result.Items);
    }

    [Fact]
    public void QuotedHeaderWithAnEmbeddedTab_StaysInOneColumn()
    {
        PlanTableParseResult result = Ok("Plan SE\t\"HB1\t(20)\"\tHB2\r\n2020Q7E 22061\tX\t\r\n2020Q7E 22062\t\tX\r\n");

        Assert.Equal(new[] { "HB1", "HB2" }, result.Types);
        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062") }, result.Items);
    }

    [Fact]
    public void DoubledQuotes_InsideAQuotedCell_AreOneQuote()
    {
        PlanTableParseResult result = Ok("Plan SE\t\"Châssis \"\"long\"\" (3)\"\r\n2020Q7E 22061\tX\r\n");

        Assert.Equal(new[] { "Châssis \"long\"" }, result.Types);
        Assert.Equal(new[] { I("Châssis \"long\"", "22061") }, result.Items);
    }

    [Fact]
    public void QuotedPlanCell_IsUnquoted()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\r\n\"2020Q7E 22061\"\tX\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
    }

    [Fact]
    public void QuoteInTheMiddleOfAnUnquotedCell_IsLiteral()
    {
        PlanTableParseResult result = Ok("Plan SE\t5\" tube\tHB2\r\n2020Q7E 22061\tX\t\r\n");

        Assert.Equal(new[] { "5\" tube", "HB2" }, result.Types);
        Assert.Equal(new[] { I("5\" tube", "22061") }, result.Items);
    }

    [Fact]
    public void UnterminatedQuote_DoesNotSwallowTheRestOfTheTable()
    {
        PlanTableParseResult result = Ok("Plan SE\t\"HB1\tHB2\r\n2020Q7E 22061\tX\t\r\n2020Q7E 22062\t\tX\r\n");

        Assert.Equal(new[] { I("\"HB1", "22061"), I("HB2", "22062") }, result.Items);
    }

    [Fact]
    public void TextRightAfterAClosingQuote_IsKept()
    {
        PlanTableParseResult result = Ok("Plan SE\t\"HB1\"b\r\n2020Q7E 22061\tX\r\n");

        Assert.Equal(new[] { I("HB1b", "22061") }, result.Items);
    }

    [Fact]
    public void QuotedCellEndingTheText_WithoutNewline_IsRead()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\r\n2020Q7E 22061\t\"X\"");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
    }

    [Fact]
    public void NonBreakingSpaces_AreSpaces_EverywhereInTheTable()
    {
        string nbsp = " ";
        string text = $"Plan{nbsp}SE\t{nbsp}HB1{nbsp}({nbsp}20{nbsp})\t{nbsp}\tHB2 (8)\r\n"
                      + $"2020Q7E{nbsp}22061{nbsp}\t{nbsp}X{nbsp}\t\t\r\n"
                      + $"{nbsp}2020Q7E{nbsp}{nbsp}22062\t{nbsp}\t{nbsp}\tX\r\n";

        PlanTableParseResult result = Ok(text);

        Assert.Equal(new[] { "HB1", "HB2" }, result.Types);
        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062") }, result.Items);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ZeroWidthAndFormattingCharacters_AreDropped()
    {
        PlanTableParseResult result = Ok("Plan SE\tH​B1‏ (20)\r\n2020Q7E 22​061\t﻿X​\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
    }

    [Fact]
    public void LeadingBom_BeforeAQuotedFirstCell_IsIgnored()
    {
        PlanTableParseResult result = Ok("﻿\"Plan SE\"\tHB1\r\n2020Q7E 22061\tX\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
    }

    [Fact]
    public void LeadingEmptyRows_AndTabOnlyRows_AreSkippedBeforeTheHeader()
    {
        PlanTableParseResult result = Ok("\r\n\t\t\t\r\n  \t \r\nPlan SE\tHB1\tHB2\r\n2020Q7E 22061\tX\t\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
        Assert.Equal(new[] { "HB1", "HB2" }, result.Types);
    }

    [Fact]
    public void BlankLinesBetweenRows_AreIgnored()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\tHB2\r\n\r\n2020Q7E 22061\tX\t\r\n\t\t\r\n\r\n2020Q7E 22062\t\tX\r\n\r\n\r\n");

        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062") }, result.Items);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RowsShorterThanTheHeader_AreRead_AsIfTheMissingCellsWereEmpty()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\tHBA\tHB2\tVF1\tVF2\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\r\n2020Q7E 22063\t\t\t\t\tX\r\n");

        Assert.Equal(new[] { I("HB1", "22061"), I("VF2", "22063") }, result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("« 22062 »", StringComparison.Ordinal));
    }

    [Fact]
    public void HeaderWithAnEmptyFirstCell_IsAccepted()
    {
        PlanTableParseResult result = Ok("\tHB1\tHB2\r\n2020Q7E 22061\tX\t\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
    }

    [Fact]
    public void EmptyHeaderCells_AndTheirColumns_AreIgnored()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\t\tHB2\r\n2020Q7E 22061\t\tX\tX\r\n2020Q7E 22062\tX\tSomething\t\r\n");

        // La croix et le texte placés sous une colonne sans titre ne comptent pas, et ne déclenchent aucune remarque.
        Assert.Equal(new[] { "HB1", "HB2" }, result.Types);
        Assert.Equal(new[] { I("HB2", "22061"), I("HB1", "22062") }, result.Items);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void DuplicateTypeNames_AreSeparateColumns_AndTheTypeIsListedOnce()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1 (20)\tHB1 (8)\tVF1\r\n2020Q7E 22061\tX\t\t\r\n2020Q7E 22062\t\tX\t\r\n2020Q7E 22063\t\t\tX\r\n");

        Assert.Equal(new[] { "HB1", "VF1" }, result.Types);
        Assert.Equal(new[] { I("HB1", "22061"), I("HB1", "22062"), I("VF1", "22063") }, result.Items);
    }

    [Fact]
    public void ARowWithTwoCrosses_GivesTwoLabels_InColumnOrder()
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\tHBA\tHB2\r\n2020Q7E 22061\tX\t\tX\r\n2020Q7E 22062\t\tX\t\r\n");

        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22061"), I("HBA", "22062") }, result.Items);
    }

    [Fact]
    public void ATypeNamedX_IsStillAType_WhenTheHeaderIsReallyAHeader()
    {
        PlanTableParseResult result = Ok("Plan SE\tX\tY\r\n2020Q7E 22061\tX\t\r\n2020Q7E 22062\t\tY\r\n");

        Assert.Equal(new[] { "X", "Y" }, result.Types);
        Assert.Equal(new[] { I("X", "22061"), I("Y", "22062") }, result.Items);
    }

    [Fact]
    public void RandomRowOrder_IsPreserved()
    {
        var random = new Random(4242);
        var sb = new StringBuilder("Plan SE\tHB1 (20)\tHBA (20)\tHB2 (8)\tVF1 (24)\tVF2 (36)\r\n");
        var expected = new List<LabelItem>();
        for (int k = 0; k < 60; k++)
        {
            int column = random.Next(PlanTableFixture.Types.Length);
            string plan = random.Next(21000, 23000).ToString();
            var cells = new string[6];
            Array.Fill(cells, "");
            cells[0] = "2020Q7E " + plan;
            cells[1 + column] = "X";
            sb.Append(string.Join("\t", cells)).Append("\r\n");
            expected.Add(I(PlanTableFixture.Types[column], plan));
        }

        PlanTableParseResult result = Ok(sb.ToString());

        Assert.Equal(expected, result.Items);
    }

    // ------------------------------------------------------------------------------------------------
    // Croix
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("x")]
    [InlineData("X")]
    [InlineData("×")]
    [InlineData("✓")]
    [InlineData("✔")]
    [InlineData("✔️")]
    [InlineData("1")]
    [InlineData("oui")]
    [InlineData("Oui")]
    [InlineData("OUI")]
    [InlineData("o")]
    [InlineData("O")]
    [InlineData("yes")]
    [InlineData("Yes")]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData(" X ")]
    [InlineData(" x ")]
    [InlineData("\"X\"")]
    [InlineData("Ｘ")]
    public void CrossMarks_AreRecognised(string cell)
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\r\n2020Q7E 22061\t" + cell + "\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("non")]
    [InlineData("NON")]
    [InlineData("-")]
    [InlineData("xx")]
    [InlineData("n")]
    [InlineData("X1")]
    [InlineData("ok")]
    [InlineData("✗")]
    [InlineData("22061")]
    public void OtherCells_AreIgnored_ButReportedInAWarning(string cell)
    {
        PlanTableParseResult result = Ok("Plan SE\tHB1\tHB2\r\n2020Q7E 22061\tX\t" + cell + "\r\n");

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
        string warning = Assert.Single(result.Warnings);
        Assert.Equal($"1 cellule(s) non vide(s) ignorée(s) : « {cell} »", warning);
    }

    [Fact]
    public void IgnoredCells_AreCounted_AndAtMostThreeDistinctExamplesAreQuoted()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2\tVF1", "2020Q7E 22061\tX\ta\tb", "2020Q7E 22062\tX\tc\td", "2020Q7E 22063\tX\ta\t"));

        string warning = Assert.Single(result.Warnings);
        Assert.StartsWith("5 cellule(s) non vide(s) ignorée(s) : « a », « b », « c »", warning, StringComparison.Ordinal);
        Assert.Equal(3, Count(warning, '«'));
        Assert.EndsWith("…", warning, StringComparison.Ordinal);        // d'autres exemples existent
    }

    [Fact]
    public void IgnoredCells_ThatAreAllTheSame_AreQuotedOnce()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\tX\tok", "2020Q7E 22062\tX\tok", "2020Q7E 22063\tX\tok"));

        Assert.Equal("3 cellule(s) non vide(s) ignorée(s) : « ok »", Assert.Single(result.Warnings));
    }

    [Fact]
    public void LongIgnoredCell_IsShortenedInTheWarning()
    {
        string junk = new('z', 80);
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\tX\t" + junk));

        string warning = Assert.Single(result.Warnings);
        Assert.DoesNotContain(junk, warning, StringComparison.Ordinal);
        Assert.Contains("…", warning, StringComparison.Ordinal);
        Assert.True(warning.Length < 100);
    }

    [Fact]
    public void RowWithAPlanButNoCross_IsIgnored_AndReported()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\tX\t", "2020Q7E 22062\t\t", "2020Q7E 22063\t\tX"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22063") }, result.Items);
        Assert.Equal("1 ligne(s) sans croix ignorée(s) : « 22062 »", Assert.Single(result.Warnings));
    }

    [Fact]
    public void RowsWithoutCross_AreCounted_AndExamplesAreLimited()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1", "2020Q7E 22061\tX", "2020Q7E 22062", "2020Q7E 22063", "2020Q7E 22064", "2020Q7E 22065", "2020Q7E 22066"));

        string warning = Assert.Single(result.Warnings);
        Assert.StartsWith("5 ligne(s) sans croix ignorée(s) : « 22062 », « 22063 », « 22064 »", warning, StringComparison.Ordinal);
        Assert.Equal(3, Count(warning, '«'));
    }

    [Fact]
    public void CrossWithoutPlan_IsIgnored_AndReported()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\tX\t", "\t\tX", " \tX\t"));

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("2 ligne(s) avec une croix mais sans n° de plan", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanWithoutDigit_IsKept_ButReported()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1", "2020Q7E 22061\tX", "Total\tX"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB1", "Total") }, result.Items);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("sans chiffre", warning, StringComparison.Ordinal);
        Assert.Contains("« Total »", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SameTypeAndPlanTwice_AreBothKept_AndReported()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\tX\t", "2020Q7E 22061\tX\tX", "2020Q7E 22062\t\tX"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB1", "22061"), I("HB2", "22061"), I("HB2", "22062") }, result.Items);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("1 étiquette(s) en double", warning, StringComparison.Ordinal);
        Assert.Contains("« HB1 22061 »", warning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------
    // Mode liste « Type <tab> Plan »
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ListMode_WithATitleRow_SkipsIt()
    {
        PlanTableParseResult result = Ok(Table("Type\tPlan", "HB1\t22061", "HB2\t2020Q7E 22062", "VF1\t22063"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062"), I("VF1", "22063") }, result.Items);
        Assert.Equal(new[] { "HB1", "HB2", "VF1" }, result.Types);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ListMode_WithoutATitleRow_KeepsEveryRow()
    {
        PlanTableParseResult result = Ok(Table("HB1\t22061", "HB1\t22062", "VF2\t22170"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB1", "22062"), I("VF2", "22170") }, result.Items);
        Assert.Equal(new[] { "HB1", "VF2" }, result.Types);
    }

    [Fact]
    public void ListMode_WithASingleRow_IsAccepted()
    {
        PlanTableParseResult result = Ok("HB1\t22062");

        Assert.Equal(new[] { I("HB1", "22062") }, result.Items);
    }

    [Fact]
    public void ListMode_NormalisesTheTypeAndExtractsThePlan()
    {
        PlanTableParseResult result = Ok(Table("\"HB1\n(20)\"\t2020Q7E 22061", "VF2 (36)\t 2020Q7E-22062"));

        Assert.Equal(new[] { I("HB1", "22061"), I("VF2", "22062") }, result.Items);
    }

    [Fact]
    public void ListMode_ExtraColumns_AreIgnored_AndReported()
    {
        PlanTableParseResult result = Ok(Table("HB1\t22061\tcommentaire", "HB2\t22062\t"));

        Assert.Equal(new[] { I("HB1", "22061"), I("HB2", "22062") }, result.Items);
        Assert.Equal("1 cellule(s) non vide(s) ignorée(s) : « commentaire »", Assert.Single(result.Warnings));
    }

    [Fact]
    public void ListMode_IsNotUsed_WhenTheTableHasCrosses()
    {
        PlanTableParseResult result = Ok(Table("Plan SE\tHB1", "2020Q7E 22061\tX"));

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);      // et non « Plan SE / HB1 », « 2020Q7E 22061 / X »
    }

    [Fact]
    public void ListMode_NeedsTwoFilledCellsOnEveryRow()
    {
        PlanTableParseResult result = Failed(Table("HB1\t22061", "HB2\t", "VF1\t22063"));

        Assert.Contains("Aucune croix", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ListMode_NeedsAtLeastOnePlanWithADigit()
    {
        Failed(Table("HB1\tabc", "HB2\tdef"));
    }

    [Fact]
    public void ListMode_IsNotMistakenFor_AHeaderRowCopiedAlone()
    {
        PlanTableParseResult result = Failed("Plan SE\tHB1 (20)\tHBA (20)\tHB2 (8)\tVF1 (24)\tVF2 (36)");

        Assert.Contains("Aucune croix", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableWithoutAnyCross_IsNotMistakenForAList()
    {
        PlanTableParseResult result = Failed(Table("Plan SE\tHB1\tHBA\tHB2", "2020Q7E 22061\t\t\t", "2020Q7E 22062\t\t\t"));

        Assert.Contains("Aucune croix", result.Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------
    // Erreurs
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n")]
    [InlineData("\t\t\t")]
    [InlineData(" \t \r\n \t\t\r\n")]
    [InlineData("  ")]
    [InlineData("﻿")]
    [InlineData("​\r\n")]
    [InlineData("\"\"\r\n\"\"\t\"\"")]
    public void EmptyClipboard_GivesTheEmptyMessage(string? text)
    {
        PlanTableParseResult result = Failed(text);

        Assert.StartsWith("Le presse-papiers est vide", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bonjour")]
    [InlineData("Plan SE")]
    [InlineData("Plan SE\r\n2020Q7E 22061\r\n2020Q7E 22062\r\n")]
    [InlineData("2020Q7E 22061\r\n")]
    public void NoTypeHeader_GivesAnErrorAskingForTheHeaderRow(string text)
    {
        PlanTableParseResult result = Failed(text);

        Assert.Contains("en-tête", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DataRowsCopiedWithoutTheHeaderRow_AreRefused_InsteadOfTreatingTheCrossAsAType()
    {
        PlanTableParseResult result = Failed("2020Q7E 22061\tX\t\t\t\t\r\n2020Q7E 22062\tX\t\t\t\t\r\n2020Q7E 22063\t\t\tX\t\t\r\n");

        Assert.Contains("en-tête", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderOnly_GivesTheNoCrossMessage()
    {
        PlanTableParseResult result = Failed("Plan SE\tHB1\tHB2\r\n");

        Assert.Equal(
            "Aucune croix (X) trouvée : sélectionnez le tableau AVEC sa ligne d'en-tête (Plan SE, HB1, HBA...) puis recopiez-le (Ctrl+C)",
            result.Error);
    }

    [Fact]
    public void RowsWithoutAnyCross_GiveTheNoCrossMessage()
    {
        PlanTableParseResult result = Failed(Table("Plan SE\tHB1\tHB2", "2020Q7E 22061\t\t", "2020Q7E 22062\t\t"));

        Assert.StartsWith("Aucune croix (X) trouvée", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossesWithoutAnyPlan_GiveAnError()
    {
        PlanTableParseResult result = Failed(Table("Plan SE\tHB1\tHB2", "\tX\t", "\t\tX"));

        Assert.Contains("n° de plan", result.Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------
    // Garde-fous
    // ------------------------------------------------------------------------------------------------

    private static string LongTable(int rows)
    {
        var sb = new StringBuilder("Plan SE\tHB1\r\n");
        for (int k = 0; k < rows; k++) sb.Append("2020Q7E ").Append(10000 + k).Append("\tX\r\n");
        return sb.ToString();
    }

    [Fact]
    public void AtMost5000Labels_AreKept_WithAWarning()
    {
        PlanTableParseResult result = Ok(LongTable(5001));

        Assert.Equal(5000, result.Items.Count);
        Assert.Equal(I("HB1", "10000"), result.Items[0]);
        Assert.Equal(I("HB1", "14999"), result.Items[^1]);
        Assert.Contains(result.Warnings, w => w.Contains("Plus de 5000 étiquettes", StringComparison.Ordinal));
    }

    [Fact]
    public void Exactly5000Labels_AreKept_WithoutWarning()
    {
        PlanTableParseResult result = Ok(LongTable(5000));

        Assert.Equal(5000, result.Items.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ListMode_IsCappedToo()
    {
        var sb = new StringBuilder();
        for (int k = 0; k < 5200; k++) sb.Append("HB1\t").Append(10000 + k).Append("\r\n");

        PlanTableParseResult result = Ok(sb.ToString());

        Assert.Equal(5000, result.Items.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Plus de 5000 étiquettes", StringComparison.Ordinal));
    }

    [Fact]
    public void MoreThan200Columns_AreNotRead_WithAWarning()
    {
        var header = new StringBuilder("Plan SE");
        for (int c = 1; c <= 250; c++) header.Append("\tT").Append(c);
        var row = new string[251];
        Array.Fill(row, "");
        row[0] = "2020Q7E 22061";
        row[3] = "X";       // colonne lue (T3)
        row[240] = "X";     // au-delà de 200 colonnes : ignorée

        PlanTableParseResult result = Ok(header + "\r\n" + string.Join("\t", row) + "\r\n");

        Assert.Equal(new[] { I("T3", "22061") }, result.Items);
        Assert.Equal(PlanTableParser.MaxColumns - 1, result.Types.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Tableau trop large", StringComparison.Ordinal));
    }

    [Fact]
    public void Exactly200Columns_AreRead_WithoutWarning()
    {
        var header = new StringBuilder("Plan SE");
        for (int c = 1; c < PlanTableParser.MaxColumns; c++) header.Append("\tT").Append(c);
        var row = new string[PlanTableParser.MaxColumns];
        Array.Fill(row, "");
        row[0] = "2020Q7E 22061";
        row[PlanTableParser.MaxColumns - 1] = "X";

        PlanTableParseResult result = Ok(header + "\r\n" + string.Join("\t", row) + "\r\n");

        Assert.Equal(new[] { I("T" + (PlanTableParser.MaxColumns - 1), "22061") }, result.Items);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void HugeTextWithManyRows_IsCut_AndStillParsedQuickly()
    {
        var sb = new StringBuilder("Plan SE\tHB1\r\n2020Q7E 22061\tX\r\n");
        for (int k = 0; k < 200_000; k++) sb.Append("2020Q7E ").Append(k).Append("\r\n");

        PlanTableParseResult result = Ok(sb.ToString());

        Assert.Equal(new[] { I("HB1", "22061") }, result.Items);
        Assert.Contains(result.Warnings, w => w.Contains("Tableau trop long", StringComparison.Ordinal));
    }

    [Fact]
    public void ManyUnterminatedQuotes_DoNotMakeTheParserQuadratic()
    {
        string text = "Plan SE\tHB1\r\n" + string.Concat(Enumerable.Repeat("\"\t\"\"\"\"\t\"\"\"\r\n", 20_000));

        PlanTableParseResult result = PlanTableParser.Parse(text);      // doit simplement se terminer

        Assert.NotNull(result);

        // Nombre impair de guillemets d'ouverture : le dernier n'est jamais refermé.
        Assert.NotNull(PlanTableParser.Parse("Plan SE\tHB1\r\n" + string.Concat(Enumerable.Repeat("\"a\r\n", 20_001))));
    }

    // ------------------------------------------------------------------------------------------------
    // ExtractPlanNumber / NormalizeTypeName
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2020Q7E 22061", "22061")]
    [InlineData("2020Q7E-22061", "22061")]
    [InlineData("2020Q7E_22061", "22061")]
    [InlineData("2020Q7E/22061", "22061")]
    [InlineData("2020Q7E22061", "22061")]       // chiffres de fin
    [InlineData("2020q7e 22061", "22061")]
    [InlineData("22061", "22061")]
    [InlineData("  22061  ", "22061")]
    [InlineData("2020Q7E 22061 ", "22061")]
    [InlineData(" 2020Q7E 22061 ", "22061")]
    [InlineData("2020Q7E\t22061", "22061")]
    [InlineData("2020Q7E\n22061", "22061")]
    [InlineData("2020Q7E\r\n22061\r\n", "22061")]
    [InlineData("ABC123", "123")]
    [InlineData("A1B2", "2")]
    [InlineData("2020Q7E 22061B", "22061B")]    // ne finit pas par un chiffre : dernier mot
    [InlineData("22061 bis", "bis")]
    [InlineData("ABC DEF", "DEF")]
    [InlineData("ABC", "ABC")]
    [InlineData("Plan SE", "SE")]
    [InlineData("12345", "12345")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(" ", "")]
    [InlineData("\t\r\n", "")]
    [InlineData(null, "")]
    public void ExtractPlanNumber_Cases(string? cell, string expected)
    {
        Assert.Equal(expected, PlanTableParser.ExtractPlanNumber(cell));
    }

    [Theory]
    [InlineData("HB1 (20)", "HB1")]
    [InlineData("HBA (20)", "HBA")]
    [InlineData("HB2 (8)", "HB2")]
    [InlineData("VF1 (24)", "VF1")]
    [InlineData("VF2 (36)", "VF2")]
    [InlineData("HB1(20)", "HB1")]
    [InlineData("HB1  (20)", "HB1")]
    [InlineData("  HB1 (20)  ", "HB1")]
    [InlineData("HB1\n(20)", "HB1")]
    [InlineData("HB1\r\n(20)", "HB1")]
    [InlineData("HB1 (20)", "HB1")]
    [InlineData("HB1  ( 20 )", "HB1")]
    [InlineData("HB1", "HB1")]
    [InlineData("hb1", "hb1")]
    [InlineData("HB 1", "HB 1")]
    [InlineData("H​B1", "HB1")]
    [InlineData("HB1 (20) (bis)", "HB1 (20)")]           // un seul groupe final est retiré
    [InlineData("Châssis (long) (12)", "Châssis (long)")]
    [InlineData("HB1 ((20))", "HB1")]
    [InlineData("HB1 (20", "HB1 (20")]                    // parenthèses non équilibrées : rien n'est retiré
    [InlineData("HB1 20)", "HB1 20)")]
    [InlineData("(20)", "(20)")]                           // on ne perd pas la colonne
    [InlineData("HB1 （20）", "HB1")]
    [InlineData("(20) HB1", "(20) HB1")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeTypeName_Cases(string? header, string expected)
    {
        Assert.Equal(expected, PlanTableParser.NormalizeTypeName(header));
    }

    // ------------------------------------------------------------------------------------------------
    // Découpage bas niveau
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void SplitTable_KeepsColumnAlignment_AndDropsEmptyRowsAndTrailingEmptyCells()
    {
        List<List<string>> rows = PlanTableParser.SplitTable("a\t\tc\t\t\r\n\r\n\t\t\r\n\"x\ty\"\t\"p\"\"q\"\r\nlast", out bool columns, out bool rowsCut);

        Assert.False(columns);
        Assert.False(rowsCut);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "a", "", "c" }, rows[0]);
        Assert.Equal(new[] { "x y", "p\"q" }, rows[1]);
        Assert.Equal(new[] { "last" }, rows[2]);
    }

    [Fact]
    public void SplitTable_HandlesCrlfLfAndCrRowEndings_Alike()
    {
        foreach (string newline in new[] { "\r\n", "\n", "\r" })
        {
            List<List<string>> rows = PlanTableParser.SplitTable("a\tb" + newline + "c\td" + newline, out _, out _);

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "a", "b" }, rows[0]);
            Assert.Equal(new[] { "c", "d" }, rows[1]);
        }
    }

    [Fact]
    public void CleanCell_CollapsesWhitespace_AndDropsInvisibleCharacters()
    {
        Assert.Equal("a b c", PlanTableParser.CleanCell("  a  \t b\r\n c ​"));
        Assert.Equal("", PlanTableParser.CleanCell("  ​﻿\0 "));
        Assert.Equal("", PlanTableParser.CleanCell(null));
        Assert.Equal("é à ç", PlanTableParser.CleanCell("é  à ç"));
    }

    // ------------------------------------------------------------------------------------------------
    // Robustesse : jamais d'exception, résultats toujours cohérents
    // ------------------------------------------------------------------------------------------------

    private static void AssertCoherent(string text)
    {
        PlanTableParseResult result = PlanTableParser.Parse(text);

        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.NotNull(result.Types);
        Assert.NotNull(result.Warnings);

        Assert.Equal(result.Error is null, result.Items.Count > 0);
        if (result.Error is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Empty(result.Types);
            Assert.Empty(result.Warnings);
            return;
        }

        Assert.True(result.Items.Count <= PlanTableParser.MaxLabels);
        foreach (LabelItem item in result.Items)
        {
            AssertCleanField(item.Type);
            AssertCleanField(item.Plan);
        }

        Assert.Equal(result.Types.Count, result.Types.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string type in result.Types) AssertCleanField(type);
        foreach (string warning in result.Warnings) Assert.False(string.IsNullOrWhiteSpace(warning));
    }

    private static void AssertCleanField(string value)
    {
        Assert.False(string.IsNullOrEmpty(value));
        Assert.Equal(value.Trim(), value);
        Assert.DoesNotContain('\t', value);
        Assert.DoesNotContain('\r', value);
        Assert.DoesNotContain('\n', value);
        Assert.DoesNotContain(value, c => char.IsControl(c));
    }

    [Fact]
    public void Fuzz_RandomCharacters_NeverThrow_AndStayCoherent()
    {
        var random = new Random(20260101);
        const string alphabet = "\t\t\t\r\n\r\n\"\"Xx×✓1  ﻿()0123456789ABHVFPlan 2020Q7E-​,;éà";
        for (int k = 0; k < 2000; k++)
        {
            int length = random.Next(0, 160);
            var sb = new StringBuilder(length);
            for (int c = 0; c < length; c++) sb.Append(alphabet[random.Next(alphabet.Length)]);

            AssertCoherent(sb.ToString());
        }
    }

    [Fact]
    public void Fuzz_RandomUnicode_IncludingLoneSurrogates_NeverThrows()
    {
        var random = new Random(7);
        for (int k = 0; k < 2000; k++)
        {
            int length = random.Next(0, 80);
            var sb = new StringBuilder(length);
            for (int c = 0; c < length; c++)
            {
                // moitié de caractères de structure, moitié de tout l'espace UTF-16 (y compris les demi-paires de substitution)
                sb.Append(random.Next(3) == 0 ? "\t\r\n\"X "[random.Next(6)] : (char)random.Next(0, 0x10000));
            }

            AssertCoherent(sb.ToString());
        }
    }

    [Fact]
    public void Fuzz_TableLikeText_MadeOfRealisticPieces_NeverThrows_AndStaysCoherent()
    {
        string[] pieces =
        {
            "Plan SE", "HB1 (20)", "\"HB1\n(20)\"", "HBA (20)", "HB2 (8)", "VF1", "VF2 (36)", "2020Q7E 22061", "2020Q7E 22062", "22063", "X", "x", "×", "✓", "1", "oui", "0", "-",
            "", "", "", " ", "\"", "\"\"", "\"abc", "Type", "Plan", "Total",
        };
        string[] separators = { "\t", "\t", "\t", "\r\n", "\r\n", "\n", "\r", "\t\t" };

        var random = new Random(99);
        for (int k = 0; k < 2000; k++)
        {
            int count = random.Next(0, 60);
            var sb = new StringBuilder();
            if (random.Next(10) == 0) sb.Append('﻿');
            for (int p = 0; p < count; p++)
            {
                sb.Append(pieces[random.Next(pieces.Length)]);
                sb.Append(separators[random.Next(separators.Length)]);
            }

            AssertCoherent(sb.ToString());
        }
    }

    [Fact]
    public void Fuzz_MutatedRealTable_NeverThrows_AndStaysCoherent()
    {
        string baseText = PlanTableFixture.BuildExcelText();
        var random = new Random(31337);
        for (int k = 0; k < 300; k++)
        {
            var chars = new StringBuilder(baseText);
            int edits = random.Next(1, 12);
            for (int e = 0; e < edits && chars.Length > 0; e++)
            {
                int at = random.Next(chars.Length);
                switch (random.Next(3))
                {
                    case 0: chars.Remove(at, 1); break;
                    case 1: chars.Insert(at, "\t\r\n\"X "[random.Next(6)]); break;
                    default: chars[at] = "\t\r\n\"Xx "[random.Next(6)]; break;
                }
            }

            AssertCoherent(chars.ToString());
        }
    }

    [Fact]
    public void ExtractPlanNumber_AndNormalizeTypeName_NeverThrow_OnGarbage()
    {
        var random = new Random(5);
        for (int k = 0; k < 2000; k++)
        {
            int length = random.Next(0, 40);
            var sb = new StringBuilder(length);
            for (int c = 0; c < length; c++) sb.Append((char)random.Next(0, 0x10000));
            string text = sb.ToString();

            string plan = PlanTableParser.ExtractPlanNumber(text);
            string type = PlanTableParser.NormalizeTypeName(text);

            Assert.Equal(plan.Trim(), plan);
            Assert.Equal(type.Trim(), type);
            Assert.Equal(plan.Length == 0, PlanTableParser.CleanCell(text).Length == 0);
            Assert.Equal(type.Length == 0, PlanTableParser.CleanCell(text).Length == 0);
        }
    }
}
