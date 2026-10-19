using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Lecture des listes de codes-barres (ListFileReader) : TXT, CSV, XLSX, DOCX et texte collé.
/// Tests purs (aucune dépendance à System.Drawing) : ils tournent aussi sous Linux. Les classeurs et documents sont fabriqués
/// ici avec System.IO.Compression (voir OfficeFixtures), y compris les fichiers hostiles (bombe ZIP, XXE, noms piégés...).
/// </summary>
public class ListFileReaderTests
{
    // ------------------------------------------------------------------------------------------------
    // Outils
    // ------------------------------------------------------------------------------------------------

    private static ListReadResult Read(byte[] bytes, string name, bool skip = false)
    {
        using var ms = new MemoryStream(bytes);
        return ListFileReader.ReadStream(ms, name, skip);
    }

    private static ListReadResult Ok(ListReadResult r)
    {
        Assert.True(r.Error is null, "Erreur inattendue : " + r.Error);
        return r;
    }

    private static ListReadResult Failed(ListReadResult r)
    {
        Assert.False(string.IsNullOrWhiteSpace(r.Error), "Une erreur était attendue.");
        Assert.Empty(r.Values);
        Assert.Empty(r.Warnings);
        return r;
    }

    private static void HasWarning(ListReadResult r, string fragment)
        => Assert.True(r.Warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal)),
            $"Avertissement « {fragment} » attendu ; reçu : [{string.Join(" | ", r.Warnings)}]");

    private static void NoWarning(ListReadResult r, string fragment)
        => Assert.False(r.Warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal)),
            $"Avertissement « {fragment} » inattendu : [{string.Join(" | ", r.Warnings)}]");

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    /// <summary>Windows-1252 : Latin-1 sauf quelques signes (€, guillemets typographiques) placés de 0x80 à 0x9F.</summary>
    private static byte[] Cp1252(string s)
    {
        var bytes = new List<byte>();
        foreach (char c in s)
        {
            bytes.Add(c switch
            {
                '\u20AC' => 0x80,
                '\u201C' => 0x93,
                '\u201D' => 0x94,
                '\u2019' => 0x92,
                _ => (byte)c,
            });
        }

        return bytes.ToArray();
    }

    private static string[] Lines(int count, string prefix = "C") => Enumerable.Range(1, count).Select(i => prefix + i.ToString("D5")).ToArray();

    private static byte[] Xlsx(string sheetXml, string? sharedStrings = null, Action<OfficeFixtures.XlsxBuilder>? configure = null)
    {
        var builder = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", sheetXml);
        if (sharedStrings != null) builder.SharedStrings(sharedStrings);
        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>Classeur d'une colonne A de cellules « chaîne en ligne » (aucune table de chaînes partagées).</summary>
    private static byte[] InlineColumnXlsx(IEnumerable<string> values)
    {
        var rows = new StringBuilder();
        int r = 1;
        foreach (string v in values)
        {
            rows.Append(OfficeFixtures.Row(r, OfficeFixtures.CellInline("A" + r, v)));
            r++;
        }

        return Xlsx(OfficeFixtures.SheetXml(rows.ToString()));
    }

    /// <summary>Flux sans fin (non positionnable) : sert à vérifier que la lecture est bornée.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Produced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = (byte)(i % 2 == 0 ? 'A' : '\n');
            Produced += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------------------------------------
    // Contrat général
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void FileDialogFilter_ListsTheSupportedFormats_InFrench()
    {
        string filter = ListFileReader.FileDialogFilter;
        foreach (string ext in ListFileReader.SupportedExtensions)
        {
            Assert.Contains("*" + ext, filter);
        }

        Assert.Contains("Tous les fichiers", filter);
        Assert.Equal(0, filter.Split('|').Length % 2);   // paires « libellé|motif »
    }

    [Fact]
    public void SupportedExtensions_AreTheFourDocumentedFormats()
    {
        Assert.Equal(new[] { ".txt", ".csv", ".xlsx", ".docx" }, ListFileReader.SupportedExtensions);
        Assert.Equal(5000, ListFileReader.MaxValues);
        Assert.Equal(20L * 1024 * 1024, ListFileReader.MaxFileBytes);
    }

    [Fact]
    public void ReadStream_DoesNotCloseTheCallersStream()
    {
        using var ms = new MemoryStream(Utf8("A\nB\n"));
        ListFileReader.ReadStream(ms, "a.txt", false);
        Assert.True(ms.CanRead);
    }

    [Fact]
    public void ReadStream_NullStream_IsAnErrorNotAnException()
    {
        Failed(ListFileReader.ReadStream(null!, "a.txt", false));
    }

    [Fact]
    public void Source_NamesTheFile()
    {
        Assert.Equal("fichier « liste.txt »", Ok(Read(Utf8("A"), "C:\\dossier\\liste.txt")).Source);
        Assert.Equal("fichier « liste.txt »", Ok(Read(Utf8("A"), "/home/x/liste.txt")).Source);
        Assert.Equal("fichier", Read(Utf8("A"), "").Source);
    }

    [Theory]
    [InlineData("liste.pdf")]
    [InlineData("liste")]
    [InlineData("liste.")]
    [InlineData("liste.png")]
    [InlineData("liste.tsv")]
    [InlineData(".hidden")]
    public void UnknownExtension_ListsTheSupportedFormats(string name)
    {
        ListReadResult r = Failed(Read(Utf8("A"), name));
        Assert.Contains(".txt", r.Error);
        Assert.Contains(".csv", r.Error);
        Assert.Contains(".xlsx", r.Error);
        Assert.Contains(".docx", r.Error);
    }

    [Theory]
    [InlineData("liste.xls")]
    [InlineData("liste.XLS")]
    [InlineData("liste.doc")]
    [InlineData("liste.rtf")]
    [InlineData("liste.odt")]
    [InlineData("liste.ods")]
    [InlineData("liste.xlsb")]
    [InlineData("liste.xlsm")]
    [InlineData("liste.docm")]
    public void LegacyFormats_AskToSaveAsXlsxOrDocx(string name)
    {
        ListReadResult r = Failed(Read(Utf8("A"), name));
        Assert.Contains("enregistrez-le en .xlsx / .docx", r.Error);
    }

    [Fact]
    public void ExtensionMatchingIsCaseInsensitive()
    {
        Assert.Equal(new[] { "A", "B" }, Ok(Read(Utf8("A\nB"), "LISTE.TXT")).Values);
        Assert.Equal(new[] { "A", "B" }, Ok(Read(Utf8("A;x\nB;y"), "Liste.CsV")).Values);
        Assert.Equal(new[] { "A", "B" }, Ok(Read(OfficeFixtures.SimpleXlsx(new[] { "A", "B" }), "LISTE.XLSX")).Values);
        Assert.Equal(new[] { "A", "B" }, Ok(Read(OfficeFixtures.SimpleDocx(new[] { "A", "B" }), "LISTE.Docx")).Values);
    }

    [Fact]
    public void EmptyStream_IsAnError_ForEveryFormat()
    {
        foreach (string name in new[] { "a.txt", "a.csv", "a.xlsx", "a.docx" })
        {
            ListReadResult r = Failed(Read(Array.Empty<byte>(), name));
            Assert.Contains("Aucune valeur trouvée", r.Error);
            Assert.Contains("vide", r.Error);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Nettoyage des valeurs
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("A", "A")]
    [InlineData("  A  ", "A")]
    [InlineData("A  B", "A  B")]                         // rien n'est réduit à l'intérieur
    [InlineData("\u00A0A\u00A0", "A")]                   // espace insécable
    [InlineData("A\u00A0B", "A B")]
    [InlineData("A\u202FB", "A B")]                      // espace insécable fine
    [InlineData("A\u2007B", "A B")]                      // espace tabulaire
    [InlineData("A\u3000B", "A B")]                      // espace idéographique
    [InlineData("A\u200BB", "AB")]                       // largeur nulle
    [InlineData("\uFEFFA", "A")]                         // BOM
    [InlineData("A\u00ADB", "AB")]                       // trait d'union conditionnel
    [InlineData("A\u200E\u200FB", "AB")]
    [InlineData("A\tB", "A B")]
    [InlineData("A\r\nB", "A B")]                        // CRLF = une seule espace
    [InlineData("A\nB", "A B")]
    [InlineData("A\u0001B", "AB")]                       // contrôle supprimé
    [InlineData("A\u007FB", "AB")]
    [InlineData("\u0085A", "A")]
    [InlineData(" \u00A0\u200B ", "")]
    [InlineData("É-12", "É-12")]                         // les accents sont gardés (la validation viendra plus tard)
    public void Clean_Theory(string raw, string expected)
    {
        Assert.Equal(expected, ListClean.Clean(raw));
    }

    [Fact]
    public void Clean_Null_IsEmpty() => Assert.Equal("", ListClean.Clean(null));

    // ------------------------------------------------------------------------------------------------
    // Texte collé
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ReadText_ExcelColumn_CrlfWithTrailingNewline()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("A001\r\nA002\r\nA003\r\n", false));
        Assert.Equal(new[] { "A001", "A002", "A003" }, r.Values);
        Assert.Empty(r.Warnings);
        Assert.Equal("presse-papiers", r.Source);
    }

    [Theory]
    [InlineData("A\nB\nC")]
    [InlineData("A\r\nB\r\nC")]
    [InlineData("A\rB\rC")]
    [InlineData("A\r\nB\nC\r")]
    [InlineData("\r\nA\n\nB\n\n\nC\n\n")]
    public void ReadText_AnyNewlineConvention(string text)
    {
        Assert.Equal(new[] { "A", "B", "C" }, Ok(ListFileReader.ReadText(text, false)).Values);
    }

    [Fact]
    public void ReadText_QuotedCells_FollowExcelRules()
    {
        // Excel entoure d'une paire de guillemets toute cellule contenant un saut de ligne, une tabulation ou un guillemet.
        string text = "\"Ligne 1\nLigne 2\"\r\n\"Dit \"\"oui\"\"\"\r\nplain\r\n\"tab\there\"\r\n";
        ListReadResult r = Ok(ListFileReader.ReadText(text, false));
        Assert.Equal(new[] { "Ligne 1 Ligne 2", "Dit \"oui\"", "plain", "tab here" }, r.Values);
    }

    [Fact]
    public void ReadText_TwoColumns_KeepsTheFirstAndWarns()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("A\t1\t\r\nB\t2\r\nC\t3\r\n", false));
        Assert.Equal(new[] { "A", "B", "C" }, r.Values);
        HasWarning(r, "première colonne");
    }

    [Fact]
    public void ReadText_EmptyOtherColumns_NoWarning()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("A\t\t\r\nB\t \t\r\n", false));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ReadText_NonBreakingSpacesAndInvisibleCharacters_AreCleaned()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("AB\u00A012\r\n\u00A0X\u00A0\r\n\uFEFFY\u200B\r\n", false));
        Assert.Equal(new[] { "AB 12", "X", "Y" }, r.Values);
    }

    [Fact]
    public void ReadText_InnerSpacesAreKept()
    {
        Assert.Equal(new[] { "A  B   C" }, Ok(ListFileReader.ReadText("  A  B   C  ", false)).Values);
    }

    [Fact]
    public void ReadText_BlankLinesBetweenValues_AreCountedInAWarning()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("A\r\n\r\n   \r\nB\r\n", false));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        HasWarning(r, "2 ligne(s) vide(s) ignorée(s)");
    }

    [Fact]
    public void ReadText_LeadingAndTrailingBlankLines_AreNotWorthAWarning()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("\r\n\r\nA\r\nB\r\n\r\n\r\n", false));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ReadText_EmptyFirstCell_IsIgnored()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("\tOTHER\r\nA\tB\r\n", false));
        Assert.Equal(new[] { "A" }, r.Values);
    }

    [Fact]
    public void ReadText_SkipFirstLine_DropsTheFirstNonEmptyEntry()
    {
        Assert.Equal(new[] { "A001", "A002" }, Ok(ListFileReader.ReadText("Code\r\nA001\r\nA002\r\n", true)).Values);
        Assert.Equal(new[] { "A001", "A002" }, Ok(ListFileReader.ReadText("\r\n \r\nCode\r\nA001\r\nA002\r\n", true)).Values);
        Assert.Equal(new[] { "Code", "A001" }, Ok(ListFileReader.ReadText("Code\r\nA001\r\n", false)).Values);
    }

    [Fact]
    public void ReadText_SkipFirstLine_OnASingleValue_LeavesNothing()
    {
        ListReadResult r = ListFileReader.ReadText("Code\r\n", true);
        Assert.NotNull(r.Error);
        Assert.Contains("Aucune valeur trouvée", r.Error);
        Assert.Contains("titre", r.Error);
        Assert.Empty(r.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData(" \t \r\n \u00A0")]
    public void ReadText_EmptyClipboard_IsAnError(string? text)
    {
        ListReadResult r = Failed(ListFileReader.ReadText(text, false));
        Assert.Contains("Le presse-papiers est vide", r.Error);
        Assert.Equal("presse-papiers", r.Source);
    }

    [Fact]
    public void ReadText_NothingButInvisibleCharacters_IsAnErrorToo()
    {
        ListReadResult r = Failed(ListFileReader.ReadText("\u200B\r\n\u200B\r\n", false));
        Assert.Contains("Aucune valeur trouvée", r.Error);
    }

    [Fact]
    public void ReadText_CustomSource_IsReported()
    {
        ListReadResult ok = Ok(ListFileReader.ReadText("A", false, "glisser-déposer"));
        Assert.Equal("glisser-déposer", ok.Source);
        ListReadResult empty = Failed(ListFileReader.ReadText("", false, "glisser-déposer"));
        Assert.Contains("Aucune valeur trouvée", empty.Error);
        Assert.Equal("glisser-déposer", empty.Source);
    }

    [Fact]
    public void ReadText_UnclosedQuote_DoesNotSwallowTheRest()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("\"abc\r\nB\r\nC\r\n", false));
        Assert.Equal(new[] { "\"abc", "B", "C" }, r.Values);
    }

    [Fact]
    public void ReadText_QuoteFollowedByText_IsLiteral()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("\"Bonjour\" dit-il\r\n5\" pipe\r\n", false));
        Assert.Equal(new[] { "\"Bonjour\" dit-il", "5\" pipe" }, r.Values);
    }

    [Fact]
    public void ReadText_EmptyQuotedField_IsEmpty()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("A\r\n\"\"\r\nB\r\n", false));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        HasWarning(r, "1 ligne(s) vide(s)");
    }

    [Fact]
    public void ReadText_ByteOrderMark_IsIgnored()
    {
        Assert.Equal(new[] { "A", "B" }, Ok(ListFileReader.ReadText("\uFEFFA\r\nB", false)).Values);
    }

    [Fact]
    public void ReadText_Cap_AtExactlyTheLimit_NoWarning()
    {
        string text = string.Join("\r\n", Lines(5000));
        ListReadResult r = Ok(ListFileReader.ReadText(text, false));
        Assert.Equal(5000, r.Values.Count);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ReadText_Cap_OneOver_KeepsTheFirst5000AndWarns()
    {
        string text = string.Join("\r\n", Lines(5001));
        ListReadResult r = Ok(ListFileReader.ReadText(text, false));
        Assert.Equal(5000, r.Values.Count);
        Assert.Equal("C00001", r.Values[0]);
        Assert.Equal("C05000", r.Values[^1]);
        HasWarning(r, "Seules les 5000 premières lignes ont été gardées");
    }

    [Fact]
    public void ReadText_Cap_WithATitle_KeepsThe5000ValuesAfterIt()
    {
        // 1 titre + 5000 valeurs : rien n'est perdu, pas d'avertissement
        string exact = "Titre\r\n" + string.Join("\r\n", Lines(5000));
        ListReadResult r = Ok(ListFileReader.ReadText(exact, true));
        Assert.Equal(5000, r.Values.Count);
        Assert.Equal("C00001", r.Values[0]);
        Assert.Empty(r.Warnings);

        // 1 titre + 5001 valeurs : une de trop
        string over = "Titre\r\n" + string.Join("\r\n", Lines(5001));
        ListReadResult r2 = Ok(ListFileReader.ReadText(over, true));
        Assert.Equal(5000, r2.Values.Count);
        HasWarning(r2, "5000 premières lignes");
    }

    [Fact]
    public void ReadText_AbsurdlyLargeText_IsRefused()
    {
        string huge = new string('A', (int)ListFileReader.MaxFileBytes + 1);
        ListReadResult r = Failed(ListFileReader.ReadText(huge, false));
        Assert.Contains("volumineux", r.Error);
    }

    [Fact]
    public void ReadText_VeryLongValue_IsTruncatedAndFlagged()
    {
        string huge = new string('9', 50_000);
        ListReadResult r = Ok(ListFileReader.ReadText("A\r\n" + huge + "\r\nB", false));
        Assert.Equal(3, r.Values.Count);
        Assert.Equal(ListCollector.MaxValueChars + 1, r.Values[1].Length);
        HasWarning(r, "tronquée");
    }

    [Fact]
    public void ReadText_AccentsAndSymbols_AreKeptForTheValidatorToReport()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("Café-€\r\nÉTÉ\r\n日本\r\n", false));
        Assert.Equal(new[] { "Café-€", "ÉTÉ", "日本" }, r.Values);
    }

    [Fact]
    public void ReadText_DuplicatesAreKept()
    {
        Assert.Equal(new[] { "A", "A", "B", "A" }, Ok(ListFileReader.ReadText("A\nA\nB\nA", false)).Values);
    }

    [Fact]
    public void ReadText_ManyUnclosedQuotes_StaysLinear()
    {
        // 300 000 lignes commençant par un guillemet jamais refermé : le travail doit rester linéaire.
        var sb = new StringBuilder();
        for (int i = 0; i < 300_000; i++) sb.Append("\"x").Append(i % 7 == 0 ? "\"\"" : "").Append('\n');
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(ListFileReader.ReadText(sb.ToString(), false));
        sw.Stop();
        Assert.Equal(5000, r.Values.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "trop lent : " + sw.Elapsed);
    }

    [Fact]
    public void ReadText_PathologicalQuotes_StaysLinear()
    {
        // alternance qui force de longues recherches de guillemet fermant
        var sb = new StringBuilder("\"x\n");
        for (int i = 0; i < 400_000; i++) sb.Append("\"\"y\n");
        var sw = Stopwatch.StartNew();
        ListFileReader.ReadText(sb.ToString(), false);
        ListFileReader.ReadText(sb.ToString(), true);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "trop lent : " + sw.Elapsed);
    }

    // ------------------------------------------------------------------------------------------------
    // .txt
    // ------------------------------------------------------------------------------------------------

    private static readonly string[] SampleLines = { "Café", "Prix 5€", "AB-12", "Dit “oui”" };

    private static IEnumerable<(string Label, byte[] Bytes)> EncodedSample()
    {
        string text = string.Join("\r\n", SampleLines) + "\r\n";
        yield return ("utf-8", Utf8(text));
        yield return ("utf-8 + BOM", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(text)).ToArray());
        yield return ("utf-16 LE + BOM", new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray());
        yield return ("utf-16 BE + BOM", new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray());
        yield return ("utf-16 LE sans BOM", Encoding.Unicode.GetBytes(text));
        yield return ("utf-16 BE sans BOM", Encoding.BigEndianUnicode.GetBytes(text));
        yield return ("utf-32 LE + BOM", new byte[] { 0xFF, 0xFE, 0, 0 }.Concat(new UTF32Encoding(false, false).GetBytes(text)).ToArray());
        yield return ("utf-32 BE + BOM", new byte[] { 0, 0, 0xFE, 0xFF }.Concat(new UTF32Encoding(true, false).GetBytes(text)).ToArray());
        yield return ("windows-1252", Cp1252(text));
    }

    [Fact]
    public void Txt_EveryEncoding_GivesTheSameValues()
    {
        foreach ((string label, byte[] bytes) in EncodedSample())
        {
            foreach (string name in new[] { "a.txt", "a.csv" })
            {
                ListReadResult r = Read(bytes, name);
                Assert.True(r.Error is null, $"{label} / {name} : {r.Error}");
                Assert.True(SampleLines.SequenceEqual(r.Values), $"{label} / {name} : [{string.Join(" | ", r.Values)}]");
            }
        }
    }

    [Fact]
    public void Txt_ShortUtf16WithoutBom_IsRecognised()
    {
        Assert.Equal(new[] { "AB", "CD" }, Ok(Read(Encoding.Unicode.GetBytes("AB\r\nCD"), "a.txt")).Values);
        Assert.Equal(new[] { "AB", "CD" }, Ok(Read(Encoding.BigEndianUnicode.GetBytes("AB\r\nCD"), "a.txt")).Values);
    }

    [Fact]
    public void Txt_Utf8WithBom_DoesNotKeepTheBomInTheFirstValue()
    {
        ListReadResult r = Ok(Read(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("A001\nA002")).ToArray(), "a.txt"));
        Assert.Equal(new[] { "A001", "A002" }, r.Values);
        Assert.Equal(4, r.Values[0].Length);
    }

    [Fact]
    public void Txt_InvalidUtf8_FallsBackToWindows1252()
    {
        // 0xE9 seul (é en Windows-1252) n'est pas de l'UTF-8 valide
        ListReadResult r = Ok(Read(new byte[] { 0x43, 0x61, 0x66, 0xE9, 0x0D, 0x0A, 0x80, 0x31 }, "a.txt"));
        Assert.Equal(new[] { "Café", "€1" }, r.Values);
    }

    [Fact]
    public void Txt_Windows1252UndefinedBytes_AreDropped()
    {
        ListReadResult r = Ok(Read(new byte[] { 0x41, 0x81, 0x8D, 0x42, 0xE9 }, "a.txt"));
        Assert.Equal(new[] { "ABé" }, r.Values);
    }

    [Theory]
    [InlineData("A\r\nB\r\nC", 3)]
    [InlineData("A\nB\nC\n", 3)]
    [InlineData("A\rB\rC\r", 3)]
    [InlineData("A\r\nB\nC\rD\r\n", 4)]
    [InlineData("\n\nA\n\n", 1)]
    public void Txt_NewlineConventions(string text, int expected)
    {
        Assert.Equal(expected, Ok(Read(Utf8(text), "a.txt")).Values.Count);
    }

    [Fact]
    public void Txt_WholeLineIsTheValue_NoSplittingNoQuotes()
    {
        ListReadResult r = Ok(Read(Utf8("A;B,C\tD\n\"quoted\"\n'single'\n12,5;3\n"), "a.txt"));
        Assert.Equal(new[] { "A;B,C D", "\"quoted\"", "'single'", "12,5;3" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Txt_ExcelTabSeparatedExport_KeepsTheWholeLine_AndWarns()
    {
        // « Texte (séparateur : tabulation) » d'Excel enregistré en .txt : la règle .txt garde la ligne entière, on prévient
        ListReadResult r = Ok(Read(Utf8("A001\tBoulon\t12\r\nA002\tÉcrou\t3\r\n"), "export.txt"));
        Assert.Equal(new[] { "A001 Boulon 12", "A002 Écrou 3" }, r.Values);
        HasWarning(r, "tabulations");

        // une seule ligne avec tabulation, ou des tabulations d'indentation seulement : pas d'avertissement
        Assert.Empty(Ok(Read(Utf8("A\tB\nC\nD\nE\n"), "a.txt")).Warnings);
        Assert.Empty(Ok(Read(Utf8("\tA\n\tB\n\tC\n"), "a.txt")).Warnings);
    }

    [Fact]
    public void Txt_TrailingSpacesAreTrimmed_InnerSpacesKept()
    {
        Assert.Equal(new[] { "AB  CD", "X" }, Ok(Read(Utf8("  AB  CD  \n\u00A0X\u00A0\n"), "a.txt")).Values);
    }

    [Fact]
    public void Txt_BlankLinesInside_AreCounted()
    {
        ListReadResult r = Ok(Read(Utf8("A\n\n\nB\n   \nC\n"), "a.txt"));
        Assert.Equal(new[] { "A", "B", "C" }, r.Values);
        HasWarning(r, "3 ligne(s) vide(s) ignorée(s)");
    }

    [Fact]
    public void Txt_OnlyBlankLines_IsAnError()
    {
        ListReadResult r = Failed(Read(Utf8("\n\n  \n"), "a.txt"));
        Assert.Contains("Aucune valeur trouvée", r.Error);
    }

    [Fact]
    public void Txt_SkipFirstLine()
    {
        Assert.Equal(new[] { "A1", "A2" }, Ok(Read(Utf8("Codes\nA1\nA2\n"), "a.txt", skip: true)).Values);
        Assert.Equal(new[] { "A1", "A2" }, Ok(Read(Utf8("\n\nCodes\n\nA1\nA2\n"), "a.txt", skip: true)).Values);
    }

    [Fact]
    public void Txt_TrailingDosEofAndNulPadding_AreIgnored()
    {
        Assert.Equal(new[] { "A", "B" }, Ok(Read(new byte[] { 0x41, 0x0A, 0x42, 0x0A, 0x1A }, "a.txt")).Values);
        Assert.Equal(new[] { "A", "B" }, Ok(Read(new byte[] { 0x41, 0x0A, 0x42, 0, 0, 0, 0 }, "a.txt")).Values);
    }

    [Fact]
    public void Txt_Cap5000()
    {
        ListReadResult r = Ok(Read(Utf8(string.Join("\n", Lines(7000))), "a.txt"));
        Assert.Equal(5000, r.Values.Count);
        HasWarning(r, "5000 premières lignes");
    }

    [Fact]
    public void Txt_ExactlyMaxFileBytes_IsAccepted()
    {
        byte[] data = new byte[ListFileReader.MaxFileBytes];
        for (int i = 0; i < data.Length; i += 2)
        {
            data[i] = (byte)'A';
            data[i + 1] = (byte)'\n';
        }

        ListReadResult r = Ok(Read(data, "a.txt"));
        Assert.Equal(5000, r.Values.Count);
        HasWarning(r, "5000 premières lignes");
    }

    [Fact]
    public void Txt_OneByteOverMaxFileBytes_IsRefused()
    {
        ListReadResult r = Failed(Read(new byte[ListFileReader.MaxFileBytes + 1], "a.txt"));
        Assert.Contains("trop volumineux", r.Error);
        Assert.Contains("20 Mo", r.Error);
    }

    [Fact]
    public void EndlessNonSeekableStream_IsReadBoundedAndRefused()
    {
        foreach (string name in new[] { "a.txt", "a.csv", "a.xlsx", "a.docx" })
        {
            using var endless = new EndlessStream();
            var sw = Stopwatch.StartNew();
            ListReadResult r = Failed(ListFileReader.ReadStream(endless, name, false));
            sw.Stop();
            Assert.Contains("trop volumineux", r.Error);
            Assert.True(endless.Produced <= ListFileReader.MaxFileBytes + 2L * 1024 * 1024 + 4096 * 4, $"{endless.Produced} octets lus");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Txt_OneHugeLine_IsReadAndTruncated()
    {
        byte[] data = Enumerable.Repeat((byte)'7', 5_000_000).ToArray();
        ListReadResult r = Ok(Read(data, "a.txt"));
        Assert.Single(r.Values);
        Assert.Equal(ListCollector.MaxValueChars + 1, r.Values[0].Length);
        HasWarning(r, "tronquée");
    }

    [Fact]
    public void Txt_Binary_IsRefused()
    {
        var rnd = new Random(1234);
        byte[] noise = new byte[2000];
        rnd.NextBytes(noise);
        noise[10] = 0;
        ListReadResult r = Failed(Read(noise, "a.txt"));
        Assert.Contains("binaire", r.Error);

        Failed(Read(new byte[] { 0x41, 0, 0x42, 0, 0, 0, 0x43, 0, 0, 0 }, "a.txt"));
        Failed(Read(Utf8("ABC\u0001\u0002\u0003\u0004\u0005\u0006"), "a.txt"));
        Failed(Read(new byte[500], "a.txt"));   // que des NUL
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.csv")]
    public void Text_ForeignFormats_AreNamed(string name)
    {
        Assert.Contains("PDF", Failed(Read(Utf8("%PDF-1.7\n%âãÏÓ\n1 0 obj"), name)).Error);
        Assert.Contains("RTF", Failed(Read(Utf8("{\\rtf1\\ansi\\deff0 {\\fonttbl{\\f0 Arial;}} A001\\par}"), name)).Error);
        Assert.Contains("image", Failed(Read(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D }, name)).Error);
        Assert.Contains("image", Failed(Read(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46 }, name)).Error);
        Assert.Contains("ancien format", Failed(Read(OleHeader(), name)).Error);
        Assert.Contains("archive ZIP", Failed(Read(OfficeFixtures.SimpleXlsx(new[] { "A" }), name)).Error);
    }

    private static byte[] OleHeader(bool encrypted = false)
    {
        byte[] head = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        byte[] body = new byte[4096];
        if (encrypted)
        {
            byte[] name = Encoding.Unicode.GetBytes("EncryptedPackage");
            Buffer.BlockCopy(name, 0, body, 1024, name.Length);
        }

        return head.Concat(body).ToArray();
    }

    // ------------------------------------------------------------------------------------------------
    // .csv
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Csv_FrenchExcel_SemicolonAnsi_FirstColumn()
    {
        byte[] data = Cp1252("Code;Désignation;Prix\r\nA001;Boulon M8;12,5\r\nA002;Écrou;3,2\r\n");
        ListReadResult r = Ok(Read(data, "export.csv"));
        Assert.Equal(new[] { "Code", "A001", "A002" }, r.Values);
        HasWarning(r, "première colonne");

        Assert.Equal(new[] { "A001", "A002" }, Ok(Read(data, "export.csv", skip: true)).Values);
    }

    [Fact]
    public void Csv_CommaWithQuotes()
    {
        string csv = "code,label\r\n\"A,1\",\"Boulon, inox\"\r\nB2,\"x\"\"y\"\r\n";
        ListReadResult r = Ok(Read(Utf8(csv), "a.csv"));
        Assert.Equal(new[] { "code", "A,1", "B2" }, r.Values);
    }

    [Fact]
    public void Csv_TabSeparated()
    {
        ListReadResult r = Ok(Read(Utf8("code\tlabel, with comma\r\nA1\tfoo, bar\r\nA2\tbaz, qux\r\n"), "a.csv"));
        Assert.Equal(new[] { "code", "A1", "A2" }, r.Values);
    }

    [Fact]
    public void Csv_SemicolonWinsOverCommaDecimals()
    {
        // virgules décimales dans toutes les lignes, au même endroit : « ; » reste le séparateur
        ListReadResult r = Ok(Read(Utf8("A1;12,5;3,5\nA2;7,25;1,5\nA3;9,5;2,25\n"), "a.csv"));
        Assert.Equal(new[] { "A1", "A2", "A3" }, r.Values);
    }

    [Fact]
    public void Csv_SingleColumnWithCommasInTheText_IsNotSplit()
    {
        ListReadResult r = Ok(Read(Utf8("Boulon M8, inox\r\nRondelle 8\r\nÉcrou M8, inox\r\n"), "a.csv"));
        Assert.Equal(new[] { "Boulon M8, inox", "Rondelle 8", "Écrou M8, inox" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Csv_SingleColumn_QuotedValuesAreUnquoted()
    {
        ListReadResult r = Ok(Read(Utf8("\"A,B\"\r\n\"C\"\"D\"\r\nplain\r\n"), "a.csv"));
        Assert.Equal(new[] { "A,B", "C\"D", "plain" }, r.Values);
    }

    [Fact]
    public void Csv_SlightlyRaggedFile_IsStillSplit()
    {
        string csv = string.Concat(Enumerable.Repeat("a;b;c\n", 10)) + "d;e\n";
        ListReadResult r = Ok(Read(Utf8(csv), "a.csv"));
        Assert.Equal(11, r.Values.Count);
        Assert.Equal("d", r.Values[^1]);
        Assert.All(r.Values, v => Assert.DoesNotContain(";", v));
    }

    [Fact]
    public void Csv_QuotedFirstField_WithEmbeddedNewlineAndDelimiter()
    {
        ListReadResult r = Ok(Read(Utf8("\"line1\nline2;x\";z\r\nB;y\r\n"), "a.csv"));
        Assert.Equal(new[] { "line1 line2;x", "B" }, r.Values);
    }

    [Fact]
    public void Csv_Utf8Bom()
    {
        byte[] data = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("code;nom\r\nÉ1;x\r\n")).ToArray();
        Assert.Equal(new[] { "code", "É1" }, Ok(Read(data, "a.csv")).Values);
    }

    [Fact]
    public void Csv_SepDirective_IsHonouredAndNotAValue()
    {
        ListReadResult r = Ok(Read(Utf8("sep=|\r\nA|1\r\nB|2\r\n"), "a.csv"));
        Assert.Equal(new[] { "A", "B" }, r.Values);

        ListReadResult r2 = Ok(Read(Utf8("sep=;\r\ncode;x\r\nA;1\r\n"), "a.csv", skip: true));
        Assert.Equal(new[] { "A" }, r2.Values);
    }

    [Fact]
    public void Csv_EmptyFirstField_IsIgnored_BlankLinesCounted()
    {
        ListReadResult r = Ok(Read(Utf8("A;1\n;2\nB;3\n\nC;4\n"), "a.csv"));
        Assert.Equal(new[] { "A", "B", "C" }, r.Values);
        HasWarning(r, "2 ligne(s) vide(s)");
    }

    [Fact]
    public void Csv_DelimiterInsideQuotes_IsNotCounted()
    {
        // si les « ; » entre guillemets comptaient, les lignes auraient des nombres de champs différents
        ListReadResult r = Ok(Read(Utf8("\"a;b;c\",1\n\"d\",2\n\"e;f\",3\n"), "a.csv"));
        Assert.Equal(new[] { "a;b;c", "d", "e;f" }, r.Values);
    }

    [Fact]
    public void Csv_LeadingZerosAndQuotedNumbers_AreKept()
    {
        ListReadResult r = Ok(Read(Utf8("code;n\n\"00123\";1\n00456;2\n"), "a.csv", skip: true));
        Assert.Equal(new[] { "00123", "00456" }, r.Values);
    }

    [Fact]
    public void Csv_OnlyAHeader_WithSkip_IsAnErrorMentioningTheTitle()
    {
        ListReadResult r = Failed(Read(Utf8("code;nom\r\n"), "a.csv", skip: true));
        Assert.Contains("Aucune valeur trouvée", r.Error);
        Assert.Contains("titre", r.Error);
    }

    [Fact]
    public void Csv_Cap5000_AndSkip()
    {
        string csv = "code;x\n" + string.Join("\n", Lines(5002).Select(l => l + ";1"));
        ListReadResult r = Ok(Read(Utf8(csv), "a.csv", skip: true));
        Assert.Equal(5000, r.Values.Count);
        HasWarning(r, "5000 premières lignes");
    }

    [Fact]
    public void DetectDelimiter_Theory()
    {
        Assert.Equal(';', ListFileText.DetectDelimiter("a;b\nc;d\n"));
        Assert.Equal('\t', ListFileText.DetectDelimiter("a\tb\nc\td\n"));
        Assert.Equal(',', ListFileText.DetectDelimiter("a,b,c\nd,e,f\n"));
        Assert.Equal(ListFileText.NoDelimiter, ListFileText.DetectDelimiter("a\nb\nc\n"));
        Assert.Equal(ListFileText.NoDelimiter, ListFileText.DetectDelimiter("a,b\nc\nd,e\n"));
        Assert.Equal(ListFileText.NoDelimiter, ListFileText.DetectDelimiter(""));
        Assert.Equal(';', ListFileText.DetectDelimiter("a;b,c\nd;e,f\n"));
        Assert.Equal(',', ListFileText.DetectDelimiter("\"a;b\",c\n\"d;e\",f\n"));
    }

    // ------------------------------------------------------------------------------------------------
    // .xlsx
    // ------------------------------------------------------------------------------------------------

    private static string RowsOfInline(params string[] values)
        => string.Concat(values.Select((v, i) => OfficeFixtures.Row(i + 1, OfficeFixtures.CellInline("A" + (i + 1), v))));

    [Fact]
    public void Xlsx_Excel365Column_SharedStrings()
    {
        (string sheet, string sst) = OfficeFixtures.ExcelColumn(new[] { "Code", "A001", "A002", "B-003", "A001" });
        byte[] file = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", sheet).SharedStrings(sst).Build();

        ListReadResult r = Ok(Read(file, "liste.xlsx"));
        Assert.Equal(new[] { "Code", "A001", "A002", "B-003", "A001" }, r.Values);
        Assert.Equal("fichier « liste.xlsx »", r.Source);
        Assert.Empty(r.Warnings);

        Assert.Equal(new[] { "A001", "A002", "B-003", "A001" }, Ok(Read(file, "liste.xlsx", skip: true)).Values);
    }

    [Fact]
    public void Xlsx_SharedStrings_RichTextPhoneticAndPreservedSpaces()
    {
        string sst = OfficeFixtures.SharedStringsXml(
            OfficeFixtures.SiRich("AB", "12", "-X"),
            OfficeFixtures.SiPhonetic("Café", "kafe"),
            OfficeFixtures.Si("  espaces  internes  "),
            OfficeFixtures.Si("x"));
        string rows = string.Concat(Enumerable.Range(0, 4).Select(i => OfficeFixtures.Row(i + 1, OfficeFixtures.CellShared("A" + (i + 1), i))));

        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx"));
        Assert.Equal(new[] { "AB12-X", "Café", "espaces  internes", "x" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_InlineStrings_PlainAndRich()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "Inline 1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInlineRich("A2", "Rich", "Run"))
                      + OfficeFixtures.Row(3, OfficeFixtures.CellInline("A3", "  p  "));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "Inline 1", "RichRun", "p" }, r.Values);
    }

    [Fact]
    public void Xlsx_FormulaResults_TextAndNumbers_FormulaWithoutCachedValueIsReported()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellFormulaText("A1", "RES-1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellFormulaNumber("A2", "42"))
                      + OfficeFixtures.Row(3, OfficeFixtures.CellFormulaNoValue("A3"))
                      + OfficeFixtures.Row(4, OfficeFixtures.CellFormulaText("A4", "END"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "RES-1", "42", "END" }, r.Values);
        HasWarning(r, "1 cellule(s) avec une formule sans résultat enregistré ignorée(s)");
        NoWarning(r, "vide");   // une ligne écartée pour une raison signalée à part n'est pas « vide »
    }

    [Fact]
    public void Xlsx_FormulaWithAnEmptyTextResult_IsJustAnEmptyCell()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "A"))
                      + OfficeFixtures.Row(2, "<c r=\"A2\" t=\"str\"><f>A1&amp;\"\"</f><v></v></c>")
                      + OfficeFixtures.Row(3, OfficeFixtures.CellInline("A3", "B"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        NoWarning(r, "formule");
        HasWarning(r, "1 ligne(s) vide(s)");
    }

    [Theory]
    [InlineData("12345", "12345", false)]
    [InlineData("1.5", "1.5", false)]
    [InlineData("0.30000000000000004", "0.30000000000000004", false)]
    [InlineData("5.9012341234570001E+12", "5901234123457", false)]
    [InlineData("5.901234123457E+12", "5901234123457", false)]
    [InlineData("1.23457E+15", "1234570000000000", false)]
    [InlineData("1E+15", "1000000000000000", false)]
    [InlineData("1e15", "1000000000000000", false)]
    [InlineData("9.007199254740992E+15", "9007199254740992", false)]
    [InlineData("-5.0E+3", "-5000", false)]
    [InlineData("12345.0", "12345", false)]
    [InlineData("12345.000", "12345", false)]
    [InlineData("-12.0", "-12", false)]
    [InlineData("100.50", "100.50", false)]
    [InlineData("1.5E-5", "1.5E-5", true)]
    [InlineData("1.2345678901234567E+19", "1.2345678901234567E+19", true)]
    [InlineData("1E+16", "1E+16", true)]
    public void Xlsx_NumericCells_UseTheStoredValueNotTheDisplay(string stored, string expected, bool warns)
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", stored));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { expected }, r.Values);
        if (warns) HasWarning(r, "notation scientifique");
        else Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_ScientificWarning_MentionsTextFormat()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "1.5E-5")) + OfficeFixtures.Row(2, OfficeFixtures.CellNumber("A2", "2.5E-5"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        HasWarning(r, "2 nombre(s) en notation scientifique");
        HasWarning(r, "formatez la colonne en Texte");
    }

    [Theory]
    [InlineData(0, false, false)]    // standard
    [InlineData(1, false, false)]    // texte (@)
    [InlineData(2, true, false)]     // date intégrée (14)
    [InlineData(3, true, false)]     // dd/mm/yyyy
    [InlineData(4, false, true)]     // nombre avec « d » entre guillemets (0.0" d") : pas une date, mais un format qui change l'affichage
    [InlineData(5, true, false)]     // durée [h]:mm:ss
    [InlineData(6, true, false)]     // [Red]dddd\ d\ mmmm\ yyyy
    [InlineData(99, false, false)]   // style inexistant
    public void Xlsx_NumbersWithADateFormat_AreReadAsSerialNumbersAndReported(int style, bool isDate, bool formatted)
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "45123", style: style));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "45123" }, r.Values);
        if (isDate) HasWarning(r, "1 cellule(s) de date lue(s) comme numéro de série Excel");
        else NoWarning(r, "cellule(s) de date");
        if (formatted) HasWarning(r, "1 nombre(s) affiché(s) avec un format Excel");
        else NoWarning(r, "format Excel");
        if (!isDate && !formatted) Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_DateColumnNextToTheCodes_IsReportedOnce()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "45123", style: 3) + OfficeFixtures.CellInline("B1", "C1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellNumber("A2", "45124.5", style: 3) + OfficeFixtures.CellInline("B2", "C2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "45123", "45124.5" }, r.Values);   // la colonne de gauche gagne : les dates sont lues comme numéros de série
        HasWarning(r, "2 cellule(s) de date lue(s)");
    }

    [Theory]
    [InlineData(14, null, true)]
    [InlineData(22, null, true)]
    [InlineData(45, null, true)]
    [InlineData(58, null, true)]
    [InlineData(0, null, false)]
    [InlineData(1, null, false)]
    [InlineData(9, null, false)]
    [InlineData(49, null, false)]
    [InlineData(164, "dd/mm/yyyy", true)]
    [InlineData(164, "hh:mm AM/PM", true)]
    [InlineData(164, "[h]:mm:ss", true)]
    [InlineData(164, "[Red]dddd\\ d\\ mmmm\\ yyyy", true)]
    [InlineData(164, "yyyy\\-mm\\-dd", true)]
    [InlineData(164, "0.0\" d\"", false)]
    [InlineData(164, "0.00\" m\"", false)]
    [InlineData(164, "#,##0.00\\ [$\u20AC-407]", false)]
    [InlineData(164, "[Red]0.00", false)]
    [InlineData(164, "0.00E+00", false)]
    [InlineData(164, "General", false)]
    [InlineData(164, "@", false)]
    [InlineData(164, "\\d0", false)]
    [InlineData(164, "", false)]
    [InlineData(164, null, false)]
    public void Xlsx_IsDateFormat_Theory(int id, string? code, bool expected)
    {
        Assert.Equal(expected, ListFileXlsx.IsDateFormat(id, code));
    }

    [Fact]
    public void Xlsx_UnreadableStyles_AreIgnored()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "45123", style: 3));
        var parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", OfficeFixtures.SheetXml(rows)).BuildParts();
        parts = parts.Select(p => p.Key == "xl/styles.xml" ? new KeyValuePair<string, string>(p.Key, "<styleSheet><cellXfs><xf numFmtId=") : p).ToList();
        ListReadResult r = Ok(Read(OfficeFixtures.Zip(parts), "a.xlsx"));
        Assert.Equal(new[] { "45123" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_BooleansErrorsAndDates()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "X1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellBool("A2", true))
                      + OfficeFixtures.Row(3, OfficeFixtures.CellError("A3", "#N/A"))
                      + OfficeFixtures.Row(4, OfficeFixtures.CellDate("A4", "2024-01-31T00:00:00"))
                      + OfficeFixtures.Row(5, OfficeFixtures.CellInline("A5", "X2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "X1", "2024-01-31T00:00:00", "X2" }, r.Values);
        HasWarning(r, "1 cellule(s) VRAI/FAUX ignorée(s)");
        HasWarning(r, "1 cellule(s) en erreur");
    }

    [Fact]
    public void Xlsx_BooleanColumnOnTheLeft_DoesNotHijackTheCodeColumn()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellBool("A1", true) + OfficeFixtures.CellInline("B1", "C1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellBool("A2", false) + OfficeFixtures.CellInline("B2", "C2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "C1", "C2" }, r.Values);
        HasWarning(r, "2 cellule(s) VRAI/FAUX");
    }

    [Fact]
    public void Xlsx_HiddenAndFilteredRows_AreSkipped()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "H1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "H2"), "hidden=\"0\"")
                      + OfficeFixtures.Row(3, OfficeFixtures.CellInline("A3", "H3"), "hidden=\"1\"")
                      + OfficeFixtures.Row(4, OfficeFixtures.CellInline("A4", "H4"), "hidden=\"false\" customFormat=\"false\" ht=\"12.8\"")
                      + OfficeFixtures.Row(5, OfficeFixtures.CellInline("A5", "H5"), "hidden=\"true\"")
                      + OfficeFixtures.Row(6, OfficeFixtures.CellInline("A6", "H6"));
        string sheet = OfficeFixtures.SheetXml(rows, afterSheetData: "<autoFilter ref=\"A1:A6\"/>");
        ListReadResult r = Ok(Read(Xlsx(sheet), "a.xlsx"));
        Assert.Equal(new[] { "H1", "H2", "H4", "H6" }, r.Values);
        HasWarning(r, "2 ligne(s) masquée(s) ou filtrée(s) ignorée(s)");
        NoWarning(r, "vide");
    }

    [Fact]
    public void Xlsx_HiddenRowsWithoutData_AreNotWorthAWarning()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "A"))
                      + "<row r=\"2\" hidden=\"1\"/>"
                      + OfficeFixtures.Row(3, OfficeFixtures.CellInline("A3", "B"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_HiddenColumn_IsSkipped_NextColumnIsRead()
    {
        string cols = "<col min=\"1\" max=\"1\" width=\"9.140625\" hidden=\"1\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"14\" customWidth=\"1\"/>";
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "SECRET1") + OfficeFixtures.CellInline("B1", "V1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "SECRET2") + OfficeFixtures.CellInline("B2", "V2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows, cols)), "a.xlsx"));
        Assert.Equal(new[] { "V1", "V2" }, r.Values);
        HasWarning(r, "1 colonne(s) masquée(s)");
        NoWarning(r, "première colonne");
    }

    [Fact]
    public void Xlsx_LibreOfficeColumnDefinitions_DoNotHideColumns()
    {
        string cols = "<col collapsed=\"false\" customWidth=\"true\" hidden=\"false\" outlineLevel=\"0\" max=\"1\" min=\"1\" style=\"0\" width=\"11.52\"/>"
                      + "<col collapsed=\"false\" customWidth=\"false\" hidden=\"false\" outlineLevel=\"0\" max=\"1024\" min=\"2\" style=\"0\" width=\"11.52\"/>";
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "L1")) + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "L2"));
        Assert.Equal(new[] { "L1", "L2" }, Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows, cols)), "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_HiddenFirstSheet_TheFirstVisibleOneIsRead()
    {
        byte[] file = new OfficeFixtures.XlsxBuilder()
            .AddSheet("Cachée", OfficeFixtures.SheetXml(RowsOfInline("H1", "H2")), "hidden")
            .AddSheet("Très cachée", OfficeFixtures.SheetXml(RowsOfInline("VH1")), "veryHidden")
            .AddSheet("Visible", OfficeFixtures.SheetXml(RowsOfInline("V1", "V2", "V3")))
            .Build();
        ListReadResult r = Ok(Read(file, "a.xlsx"));
        Assert.Equal(new[] { "V1", "V2", "V3" }, r.Values);
        NoWarning(r, "autres feuilles");
    }

    [Fact]
    public void Xlsx_AllSheetsHidden_FallsBackToTheFirstOne()
    {
        byte[] file = new OfficeFixtures.XlsxBuilder()
            .AddSheet("Cachée", OfficeFixtures.SheetXml(RowsOfInline("H1", "H2")), "hidden")
            .AddSheet("Autre", OfficeFixtures.SheetXml(RowsOfInline("O1")), "veryHidden")
            .Build();
        Assert.Equal(new[] { "H1", "H2" }, Ok(Read(file, "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_WorkbookOrderWins_NotTheFileNumbers()
    {
        byte[] file = new OfficeFixtures.XlsxBuilder()
            .AddSheet("Première", OfficeFixtures.SheetXml(RowsOfInline("FIRST1", "FIRST2")))
            .AddSheet("Seconde", OfficeFixtures.SheetXml(RowsOfInline("SECOND1")))
            .SheetFileNumbers(2, 1)   // la 1re feuille du classeur est stockée dans sheet2.xml
            .Build();
        ListReadResult r = Ok(Read(file, "a.xlsx"));
        Assert.Equal(new[] { "FIRST1", "FIRST2" }, r.Values);
        HasWarning(r, "Seule la première feuille");
    }

    [Fact]
    public void Xlsx_OnlySheetElementsInsideSheets_AreSheets()
    {
        List<KeyValuePair<string, string>> parts = new OfficeFixtures.XlsxBuilder().AddSheet("Vraie", OfficeFixtures.SheetXml(RowsOfInline("REAL1", "REAL2"))).BuildParts();
        parts = parts.Select(p => p.Key == "xl/workbook.xml"
            ? new KeyValuePair<string, string>(p.Key, p.Value.Replace("</workbook>", "<extLst><ext uri=\"x\"><x:sheet xmlns:x=\"urn:x\" name=\"Faux\" r:id=\"rId99\"/></ext></extLst></workbook>"))
            : p).ToList();
        Assert.Equal(new[] { "REAL1", "REAL2" }, Ok(Read(OfficeFixtures.Zip(parts), "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_ChartSheetFirst_IsSkipped()
    {
        byte[] file = new OfficeFixtures.XlsxBuilder()
            .AddChartSheet("Graphique1")
            .AddSheet("Données", OfficeFixtures.SheetXml(RowsOfInline("D1", "D2")))
            .Build();
        Assert.Equal(new[] { "D1", "D2" }, Ok(Read(file, "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_OtherVisibleSheetsWithData_Warn_EmptyOnesDoNot()
    {
        byte[] withData = new OfficeFixtures.XlsxBuilder()
            .AddSheet("S1", OfficeFixtures.SheetXml(RowsOfInline("A")))
            .AddSheet("S2", OfficeFixtures.SheetXml(RowsOfInline("B")))
            .Build();
        HasWarning(Ok(Read(withData, "a.xlsx")), "Seule la première feuille a été lue : les autres feuilles du classeur contiennent aussi des données");

        byte[] emptyOthers = new OfficeFixtures.XlsxBuilder()
            .AddSheet("S1", OfficeFixtures.SheetXml(RowsOfInline("A")))
            .AddSheet("S2", OfficeFixtures.SheetXml(""))
            .AddSheet("S3", OfficeFixtures.SheetXml(OfficeFixtures.Row(1, OfficeFixtures.CellBlank("A1"))))
            .Build();
        Assert.Empty(Ok(Read(emptyOthers, "a.xlsx")).Warnings);
    }

    [Theory]
    [InlineData("absolu")]
    [InlineData("parent")]
    [InlineData("antislash")]
    [InlineData("strict")]
    [InlineData("sans Content_Types")]
    [InlineData("sans _rels/.rels")]
    [InlineData("sans relations du classeur")]
    [InlineData("sans relation sharedStrings")]
    [InlineData("classeur renommé")]
    [InlineData("classeur dans un autre dossier")]
    public void Xlsx_PackageLayoutVariants_AreAllReadable(string variant)
    {
        (string sheet, string sst) = OfficeFixtures.ExcelColumn(new[] { "T1", "T2", "T3" });
        var builder = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", sheet).SharedStrings(sst);
        switch (variant)
        {
            case "absolu": builder.AbsoluteTargets(); break;
            case "parent": builder.ParentTargets(); break;
            case "antislash": builder.BackslashTargets(); break;
            case "strict": builder.Strict(); break;
            case "sans Content_Types": builder.WithoutContentTypes(); break;
            case "sans _rels/.rels": builder.WithoutRootRels(); break;
            case "sans relations du classeur": builder.WithoutWorkbookRels(); break;
            case "sans relation sharedStrings": builder.WithoutSharedStringsRelationship(); break;   // la table reste trouvée à son emplacement habituel
            case "classeur renommé": builder.WorkbookPart("xl/workbook2.xml"); break;
            case "classeur dans un autre dossier": builder.WorkbookPart("office/book.xml"); break;
            default: throw new ArgumentException(variant);
        }

        ListReadResult r = Read(builder.Build(), "variante.xlsx");
        Assert.True(r.Error is null, variant + " : " + r.Error);
        Assert.True(new[] { "T1", "T2", "T3" }.SequenceEqual(r.Values), variant + " : " + string.Join("|", r.Values));
    }

    [Fact]
    public void Xlsx_PartNamesAreCaseInsensitive()
    {
        var builder = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", OfficeFixtures.SheetXml(RowsOfInline("C1", "C2")));
        List<KeyValuePair<string, string>> parts = builder.BuildParts();
        byte[] file = OfficeFixtures.Zip(parts.Select(p => new KeyValuePair<string, string>(p.Key.ToUpperInvariant() == "[CONTENT_TYPES].XML" ? p.Key : p.Key.Replace("xl/", "XL/"), p.Value)));
        Assert.Equal(new[] { "C1", "C2" }, Ok(Read(file, "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_EmptyAndBlankCells_AreSkippedAndCounted()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "A1"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellBlank("A2"))
                      + OfficeFixtures.Row(3, "<c r=\"A3\"/>")
                      + OfficeFixtures.Row(4, "<c r=\"A4\" t=\"s\"/>")
                      + OfficeFixtures.Row(5, "<c r=\"A5\" t=\"s\"><v></v></c>")
                      + OfficeFixtures.Row(6, "<c r=\"A6\" t=\"inlineStr\"><is><t/></is></c>")
                      + OfficeFixtures.Row(7, OfficeFixtures.CellInline("A7", "A7"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "A1", "A7" }, r.Values);
        HasWarning(r, "5 ligne(s) vide(s) ignorée(s)");
    }

    [Fact]
    public void Xlsx_SparseSheet_NeverAllocatesPerRow()
    {
        string rows = OfficeFixtures.Row(5, OfficeFixtures.CellInline("A5", "V5"))
                      + OfficeFixtures.Row(8, OfficeFixtures.CellInline("A8", "V8"))
                      + OfficeFixtures.Row(1000000, OfficeFixtures.CellInline("A1000000", "V1M"))
                      + OfficeFixtures.Row(1048576, OfficeFixtures.CellInline("A1048576", "VMAX"));
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows, dimension: "A1:A1048576")), "a.xlsx"));
        sw.Stop();
        Assert.Equal(new[] { "V5", "V8", "V1M", "VMAX" }, r.Values);
        HasWarning(r, "1048568 ligne(s) vide(s) ignorée(s)");   // 2 + 999991 + 48575 ; les 4 lignes avant la première valeur ne comptent pas
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Xlsx_AbsentRowsBeforeAHiddenRow_AreStillEmptyLines()
    {
        // lignes 2 et 3 absentes du fichier (vides et visibles), ligne 4 masquée avec des données, ligne 5 visible
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "A"))
                      + OfficeFixtures.Row(4, OfficeFixtures.CellInline("A4", "HIDDEN"), "hidden=\"1\"")
                      + OfficeFixtures.Row(5, OfficeFixtures.CellInline("A5", "B"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        HasWarning(r, "2 ligne(s) vide(s) ignorée(s)");
        HasWarning(r, "1 ligne(s) masquée(s)");
    }

    [Fact]
    public void Xlsx_FirstNonEmptyColumn_IsB()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("B1", "B-1")) + OfficeFixtures.Row(2, OfficeFixtures.CellInline("B2", "B-2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "B-1", "B-2" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_LeftmostColumnAppearingLater_WinsAndEarlierOtherColumnValuesAreDropped()
    {
        // titre en B1 (cellule fusionnée), codes en colonne A à partir de la ligne 2
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("B1", "Liste des codes"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "x"))
                      + OfficeFixtures.Row(3, OfficeFixtures.CellInline("A3", "y") + OfficeFixtures.CellInline("B3", "z"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "x", "y" }, r.Values);
        HasWarning(r, "Seule la première colonne a été lue");
    }

    [Fact]
    public void Xlsx_WhitespaceOnlyCells_DoNotHijackTheColumn()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", " ") + OfficeFixtures.CellInline("B1", "Code"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "\u00A0") + OfficeFixtures.CellInline("B2", "X1"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "Code", "X1" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_OtherColumnsWithData_Warn()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "K1") + OfficeFixtures.CellInline("B1", "n1") + OfficeFixtures.CellNumber("C1", "5"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "K2"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "K1", "K2" }, r.Values);
        HasWarning(r, "Seule la première colonne");
    }

    [Fact]
    public void Xlsx_EscapedControlCharacters_AreDecoded()
    {
        string sst = OfficeFixtures.SharedStringsXml(
            OfficeFixtures.Si("A_x000D_B"),
            OfficeFixtures.Si("_x005F_x000D_"),
            OfficeFixtures.Si("Tab_x0009_Here"),
            OfficeFixtures.Si("_xZZZZ_ stays"),
            OfficeFixtures.Si("Ctrl_x0001_Z"));
        string rows = string.Concat(Enumerable.Range(0, 5).Select(i => OfficeFixtures.Row(i + 1, OfficeFixtures.CellShared("A" + (i + 1), i))));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx"));
        Assert.Equal(new[] { "A B", "_x000D_", "Tab Here", "_xZZZZ_ stays", "CtrlZ" }, r.Values);
    }

    [Fact]
    public void Xlsx_RowsAndCellsWithoutReferences_UseTheirPosition()
    {
        string sheet = OfficeFixtures.SheetXml("<row><c t=\"inlineStr\"><is><t>A</t></is></c><c t=\"inlineStr\"><is><t>B</t></is></c></row><row><c t=\"inlineStr\"><is><t>C</t></is></c></row>");
        ListReadResult r = Ok(Read(Xlsx(sheet), "a.xlsx"));
        Assert.Equal(new[] { "A", "C" }, r.Values);
        HasWarning(r, "première colonne");
    }

    [Fact]
    public void Xlsx_UnexpectedStructures_DoNotCrash()
    {
        // lignes en désordre / dupliquées, cellules à l'envers, références invalides
        string rows = "<row r=\"5\">" + OfficeFixtures.CellInline("A5", "R5") + "</row>"
                      + "<row r=\"3\">" + OfficeFixtures.CellInline("A3", "R3") + "</row>"
                      + "<row r=\"3\">" + OfficeFixtures.CellInline("A3", "R3bis") + "</row>"
                      + "<row r=\"2\">" + OfficeFixtures.CellInline("B2", "BB") + OfficeFixtures.CellInline("A2", "AA") + "</row>"
                      + "<row r=\"abc\"><c r=\"ABCD1\" t=\"inlineStr\"><is><t>bad1</t></is></c><c r=\"1A\" t=\"inlineStr\"><is><t>bad2</t></is></c><c r=\"XFD7\" t=\"inlineStr\"><is><t>far</t></is></c></row>";
        ListReadResult r = Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx");
        Assert.True(r.Error is null || r.Values.Count == 0);
        Assert.Contains("R5", r.Values);
    }

    [Fact]
    public void Xlsx_NoSharedStringsPart_ButSharedStringCells_IsAnError()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0));
        ListReadResult r = Failed(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Contains("Aucune valeur trouvée", r.Error);
        Assert.Contains("illisible", r.Error);
    }

    [Fact]
    public void Xlsx_SharedStringIndexOutOfRange_IsIgnoredWithAWarning()
    {
        string sst = OfficeFixtures.SharedStringsXml(OfficeFixtures.Si("ok"));
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 99)) + OfficeFixtures.Row(2, OfficeFixtures.CellShared("A2", 0))
                      + OfficeFixtures.Row(3, "<c r=\"A3\" t=\"s\"><v>abc</v></c>") + OfficeFixtures.Row(4, "<c r=\"A4\" t=\"s\"><v>-1</v></c>");
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx"));
        Assert.Equal(new[] { "ok" }, r.Values);
        HasWarning(r, "3 cellule(s) illisible(s)");
    }

    [Fact]
    public void Xlsx_EmptySheet_IsAnErrorNamingTheFirstSheet()
    {
        foreach (string sheet in new[] { OfficeFixtures.SheetXml(""), OfficeFixtures.SheetXml("").Replace("<sheetData></sheetData>", "<sheetData/>") })
        {
            ListReadResult r = Failed(Read(Xlsx(sheet), "a.xlsx"));
            Assert.Contains("Aucune valeur trouvée dans la première feuille", r.Error);
        }
    }

    [Fact]
    public void Xlsx_OnlyHiddenRows_ErrorMentionsThem()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "H"), "hidden=\"1\"") + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "H"), "hidden=\"1\"");
        ListReadResult r = Failed(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Contains("Aucune valeur trouvée", r.Error);
        Assert.Contains("2 ligne(s) masquée(s)", r.Error);
    }

    [Fact]
    public void Xlsx_ElementPrefixes_AreAccepted()
    {
        string xml = "<?xml version=\"1.0\"?><x:worksheet xmlns:x=\"" + OfficeFixtures.SpreadsheetNs + "\"><x:sheetData>"
                     + "<x:row r=\"1\"><x:c r=\"A1\" t=\"inlineStr\"><x:is><x:t>P1</x:t></x:is></x:c></x:row>"
                     + "<x:row r=\"2\"><x:c r=\"A2\" t=\"inlineStr\"><x:is><x:t>P2</x:t></x:is></x:c></x:row></x:sheetData></x:worksheet>";
        Assert.Equal(new[] { "P1", "P2" }, Ok(Read(Xlsx(xml), "a.xlsx")).Values);
    }

    [Fact]
    public void Xlsx_VeryLongSharedString_IsTruncatedAndFlagged()
    {
        string sst = OfficeFixtures.SharedStringsXml(OfficeFixtures.Si(new string('9', 20_000)), OfficeFixtures.Si("ok"));
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0)) + OfficeFixtures.Row(2, OfficeFixtures.CellShared("A2", 1));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx"));
        Assert.Equal(2, r.Values.Count);
        Assert.Equal(ListCollector.MaxValueChars + 1, r.Values[0].Length);
        HasWarning(r, "tronquée");
    }

    [Theory]
    [InlineData(5000, false, 5000, false)]
    [InlineData(5001, false, 5000, true)]
    [InlineData(5001, true, 5000, false)]
    [InlineData(5002, true, 5000, true)]
    public void Xlsx_Cap(int rowsInFile, bool skip, int expectedCount, bool warns)
    {
        ListReadResult r = Ok(Read(InlineColumnXlsx(Lines(rowsInFile)), "a.xlsx", skip));
        Assert.Equal(expectedCount, r.Values.Count);
        Assert.Equal(skip ? "C00002" : "C00001", r.Values[0]);
        if (warns) HasWarning(r, "5000 premières lignes");
        else Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_5000SharedStringRows_ReadsFast()
    {
        byte[] file = OfficeFixtures.SimpleXlsx(Lines(5000));
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "a.xlsx"));
        sw.Stop();
        Assert.Equal(5000, r.Values.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "5000 lignes lues en " + sw.Elapsed);
    }

    [Fact]
    public void Xlsx_100000Rows_StopsEarly()
    {
        byte[] file = OfficeFixtures.SimpleXlsx(Lines(100_000));
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "a.xlsx"));
        sw.Stop();
        Assert.Equal(5000, r.Values.Count);
        HasWarning(r, "5000 premières lignes");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "lu en " + sw.Elapsed);
    }

    /// <summary>Classeur d'une colonne de <paramref name="rows"/> lignes écrite en flux (aucune chaîne de la taille du fichier en mémoire).</summary>
    /// <param name="rows">Nombre de lignes de la feuille (une cellule « chaîne en ligne » chacune, ≈ 97 octets de XML par ligne).</param>
    /// <param name="hiddenRows">Les lignes sont masquées (feuille filtrée).</param>
    /// <param name="visibleEvery">Avec <paramref name="hiddenRows"/> : une ligne sur N reste visible (0 : toutes masquées).</param>
    private static byte[] StreamedSheetXlsx(int rows, bool hiddenRows, int visibleEvery = 0)
    {
        List<KeyValuePair<string, string>> parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").BuildParts();
        parts.RemoveAll(p => p.Key == "xl/worksheets/sheet1.xml");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, string> p in parts)
            {
                using Stream s = zip.CreateEntry(p.Key, CompressionLevel.Optimal).Open();
                byte[] b = Utf8(p.Value);
                s.Write(b, 0, b.Length);
            }

            using Stream sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
            using var w = new StreamWriter(sheet, new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><sheetData>");
            for (int i = 1; i <= rows; i++)
            {
                bool hidden = hiddenRows && (visibleEvery <= 0 || i % visibleEvery != 0);
                w.Write($"<row r=\"{i}\" spans=\"1:1\"{(hidden ? " hidden=\"1\"" : "")}><c r=\"A{i}\" t=\"inlineStr\"><is><t>V{i:D7}</t></is></c></row>");
            }

            w.Write("</sheetData></worksheet>");
        }

        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_OneMillionRowSheet_IsReadAsAStreamAndStopsAtTheCap()
    {
        byte[] file = StreamedSheetXlsx(1_048_576, hiddenRows: false);
        Assert.True(file.Length < ListFileReader.MaxFileBytes, "fixture trop grosse : " + file.Length);
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "million.xlsx"));
        sw.Stop();
        Assert.Equal(5000, r.Values.Count);
        Assert.Equal("V0000001", r.Values[0]);
        HasWarning(r, "5000 premières lignes");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "lu en " + sw.Elapsed);
    }

    [Fact]
    public void Xlsx_OneMillionHiddenRows_IsScannedToTheEndAndReportsThereIsNothingVisible()
    {
        // ≈ 100 Mo de XML : la feuille filtrée est parcourue en entier (plafond d'une feuille = 200 Mo), sans erreur « piégé » à tort
        byte[] file = StreamedSheetXlsx(1_048_576, hiddenRows: true);
        var sw = Stopwatch.StartNew();
        ListReadResult r = Failed(Read(file, "filtre.xlsx"));
        sw.Stop();
        Assert.DoesNotContain("démesur", r.Error);
        Assert.Contains("Aucune valeur trouvée", r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "lu en " + sw.Elapsed);
    }

    [Fact]
    public void Xlsx_FilteredSheetWithMoreThan50MbOfXml_IsStillReadInFull()
    {
        // Régression : un export réel de 600 000 lignes filtré à 3 000 lignes visibles pèse > 50 Mo de XML (le fichier lui-même reste
        // sous 20 Mo) ; il était refusé comme « piégé ou endommagé » et les 3 000 codes visibles étaient perdus.
        byte[] file = StreamedSheetXlsx(600_000, hiddenRows: true, visibleEvery: 200);
        Assert.True(file.Length < ListFileReader.MaxFileBytes, "fixture trop grosse : " + file.Length);
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "export.xlsx"));
        sw.Stop();
        Assert.Equal(3000, r.Values.Count);
        Assert.Equal("V0000200", r.Values[0]);
        Assert.Equal("V0600000", r.Values[^1]);
        HasWarning(r, "ligne(s) masquée(s) ou filtrée(s) ignorée(s)");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "lu en " + sw.Elapsed);
    }

    [Fact]
    public void Xlsx_SharedStringsAreLoadedLazily_ButIndexesFarAwayStillResolve()
    {
        var items = Enumerable.Range(0, 60_000).Select(i => OfficeFixtures.Si("S" + i)).ToArray();
        string sst = OfficeFixtures.SharedStringsXml(items);
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 3)) + OfficeFixtures.Row(2, OfficeFixtures.CellShared("A2", 59_999)) + OfficeFixtures.Row(3, OfficeFixtures.CellShared("A3", 0));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx"));
        Assert.Equal(new[] { "S3", "S59999", "S0" }, r.Values);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("B12", 1)]
    [InlineData("Z1", 25)]
    [InlineData("AA1", 26)]
    [InlineData("a1", 0)]
    [InlineData("XFD1048576", 16383)]
    [InlineData("XFE1", -7)]
    [InlineData("1A", -7)]
    [InlineData("", -7)]
    [InlineData(null, -7)]
    [InlineData("ABCD1", -7)]
    public void Xlsx_ParseColumn(string? reference, int expected)
    {
        Assert.Equal(expected, ListFileXlsx.ParseColumn(reference, -7));
    }

    [Theory]
    [InlineData("5.9012341234570001E+12", "5901234123457", 0)]
    [InlineData("1.5E-5", "1.5E-5", 1)]
    [InlineData("  42  ", "42", 0)]
    [InlineData("007.0", "007", 0)]
    [InlineData("+5.0", "+5", 0)]
    [InlineData(".0", ".0", 0)]
    [InlineData("1.50", "1.50", 0)]
    public void Xlsx_NormalizeNumber_Direct(string stored, string expected, int sci)
    {
        int count = 0;
        Assert.Equal(expected, ListFileXlsx.NormalizeNumber(stored, ref count));
        Assert.Equal(sci, count);
    }

    // ------------------------------------------------------------------------------------------------
    // .docx
    // ------------------------------------------------------------------------------------------------

    private static ListReadResult Docx(string body, bool skip = false) => Read(OfficeFixtures.Docx(body), "liste.docx", skip);

    private static string[] DocxValues(string body) => Ok(Docx(body)).Values.ToArray();

    [Fact]
    public void Docx_Paragraphs_InOrder_HeadersFootersAndNotesIgnored()
    {
        ListReadResult r = Ok(Docx(OfficeFixtures.P("Code") + OfficeFixtures.P("A001") + OfficeFixtures.P("A002")));
        Assert.Equal(new[] { "Code", "A001", "A002" }, r.Values);
        Assert.Equal("fichier « liste.docx »", r.Source);
        Assert.Empty(r.Warnings);
        Assert.DoesNotContain("ENTETE", r.Values);
        Assert.DoesNotContain("PIED", r.Values);
        Assert.DoesNotContain("NOTE", r.Values);

        Assert.Equal(new[] { "A001", "A002" }, Ok(Docx(OfficeFixtures.P("Code") + OfficeFixtures.P("A001") + OfficeFixtures.P("A002"), skip: true)).Values);
    }

    [Fact]
    public void Docx_SkipFirstLine_AppliesToTheFirstParagraphEvenInATable()
    {
        string table = OfficeFixtures.Tbl(
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("Titre")), OfficeFixtures.Tc(OfficeFixtures.P("autre"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("V1")), OfficeFixtures.Tc(OfficeFixtures.P("x"))));
        Assert.Equal(new[] { "V1" }, Ok(Docx(table, skip: true)).Values);
        Assert.Equal(new[] { "Titre", "V1" }, Ok(Docx(table)).Values);
    }

    [Fact]
    public void Docx_RunsSplitMidWord_AreGluedBack()
    {
        Assert.Equal(new[] { "AB12-34" }, DocxValues(OfficeFixtures.PRuns("AB", "12", "-", "34")));
        Assert.Equal(new[] { "A001" }, DocxValues(OfficeFixtures.PRuns("A", "0", "0", "1")));
    }

    [Fact]
    public void Docx_TabsBreaksAndNoBreakHyphens()
    {
        string p = OfficeFixtures.PRaw(
            OfficeFixtures.R("A") + "<w:r><w:tab/></w:r>" + OfficeFixtures.R("B") + "<w:r><w:br/></w:r>" + OfficeFixtures.R("C")
            + "<w:r><w:br w:type=\"page\"/></w:r>" + OfficeFixtures.R("D") + "<w:r><w:noBreakHyphen/><w:t>9</w:t></w:r>");
        Assert.Equal(new[] { "A B C D-9" }, DocxValues(p));
    }

    [Fact]
    public void Docx_TabStopDefinitionsAreNotTabs()
    {
        string p = OfficeFixtures.PRaw("<w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"2835\"/></w:tabs></w:pPr>" + OfficeFixtures.R("X") + OfficeFixtures.R("Y"));
        Assert.Equal(new[] { "XY" }, DocxValues(p));
    }

    [Fact]
    public void Docx_TrackedChanges_InsertionsKept_DeletionsDropped()
    {
        string p = OfficeFixtures.PRaw(
            OfficeFixtures.R("keep ")
            + "<w:ins w:id=\"1\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\"><w:r><w:t>new</w:t></w:r></w:ins>"
            + "<w:del w:id=\"2\" w:author=\"A\" w:date=\"2024-01-01T00:00:00Z\"><w:r><w:delText>old</w:delText></w:r></w:del>"
            + "<w:r><w:delText>stray</w:delText></w:r>");
        Assert.Equal(new[] { "keep new" }, DocxValues(p));
    }

    [Fact]
    public void Docx_MovedText_OnlyTheDestinationCounts()
    {
        string p = OfficeFixtures.PRaw(
            "<w:moveFrom w:id=\"1\" w:author=\"A\"><w:r><w:t>moved-away</w:t></w:r></w:moveFrom>"
            + "<w:moveTo w:id=\"2\" w:author=\"A\"><w:r><w:t>moved-here</w:t></w:r></w:moveTo>");
        Assert.Equal(new[] { "moved-here" }, DocxValues(p));
    }

    [Fact]
    public void Docx_FullyDeletedParagraph_IsEmpty()
    {
        string body = OfficeFixtures.P("A")
                      + OfficeFixtures.PRaw("<w:pPr><w:rPr><w:del w:id=\"3\" w:author=\"A\"/></w:rPr></w:pPr><w:del w:id=\"4\" w:author=\"A\"><w:r><w:delText>gone</w:delText></w:r></w:del>")
                      + OfficeFixtures.P("B");
        ListReadResult r = Ok(Docx(body));
        Assert.Equal(new[] { "A", "B" }, r.Values);
    }

    [Fact]
    public void Docx_EmptyParagraphs_AreSkipped_InteriorOnesCounted()
    {
        string body = OfficeFixtures.P("A") + "<w:p/>" + "<w:p></w:p>" + OfficeFixtures.P("  ") + OfficeFixtures.P("B") + "<w:p/>";
        ListReadResult r = Ok(Docx(body));
        Assert.Equal(new[] { "A", "B" }, r.Values);
        HasWarning(r, "3 ligne(s) vide(s) ignorée(s)");
    }

    [Fact]
    public void Docx_TableFirstCellOfEachRow_InDocumentOrder()
    {
        string table = OfficeFixtures.Tbl(
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("H1")), OfficeFixtures.Tc(OfficeFixtures.P("H2"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("A001")), OfficeFixtures.Tc(OfficeFixtures.P("x"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("A002") + OfficeFixtures.P("A002b")), OfficeFixtures.Tc(OfficeFixtures.P("y"))));
        string body = OfficeFixtures.P("Avant") + table + OfficeFixtures.P("Après");
        Assert.Equal(new[] { "Avant", "H1", "A001", "A002", "A002b", "Après" }, DocxValues(body));
    }

    [Fact]
    public void Docx_MergedCells()
    {
        string table = OfficeFixtures.Tbl(
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("SPAN"), "<w:gridSpan w:val=\"2\"/>")),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("V1"), "<w:vMerge w:val=\"restart\"/>"), OfficeFixtures.Tc(OfficeFixtures.P("r1"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc("<w:p/>", "<w:vMerge/>"), OfficeFixtures.Tc(OfficeFixtures.P("r2"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("LAST")), OfficeFixtures.Tc(OfficeFixtures.P("r3"))));
        Assert.Equal(new[] { "SPAN", "V1", "LAST" }, DocxValues(table));
    }

    [Fact]
    public void Docx_NestedTables_AreIgnored()
    {
        string nested = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("inner1")), OfficeFixtures.Tc(OfficeFixtures.P("inner2"))));
        string table = OfficeFixtures.Tbl(
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("outer1") + nested + "<w:p/>"), OfficeFixtures.Tc(OfficeFixtures.P("o2"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("outer2")), OfficeFixtures.Tc(nested + "<w:p/>")));
        Assert.Equal(new[] { "outer1", "outer2" }, DocxValues(table + OfficeFixtures.P("after")).Take(2).ToArray());
        Assert.Equal(new[] { "outer1", "outer2", "after" }, DocxValues(table + OfficeFixtures.P("after")));
    }

    [Fact]
    public void Docx_TwoSuccessiveTables_BothFirstCellsOnly()
    {
        string t1 = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("T1A")), OfficeFixtures.Tc(OfficeFixtures.P("x"))));
        string t2 = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("T2A")), OfficeFixtures.Tc(OfficeFixtures.P("y"))), OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("T2B")), OfficeFixtures.Tc(OfficeFixtures.P("z"))));
        Assert.Equal(new[] { "T1A", "T2A", "T2B" }, DocxValues(t1 + "<w:p/>" + t2));
    }

    [Fact]
    public void Docx_ContentControls_AreTraversed()
    {
        string block = "<w:sdt><w:sdtPr><w:alias w:val=\"Code\"/><w:tag w:val=\"t\"/><w:id w:val=\"1\"/><w:text/></w:sdtPr><w:sdtEndPr/><w:sdtContent>"
                       + OfficeFixtures.P("dans-sdt") + "</w:sdtContent></w:sdt>";
        string inline = OfficeFixtures.PRaw("<w:sdt><w:sdtPr><w:id w:val=\"2\"/></w:sdtPr><w:sdtContent><w:r><w:t>inline-sdt</w:t></w:r></w:sdtContent></w:sdt>");
        string dropdown = OfficeFixtures.PRaw("<w:sdt><w:sdtPr><w:dropDownList><w:listItem w:displayText=\"Rouge\" w:value=\"R\"/><w:listItem w:displayText=\"Vert\" w:value=\"V\"/></w:dropDownList></w:sdtPr><w:sdtContent><w:r><w:t>Rouge</w:t></w:r></w:sdtContent></w:sdt>");
        string nested = "<w:sdt><w:sdtPr/><w:sdtContent><w:sdt><w:sdtPr/><w:sdtContent>" + OfficeFixtures.P("deep") + "</w:sdtContent></w:sdt></w:sdtContent></w:sdt>";
        string inCell = OfficeFixtures.Tbl(OfficeFixtures.Tr("<w:sdt><w:sdtPr/><w:sdtContent>" + OfficeFixtures.Tc(OfficeFixtures.P("sdt-cell")) + "</w:sdtContent></w:sdt>", OfficeFixtures.Tc(OfficeFixtures.P("2nd"))));
        Assert.Equal(new[] { "dans-sdt", "inline-sdt", "Rouge", "deep", "sdt-cell" }, DocxValues(block + inline + dropdown + nested + inCell));
    }

    [Fact]
    public void Docx_ContentControlShowingItsPlaceholder_IsNotAValue()
    {
        string placeholder = "<w:sdt><w:sdtPr><w:id w:val=\"7\"/><w:showingPlcHdr/><w:text/></w:sdtPr><w:sdtContent><w:p><w:r><w:rPr><w:rStyle w:val=\"Textedelespacerserv\"/></w:rPr><w:t>Cliquez ici pour taper du texte.</w:t></w:r></w:p></w:sdtContent></w:sdt>";
        string filled = "<w:sdt><w:sdtPr><w:showingPlcHdr w:val=\"0\"/></w:sdtPr><w:sdtContent>" + OfficeFixtures.P("rempli") + "</w:sdtContent></w:sdt>";
        string innerPlaceholderOnly = "<w:sdt><w:sdtPr/><w:sdtContent>" + OfficeFixtures.P("avant") + placeholder + OfficeFixtures.P("après") + "</w:sdtContent></w:sdt>";
        Assert.Equal(new[] { "rempli", "avant", "après" }, DocxValues(placeholder + filled + innerPlaceholderOnly));
    }

    private static string TextBox(string text)
        => "<mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:inline><a:graphic><a:graphicData uri=\"x\"><wps:wsp><wps:txbx><w:txbxContent>"
           + $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"
           + "</w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice>"
           + "<mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent>"
           + $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"
           + "</w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent>";

    [Fact]
    public void Docx_AlternateContent_ChoiceIsReadOnce_FallbackSkipped()
    {
        // le texte de la zone de texte est dans <mc:Choice> ET dans <mc:Fallback> : une seule valeur
        string body = OfficeFixtures.PRaw(OfficeFixtures.R("host") + "<w:r>" + TextBox("BOX") + "</w:r>" + OfficeFixtures.R(" end"));
        Assert.Equal(new[] { "host end", "BOX" }, DocxValues(body));
    }

    [Fact]
    public void Docx_AlternateContent_AroundRuns()
    {
        string body = OfficeFixtures.PRaw(
            "<mc:AlternateContent><mc:Choice Requires=\"w14\"><w:r><w:t>choice</w:t></w:r></mc:Choice><mc:Fallback><w:r><w:t>fallback</w:t></w:r></mc:Fallback></mc:AlternateContent>");
        Assert.Equal(new[] { "choice" }, DocxValues(body));
    }

    [Fact]
    public void Docx_AlternateContent_SecondChoiceIgnored_FallbackAloneIsTaken()
    {
        string twoChoices = OfficeFixtures.PRaw(
            "<mc:AlternateContent><mc:Choice Requires=\"a\"><w:r><w:t>one</w:t></w:r></mc:Choice><mc:Choice Requires=\"b\"><w:r><w:t>two</w:t></w:r></mc:Choice></mc:AlternateContent>");
        Assert.Equal(new[] { "one" }, DocxValues(twoChoices));

        string fallbackOnly = OfficeFixtures.PRaw("<mc:AlternateContent><mc:Fallback><w:r><w:t>only-fallback</w:t></w:r></mc:Fallback></mc:AlternateContent>");
        Assert.Equal(new[] { "only-fallback" }, DocxValues(fallbackOnly));
    }

    [Fact]
    public void Docx_AlternateContent_AtBlockLevel()
    {
        string body = OfficeFixtures.P("before")
                      + "<mc:AlternateContent><mc:Choice Requires=\"w14\">" + OfficeFixtures.P("block-choice") + "</mc:Choice><mc:Fallback>" + OfficeFixtures.P("block-fallback") + "</mc:Fallback></mc:AlternateContent>"
                      + OfficeFixtures.P("after");
        Assert.Equal(new[] { "before", "block-choice", "after" }, DocxValues(body));
    }

    [Fact]
    public void Docx_TextBoxWithoutAlternateContent_IsAValueAfterItsHost()
    {
        string body = OfficeFixtures.PRaw(
            OfficeFixtures.R("host ") + "<w:r><w:pict><v:shape><v:textbox><w:txbxContent>" + OfficeFixtures.P("legacy-box") + "</w:txbxContent></v:textbox></v:shape></w:pict></w:r>" + OfficeFixtures.R("tail"));
        Assert.Equal(new[] { "host tail", "legacy-box" }, DocxValues(body + OfficeFixtures.P("next")).Take(2).ToArray());
        Assert.Equal(3, DocxValues(body + OfficeFixtures.P("next")).Length);
    }

    [Fact]
    public void Docx_HyperlinksFieldsAndSmartTags()
    {
        string body = OfficeFixtures.PRaw("<w:hyperlink r:id=\"rId5\" w:history=\"1\"><w:r><w:t>link-text</w:t></w:r></w:hyperlink>")
                      + OfficeFixtures.PRaw("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> MERGEFIELD Code </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>RESULT</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>")
                      + OfficeFixtures.PRaw("<w:fldSimple w:instr=\" PAGE \"><w:r><w:t>3</w:t></w:r></w:fldSimple>")
                      + OfficeFixtures.PRaw("<w:smartTag w:uri=\"x\" w:element=\"place\"><w:r><w:t>smart</w:t></w:r></w:smartTag>")
                      + OfficeFixtures.PRaw("<w:bookmarkStart w:id=\"0\" w:name=\"b\"/><w:proofErr w:type=\"gramStart\"/><w:r><w:t>marked</w:t></w:r><w:bookmarkEnd w:id=\"0\"/>");
        Assert.Equal(new[] { "link-text", "RESULT", "3", "smart", "marked" }, DocxValues(body));
    }

    [Fact]
    public void Docx_RubyAnnotations_AreSkipped()
    {
        string body = OfficeFixtures.PRaw("<w:r><w:ruby><w:rubyPr><w:rubyAlign w:val=\"distributeSpace\"/></w:rubyPr><w:rt><w:r><w:t>kafe</w:t></w:r></w:rt><w:rubyBase><w:r><w:t>Café</w:t></w:r></w:rubyBase></w:ruby></w:r>");
        Assert.Equal(new[] { "Café" }, DocxValues(body));
    }

    [Fact]
    public void Docx_MathText_IsNotParagraphText()
    {
        string body = OfficeFixtures.PRaw(OfficeFixtures.R("A") + "<m:oMath><m:r><m:t>x+1</m:t></m:r></m:oMath>" + OfficeFixtures.R("B"));
        Assert.Equal(new[] { "AB" }, DocxValues(body));
    }

    [Fact]
    public void Docx_NonBreakingSpacesAndInvisibleCharacters_AreCleaned()
    {
        Assert.Equal(new[] { "AB 12", "X" }, DocxValues(OfficeFixtures.P("AB\u00A012") + OfficeFixtures.P("\u00A0\u200BX\u00A0")));
    }

    [Fact]
    public void Docx_StrictNamespaces_AbsoluteTarget_AndMissingRels()
    {
        string body = OfficeFixtures.P("S1") + OfficeFixtures.P("S2");
        Assert.Equal(new[] { "S1", "S2" }, Ok(Read(OfficeFixtures.Docx(body, strict: true), "a.docx")).Values);
        Assert.Equal(new[] { "S1", "S2" }, Ok(Read(OfficeFixtures.Docx(body, absoluteTarget: true), "a.docx")).Values);
        Assert.Equal(new[] { "S1", "S2" }, Ok(Read(OfficeFixtures.Docx(body, withRels: false), "a.docx")).Values);
    }

    [Fact]
    public void Docx_NoParagraphText_IsAnError()
    {
        ListReadResult r = Failed(Docx("<w:p/>" + OfficeFixtures.P("  ")));
        Assert.Contains("Aucune valeur trouvée dans ce document Word", r.Error);
        ListReadResult r2 = Failed(Docx(""));
        Assert.Contains("Aucune valeur trouvée", r2.Error);
    }

    [Fact]
    public void Docx_VeryLongParagraph_IsTruncatedAndFlagged()
    {
        ListReadResult r = Ok(Docx(OfficeFixtures.P(new string('8', 60_000)) + OfficeFixtures.P("ok")));
        Assert.Equal(2, r.Values.Count);
        Assert.Equal(ListCollector.MaxValueChars + 1, r.Values[0].Length);
        HasWarning(r, "tronquée");
    }

    [Theory]
    [InlineData(5000, false, 5000, false)]
    [InlineData(5001, false, 5000, true)]
    [InlineData(5001, true, 5000, false)]
    [InlineData(6000, true, 5000, true)]
    public void Docx_Cap(int paragraphs, bool skip, int expectedCount, bool warns)
    {
        byte[] file = OfficeFixtures.SimpleDocx(Lines(paragraphs));
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "a.docx", skip));
        sw.Stop();
        Assert.Equal(expectedCount, r.Values.Count);
        if (warns) HasWarning(r, "5000 premières lignes");
        else Assert.Empty(r.Warnings);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Docx_LargeDocument_FromAnotherTool_IsReadFast()
    {
        // 50 000 paragraphes : la lecture s'arrête à la limite sans parcourir le reste
        byte[] file = OfficeFixtures.SimpleDocx(Lines(50_000));
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "a.docx"));
        sw.Stop();
        Assert.Equal(5000, r.Values.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    // ------------------------------------------------------------------------------------------------
    // Fichiers hostiles ou faux
    // ------------------------------------------------------------------------------------------------

    private static string SheetHeader => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\">";

    private static List<KeyValuePair<string, string>> XlsxPartsWithout(string partName)
    {
        List<KeyValuePair<string, string>> parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").SharedStrings(OfficeFixtures.SharedStringsXml(OfficeFixtures.Si("ok"))).BuildParts();
        parts.RemoveAll(p => p.Key == partName);
        return parts;
    }

    private static void AssertRefusedFast(byte[] file, string name, string expectedFragment, double maxSeconds = 20)
    {
        int before = ListFileReader.UnexpectedFailureCount;
        var sw = Stopwatch.StartNew();
        ListReadResult r = Failed(Read(file, name));
        sw.Stop();
        Assert.Contains(expectedFragment, r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(maxSeconds), "refusé en " + sw.Elapsed);
        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    [Fact]
    public void ZipBomb_Sheet_IsRefusedOnTheActualBytesRead()
    {
        // une feuille a droit à tout le plafond cumulé (200 Mo) : la bombe doit le dépasser
        byte[] bomb = OfficeFixtures.ZipWithHugePart(XlsxPartsWithout("xl/worksheets/sheet1.xml"), "xl/worksheets/sheet1.xml",
            SheetHeader + "<sheetData>", " ", "</sheetData></worksheet>", ListBudget.MaxSheetBytes + 10L * 1024 * 1024);
        Assert.True(bomb.Length < 1_000_000, "la bombe doit rester minuscule : " + bomb.Length);
        AssertRefusedFast(bomb, "bombe.xlsx", "démesur");
    }

    [Fact]
    public void ZipBomb_SharedStrings_IsRefused()
    {
        List<KeyValuePair<string, string>> parts = XlsxPartsWithout("xl/sharedStrings.xml");
        parts.RemoveAll(p => p.Key == "xl/worksheets/sheet1.xml");
        parts.Add(new("xl/worksheets/sheet1.xml", OfficeFixtures.SheetXml(OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0)))));
        byte[] bomb = OfficeFixtures.ZipWithHugePart(parts, "xl/sharedStrings.xml",
            "<?xml version=\"1.0\"?><sst xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><si><t>", "A", "</t></si></sst>", 60L * 1024 * 1024);
        Assert.True(bomb.Length < 200_000);
        AssertRefusedFast(bomb, "bombe.xlsx", "démesur");
    }

    [Fact]
    public void ZipBomb_Workbook_AndRelationships_AreRefused()
    {
        byte[] wb = OfficeFixtures.ZipWithHugePart(XlsxPartsWithout("xl/workbook.xml"), "xl/workbook.xml",
            "<?xml version=\"1.0\"?><workbook xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><sheets>", " ", "</sheets></workbook>", 60L * 1024 * 1024);
        AssertRefusedFast(wb, "bombe.xlsx", "démesur");

        byte[] rels = OfficeFixtures.ZipWithHugePart(XlsxPartsWithout("_rels/.rels"), "_rels/.rels",
            "<?xml version=\"1.0\"?><Relationships xmlns=\"" + OfficeFixtures.PackageRelsNs + "\">", " ", "</Relationships>", 60L * 1024 * 1024);
        AssertRefusedFast(rels, "bombe.xlsx", "démesur");
    }

    [Fact]
    public void ZipBomb_Docx_IsRefused()
    {
        List<KeyValuePair<string, string>> parts = OfficeFixtures.DocxParts(OfficeFixtures.P("x"));
        parts.RemoveAll(p => p.Key == "word/document.xml");
        byte[] bomb = OfficeFixtures.ZipWithHugePart(parts, "word/document.xml",
            "<?xml version=\"1.0\"?><w:document xmlns:w=\"" + OfficeFixtures.WordNs + "\"><w:body><w:p><w:r><w:t>", "A", "</w:t></w:r></w:p></w:body></w:document>", 60L * 1024 * 1024);
        Assert.True(bomb.Length < 200_000);
        AssertRefusedFast(bomb, "bombe.docx", "démesur");
    }

    [Fact]
    public void ZipBomb_JustUnderTheEntryCap_IsStillReadable()
    {
        // 150 Mo de blancs puis une vraie ligne : sous le plafond d'une feuille (200 Mo), la lecture aboutit
        byte[] file = OfficeFixtures.ZipWithHugePart(XlsxPartsWithout("xl/worksheets/sheet1.xml"), "xl/worksheets/sheet1.xml",
            SheetHeader + "<sheetData>", " ", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>FOUND</t></is></c></row></sheetData></worksheet>", 150L * 1024 * 1024);
        var sw = Stopwatch.StartNew();
        ListReadResult r = Ok(Read(file, "gros.xlsx"));
        sw.Stop();
        Assert.Equal(new[] { "FOUND" }, r.Values);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ZipBomb_DeclaredSizeLies_StillBoundedAndNeverThrows()
    {
        byte[] file = OfficeFixtures.ZipWithHugePart(XlsxPartsWithout("xl/worksheets/sheet1.xml"), "xl/worksheets/sheet1.xml",
            SheetHeader + "<sheetData>", " ", "</sheetData></worksheet>", 20L * 1024 * 1024);

        // met à 100 la taille « non compressée » annoncée dans l'en-tête local et dans le répertoire central de la partie piégée
        byte[] patched = (byte[])file.Clone();
        PatchDeclaredSize(patched, "xl/worksheets/sheet1.xml", 100);

        int before = ListFileReader.UnexpectedFailureCount;
        var sw = Stopwatch.StartNew();
        ListReadResult r = Read(patched, "menteur.xlsx");
        sw.Stop();
        Assert.NotNull(r);
        Assert.True(r.Error is not null || r.Values.Count == 0 || r.Values.Count > 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    private static void PatchDeclaredSize(byte[] zip, string entryName, int size)
    {
        byte[] name = Encoding.ASCII.GetBytes(entryName);
        for (int i = 0; i + 46 < zip.Length; i++)
        {
            bool central = zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02;
            bool local = zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04;
            if (!central && !local) continue;

            int nameOffset = central ? i + 46 : i + 30;
            if (nameOffset + name.Length > zip.Length || !zip.AsSpan(nameOffset, name.Length).SequenceEqual(name)) continue;

            int sizeOffset = central ? i + 24 : i + 22;
            BitConverter.GetBytes(size).CopyTo(zip, sizeOffset);
        }
    }

    [Fact]
    public void Zip_TooManyEntries_IsRefused()
    {
        var entries = new List<KeyValuePair<string, string>>(XlsxPartsWithout("nothing"));
        for (int i = 0; i < 6000; i++) entries.Add(new("junk/f" + i + ".txt", "x"));
        AssertRefusedFast(OfficeFixtures.Zip(entries), "beaucoup.xlsx", "nombre anormal");
    }

    [Fact]
    public void Zip_ManyEntriesButUnderTheCap_IsFine()
    {
        var entries = new List<KeyValuePair<string, string>>(XlsxPartsWithout("nothing"));
        for (int i = 0; i < 4500; i++) entries.Add(new("junk/f" + i + ".txt", "x"));
        ListReadResult r = Read(OfficeFixtures.Zip(entries), "beaucoup.xlsx");
        // la feuille « x » n'a pas de ligne : erreur « aucune valeur », mais pas de refus de l'archive
        Assert.DoesNotContain("nombre anormal", r.Error ?? "");
    }

    [Fact]
    public void Zip_EntryNamesWithTraversalOrAbsolutePaths_AreIgnored_NothingIsExtracted()
    {
        var entries = new List<(string Name, byte[] Data)>();
        var builder = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", OfficeFixtures.SheetXml(RowsOfInline("SAFE1", "SAFE2")));
        foreach (KeyValuePair<string, string> p in builder.BuildParts()) entries.Add((p.Key, Utf8(p.Value)));
        entries.Add(("../../evil.txt", Utf8("x")));
        entries.Add(("/etc/cron.d/evil", Utf8("x")));
        entries.Add(("C:\\Windows\\evil.txt", Utf8("x")));
        entries.Add(("..\\..\\evil2.txt", Utf8("x")));
        entries.Add(("xl/../../up.xml", Utf8("x")));
        entries.Add(("xl/worksheets/", Array.Empty<byte>()));
        entries.Add(("con:stream\\x", Utf8("x")));

        string marker = Path.Combine(Path.GetTempPath(), "evil.txt");
        bool existedBefore = File.Exists(marker);
        ListReadResult r = Ok(Read(OfficeFixtures.Zip(entries), "slip.xlsx"));
        Assert.Equal(new[] { "SAFE1", "SAFE2" }, r.Values);
        Assert.Equal(existedBefore, File.Exists(marker));
    }

    [Fact]
    public void Zip_RelationshipTargetOutsideThePackage_IsNotFollowed()
    {
        List<KeyValuePair<string, string>> parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").BuildParts();
        parts.RemoveAll(p => p.Key == "xl/worksheets/sheet1.xml");
        parts = parts.Select(p => p.Key == "xl/_rels/workbook.xml.rels"
            ? new KeyValuePair<string, string>(p.Key, p.Value.Replace("worksheets/sheet1.xml", "../../../etc/passwd"))
            : p).ToList();
        ListReadResult r = Failed(Read(OfficeFixtures.Zip(parts), "dehors.xlsx"));
        Assert.Contains("aucune feuille", r.Error);
    }

    [Theory]
    [InlineData("xl/workbook.xml", "xl/workbook.xml")]
    [InlineData("/xl/workbook.xml", "xl/workbook.xml")]
    [InlineData("xl\\worksheets\\sheet1.xml", "xl/worksheets/sheet1.xml")]
    [InlineData("./xl/./workbook.xml", "xl/workbook.xml")]
    [InlineData("xl/worksheets/../workbook.xml", "xl/workbook.xml")]
    [InlineData("xl//workbook.xml", "xl/workbook.xml")]
    [InlineData("../evil.xml", null)]
    [InlineData("xl/../../evil.xml", null)]
    [InlineData("..\\evil.xml", null)]
    [InlineData("C:\\Windows\\x.xml", null)]
    [InlineData("C:/x.xml", null)]
    [InlineData("xl/", null)]
    [InlineData("/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("a\0b.xml", null)]
    public void NormalizeName_Theory(string? name, string? expected)
    {
        Assert.Equal(expected, ListZipPackage.NormalizeName(name));
    }

    [Theory]
    [InlineData("xl", "worksheets/sheet1.xml", "xl/worksheets/sheet1.xml")]
    [InlineData("xl", "/xl/worksheets/sheet1.xml", "xl/worksheets/sheet1.xml")]
    [InlineData("xl", "../docProps/app.xml", "docProps/app.xml")]
    [InlineData("xl", "../../x.xml", null)]
    [InlineData("xl", "worksheets\\sheet1.xml", "xl/worksheets/sheet1.xml")]
    [InlineData("xl", "sheet1.xml#frag", "xl/sheet1.xml")]
    [InlineData("", "xl/workbook.xml", "xl/workbook.xml")]
    [InlineData("", "/xl/workbook.xml", "xl/workbook.xml")]
    [InlineData("xl", "", null)]
    [InlineData("xl", "   ", null)]
    public void ResolveTarget_Theory(string dir, string target, string? expected)
    {
        Assert.Equal(expected, ListZipPackage.ResolveTarget(dir, target));
    }

    [Fact]
    public void Budget_RefusesMoreThanTheTotalAndTooMuchTime()
    {
        var budget = new ListBudget(100, TimeSpan.FromSeconds(30));
        budget.Consume(60);
        budget.Consume(40);
        Assert.Throws<ListFileException>(() => budget.Consume(1));

        var slow = new ListBudget(long.MaxValue, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(30);
        Assert.Throws<ListFileException>(() => slow.CheckTime());
    }

    [Fact]
    public void LimitedStream_CountsActualBytes()
    {
        var budget = new ListBudget();
        using (var ok = new ListLimitedStream(new MemoryStream(new byte[100]), 100, budget))
        {
            byte[] buffer = new byte[64];
            int total = 0;
            int n;
            while ((n = ok.Read(buffer, 0, buffer.Length)) > 0) total += n;
            Assert.Equal(100, total);
        }

        using var tooMuch = new ListLimitedStream(new MemoryStream(new byte[101]), 100, budget);
        byte[] b = new byte[64];
        Assert.Throws<ListFileException>(() =>
        {
            while (tooMuch.Read(b, 0, b.Length) > 0)
            {
            }
        });
    }

    private const string XxeDoctype = "<!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>";

    private const string LaughsDoctype =
        "<!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">"
        + "<!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">"
        + "<!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\"><!ENTITY lol5 \"&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;\">"
        + "<!ENTITY lol6 \"&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;\"><!ENTITY lol7 \"&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;\">"
        + "<!ENTITY lol8 \"&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;\"><!ENTITY lol9 \"&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;\">]>";

    [Theory]
    [InlineData("xxe")]
    [InlineData("laughs")]
    [InlineData("plain")]
    public void Xxe_InTheSheet_IsRefused(string kind)
    {
        string doctype = kind == "xxe" ? XxeDoctype : kind == "laughs" ? LaughsDoctype : "<!DOCTYPE worksheet>";
        string entity = kind == "xxe" ? "&xxe;" : kind == "laughs" ? "&lol9;" : "x";
        string sheet = "<?xml version=\"1.0\"?>" + doctype + "<worksheet xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + entity + "</t></is></c></row></sheetData></worksheet>";
        ListReadResult r = Failed(Read(Xlsx(sheet), "a.xlsx"));
        Assert.Contains("DTD", r.Error);
        Assert.DoesNotContain("root:", r.Error);
    }

    [Fact]
    public void Xxe_InTheOtherXlsxParts_IsRefused()
    {
        string sst = "<?xml version=\"1.0\"?>" + XxeDoctype + "<sst xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><si><t>&xxe;</t></si></sst>";
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0));
        Assert.Contains("DTD", Failed(Read(Xlsx(OfficeFixtures.SheetXml(rows), sst), "a.xlsx")).Error);

        List<KeyValuePair<string, string>> parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", OfficeFixtures.SheetXml(RowsOfInline("A"))).BuildParts();
        foreach (string target in new[] { "xl/workbook.xml", "_rels/.rels", "xl/_rels/workbook.xml.rels" })
        {
            var hostile = parts.Select(p => p.Key == target ? new KeyValuePair<string, string>(p.Key, p.Value.Replace("?>", "?>" + XxeDoctype)) : p).ToList();
            ListReadResult r = Failed(Read(OfficeFixtures.Zip(hostile), "a.xlsx"));
            Assert.Contains("DTD", r.Error);
        }
    }

    [Fact]
    public void Xxe_InTheDocx_IsRefused()
    {
        List<KeyValuePair<string, string>> parts = OfficeFixtures.DocxParts(OfficeFixtures.P("&xxe;"));
        var hostile = parts.Select(p => p.Key == "word/document.xml" ? new KeyValuePair<string, string>(p.Key, p.Value.Replace("?>", "?>" + XxeDoctype)) : p).ToList();
        Assert.Contains("DTD", Failed(Read(OfficeFixtures.Zip(hostile), "a.docx")).Error);

        var laughs = parts.Select(p => p.Key == "word/document.xml" ? new KeyValuePair<string, string>(p.Key, p.Value.Replace("?>", "?>" + LaughsDoctype).Replace("&xxe;", "&lol9;")) : p).ToList();
        AssertRefusedFast(OfficeFixtures.Zip(laughs), "a.docx", "DTD", 5);
    }

    [Fact]
    public void DeeplyNestedXml_IsRefusedOrSkipped_NeverCrashes()
    {
        string open = string.Concat(Enumerable.Repeat("<a>", 100_000));
        string close = string.Concat(Enumerable.Repeat("</a>", 100_000));

        // avant les données : refusé (profondeur)
        string beforeData = SheetHeader + "<sheetPr>" + open + close + "</sheetPr><sheetData/></worksheet>";
        AssertRefusedFast(Xlsx(beforeData), "profond.xlsx", "imbriquée", 10);

        // dans une cellule : sauté en flux, sans récursion
        string inCell = SheetHeader + "<sheetData><row r=\"1\"><c r=\"A1\"><x>" + open + close + "</x></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>AFTER</t></is></c></row></sheetData></worksheet>";
        var sw = Stopwatch.StartNew();
        ListReadResult r = Read(Xlsx(inCell), "profond.xlsx");
        sw.Stop();
        Assert.True(r.Error is null || r.Values.Count == 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));

        // Word : paragraphes ou zones de contenu imbriqués à l'infini
        string w = string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 100_000)) + OfficeFixtures.P("x") + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 100_000));
        AssertRefusedFast(OfficeFixtures.Docx(w), "profond.docx", "imbriquée", 10);
        string p = string.Concat(Enumerable.Repeat("<w:p>", 100_000)) + string.Concat(Enumerable.Repeat("</w:p>", 100_000));
        AssertRefusedFast(OfficeFixtures.Docx(p), "profond2.docx", "imbriquée", 10);
    }

    [Fact]
    public void ModeratelyNestedDocument_IsFine()
    {
        // 20 tableaux imbriqués (extrême mais légitime) puis un paragraphe : lu normalement
        string inner = OfficeFixtures.P("deep");
        for (int i = 0; i < 20; i++) inner = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(inner + "<w:p/>")));
        Assert.Contains("deep", DocxValues(OfficeFixtures.P("top") + inner).Concat(new[] { "deep" }));
        ListReadResult r = Ok(Docx(OfficeFixtures.P("top") + inner));
        Assert.Equal("top", r.Values[0]);
    }

    [Fact]
    public void Ole2Files_AreRecognised_LegacyOrPasswordProtected()
    {
        foreach (string name in new[] { "a.xlsx", "a.docx" })
        {
            Assert.Contains("ancien format", Failed(Read(OleHeader(), name)).Error);
            ListReadResult enc = Failed(Read(OleHeader(encrypted: true), name));
            Assert.Contains("mot de passe", enc.Error);
        }

        foreach (string name in new[] { "a.txt", "a.csv" })
        {
            Assert.Contains("mot de passe", Failed(Read(OleHeader(encrypted: true), name)).Error);
        }
    }

    [Fact]
    public void CorruptZips_AreErrorsNotExceptions()
    {
        byte[] good = OfficeFixtures.SimpleXlsx(new[] { "A", "B", "C" });
        foreach (string name in new[] { "a.xlsx", "a.docx" })
        {
            Failed(Read(good.Take(good.Length / 2).ToArray(), name));
            Failed(Read(good.Take(22).ToArray(), name));
            Failed(Read(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, name));
            Failed(Read(new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Enumerable.Repeat((byte)0xAB, 500)).ToArray(), name));
            Failed(Read(good.Skip(10).ToArray(), name));

            // répertoire central détruit
            byte[] noCentral = (byte[])good.Clone();
            for (int i = noCentral.Length - 80; i < noCentral.Length; i++) noCentral[i] = 0;
            Failed(Read(noCentral, name));
        }
    }

    [Fact]
    public void EmptyZip_IsNotAWorkbookNorADocument()
    {
        byte[] empty = new byte[22];
        empty[0] = 0x50;
        empty[1] = 0x4B;
        empty[2] = 0x05;
        empty[3] = 0x06;
        Assert.Contains("n'est pas un classeur Excel", Failed(Read(empty, "a.xlsx")).Error);
        Assert.Contains("n'est pas un document Word", Failed(Read(empty, "a.docx")).Error);
    }

    [Fact]
    public void WrongKindOfOfficeFile_IsNamed()
    {
        byte[] word = OfficeFixtures.SimpleDocx(new[] { "A" });
        byte[] excel = OfficeFixtures.SimpleXlsx(new[] { "A" });
        Assert.Contains("document Word", Failed(Read(word, "a.xlsx")).Error);
        Assert.Contains("classeur Excel", Failed(Read(excel, "a.docx")).Error);

        byte[] odf = OfficeFixtures.Zip(new Dictionary<string, string> { ["mimetype"] = "application/vnd.oasis.opendocument.spreadsheet", ["content.xml"] = "<x/>" });
        Assert.Contains("OpenDocument", Failed(Read(odf, "a.xlsx")).Error);
        Assert.Contains("OpenDocument", Failed(Read(odf, "a.docx")).Error);

        byte[] other = OfficeFixtures.Zip(new Dictionary<string, string> { ["readme.txt"] = "hello" });
        Assert.Contains("xl/workbook.xml", Failed(Read(other, "a.xlsx")).Error);
        Assert.Contains("word/document.xml", Failed(Read(other, "a.docx")).Error);
    }

    [Fact]
    public void TextRenamedAsOffice_IsExplained()
    {
        byte[] csv = Utf8("a;b\n1;2\n");
        Assert.Contains("contient du texte", Failed(Read(csv, "export.xlsx")).Error);
        Assert.Contains("contient du texte", Failed(Read(csv, "export.docx")).Error);
        Assert.Contains("PDF", Failed(Read(Utf8("%PDF-1.4 ..."), "export.xlsx")).Error);
        Assert.Contains("RTF", Failed(Read(Utf8("{\\rtf1 hello}"), "export.docx")).Error);
        Assert.Contains("illisible", Failed(Read(new byte[] { 1, 2, 3, 0, 0, 5, 6, 7, 8, 0, 0, 0 }, "export.xlsx")).Error);
    }

    [Fact]
    public void MissingParts_AreExplained()
    {
        // pas de feuille dans le classeur
        string wb = "<?xml version=\"1.0\"?><workbook xmlns=\"" + OfficeFixtures.SpreadsheetNs + "\"><sheets/></workbook>";
        List<KeyValuePair<string, string>> noSheets = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").BuildParts();
        noSheets.RemoveAll(p => p.Key == "xl/worksheets/sheet1.xml");
        noSheets = noSheets.Select(p => p.Key == "xl/workbook.xml" ? new KeyValuePair<string, string>(p.Key, wb) : p).ToList();
        Assert.Contains("aucune feuille", Failed(Read(OfficeFixtures.Zip(noSheets), "a.xlsx")).Error);

        // la relation désigne une partie qui n'existe pas
        List<KeyValuePair<string, string>> dangling = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").BuildParts();
        dangling.RemoveAll(p => p.Key == "xl/worksheets/sheet1.xml");
        Assert.Contains("aucune feuille", Failed(Read(OfficeFixtures.Zip(dangling), "a.xlsx")).Error);

        // pas de document.xml
        List<KeyValuePair<string, string>> docParts = OfficeFixtures.DocxParts(OfficeFixtures.P("x"));
        docParts.RemoveAll(p => p.Key == "word/document.xml");
        Assert.Contains("word/document.xml", Failed(Read(OfficeFixtures.Zip(docParts), "a.docx")).Error);
    }

    [Fact]
    public void MalformedXml_IsAnErrorNotAnException()
    {
        int before = ListFileReader.UnexpectedFailureCount;
        foreach (string xml in new[] { "", "<", "<worksheet><sheetData><row>", SheetHeader + "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>unclosed", "not xml at all", "\u0000\u0001\u0002", "<a></b>" })
        {
            ListReadResult r = Read(Xlsx(xml), "a.xlsx");
            Assert.True(r.Error is not null, "XML mal formé : erreur attendue pour « " + xml + " »");
            Assert.Empty(r.Values);
        }

        foreach (string body in new[] { "<w:p><w:r><w:t>unclosed", "<w:p></w:r>", "<<<" })
        {
            ListReadResult r = Docx(body);
            Assert.True(r.Error is not null, "XML mal formé : erreur attendue");
        }

        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    [Fact]
    public void XmlControlCharacters_AsCharacterReferences_AreToleratedAndCleaned()
    {
        string rows = "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A&#x1;B</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>C&#11;D</t></is></c></row>";
        ListReadResult r = Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx");
        Assert.True(r.Error is null, r.Error);
        Assert.Equal(new[] { "AB", "C D" }, r.Values);
    }

    [Fact]
    public void XmlWithRawControlCharacters_IsAnErrorNotAnException()
    {
        // un caractère de contrôle écrit tel quel rend le XML invalide (Excel écrit _x0001_) : erreur claire, pas d'exception
        string rows = "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>A" + (char)1 + "B</t></is></c></row>";
        ListReadResult r = Failed(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Contains("illisible", r.Error);
    }

    [Fact]
    public void Utf16EncodedXmlParts_AreRead()
    {
        string sheet = OfficeFixtures.SheetXml(RowsOfInline("U1", "U2")).Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"");
        var parts = new OfficeFixtures.XlsxBuilder().AddSheet("Feuil1", "x").BuildParts().Where(p => p.Key != "xl/worksheets/sheet1.xml")
            .Select(p => (p.Key, Utf8(p.Value))).ToList();
        parts.Add(("xl/worksheets/sheet1.xml", new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(sheet)).ToArray()));
        Assert.Equal(new[] { "U1", "U2" }, Ok(Read(OfficeFixtures.Zip(parts), "a.xlsx")).Values);
    }

    // ------------------------------------------------------------------------------------------------
    // Fichiers sur disque
    // ------------------------------------------------------------------------------------------------

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "BarcodeTray.ListTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadFile_ReadsEachFormatFromDisk()
    {
        string dir = TempDir();
        try
        {
            string txt = Path.Combine(dir, "liste été.txt");
            string csv = Path.Combine(dir, "liste.CSV");
            string xlsx = Path.Combine(dir, "liste.xlsx");
            string docx = Path.Combine(dir, "liste.docx");
            File.WriteAllBytes(txt, Utf8("T1\nT2\n"));
            File.WriteAllBytes(csv, Cp1252("code;nom\nC1;é\nC2;è\n"));
            File.WriteAllBytes(xlsx, OfficeFixtures.SimpleXlsx(new[] { "X1", "X2" }));
            File.WriteAllBytes(docx, OfficeFixtures.SimpleDocx(new[] { "D1", "D2" }));

            ListReadResult rt = Ok(ListFileReader.ReadFile(txt, false));
            Assert.Equal(new[] { "T1", "T2" }, rt.Values);
            Assert.Equal("fichier « liste été.txt »", rt.Source);
            Assert.Equal(new[] { "C1", "C2" }, Ok(ListFileReader.ReadFile(csv, true)).Values);
            Assert.Equal(new[] { "X1", "X2" }, Ok(ListFileReader.ReadFile(xlsx, false)).Values);
            Assert.Equal(new[] { "D2" }, Ok(ListFileReader.ReadFile(docx, true)).Values);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFile_MissingFolderEmptyPath()
    {
        string dir = TempDir();
        try
        {
            Assert.Contains("introuvable", Failed(ListFileReader.ReadFile(Path.Combine(dir, "absent.txt"), false)).Error);
            Assert.Contains("introuvable", Failed(ListFileReader.ReadFile(Path.Combine(dir, "nodir", "absent.xlsx"), false)).Error);
            Assert.Contains("dossier", Failed(ListFileReader.ReadFile(dir, false)).Error);
            Directory.CreateDirectory(Path.Combine(dir, "sous.txt"));
            Assert.Contains("dossier", Failed(ListFileReader.ReadFile(Path.Combine(dir, "sous.txt"), false)).Error);
            Failed(ListFileReader.ReadFile("", false));
            Failed(ListFileReader.ReadFile("   ", false));
            Assert.Contains("formats acceptés", Failed(ListFileReader.ReadFile(Path.Combine(dir, "notes.pdf"), false)).Error);
            Assert.Contains("enregistrez-le en .xlsx / .docx", Failed(ListFileReader.ReadFile(Path.Combine(dir, "ancien.xls"), false)).Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFile_BiggerThanTheLimit_IsRefusedWithoutReadingIt()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "gros.txt");
            using (var fs = new FileStream(path, FileMode.Create))
            {
                fs.SetLength(ListFileReader.MaxFileBytes + 1);
            }

            var sw = Stopwatch.StartNew();
            ListReadResult r = Failed(ListFileReader.ReadFile(path, false));
            sw.Stop();
            Assert.Contains("trop volumineux", r.Error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFile_FileOpenElsewhere_GivesAHelpfulMessage()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "verrouille.txt");
            File.WriteAllText(path, "A\nB\n");
            using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            ListReadResult r = ListFileReader.ReadFile(path, false);
            if (r.Error is null) return;   // verrou exclusif non appliqué sur cette plateforme : rien à vérifier
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
            Assert.Empty(r.Values);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("utilisé par un autre programme", r.Error);   // violation de partage : Excel / Word ouvert dessus
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadFile_FileOpenForWritingByAnotherProgram_StillReadable()
    {
        // Excel garde le classeur ouvert avec un accès en écriture mais autorise la lecture
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "ouvert.txt");
            File.WriteAllText(path, "A\nB\n");
            using var excelLike = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
            Assert.Equal(new[] { "A", "B" }, Ok(ListFileReader.ReadFile(path, false)).Values);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Robustesse : fuzz, concurrence
    // ------------------------------------------------------------------------------------------------

    private static void AssertWellFormed(ListReadResult r)
    {
        Assert.NotNull(r);
        Assert.NotNull(r.Values);
        Assert.NotNull(r.Warnings);
        Assert.NotNull(r.Source);
        if (r.Error != null)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
            Assert.Empty(r.Values);
            return;
        }

        Assert.NotEmpty(r.Values);
        Assert.True(r.Values.Count <= ListFileReader.MaxValues);
        foreach (string v in r.Values)
        {
            Assert.False(string.IsNullOrEmpty(v));
            Assert.Equal(v.Trim(), v);
            Assert.True(v.Length <= ListCollector.MaxValueChars + 1);
            foreach (char c in v)
            {
                Assert.False(c < 0x20 || (c >= 0x7F && c <= 0x9F), $"caractère de contrôle U+{(int)c:X4} dans une valeur");
            }
        }
    }

    [Fact]
    public void Fuzz_RandomBytes_UnderEveryExtension_NeverThrow()
    {
        var rnd = new Random(20240601);
        int before = ListFileReader.UnexpectedFailureCount;
        foreach (string name in new[] { "f.txt", "f.csv", "f.xlsx", "f.docx", "f.xls", "f" })
        {
            for (int i = 0; i < 300; i++)
            {
                byte[] data = new byte[rnd.Next(0, 3000)];
                rnd.NextBytes(data);
                switch (i % 6)
                {
                    case 1 when data.Length > 4:
                        data[0] = 0x50; data[1] = 0x4B; data[2] = 3; data[3] = 4;   // en-tête ZIP
                        break;
                    case 2 when data.Length > 8:
                        Buffer.BlockCopy(OleHeader(), 0, data, 0, 8);               // en-tête OLE2
                        break;
                    case 3:
                        for (int k = 0; k < data.Length; k++) data[k] = (byte)(data[k] % 96 + 32);   // ASCII imprimable
                        break;
                }

                AssertWellFormed(Read(data, name, skip: i % 2 == 0));
            }
        }

        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    [Fact]
    public void Fuzz_RandomText_ReadText_NeverThrows()
    {
        var rnd = new Random(7);
        string alphabet = "\"\"\"\t\t\r\n\n;,  AB12\u00A0\u200B'é-";
        int before = ListFileReader.UnexpectedFailureCount;
        for (int i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            int len = rnd.Next(0, 80);
            for (int k = 0; k < len; k++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            AssertWellFormed(ListFileReader.ReadText(sb.ToString(), i % 3 == 0));
            AssertWellFormed(Read(Utf8(sb.ToString()), "f.csv", i % 3 == 0));
            AssertWellFormed(Read(Utf8(sb.ToString()), "f.txt"));
        }

        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    private static byte[] RichXlsx()
    {
        string sst = OfficeFixtures.SharedStringsXml(OfficeFixtures.SiRich("AB", "12"), OfficeFixtures.SiPhonetic("Café", "kafe"), OfficeFixtures.Si("x_x000D_y"));
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0) + OfficeFixtures.CellInline("B1", "b"))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellShared("A2", 1), "hidden=\"1\"")
                      + OfficeFixtures.Row(3, OfficeFixtures.CellShared("A3", 2) + OfficeFixtures.CellNumber("B3", "5.9E+12"))
                      + OfficeFixtures.Row(4, OfficeFixtures.CellBool("A4", true))
                      + OfficeFixtures.Row(9, OfficeFixtures.CellFormulaText("A9", "F"));
        return new OfficeFixtures.XlsxBuilder()
            .AddSheet("Feuil1", OfficeFixtures.SheetXml(rows, "<col min=\"3\" max=\"3\" hidden=\"1\"/>"))
            .AddSheet("Feuil2", OfficeFixtures.SheetXml(RowsOfInline("second")), "hidden")
            .SharedStrings(sst)
            .Build();
    }

    private static byte[] RichDocx()
    {
        string body = OfficeFixtures.P("one") + OfficeFixtures.PRuns("t", "wo")
                      + OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("cell")), OfficeFixtures.Tc(OfficeFixtures.P("c2"))))
                      + OfficeFixtures.PRaw(OfficeFixtures.R("host") + "<w:r>" + TextBox("BOX") + "</w:r>")
                      + "<w:sdt><w:sdtPr><w:showingPlcHdr/></w:sdtPr><w:sdtContent>" + OfficeFixtures.P("plc") + "</w:sdtContent></w:sdt>"
                      + OfficeFixtures.PRaw("<w:ins w:id=\"1\"><w:r><w:t>ins</w:t></w:r></w:ins><w:del w:id=\"2\"><w:r><w:delText>del</w:delText></w:r></w:del>");
        return OfficeFixtures.Docx(body);
    }

    [Fact]
    public void RichFixtures_AreReadAsExpected_BeforeFuzzingThem()
    {
        ListReadResult x = Ok(Read(RichXlsx(), "a.xlsx"));
        Assert.Equal(new[] { "AB12", "x y", "F" }, x.Values);   // ligne 2 masquée, ligne 4 booléenne
        ListReadResult d = Ok(Read(RichDocx(), "a.docx"));
        Assert.Equal(new[] { "one", "two", "cell", "host", "BOX", "ins" }, d.Values);
    }

    [Fact]
    public void Fuzz_MutatedCompressedOfficeFiles_NeverThrow()
    {
        var rnd = new Random(99);
        int before = ListFileReader.UnexpectedFailureCount;
        foreach ((byte[] good, string name) in new[] { (RichXlsx(), "f.xlsx"), (RichDocx(), "f.docx") })
        {
            for (int i = 0; i < 400; i++)
            {
                byte[] data = (byte[])good.Clone();
                int flips = rnd.Next(1, 8);
                for (int k = 0; k < flips; k++) data[rnd.Next(data.Length)] = (byte)rnd.Next(256);
                if (i % 5 == 0) data = data.Take(rnd.Next(1, data.Length)).ToArray();
                if (i % 7 == 0) data = data.Skip(rnd.Next(0, 40)).ToArray();
                AssertWellFormed(Read(data, name, skip: i % 2 == 0));
            }
        }

        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    [Fact]
    public void Fuzz_MutatedStoredXml_NeverThrows()
    {
        // entrées NON compressées : les octets modifiés atteignent l'analyseur XML tels quels
        var rnd = new Random(31337);
        int before = ListFileReader.UnexpectedFailureCount;
        var xlsxParts = new OfficeFixtures.XlsxBuilder()
            .AddSheet("Feuil1", OfficeFixtures.SheetXml(OfficeFixtures.Row(1, OfficeFixtures.CellShared("A1", 0)) + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "inl"), "hidden=\"0\"")))
            .SharedStrings(OfficeFixtures.SharedStringsXml(OfficeFixtures.SiRich("A", "B"), OfficeFixtures.Si("z")))
            .BuildParts();
        var docxParts = OfficeFixtures.DocxParts(OfficeFixtures.P("one") + TextBox("BOX") + OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("c")))));

        foreach ((List<KeyValuePair<string, string>> parts, string name) in new[] { (xlsxParts, "f.xlsx"), (docxParts, "f.docx") })
        {
            byte[] good = OfficeFixtures.Zip(parts.Select(p => (p.Key, Utf8(p.Value))), CompressionLevel.NoCompression);
            for (int i = 0; i < 500; i++)
            {
                byte[] data = (byte[])good.Clone();
                int flips = rnd.Next(1, 6);
                for (int k = 0; k < flips; k++)
                {
                    int pos = rnd.Next(data.Length);
                    // privilégie les caractères qui structurent le XML
                    data[pos] = rnd.Next(3) == 0 ? (byte)"<>/\"=&;! ?x"[rnd.Next(11)] : (byte)rnd.Next(256);
                }

                AssertWellFormed(Read(data, name));
            }
        }

        Assert.Equal(before, ListFileReader.UnexpectedFailureCount);
    }

    [Fact]
    public void ConcurrentReads_AreIndependent()
    {
        byte[] xlsx = OfficeFixtures.SimpleXlsx(Lines(300));
        byte[] docx = OfficeFixtures.SimpleDocx(Lines(300));
        string text = string.Join("\r\n", Lines(300));
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 48, i =>
        {
            ListReadResult r = (i % 3) switch
            {
                0 => Read(xlsx, "a.xlsx"),
                1 => Read(docx, "a.docx"),
                _ => ListFileReader.ReadText(text, false),
            };
            if (r.Error != null || r.Values.Count != 300 || r.Values[299] != "C00300") failures.Add($"{i}: {r.Error} {r.Values.Count}");
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void Performance_5000LinesInEveryFormat_UnderTwoSeconds()
    {
        string[] lines = Lines(5000);
        var cases = new List<(string Name, Func<ListReadResult> Run)>
        {
            ("txt", () => Read(Utf8(string.Join("\r\n", lines)), "a.txt")),
            ("csv", () => Read(Utf8(string.Join("\r\n", lines.Select(l => l + ";x;y"))), "a.csv")),
            ("paste", () => ListFileReader.ReadText(string.Join("\r\n", lines.Select(l => l + "\tx")), false)),
        };
        byte[] xlsx = OfficeFixtures.SimpleXlsx(lines);
        byte[] docx = OfficeFixtures.SimpleDocx(lines);
        cases.Add(("xlsx", () => Read(xlsx, "a.xlsx")));
        cases.Add(("docx", () => Read(docx, "a.docx")));

        foreach ((string name, Func<ListReadResult> run) in cases)
        {
            var sw = Stopwatch.StartNew();
            ListReadResult r = Ok(run());
            sw.Stop();
            Assert.Equal(5000, r.Values.Count);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{name} : 5000 lignes lues en {sw.Elapsed}");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : notation scientifique venue du texte (collage, .txt, .csv, .docx, chaînes partagées)
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("5,9012E+12", true)]
    [InlineData("5.9012E+12", true)]
    [InlineData("1,23457E+15", true)]
    [InlineData("-1.5E-5", true)]
    [InlineData("+2E+3", true)]
    [InlineData("1e+5", true)]
    [InlineData("12E+05", true)]
    [InlineData("1E5", false)]        // référence : le signe de l'exposant est obligatoire
    [InlineData("4E5", false)]
    [InlineData("12E3", false)]
    [InlineData("A1E+5", false)]
    [InlineData("E+12", false)]
    [InlineData("1.E+5", false)]
    [InlineData("1,E+5", false)]
    [InlineData(".5E+3", false)]
    [InlineData("5E+", false)]
    [InlineData("5E-", false)]
    [InlineData("5E+12X", false)]
    [InlineData("5E+1 2", false)]
    [InlineData("1234", false)]
    [InlineData("ABC-E+1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ScientificNotation_IsRecognisedOnlyWhenItLooksLikeAnExcelNumber(string? value, bool expected)
    {
        Assert.Equal(expected, ListCollector.IsScientificNotation(value));
    }

    [Fact]
    public void ReadText_ExcelColumnShownInScientificNotation_IsReportedNotSilentlyAccepted()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("5,9012E+12\r\n5,9013E+12\r\n1,23457E+15\r\n", false));
        Assert.Equal(new[] { "5,9012E+12", "5,9013E+12", "1,23457E+15" }, r.Values);
        HasWarning(r, "3 nombre(s) en notation scientifique");
        HasWarning(r, "formatez la colonne en Texte");
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.csv")]
    public void TextFiles_WithScientificNotation_AreReported(string name)
    {
        ListReadResult r = Ok(Read(Utf8("5.9012E+12\r\n5,9013E+12\r\nAAA-1\r\n"), name));
        Assert.Equal(3, r.Values.Count);
        HasWarning(r, "2 nombre(s) en notation scientifique");
    }

    [Fact]
    public void Csv_ScientificNotationInTheFirstColumnOfAFrenchExcelExport_IsReported()
    {
        ListReadResult r = Ok(Read(Utf8("EAN;Libelle\r\n5,9012E+12;Pomme\r\n5,9013E+12;Poire\r\n"), "export.csv", skip: true));
        Assert.Equal(new[] { "5,9012E+12", "5,9013E+12" }, r.Values);
        HasWarning(r, "2 nombre(s) en notation scientifique");
    }

    [Fact]
    public void Xlsx_ScientificNotationInSharedStringCells_IsReported()
    {
        // texte « 5,9012E+12 » enregistré comme chaîne (colonne copiée-collée depuis un affichage abrégé) : aucune cellule numérique
        ListReadResult r = Ok(Read(OfficeFixtures.SimpleXlsx(new[] { "5,9012E+12", "AAA-1", "5,9013E+12" }), "a.xlsx"));
        Assert.Equal(3, r.Values.Count);
        HasWarning(r, "2 nombre(s) en notation scientifique");
    }

    [Fact]
    public void Docx_ScientificNotation_IsReported()
    {
        ListReadResult r = Ok(Docx(OfficeFixtures.P("5,9012E+12") + OfficeFixtures.P("AAA-1")));
        HasWarning(r, "1 nombre(s) en notation scientifique");
    }

    [Theory]
    [InlineData("1.5E-5")]
    [InlineData("1.5E20")]       // Java (Apache POI) n'écrit pas le « + » : c'est bien un nombre
    [InlineData("1.2345678901234567E+19")]
    public void Xlsx_ScientificNumericCell_IsReportedExactlyOnce(string stored)
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", stored)) + OfficeFixtures.Row(2, OfficeFixtures.CellNumber("A2", "12345"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(1, r.Warnings.Count(w => w.Contains("notation scientifique", StringComparison.Ordinal)));
        HasWarning(r, "1 nombre(s) en notation scientifique");
    }

    [Fact]
    public void ScientificNotation_InTheSkippedTitle_OrInSafeCodes_IsNotReported()
    {
        ListReadResult title = Ok(ListFileReader.ReadText("1,5E+3\r\nAAA-1\r\nBBB-2\r\n", true));
        Assert.Equal(new[] { "AAA-1", "BBB-2" }, title.Values);
        NoWarning(title, "scientifique");

        ListReadResult safe = Ok(ListFileReader.ReadText("4E5\r\nAB12E3\r\n12E3\r\n", false));
        Assert.Equal(3, safe.Values.Count);
        NoWarning(safe, "scientifique");
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : format d'affichage des cellules numériques d'un classeur (00000, "CH-"0000, 0.00, #,##0...)
    // ------------------------------------------------------------------------------------------------

    private static ListReadResult NumberWithStyle(string stored, int style)
        => Ok(Read(Xlsx(OfficeFixtures.SheetXml(OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", stored, style: style)))), "a.xlsx"));

    [Theory]
    [InlineData(7, "123", "00123")]            // 00000
    [InlineData(7, "12345", "12345")]
    [InlineData(7, "1234567", "1234567")]      // plus de chiffres que de zéros : Excel affiche tout
    [InlineData(7, "0", "00000")]
    [InlineData(8, "7", "CH-0007")]            // "CH-"0000
    [InlineData(8, "12345", "CH-12345")]
    [InlineData(13, "42", "00042")]            // 00000;\-00000 : seule la première section compte
    [InlineData(11, "123", "123")]             // format intégré « 0 » : entier tel quel
    public void Xlsx_ZeroPaddingNumberFormats_AreAppliedLikeExcelDisplaysThem(int style, string stored, string expected)
    {
        ListReadResult r = NumberWithStyle(stored, style);
        Assert.Equal(new[] { expected }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData(9, "5")]            // 0.00 : afficherait 5.00
    [InlineData(10, "1234567")]     // #,##0 : afficherait 1,234,567
    [InlineData(12, "123")]         // 00\-000 : répartition des chiffres non gérée
    [InlineData(14, "123")]         // symbole monétaire non ASCII
    [InlineData(4, "12")]           // 0.0" d"
    public void Xlsx_OtherNumberFormats_KeepTheStoredValueAndWarn(int style, string stored)
    {
        ListReadResult r = NumberWithStyle(stored, style);
        Assert.Equal(new[] { stored }, r.Values);
        HasWarning(r, "1 nombre(s) affiché(s) avec un format Excel");
        HasWarning(r, "valeur brute");
        HasWarning(r, "« Coller »");
    }

    [Theory]
    [InlineData("1.5")]      // 00000 sur une décimale : Excel arrondirait
    [InlineData("-5")]       // négatif : « -00005 », non géré
    [InlineData("1.5E-5")]
    public void Xlsx_ZeroPaddingFormatOnANonInteger_KeepsTheStoredValueAndWarns(string stored)
    {
        ListReadResult r = NumberWithStyle(stored, 7);
        Assert.Equal(new[] { stored }, r.Values);
        HasWarning(r, "format Excel");
    }

    [Fact]
    public void Xlsx_LeadingZeroColumn_ReadsLikeThePasteWouldGive()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "123", style: 7)) + OfficeFixtures.Row(2, OfficeFixtures.CellNumber("A2", "45", style: 7))
                      + OfficeFixtures.Row(3, OfficeFixtures.CellNumber("A3", "6789", style: 7));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        ListReadResult pasted = Ok(ListFileReader.ReadText("00123\r\n00045\r\n06789\r\n", false));
        Assert.Equal(pasted.Values, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Xlsx_FormattedNumbersInAnotherColumn_AreNotReported()
    {
        // codes en A, prix « 0.00 » en B, date en C : seule la colonne lue compte pour les remarques
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "AAA-1") + OfficeFixtures.CellNumber("B1", "5", style: 9) + OfficeFixtures.CellNumber("C1", "45123", style: 3))
                      + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "BBB-2") + OfficeFixtures.CellNumber("B2", "6", style: 9) + OfficeFixtures.CellNumber("C2", "45124", style: 3));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "AAA-1", "BBB-2" }, r.Values);
        NoWarning(r, "format Excel");
        NoWarning(r, "date");
        HasWarning(r, "Seule la première colonne a été lue");
    }

    [Fact]
    public void Xlsx_FormattedNumbersInAHiddenRow_AreNotReported()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellNumber("A1", "5", style: 9), "hidden=\"1\"") + OfficeFixtures.Row(2, OfficeFixtures.CellNumber("A2", "6", style: 0));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "6" }, r.Values);
        NoWarning(r, "format Excel");
    }

    [Theory]
    [InlineData(0, null, "Plain")]
    [InlineData(49, null, "Plain")]
    [InlineData(1, null, "ZeroPad||1|")]
    [InlineData(2, null, "Other")]
    [InlineData(9, null, "Other")]
    [InlineData(37, null, "Other")]
    [InlineData(164, "00000", "ZeroPad||5|")]
    [InlineData(164, "0", "ZeroPad||1|")]
    [InlineData(164, "\"CH-\"0000", "ZeroPad|CH-|4|")]
    [InlineData(164, "0000\" pcs\"", "ZeroPad||4| pcs")]
    [InlineData(164, "\\N\\°0000", "Other")]                 // « ° » n'est pas de l'ASCII
    [InlineData(164, "\\N0000", "ZeroPad|N|4|")]
    [InlineData(164, "[Red]0000", "ZeroPad||4|")]
    [InlineData(164, "00000;-00000", "ZeroPad||5|")]
    [InlineData(164, "00000;;;@", "ZeroPad||5|")]
    [InlineData(164, "0000_)", "ZeroPad||4| ")]
    [InlineData(164, "(0000)", "ZeroPad|(|4|)")]
    [InlineData(164, "0.00", "Other")]
    [InlineData(164, "#,##0", "Other")]
    [InlineData(164, "0%", "Other")]
    [InlineData(164, "0.00E+00", "Other")]
    [InlineData(164, "# ?/?", "Other")]
    [InlineData(164, "00\\-000", "Other")]
    [InlineData(164, "[>100]0000", "Other")]
    [InlineData(164, "\"abc\"", "Other")]
    [InlineData(164, "\"CH-", "Other")]
    [InlineData(164, "General", "Plain")]
    [InlineData(164, "@", "Plain")]
    [InlineData(164, "", "Plain")]
    [InlineData(164, null, "Plain")]
    [InlineData(164, "[$€-407] 0000", "Other")]
    public void Xlsx_ClassifyNumberFormat(int id, string? code, string expected)
    {
        ListFileXlsx.CellNumberFormat f = ListFileXlsx.ClassifyNumberFormat(id, code);
        string actual = f.Kind == ListFileXlsx.CellFormatKind.ZeroPad ? $"ZeroPad|{f.Prefix}|{f.Digits}|{f.Suffix}" : f.Kind.ToString();
        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : .docx — tabulations / sauts de ligne dans un paragraphe, colonnes d'un tableau
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Docx_TabSeparatedColumnsInAParagraph_AreMergedAndReported()
    {
        string body = OfficeFixtures.PRaw(OfficeFixtures.R("AB-001") + "<w:r><w:tab/></w:r>" + OfficeFixtures.R("Vis M5"))
                      + OfficeFixtures.PRaw(OfficeFixtures.R("AB-002") + "<w:r><w:tab/></w:r>" + OfficeFixtures.R("Écrou M5"))
                      + OfficeFixtures.P("AB-003");
        ListReadResult r = Ok(Docx(body));
        Assert.Equal(new[] { "AB-001 Vis M5", "AB-002 Écrou M5", "AB-003" }, r.Values);
        HasWarning(r, "2 paragraphe(s) contiennent des tabulations ou des sauts de ligne");
        HasWarning(r, "touche Entrée");
    }

    [Fact]
    public void Docx_ShiftEnterLineBreakBetweenTwoCodes_IsReported()
    {
        string body = OfficeFixtures.PRaw(OfficeFixtures.R("CODE-B") + "<w:r><w:br/></w:r>" + OfficeFixtures.R("CODE-C")) + OfficeFixtures.P("CODE-D");
        ListReadResult r = Ok(Docx(body));
        Assert.Equal(new[] { "CODE-B CODE-C", "CODE-D" }, r.Values);
        HasWarning(r, "1 paragraphe(s) contiennent des tabulations ou des sauts de ligne (Maj+Entrée)");
    }

    [Fact]
    public void Docx_LeadingOrTrailingTabs_OrTabStopsOnly_AreNotReported()
    {
        string body = OfficeFixtures.PRaw("<w:r><w:tab/></w:r>" + OfficeFixtures.R("INDENTÉ"))
                      + OfficeFixtures.PRaw(OfficeFixtures.R("FIN") + "<w:r><w:tab/></w:r>")
                      + OfficeFixtures.PRaw("<w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"2835\"/></w:tabs></w:pPr>" + OfficeFixtures.R("X") + OfficeFixtures.R("Y"))
                      + OfficeFixtures.PRaw(OfficeFixtures.R("A") + "<w:r><w:br w:type=\"page\"/></w:r>");
        ListReadResult r = Ok(Docx(body));
        Assert.Equal(new[] { "INDENTÉ", "FIN", "XY", "A" }, r.Values);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Docx_TableWithSeveralColumns_ReportsThatOnlyTheFirstWasRead()
    {
        string table = OfficeFixtures.Tbl(
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("AB-001")), OfficeFixtures.Tc(OfficeFixtures.P("Vis M5")), OfficeFixtures.Tc(OfficeFixtures.P("12"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("AB-002")), OfficeFixtures.Tc(OfficeFixtures.P("Écrou")), OfficeFixtures.Tc(OfficeFixtures.P("7"))),
            OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("AB-003")), OfficeFixtures.Tc("<w:p/>"), OfficeFixtures.Tc("<w:p/>")));
        ListReadResult r = Ok(Docx(table));
        Assert.Equal(new[] { "AB-001", "AB-002", "AB-003" }, r.Values);
        HasWarning(r, "Seule la première colonne des tableaux a été lue");
        HasWarning(r, "2 ligne(s) de tableau concernée(s)");
    }

    [Fact]
    public void Docx_OneColumnTable_OrEmptyOtherCells_OrDeletedText_AreNotReported()
    {
        string oneColumn = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("A1"))), OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("A2"))));
        string emptyOthers = OfficeFixtures.Tbl(OfficeFixtures.Tr(OfficeFixtures.Tc(OfficeFixtures.P("B1")), OfficeFixtures.Tc("<w:p/>"), OfficeFixtures.Tc(OfficeFixtures.P("  "))));
        string deleted = OfficeFixtures.Tbl(OfficeFixtures.Tr(
            OfficeFixtures.Tc(OfficeFixtures.P("C1")),
            OfficeFixtures.Tc(OfficeFixtures.PRaw("<w:del w:id=\"1\" w:author=\"x\"><w:r><w:delText>supprimé</w:delText></w:r></w:del>"))));
        ListReadResult r = Ok(Docx(oneColumn + "<w:p/>" + emptyOthers + "<w:p/>" + deleted));
        Assert.Equal(new[] { "A1", "A2", "B1", "C1" }, r.Values.Where(v => v.Length > 0).ToArray());
        NoWarning(r, "première colonne");
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : texte collé depuis Word (numérotation automatique, puces)
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.", true)]
    [InlineData("12)", true)]
    [InlineData("(3)", true)]
    [InlineData("a.", true)]
    [InlineData("B)", true)]
    [InlineData("iv.", true)]
    [InlineData("XII.", true)]
    [InlineData("1.1.", true)]
    [InlineData("2.3.4)", true)]
    [InlineData("•", true)]
    [InlineData("·", true)]
    [InlineData("§", true)]
    [InlineData("-", true)]
    [InlineData("", true)]
    [InlineData("", true)]
    [InlineData("1", false)]            // un code à un chiffre n'est pas une numérotation
    [InlineData("12345.", false)]
    [InlineData("AB.", false)]
    [InlineData("mix.", true)]           // chiffres romains en minuscules (m, i, x)
    [InlineData("AAA-1", false)]
    [InlineData("1.A", false)]
    [InlineData("1..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ListMarkers_RecogniseWordNumberingAndBullets(string? value, bool expected)
    {
        Assert.Equal(expected, ListMarkers.IsMarker(value));
    }

    [Fact]
    public void ReadText_WordNumberedList_UsesTheTextNotTheNumber()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("1.\tAAA-1\r\n2.\tBBB-2\r\n3.\tCCC-3\r\n", false));
        Assert.Equal(new[] { "AAA-1", "BBB-2", "CCC-3" }, r.Values);
        HasWarning(r, "numérotation automatique de Word");
        HasWarning(r, "deuxième colonne");
        NoWarning(r, "Seule la première colonne a été lue");
    }

    [Theory]
    [InlineData("•")]
    [InlineData("·")]
    [InlineData("")]
    [InlineData("(1)")]
    [InlineData("a)")]
    public void ReadText_WordBulletsAndOtherNumbering_UseTheText(string marker)
    {
        ListReadResult r = Ok(ListFileReader.ReadText(marker + "\tAAA-1\r\n" + marker + "\tBBB-2\r\n", false));
        Assert.Equal(new[] { "AAA-1", "BBB-2" }, r.Values);
        HasWarning(r, "numéros ou des puces");
    }

    [Fact]
    public void ReadText_WordNumberedListWithATitle_AndMoreColumns()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("N°\tCode\tNom\r\n1.\tAAA\tPomme\r\n2.\tBBB\tPoire\r\n", true));
        Assert.Equal(new[] { "AAA", "BBB" }, r.Values);
        Assert.Equal("Code", r.SkippedTitle);
        HasWarning(r, "les suivantes sont ignorées");
    }

    [Fact]
    public void ReadText_TwoColumnsWhoseFirstIsNotAList_StillTakesTheFirstColumn()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("AAA-1\tPomme\r\nBBB-2\tPoire\r\n", false));
        Assert.Equal(new[] { "AAA-1", "BBB-2" }, r.Values);
        HasWarning(r, "Seule la première colonne a été lue");

        ListReadResult plainNumbers = Ok(ListFileReader.ReadText("1\tAAA\r\n2\tBBB\r\n", false));
        Assert.Equal(new[] { "1", "2" }, plainNumbers.Values);   // « 1 » sans point : c'est un code
    }

    [Fact]
    public void ReadText_NumberingOnlyColumn_OrHalfNumbered_IsLeftAlone()
    {
        // une seule colonne « 1. 2. 3. » : pas de deuxième cellule, rien à substituer
        ListReadResult single = Ok(ListFileReader.ReadText("1.\r\n2.\r\n3.\r\n", false));
        Assert.Equal(new[] { "1.", "2.", "3." }, single.Values);

        // la moitié seulement des lignes sont numérotées : ce n'est pas une numérotation
        ListReadResult half = Ok(ListFileReader.ReadText("1.\tAAA\r\nXX-9\tBBB\r\n2.\tCCC\r\nYY-8\tDDD\r\n", false));
        Assert.Equal(new[] { "1.", "XX-9", "2.", "YY-8" }, half.Values);

        // numérotation, mais la 2e cellule manque sur plus de 10 % des lignes : on ne devine pas
        ListReadResult gaps = Ok(ListFileReader.ReadText("1.\tAAA\r\n2.\r\n3.\r\n4.\tDDD\r\n", false));
        Assert.Equal(new[] { "1.", "2.", "3.", "4." }, gaps.Values);
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : sauts de ligne à l'intérieur d'une valeur
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void ReadText_QuotedCellWithALineBreak_BecomesASpaceAndIsReported()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("\"A\r\nB\"\r\nC\r\n", false));
        Assert.Equal(new[] { "A B", "C" }, r.Values);
        HasWarning(r, "1 valeur(s) contenaient un saut de ligne");
        HasWarning(r, "espace");
    }

    [Fact]
    public void Csv_MultiLineQuotedField_IsReported()
    {
        ListReadResult r = Ok(Read(Utf8("code;n\r\n\"X\nY\";1\r\nZ;2\r\n"), "a.csv", skip: true));
        Assert.Equal(new[] { "X Y", "Z" }, r.Values);
        HasWarning(r, "1 valeur(s) contenaient un saut de ligne");
    }

    [Fact]
    public void Xlsx_AltEnterInsideACell_IsReported()
    {
        string rows = OfficeFixtures.Row(1, OfficeFixtures.CellInline("A1", "A\nB")) + OfficeFixtures.Row(2, OfficeFixtures.CellInline("A2", "C"));
        ListReadResult r = Ok(Read(Xlsx(OfficeFixtures.SheetXml(rows)), "a.xlsx"));
        Assert.Equal(new[] { "A B", "C" }, r.Values);
        HasWarning(r, "1 valeur(s) contenaient un saut de ligne");
    }

    [Fact]
    public void LineBreaks_OnlyAtTheEdges_OrInTheSkippedTitle_AreNotReported()
    {
        ListReadResult edges = Ok(ListFileReader.ReadText("\"A\r\n\"\r\n\"\nB\"\r\n", false));
        Assert.Equal(new[] { "A", "B" }, edges.Values);
        NoWarning(edges, "saut de ligne");

        ListReadResult title = Ok(ListFileReader.ReadText("\"Code\nEAN\"\r\nAAA\r\nBBB\r\n", true));
        Assert.Equal(new[] { "AAA", "BBB" }, title.Values);
        NoWarning(title, "saut de ligne");
    }

    // ------------------------------------------------------------------------------------------------
    // Revue : titre ignoré (première ligne)
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void SkippedTitle_IsReturnedWithTheResult_ButNotAsAWarning()
    {
        ListReadResult r = Ok(ListFileReader.ReadText("Référence\r\nAAA-1\r\nBBB-2\r\n", true));
        Assert.Equal("Référence", r.SkippedTitle);
        Assert.Empty(r.Warnings);

        Assert.Null(Ok(ListFileReader.ReadText("Référence\r\nAAA-1\r\n", false)).SkippedTitle);

        ListReadResult onlyTitle = ListFileReader.ReadText("Référence\r\n", true);
        Assert.NotNull(onlyTitle.Error);
        Assert.Null(onlyTitle.SkippedTitle);
    }

    [Fact]
    public void SkippedTitle_IsReturnedByEveryFormat()
    {
        Assert.Equal("Code", Ok(Read(Utf8("Code\nA1\nA2\n"), "a.txt", skip: true)).SkippedTitle);
        Assert.Equal("Code", Ok(Read(Utf8("Code;n\nA1;1\nA2;2\n"), "a.csv", skip: true)).SkippedTitle);
        Assert.Equal("Code", Ok(Read(OfficeFixtures.SimpleXlsx(new[] { "Code", "A1", "A2" }), "a.xlsx", skip: true)).SkippedTitle);
        Assert.Equal("Code", Ok(Docx(OfficeFixtures.P("Code") + OfficeFixtures.P("A1"), skip: true)).SkippedTitle);
    }
}
