using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace BarcodeTray.Tests.Support;

/// <summary>
/// Fabrique, dans les tests et sans fichier sur disque, des classeurs .xlsx et documents .docx réalistes (mêmes structures de paquet
/// qu'Excel 365, LibreOffice, Google Sheets et Word) avec <c>System.IO.Compression</c> seulement.
/// <para>Fonctions pures (aucune dépendance à System.Drawing) : utilisables sous Linux comme sous Windows.</para>
/// </summary>
internal static class OfficeFixtures
{
    internal const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const string PackageRelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    internal const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private const string XmlDecl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    /// <summary>Échappement XML d'un texte (&amp; &lt; &gt; et guillemets).</summary>
    internal static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    // ------------------------------------------------------------------------------------------------
    // ZIP
    // ------------------------------------------------------------------------------------------------

    /// <summary>Archive ZIP dont les entrées sont des textes UTF-8 (sans BOM).</summary>
    internal static byte[] Zip(IEnumerable<KeyValuePair<string, string>> parts, CompressionLevel level = CompressionLevel.Optimal)
        => Zip(parts.Select(p => (p.Key, new UTF8Encoding(false).GetBytes(p.Value))), level);

    /// <summary>Archive ZIP d'entrées binaires. Les noms sont écrits tels quels (« ../x », « /abs », « a\b » possibles : tests de sécurité).</summary>
    internal static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, level);
                using Stream s = entry.Open();
                s.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Archive contenant une partie de <paramref name="size"/> octets répétant <paramref name="fill"/> (« bombe » : quelques Ko pour des centaines de Mo),
    /// écrite par morceaux (aucune allocation de la taille décompressée). <paramref name="prefix"/> / <paramref name="suffix"/> encadrent le remplissage.
    /// </summary>
    internal static byte[] ZipWithHugePart(
        IEnumerable<KeyValuePair<string, string>> otherParts,
        string hugeName,
        string prefix,
        string fill,
        string suffix,
        long size)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, string> p in otherParts)
            {
                ZipArchiveEntry e = zip.CreateEntry(p.Key, CompressionLevel.Optimal);
                using Stream s = e.Open();
                byte[] b = new UTF8Encoding(false).GetBytes(p.Value);
                s.Write(b, 0, b.Length);
            }

            ZipArchiveEntry huge = zip.CreateEntry(hugeName, CompressionLevel.SmallestSize);
            using Stream hs = huge.Open();
            byte[] head = Encoding.UTF8.GetBytes(prefix);
            hs.Write(head, 0, head.Length);
            byte[] chunk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(fill, Math.Max(1, 65536 / Math.Max(1, fill.Length)))));
            long written = 0;
            while (written < size)
            {
                int n = (int)Math.Min(chunk.Length, size - written);
                hs.Write(chunk, 0, n);
                written += n;
            }

            byte[] tail = Encoding.UTF8.GetBytes(suffix);
            hs.Write(tail, 0, tail.Length);
        }

        return ms.ToArray();
    }

    /// <summary>Textes des entrées d'une archive (pour vérifier ce que produit une fixture).</summary>
    internal static Dictionary<string, string> Unzip(byte[] zipBytes)
    {
        var result = new Dictionary<string, string>();
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            result[e.FullName] = reader.ReadToEnd();
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // XLSX : cellules, lignes, feuilles, chaînes partagées
    // ------------------------------------------------------------------------------------------------

    /// <summary>Cellule « chaîne partagée » (Excel, LibreOffice, Google Sheets).</summary>
    internal static string CellShared(string reference, int index, int? style = null)
        => $"<c r=\"{reference}\"{StyleAttr(style)} t=\"s\"><v>{index}</v></c>";

    /// <summary>Cellule « chaîne en ligne » (générateurs simples : openpyxl write-only, xlsx-populate...).</summary>
    internal static string CellInline(string reference, string text)
        => $"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>";

    /// <summary>Cellule « chaîne en ligne » en texte riche (plusieurs exécutions).</summary>
    internal static string CellInlineRich(string reference, params string[] runs)
        => $"<c r=\"{reference}\" t=\"inlineStr\"><is>{string.Concat(runs.Select(r => $"<r><rPr><b/></rPr><t xml:space=\"preserve\">{Esc(r)}</t></r>"))}</is></c>";

    /// <summary>Cellule numérique : <paramref name="storedText"/> est exactement le texte écrit dans &lt;v&gt;.</summary>
    internal static string CellNumber(string reference, string storedText, int? style = null)
        => $"<c r=\"{reference}\"{StyleAttr(style)}><v>{storedText}</v></c>";

    /// <summary>Résultat texte d'une formule (type « str »).</summary>
    internal static string CellFormulaText(string reference, string text, string formula = "A1&amp;\"\"")
        => $"<c r=\"{reference}\" t=\"str\"><f>{formula}</f><v>{Esc(text)}</v></c>";

    /// <summary>Résultat numérique d'une formule (valeur mise en cache).</summary>
    internal static string CellFormulaNumber(string reference, string storedText, string formula = "1+1")
        => $"<c r=\"{reference}\"><f>{formula}</f><v>{storedText}</v></c>";

    /// <summary>Formule sans valeur mise en cache.</summary>
    internal static string CellFormulaNoValue(string reference, string formula = "1+1")
        => $"<c r=\"{reference}\"><f>{formula}</f></c>";

    internal static string CellBool(string reference, bool value) => $"<c r=\"{reference}\" t=\"b\"><v>{(value ? 1 : 0)}</v></c>";

    internal static string CellError(string reference, string error = "#N/A") => $"<c r=\"{reference}\" t=\"e\"><v>{Esc(error)}</v></c>";

    internal static string CellDate(string reference, string iso) => $"<c r=\"{reference}\" t=\"d\"><v>{iso}</v></c>";

    /// <summary>Cellule vide mais mise en forme (très fréquent dans un vrai fichier).</summary>
    internal static string CellBlank(string reference, int style = 1) => $"<c r=\"{reference}\" s=\"{style}\"/>";

    /// <summary>Ligne. <paramref name="extraAttributes"/> : attributs supplémentaires (« hidden="1" », « ht="30" customHeight="1" »...).</summary>
    internal static string Row(int number, string cells, string? extraAttributes = null)
        => $"<row r=\"{number}\" spans=\"1:3\"{(extraAttributes == null ? "" : " " + extraAttributes)}>{cells}</row>";

    /// <summary>Feuille complète à partir du contenu de &lt;sheetData&gt;.</summary>
    internal static string SheetXml(string sheetDataInner, string? cols = null, string? afterSheetData = null, string? dimension = "A1:C10")
        => XmlDecl
           + $"<worksheet xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\">"
           + (dimension == null ? "" : $"<dimension ref=\"{dimension}\"/>")
           + "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\"/></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>"
           + (cols == null ? "" : $"<cols>{cols}</cols>")
           + $"<sheetData>{sheetDataInner}</sheetData>"
           + (afterSheetData ?? "")
           + "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></worksheet>";

    /// <summary>Table des chaînes partagées à partir de ses éléments &lt;si&gt;.</summary>
    internal static string SharedStringsXml(params string[] si)
        => XmlDecl + $"<sst xmlns=\"{SpreadsheetNs}\" count=\"{si.Length}\" uniqueCount=\"{si.Length}\">{string.Concat(si)}</sst>";

    /// <summary>&lt;si&gt; de texte simple (xml:space="preserve" si le texte commence ou finit par une espace).</summary>
    internal static string Si(string text)
        => text.Length > 0 && (text[0] == ' ' || text[^1] == ' ' || text.Contains('\n'))
            ? $"<si><t xml:space=\"preserve\">{Esc(text)}</t></si>"
            : $"<si><t>{Esc(text)}</t></si>";

    /// <summary>&lt;si&gt; en texte riche (une exécution par morceau, avec mise en forme).</summary>
    internal static string SiRich(params string[] runs)
        => "<si>" + string.Concat(runs.Select((r, i) =>
            $"<r><rPr><b/><sz val=\"11\"/><color theme=\"1\"/><rFont val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></rPr><t xml:space=\"preserve\">{Esc(r)}</t></r>")) + "</si>";

    /// <summary>&lt;si&gt; avec annotation phonétique (japonais) : le texte de base seul doit être lu.</summary>
    internal static string SiPhonetic(string baseText, string reading)
        => $"<si><t>{Esc(baseText)}</t><rPh sb=\"0\" eb=\"{baseText.Length}\"><t>{Esc(reading)}</t></rPh><phoneticPr fontId=\"1\"/></si>";

    private static string StyleAttr(int? style) => style.HasValue ? $" s=\"{style.Value}\"" : "";

    /// <summary>
    /// Une colonne A de textes comme Excel 365 l'enregistre : chaînes partagées (dans l'ordre de première apparition), une ligne par valeur.
    /// Une valeur null laisse la ligne sans cellule.
    /// </summary>
    internal static (string SheetXml, string SharedStrings) ExcelColumn(IEnumerable<string?> values)
    {
        var index = new Dictionary<string, int>();
        var strings = new List<string>();
        var rows = new StringBuilder();
        int r = 1;
        foreach (string? v in values)
        {
            if (v == null)
            {
                rows.Append($"<row r=\"{r}\" spans=\"1:1\"/>");
            }
            else
            {
                if (!index.TryGetValue(v, out int i))
                {
                    i = strings.Count;
                    index[v] = i;
                    strings.Add(Si(v));
                }

                rows.Append(Row(r, CellShared("A" + r, i)));
            }

            r++;
        }

        return (SheetXml(rows.ToString(), dimension: "A1:A" + Math.Max(1, r - 1)), SharedStringsXml(strings.ToArray()));
    }

    /// <summary>Classeur .xlsx prêt à l'emploi : une colonne de textes en chaînes partagées (feuille « Feuil1 »).</summary>
    internal static byte[] SimpleXlsx(IEnumerable<string?> column)
    {
        (string sheet, string sst) = ExcelColumn(column);
        return new XlsxBuilder().AddSheet("Feuil1", sheet).SharedStrings(sst).Build();
    }

    /// <summary>Constructeur d'un paquet .xlsx (workbook.xml, relations, [Content_Types].xml, styles...).</summary>
    internal sealed class XlsxBuilder
    {
        private readonly List<(string Name, string Xml, string? State, string? RelType)> _sheets = new();
        private readonly Dictionary<string, string> _extraParts = new();
        private string? _sharedStrings;
        private bool _absoluteTargets;
        private bool _backslashTargets;
        private bool _parentTargets;
        private bool _withContentTypes = true;
        private bool _withRootRels = true;
        private bool _withWorkbookRels = true;
        private bool _withSharedStringsRelationship = true;
        private bool _strict;
        private string _workbookPart = "xl/workbook.xml";
        private int[]? _sheetFileNumbers;

        /// <summary>Ajoute une feuille. <paramref name="state"/> : « hidden », « veryHidden » ou null (visible).</summary>
        internal XlsxBuilder AddSheet(string name, string sheetXml, string? state = null)
        {
            _sheets.Add((name, sheetXml, state, null));
            return this;
        }

        /// <summary>Ajoute une feuille de graphique (relation « chartsheet », pas une feuille de calcul).</summary>
        internal XlsxBuilder AddChartSheet(string name)
        {
            _sheets.Add((name, XmlDecl + "<chartsheet xmlns=\"" + SpreadsheetNs + "\"/>", null, "chartsheet"));
            return this;
        }

        internal XlsxBuilder SharedStrings(string xml)
        {
            _sharedStrings = xml;
            return this;
        }

        /// <summary>Cibles de relations absolues (« /xl/worksheets/sheet1.xml »), comme Google Sheets.</summary>
        internal XlsxBuilder AbsoluteTargets()
        {
            _absoluteTargets = true;
            return this;
        }

        /// <summary>Cibles avec « .. » (« ../xl/worksheets/sheet1.xml »).</summary>
        internal XlsxBuilder ParentTargets()
        {
            _parentTargets = true;
            return this;
        }

        /// <summary>Cibles avec antislashs (certains générateurs Windows) et entrées d'archive aux noms à antislashs.</summary>
        internal XlsxBuilder BackslashTargets()
        {
            _backslashTargets = true;
            return this;
        }

        internal XlsxBuilder WithoutContentTypes()
        {
            _withContentTypes = false;
            return this;
        }

        internal XlsxBuilder WithoutRootRels()
        {
            _withRootRels = false;
            return this;
        }

        internal XlsxBuilder WithoutWorkbookRels()
        {
            _withWorkbookRels = false;
            return this;
        }

        /// <summary>Le fichier sharedStrings.xml existe mais le classeur ne le déclare pas dans ses relations.</summary>
        internal XlsxBuilder WithoutSharedStringsRelationship()
        {
            _withSharedStringsRelationship = false;
            return this;
        }

        /// <summary>Espaces de noms « Strict Open XML » (purl.oclc.org) au lieu des espaces transitoires.</summary>
        internal XlsxBuilder Strict()
        {
            _strict = true;
            return this;
        }

        /// <summary>Nom de la partie classeur (« xl/workbook.xml » par défaut).</summary>
        internal XlsxBuilder WorkbookPart(string name)
        {
            _workbookPart = name;
            return this;
        }

        /// <summary>Numéros de fichier des feuilles (sheet{n}.xml) : permet un ordre du classeur différent de l'ordre des fichiers.</summary>
        internal XlsxBuilder SheetFileNumbers(params int[] numbers)
        {
            _sheetFileNumbers = numbers;
            return this;
        }

        internal XlsxBuilder Part(string name, string content)
        {
            _extraParts[name] = content;
            return this;
        }

        internal byte[] Build() => Zip(BuildParts().Select(p => (p.Key, new UTF8Encoding(false).GetBytes(p.Value))));

        /// <summary>Les entrées du paquet (nom, contenu), pour les tests qui retouchent le paquet avant de le compresser.</summary>
        internal List<KeyValuePair<string, string>> BuildParts()
        {
            string ssNs = _strict ? "http://purl.oclc.org/ooxml/spreadsheetml/main" : SpreadsheetNs;
            string rNs = _strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : RelationshipsNs;
            string relBase = _strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            string dir = _workbookPart.Contains('/') ? _workbookPart.Substring(0, _workbookPart.LastIndexOf('/')) : "";
            string wbFile = _workbookPart.Substring(_workbookPart.LastIndexOf('/') + 1);
            var parts = new List<KeyValuePair<string, string>>();

            int n = _sheets.Count;
            int[] fileNumbers = _sheetFileNumbers ?? Enumerable.Range(1, n).ToArray();

            // workbook.xml : comme Excel (bookViews, sheets avec sheetId et r:id, calcPr)
            var wb = new StringBuilder();
            wb.Append(XmlDecl);
            wb.Append($"<workbook xmlns=\"{ssNs}\" xmlns:r=\"{rNs}\"><fileVersion appName=\"xl\" lastEdited=\"7\" lowestEdited=\"7\" rupBuild=\"22228\"/>");
            wb.Append("<workbookPr defaultThemeVersion=\"166925\"/><bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"28800\" windowHeight=\"12000\"/></bookViews><sheets>");
            for (int i = 0; i < n; i++)
            {
                (string name, _, string? state, _) = _sheets[i];
                wb.Append($"<sheet name=\"{Esc(name)}\" sheetId=\"{i + 3}\"{(state == null ? "" : $" state=\"{state}\"")} r:id=\"rId{i + 1}\"/>");
            }

            wb.Append("</sheets><definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">Feuil1!$A$1:$A$3</definedName></definedNames><calcPr calcId=\"191029\"/></workbook>");
            parts.Add(new(_workbookPart, wb.ToString()));

            // relations du classeur : feuilles rId1..n, puis styles, chaînes partagées (comme Excel)
            string Target(string path)
            {
                // path relatif au dossier du classeur, ex. "worksheets/sheet1.xml"
                if (_absoluteTargets) return "/" + (dir.Length > 0 ? dir + "/" : "") + path;
                if (_parentTargets) return "../" + (dir.Length > 0 ? dir + "/" : "") + path;
                if (_backslashTargets) return path.Replace('/', '\\');
                return path;
            }

            var rels = new StringBuilder();
            rels.Append(XmlDecl).Append($"<Relationships xmlns=\"{PackageRelsNs}\">");
            for (int i = 0; i < n; i++)
            {
                string type = _sheets[i].RelType ?? "worksheet";
                string folder = type == "chartsheet" ? "chartsheets" : "worksheets";
                string file = type == "chartsheet" ? $"chart{fileNumbers[i]}.xml" : $"sheet{fileNumbers[i]}.xml";
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{relBase}/{type}\" Target=\"{Target(folder + "/" + file)}\"/>");
            }

            rels.Append($"<Relationship Id=\"rId{n + 1}\" Type=\"{relBase}/styles\" Target=\"{Target("styles.xml")}\"/>");
            if (_sharedStrings != null && _withSharedStringsRelationship)
            {
                rels.Append($"<Relationship Id=\"rId{n + 2}\" Type=\"{relBase}/sharedStrings\" Target=\"{Target("sharedStrings.xml")}\"/>");
            }

            rels.Append("</Relationships>");
            if (_withWorkbookRels)
            {
                string relsPath = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + wbFile + ".rels";
                parts.Add(new(relsPath, rels.ToString()));
            }

            string prefix = dir.Length > 0 ? dir + "/" : "";
            for (int i = 0; i < n; i++)
            {
                string type = _sheets[i].RelType ?? "worksheet";
                string name = type == "chartsheet" ? $"{prefix}chartsheets/chart{fileNumbers[i]}.xml" : $"{prefix}worksheets/sheet{fileNumbers[i]}.xml";
                if (_backslashTargets) name = name.Replace('/', '\\');
                parts.Add(new(name, _sheets[i].Xml));
            }

            parts.Add(new(prefix + "styles.xml", StylesXml(ssNs)));
            if (_sharedStrings != null) parts.Add(new(prefix + "sharedStrings.xml", _sharedStrings));

            if (_withRootRels)
            {
                parts.Add(new("_rels/.rels", XmlDecl + $"<Relationships xmlns=\"{PackageRelsNs}\">"
                    + $"<Relationship Id=\"rId3\" Type=\"{relBase}/extended-properties\" Target=\"docProps/app.xml\"/>"
                    + $"<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
                    + $"<Relationship Id=\"rId1\" Type=\"{relBase}/officeDocument\" Target=\"{_workbookPart}\"/></Relationships>"));
                parts.Add(new("docProps/app.xml", XmlDecl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Microsoft Excel</Application></Properties>"));
                parts.Add(new("docProps/core.xml", XmlDecl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\"/>"));
            }

            if (_withContentTypes)
            {
                var ct = new StringBuilder();
                ct.Append(XmlDecl).Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
                ct.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
                ct.Append($"<Override PartName=\"/{_workbookPart}\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                for (int i = 0; i < n; i++)
                {
                    ct.Append($"<Override PartName=\"/{prefix}worksheets/sheet{fileNumbers[i]}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                }

                ct.Append("</Types>");
                parts.Add(new("[Content_Types].xml", ct.ToString()));
            }

            foreach (KeyValuePair<string, string> extra in _extraParts) parts.Add(extra);
            return parts;
        }
    }

    /// <summary>
    /// styles.xml comme Excel l'écrit. Styles de cellule (attribut s) : 0 standard, 1 texte (@), 2 date intégrée (14), 3 date personnalisée
    /// (dd/mm/yyyy), 4 nombre avec un « d » ENTRE GUILLEMETS (pas une date), 5 durée ([h]:mm:ss), 6 date longue entre crochets de couleur,
    /// 7 zéros de tête (00000), 8 zéros avec préfixe ("CH-"0000), 9 deux décimales (intégré 2), 10 séparateur de milliers (intégré 3),
    /// 11 entier (intégré 1), 12 zéros séparés (00-000), 13 zéros avec une section négative (00000;-00000), 14 zéros avec symbole monétaire non ASCII.
    /// </summary>
    internal static string StylesXml(string ns = SpreadsheetNs)
        => XmlDecl + $"<styleSheet xmlns=\"{ns}\">"
           + "<numFmts count=\"9\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"0.0&quot; d&quot;\"/>"
           + "<numFmt numFmtId=\"166\" formatCode=\"[h]:mm:ss\"/><numFmt numFmtId=\"167\" formatCode=\"[Red]dddd\\ d\\ mmmm\\ yyyy\"/>"
           + "<numFmt numFmtId=\"168\" formatCode=\"00000\"/><numFmt numFmtId=\"169\" formatCode=\"&quot;CH-&quot;0000\"/>"
           + "<numFmt numFmtId=\"170\" formatCode=\"00\\-000\"/><numFmt numFmtId=\"171\" formatCode=\"00000;\\-00000\"/>"
           + "<numFmt numFmtId=\"172\" formatCode=\"[$&#8364;-407]\\ 0000\"/></numFmts>"
           + "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
           + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
           + "<cellStyleXfs count=\"1\"><xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
           + "<cellXfs count=\"15\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
           + "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"168\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"169\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"170\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"171\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"
           + "<xf numFmtId=\"172\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>"
           + "<dxfs count=\"0\"/></styleSheet>";

    // ------------------------------------------------------------------------------------------------
    // DOCX : paragraphes, tableaux, document
    // ------------------------------------------------------------------------------------------------

    /// <summary>Paragraphe d'une seule exécution.</summary>
    internal static string P(string text) => $"<w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr><w:r><w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r></w:p>";

    /// <summary>Paragraphe dont le texte est coupé en plusieurs exécutions (Word coupe n'importe où : correcteur, révisions, langue...).</summary>
    internal static string PRuns(params string[] runs)
        => "<w:p><w:r><w:rPr><w:lang w:val=\"fr-FR\"/></w:rPr>" + string.Join(
            "</w:r><w:proofErr w:type=\"spellStart\"/><w:r><w:rPr><w:noProof/></w:rPr>",
            runs.Select(r => $"<w:t xml:space=\"preserve\">{Esc(r)}</w:t>")) + "</w:r></w:p>";

    /// <summary>Paragraphe dont le contenu est donné tel quel.</summary>
    internal static string PRaw(string inner) => $"<w:p>{inner}</w:p>";

    /// <summary>Exécution de texte.</summary>
    internal static string R(string text) => $"<w:r><w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";

    internal static string Tbl(params string[] rows)
        => "<w:tbl><w:tblPr><w:tblStyle w:val=\"Grilledutableau\"/><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid>"
           + string.Concat(rows) + "</w:tbl>";

    internal static string Tr(params string[] cells) => "<w:tr>" + string.Concat(cells) + "</w:tr>";

    /// <summary>Cellule : <paramref name="content"/> = paragraphes (ou tableau imbriqué).</summary>
    internal static string Tc(string content, string? properties = null)
        => $"<w:tc><w:tcPr><w:tcW w:w=\"3000\" w:type=\"dxa\"/>{properties}</w:tcPr>{content}</w:tc>";

    /// <summary>Document Word complet à partir du contenu du corps.</summary>
    internal static string DocumentXml(string bodyInner, bool strict = false)
    {
        string w = strict ? "http://purl.oclc.org/ooxml/wordprocessingml/main" : WordNs;
        string r = strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : RelationshipsNs;
        return XmlDecl
               + $"<w:document xmlns:w=\"{w}\" xmlns:r=\"{r}\" xmlns:mc=\"{McNs}\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" "
               + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
               + "xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" mc:Ignorable=\"w14\">"
               + $"<w:body>{bodyInner}<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId8\"/><w:footerReference w:type=\"default\" r:id=\"rId9\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
    }

    /// <summary>Document .docx complet (en-tête et pied de page contenant « ENTETE » / « PIED » : ils ne doivent jamais être lus).</summary>
    internal static byte[] Docx(string bodyInner, bool strict = false, bool absoluteTarget = false, bool withRels = true)
    {
        var parts = DocxParts(bodyInner, strict, absoluteTarget, withRels);
        return Zip(parts);
    }

    internal static List<KeyValuePair<string, string>> DocxParts(string bodyInner, bool strict = false, bool absoluteTarget = false, bool withRels = true)
    {
        string relBase = strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        string w = strict ? "http://purl.oclc.org/ooxml/wordprocessingml/main" : WordNs;
        var parts = new List<KeyValuePair<string, string>>
        {
            new("[Content_Types].xml", XmlDecl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            new("word/document.xml", DocumentXml(bodyInner, strict)),
            new("word/header1.xml", XmlDecl + $"<w:hdr xmlns:w=\"{w}\"><w:p><w:r><w:t>ENTETE</w:t></w:r></w:p></w:hdr>"),
            new("word/footer1.xml", XmlDecl + $"<w:ftr xmlns:w=\"{w}\"><w:p><w:r><w:t>PIED</w:t></w:r></w:p></w:ftr>"),
            new("word/footnotes.xml", XmlDecl + $"<w:footnotes xmlns:w=\"{w}\"><w:footnote w:id=\"1\"><w:p><w:r><w:t>NOTE</w:t></w:r></w:p></w:footnote></w:footnotes>"),
            new("word/styles.xml", XmlDecl + $"<w:styles xmlns:w=\"{w}\"><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>"),
        };
        if (withRels)
        {
            parts.Add(new("_rels/.rels", XmlDecl + $"<Relationships xmlns=\"{PackageRelsNs}\"><Relationship Id=\"rId1\" Type=\"{relBase}/officeDocument\" Target=\"{(absoluteTarget ? "/" : "")}word/document.xml\"/></Relationships>"));
            parts.Add(new("word/_rels/document.xml.rels", XmlDecl + $"<Relationships xmlns=\"{PackageRelsNs}\"><Relationship Id=\"rId8\" Type=\"{relBase}/header\" Target=\"header1.xml\"/><Relationship Id=\"rId9\" Type=\"{relBase}/footer\" Target=\"footer1.xml\"/></Relationships>"));
        }

        return parts;
    }

    /// <summary>Document .docx prêt à l'emploi : un paragraphe par texte.</summary>
    internal static byte[] SimpleDocx(IEnumerable<string> paragraphs) => Docx(string.Concat(paragraphs.Select(P)));
}
