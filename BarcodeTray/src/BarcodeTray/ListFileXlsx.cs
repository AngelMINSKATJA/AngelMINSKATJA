using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace BarcodeTray;

/// <summary>
/// Lecture d'un classeur Excel (.xlsx) en flux (XmlReader, jamais de DOM) :
/// première feuille visible, première colonne contenant des valeurs, lignes et colonnes masquées ignorées (comme un copier-coller).
/// <para>
/// Règles : texte des cellules « chaîne partagée », « chaîne en ligne » et « résultat de formule texte » (échappements _xHHHH_ décodés,
/// texte riche concaténé, annotations phonétiques ignorées) ; nombres = texte tel qu'enregistré (jamais l'affichage), la notation
/// scientifique d'un entier exact (≤ 2^53) est ramenée aux chiffres, « 12345.0 » devient « 12345 » ; booléens et erreurs sont ignorés
/// (avertissement) ; dates ISO (type « d ») gardées telles quelles.
/// </para>
/// <para>
/// Format d'affichage d'un nombre : un format fait SEULEMENT de zéros (« 00000 », « "CH-"0000 ») est appliqué à un entier positif, comme
/// Excel l'affiche (123 devient « 00123 », comme le collerait « Coller ») ; tout autre format (décimales fixes, séparateur de milliers,
/// pourcentage...) laisse la valeur brute et ajoute une remarque.
/// </para>
/// </summary>
internal static class ListFileXlsx
{
    private const int MaxHiddenColumnRanges = 2000;
    private const int MaxSharedStringChars = 16_000_000;
    private const int MaxSharedStrings = 1_200_000;
    private const int MaxOtherSheetProbes = 12;
    private const long OtherSheetProbeBytes = 2L * 1024 * 1024;
    private const double MaxExactInteger = 9007199254740992d;   // 2^53

    /// <summary>Compteurs d'avertissements d'une lecture de feuille.</summary>
    private sealed class SheetStats
    {
        internal int HiddenRowsWithData;
        internal readonly HashSet<int> HiddenColumnsWithData = new();
        internal int Booleans;
        internal int Errors;
        internal int Scientific;          // sert seulement à repérer la notation scientifique d'une cellule numérique (la remarque est faite par ListCollector)
        internal int BadReferences;
        internal int FormulasWithoutValue;
        internal int DateCells;           // cellules de la colonne lue : une colonne de dates à côté n'est pas concernée
        internal int FormattedNumbers;    // idem : nombres dont l'affichage Excel diffère de la valeur enregistrée
        internal bool OtherColumns;
    }

    private readonly record struct SheetInfo(string Name, string? RelId, bool Hidden);

    internal static void Read(ListZipPackage pkg, ListCollector collector, List<string> warnings)
    {
        string? workbookPart = pkg.FindMainPart("xl/workbook.xml");
        if (workbookPart == null)
        {
            throw NotAWorkbook(pkg);
        }

        ZipArchiveEntry workbookEntry = pkg.Find(workbookPart)!;
        string dir = ListZipPackage.DirectoryOf(workbookPart);
        List<ListRelationship> rels = pkg.ReadRelationships(ListZipPackage.RelsPathFor(workbookPart));
        List<SheetInfo> sheets = ReadSheetList(pkg, workbookEntry);

        // Feuille à lire : la première qui existe et n'est pas masquée (repli : la première qui existe).
        var candidates = new List<(SheetInfo Info, ZipArchiveEntry Entry)>();
        foreach (SheetInfo sheet in sheets)
        {
            if (sheet.RelId == null) continue;
            ListRelationship? rel = FindRel(rels, sheet.RelId);
            if (rel == null || !rel.Value.Type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase)) continue;   // feuille de graphique...
            string? part = ListZipPackage.ResolveTarget(dir, rel.Value.Target);
            ZipArchiveEntry? entry = part == null ? null : pkg.Find(part);
            if (entry != null) candidates.Add((sheet, entry));
        }

        if (candidates.Count == 0)
        {
            // Relations absentes ou cassées : nom habituel de la première feuille.
            ZipArchiveEntry? fallback = pkg.Find("xl/worksheets/sheet1.xml");
            if (fallback == null)
            {
                throw new ListFileException("Ce classeur Excel ne contient aucune feuille lisible (fichier endommagé). Ouvrez-le dans Excel puis enregistrez-le de nouveau.");
            }

            candidates.Add((new SheetInfo("", null, false), fallback));
        }

        int chosen = candidates.FindIndex(c => !c.Info.Hidden);
        if (chosen < 0) chosen = 0;

        // Table des chaînes partagées (lue au fil de l'eau, jamais en entier si la feuille n'en réclame qu'un début).
        string? sstPart = null;
        foreach (ListRelationship rel in rels)
        {
            if (rel.Type.EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase))
            {
                sstPart = ListZipPackage.ResolveTarget(dir, rel.Target);
                break;
            }
        }

        ZipArchiveEntry? sstEntry = pkg.Find(sstPart ?? "xl/sharedStrings.xml") ?? pkg.Find("xl/sharedStrings.xml");

        string? stylesPart = null;
        foreach (ListRelationship rel in rels)
        {
            if (rel.Type.EndsWith("/styles", StringComparison.OrdinalIgnoreCase))
            {
                stylesPart = ListZipPackage.ResolveTarget(dir, rel.Target);
                break;
            }
        }

        var styles = new StyleTable(pkg, pkg.Find(stylesPart ?? "xl/styles.xml") ?? pkg.Find("xl/styles.xml"));

        var stats = new SheetStats();
        using (SharedStringTable? sst = sstEntry == null ? null : new SharedStringTable(pkg, sstEntry))
        {
            ReadSheet(pkg, candidates[chosen].Entry, sst, styles, collector, stats);
        }

        AddSheetWarnings(stats, warnings);

        // Autres feuilles visibles contenant des données : simple avertissement (sondage borné).
        int probes = 0;
        for (int i = 0; i < candidates.Count && probes < MaxOtherSheetProbes; i++)
        {
            if (i == chosen || candidates[i].Info.Hidden) continue;
            probes++;
            if (SheetHasData(pkg, candidates[i].Entry))
            {
                warnings.Add("Seule la première feuille a été lue : les autres feuilles du classeur contiennent aussi des données.");
                break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Classeur : liste des feuilles
    // ------------------------------------------------------------------------------------------------

    private static ListFileException NotAWorkbook(ListZipPackage pkg)
    {
        if (pkg.Has("word/document.xml"))
        {
            return new ListFileException("Ce fichier est un document Word, pas un classeur Excel : changez son extension en .docx (ou rechargez-le comme document Word).");
        }

        if (pkg.Has("content.xml") && pkg.Has("mimetype"))
        {
            return new ListFileException("Ce fichier est au format OpenDocument (.ods / .odt) : ouvrez-le puis enregistrez-le en .xlsx / .docx.");
        }

        return new ListFileException("Ce fichier n'est pas un classeur Excel (.xlsx) valide : la partie « xl/workbook.xml » est introuvable. Ouvrez-le dans Excel puis enregistrez-le de nouveau en .xlsx.");
    }

    private static ListRelationship? FindRel(List<ListRelationship> rels, string id)
    {
        foreach (ListRelationship rel in rels)
        {
            if (rel.Id == id) return rel;
        }

        return null;
    }

    private static List<SheetInfo> ReadSheetList(ListZipPackage pkg, ZipArchiveEntry workbook)
    {
        var sheets = new List<SheetInfo>();
        using XmlReader r = pkg.OpenXml(workbook);
        if (r.MoveToContent() != XmlNodeType.Element || r.LocalName != "workbook")
        {
            throw NotAWorkbook(pkg);   // la partie principale de l'archive n'est pas un classeur (document Word renommé...)
        }

        bool inSheets = false;
        int sheetsDepth = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.EndElement)
            {
                if (inSheets && r.LocalName == "sheets" && r.Depth == sheetsDepth) break;   // le reste du classeur est inutile
                continue;
            }

            if (r.NodeType != XmlNodeType.Element) continue;
            ListXml.CheckDepth(r);

            if (r.LocalName == "sheets")
            {
                inSheets = !r.IsEmptyElement;
                sheetsDepth = r.Depth;
                continue;
            }

            if (!inSheets || r.LocalName != "sheet") continue;

            string name = r.GetAttribute("name") ?? "";
            string? state = r.GetAttribute("state");
            bool hidden = string.Equals(state, "hidden", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(state, "veryHidden", StringComparison.OrdinalIgnoreCase);
            string? relId = null;
            if (r.MoveToFirstAttribute())
            {
                do
                {
                    if (r.LocalName == "id" && (r.Prefix == "r" || r.NamespaceURI.Contains("officeDocument/relationships", StringComparison.Ordinal)))
                    {
                        relId = r.Value;
                    }
                }
                while (r.MoveToNextAttribute());

                r.MoveToElement();
            }

            if (sheets.Count >= 5000) break;   // garde-fou
            sheets.Add(new SheetInfo(name, relId, hidden));
        }

        return sheets;
    }

    // ------------------------------------------------------------------------------------------------
    // Feuille
    // ------------------------------------------------------------------------------------------------

    private static void ReadSheet(ListZipPackage pkg, ZipArchiveEntry entry, SharedStringTable? sst, StyleTable styles, ListCollector collector, SheetStats st)
    {
        var hiddenColumns = new List<(int Min, int Max)>();
        using XmlReader r = pkg.OpenXml(entry, ListBudget.MaxSheetBytes);   // une feuille filtrée se lit en entier : plafond cumulé

        int bestColumn = int.MaxValue;   // colonne (0 = A) en cours de lecture : la plus à gauche vue jusqu'ici
        long lastRow = 0;                // numéro de la dernière ligne vue (1 = première)
        bool inSheetData = false;

        r.Read();
        while (!r.EOF)
        {
            ListXml.CheckDepth(r);

            if (r.NodeType == XmlNodeType.Element)
            {
                string local = r.LocalName;
                if (!inSheetData)
                {
                    if (local == "col")
                    {
                        if (ListXml.IsTrue(r.GetAttribute("hidden")) && hiddenColumns.Count < MaxHiddenColumnRanges)
                        {
                            int min = ParseInt(r.GetAttribute("min"), 1);
                            int max = ParseInt(r.GetAttribute("max"), min);
                            hiddenColumns.Add((min - 1, max - 1));
                        }
                    }
                    else if (local == "sheetData")
                    {
                        inSheetData = true;
                        if (r.IsEmptyElement) return;
                    }
                }
                else if (local == "row")
                {
                    if (!ReadRow(r, sst, styles, collector, st, hiddenColumns, ref bestColumn, ref lastRow)) return;
                    continue;   // ReadRow a déjà avancé jusqu'au nœud suivant
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && inSheetData && r.LocalName == "sheetData")
            {
                return;   // tout ce qui suit (cellules fusionnées, mise en forme...) est inutile
            }

            r.Read();
        }
    }

    /// <summary>Lit une ligne (le lecteur est sur &lt;row&gt;) et l'avance jusqu'au nœud suivant. Faux : la limite est atteinte, arrêtez-vous.</summary>
    private static bool ReadRow(
        XmlReader r,
        SharedStringTable? sst,
        StyleTable styles,
        ListCollector collector,
        SheetStats st,
        List<(int Min, int Max)> hiddenColumns,
        ref int bestColumn,
        ref long lastRow)
    {
        bool hiddenRow = ListXml.IsTrue(r.GetAttribute("hidden"));
        long rowNumber = ParseLong(r.GetAttribute("r"), lastRow + 1);
        if (rowNumber <= lastRow) rowNumber = lastRow + 1;

        // Lignes absentes du fichier entre deux lignes présentes = lignes vides (aucun tableau n'est alloué : feuille « éparse » sans risque).
        long gap = rowNumber - lastRow - 1;
        if (gap > 0) collector.AddEmpty((int)Math.Min(gap, int.MaxValue / 4));
        lastRow = rowNumber;

        bool rowHasData = false;        // au moins une cellule non vide (pour compter les lignes masquées)
        bool addedToColumn = false;     // une valeur a été retenue dans la colonne lue
        bool rowSkipped = false;        // une cellule a été écartée pour une raison signalée à part (booléen, erreur, formule sans résultat...)

        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            int depth = r.Depth;
            int lastColumn = -1;
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;

                if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                {
                    int scientificBefore = st.Scientific;
                    ReadCell(r, sst, styles, st, ref lastColumn, out int column, out string? text, out bool skipped, out bool numeric, out int style);
                    if (skipped) rowSkipped = true;
                    if (text == null) continue;
                    bool scientific = st.Scientific != scientificBefore;

                    string clean = ListClean.Clean(text);
                    if (clean.Length == 0) continue;

                    rowHasData = true;
                    if (hiddenRow) continue;

                    if (IsHiddenColumn(hiddenColumns, column))
                    {
                        st.HiddenColumnsWithData.Add(column);
                        continue;
                    }

                    if (column < bestColumn)
                    {
                        // Une colonne plus à gauche apparaît : les valeurs retenues jusque-là appartenaient à une autre colonne.
                        // (Elle n'avait aucune valeur dans les lignes précédentes, sinon elle aurait déjà été retenue : rien n'est perdu.)
                        if (bestColumn != int.MaxValue) st.OtherColumns = true;
                        collector.Reset();
                        st.DateCells = 0;
                        st.FormattedNumbers = 0;
                        bestColumn = column;
                    }
                    else if (column > bestColumn)
                    {
                        st.OtherColumns = true;
                        continue;
                    }

                    if (numeric) clean = ApplyNumberFormat(styles, style, clean, st);

                    ListValueFlags flags = ListValueFlags.None;
                    if (scientific) flags |= ListValueFlags.Scientific;
                    if (ListCollector.HasInnerLineBreak(text)) flags |= ListValueFlags.LineBreak;
                    if (!collector.Add(clean, flags)) return false;
                    addedToColumn = true;
                    continue;
                }

                if (r.NodeType == XmlNodeType.Element)
                {
                    r.Skip();
                    continue;
                }

                r.Read();
            }

            r.Read();   // passe </row>
        }

        if (hiddenRow)
        {
            if (rowHasData) st.HiddenRowsWithData++;
        }
        else if (!addedToColumn && !rowSkipped)
        {
            collector.AddEmpty(1);   // les lignes écartées pour une raison signalée à part ne sont pas « vides »
        }

        return true;
    }

    /// <summary>
    /// Lit une cellule (le lecteur est sur &lt;c&gt;), la consomme entièrement, et rend son texte : null = vide, ignorée ou non textuelle.
    /// </summary>
    private static void ReadCell(
        XmlReader r,
        SharedStringTable? sst,
        StyleTable styles,
        SheetStats st,
        ref int lastColumn,
        out int column,
        out string? text,
        out bool skipped,
        out bool numeric,
        out int style)
    {
        string? reference = r.GetAttribute("r");
        string? type = r.GetAttribute("t");
        style = ParseInt(r.GetAttribute("s"), 0);
        column = ParseColumn(reference, lastColumn + 1);
        lastColumn = column;
        skipped = false;
        numeric = false;

        string? value = null;
        string? inline = null;
        bool hasFormula = false;

        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            int depth = r.Depth;
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;

                if (r.NodeType == XmlNodeType.Element)
                {
                    string local = r.LocalName;
                    if (local == "v")
                    {
                        value = ListXml.ReadText(r, ListCollector.MaxValueChars + 1);
                        continue;
                    }

                    if (local == "is")
                    {
                        inline = ReadRichString(r);
                        continue;
                    }

                    if (local == "f") hasFormula = true;
                    r.Skip();   // <f> (formule), <extLst>...
                    continue;
                }

                r.Read();
            }

            r.Read();   // passe </c>
        }

        switch (type)
        {
            case "s":
                if (string.IsNullOrWhiteSpace(value))
                {
                    text = null;
                }
                else if (sst != null && int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    text = sst.Get(index);
                    if (text == null)
                    {
                        st.BadReferences++;
                        skipped = true;
                    }
                }
                else
                {
                    text = null;
                    st.BadReferences++;
                    skipped = true;
                }

                break;
            case "inlineStr":
                text = inline ?? (value == null ? null : ListXml.DecodeXmlEscapes(value));
                break;
            case "str":
                text = value == null ? null : ListXml.DecodeXmlEscapes(value);
                break;
            case "b":
                text = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    st.Booleans++;
                    skipped = true;
                }

                break;
            case "e":
                text = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    st.Errors++;
                    skipped = true;
                }

                break;
            case "d":
                text = value;
                break;
            case null:
            case "":
            case "n":
                if (string.IsNullOrWhiteSpace(value))
                {
                    text = null;
                }
                else
                {
                    text = NormalizeNumber(value, ref st.Scientific);
                    numeric = true;   // le format d'affichage (dates, zéros de tête, décimales...) est examiné quand la cellule est retenue
                }

                break;
            default:
                text = value == null ? null : ListXml.DecodeXmlEscapes(value);   // type inconnu : on garde le texte
                break;
        }

        // Formule dont le résultat n'a jamais été calculé (fichier produit par un script, jamais ouvert dans Excel).
        if (text == null && hasFormula && !skipped && (value == null || (type != "str" && string.IsNullOrWhiteSpace(value))))
        {
            st.FormulasWithoutValue++;
            skipped = true;
        }
    }

    /// <summary>
    /// Nombre tel qu'enregistré. Notation scientifique d'un entier exact (≤ 2^53) : chiffres complets (« 5.9012341234570001E+12 » donne
    /// « 5901234123457 ») ; sinon texte conservé et compté pour l'avertissement. « 12345.0 » devient « 12345 ».
    /// </summary>
    internal static string NormalizeNumber(string raw, ref int scientific)
    {
        string v = raw.Trim();
        if (v.Length == 0) return v;

        if (v.IndexOf('E') >= 0 || v.IndexOf('e') >= 0)
        {
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                && !double.IsNaN(d) && !double.IsInfinity(d)
                && d == Math.Floor(d) && Math.Abs(d) <= MaxExactInteger)
            {
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            }

            scientific++;
            return v;
        }

        // « 12345.0 », « 12345.000 » : valeur entière écrite avec une fraction nulle (pandas, openpyxl...).
        int dot = v.IndexOf('.');
        if (dot > 0 && dot < v.Length - 1)
        {
            bool zeros = true;
            for (int i = dot + 1; i < v.Length; i++)
            {
                if (v[i] != '0')
                {
                    zeros = false;
                    break;
                }
            }

            int start = v[0] == '-' || v[0] == '+' ? 1 : 0;
            if (zeros && dot > start)
            {
                bool digits = true;
                for (int i = start; i < dot; i++)
                {
                    if (v[i] < '0' || v[i] > '9')
                    {
                        digits = false;
                        break;
                    }
                }

                if (digits) return v.Substring(0, dot);
            }
        }

        return v;
    }

    private static void AddSheetWarnings(SheetStats st, List<string> warnings)
    {
        if (st.HiddenRowsWithData > 0)
        {
            warnings.Add($"{st.HiddenRowsWithData} ligne(s) masquée(s) ou filtrée(s) ignorée(s).");
        }

        if (st.HiddenColumnsWithData.Count > 0)
        {
            warnings.Add($"{st.HiddenColumnsWithData.Count} colonne(s) masquée(s) ignorée(s).");
        }

        if (st.OtherColumns)
        {
            warnings.Add("Seule la première colonne a été lue (la plus à gauche qui contient des valeurs) : les autres colonnes sont ignorées.");
        }

        if (st.Booleans > 0)
        {
            warnings.Add($"{st.Booleans} cellule(s) VRAI/FAUX ignorée(s).");
        }

        if (st.Errors > 0)
        {
            warnings.Add($"{st.Errors} cellule(s) en erreur (#N/A, #REF!...) ignorée(s).");
        }

        if (st.FormulasWithoutValue > 0)
        {
            warnings.Add($"{st.FormulasWithoutValue} cellule(s) avec une formule sans résultat enregistré ignorée(s) : ouvrez le fichier dans Excel et enregistrez-le pour que les formules soient calculées.");
        }

        if (st.FormattedNumbers > 0)
        {
            warnings.Add($"{st.FormattedNumbers} nombre(s) affiché(s) avec un format Excel (décimales fixes, séparateur de milliers, pourcentage…) : le fichier contient "
                         + "la valeur brute, pas ce qui s'affiche. Formatez la colonne en Texte, ou copiez-la (Ctrl+C) puis utilisez « Coller ».");
        }

        if (st.DateCells > 0)
        {
            warnings.Add($"{st.DateCells} cellule(s) de date lue(s) comme numéro de série Excel (45322 pour le 31/01/2024) : copiez plutôt les cellules dans Excel (Ctrl+C) puis utilisez « Coller », ou convertissez la colonne en texte.");
        }

        if (st.BadReferences > 0)
        {
            warnings.Add($"{st.BadReferences} cellule(s) illisible(s) ignorée(s) (fichier partiellement endommagé).");
        }
    }

    private static bool IsHiddenColumn(List<(int Min, int Max)> ranges, int column)
    {
        foreach ((int min, int max) in ranges)
        {
            if (column >= min && column <= max) return true;
        }

        return false;
    }

    /// <summary>Sonde une autre feuille : contient-elle au moins une cellule renseignée ? (lecture bornée, erreurs ignorées)</summary>
    private static bool SheetHasData(ListZipPackage pkg, ZipArchiveEntry entry)
    {
        try
        {
            var budget = new ListBudget(OtherSheetProbeBytes, TimeSpan.FromSeconds(3));
            using Stream raw = entry.Open();
            using var limited = new ListLimitedStream(raw, OtherSheetProbeBytes, budget);
            using XmlReader r = ListXml.Create(limited);
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    ListXml.CheckDepth(r);
                    string local = r.LocalName;
                    if (local == "v" || local == "is")
                    {
                        string text = local == "v" ? ListXml.ReadText(r, 64) : ReadRichString(r);   // avance jusqu'au nœud suivant
                        if (!string.IsNullOrWhiteSpace(text)) return true;
                        continue;
                    }

                    if (local == "f")
                    {
                        r.Skip();
                        continue;
                    }
                }

                r.Read();
            }
        }
        catch (Exception ex) when (ex is ListFileException or XmlException or InvalidDataException or IOException or NotSupportedException)
        {
            // sondage seulement : on n'en dit rien
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------
    // Texte riche et table des chaînes partagées
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lit un &lt;si&gt; ou un &lt;is&gt; (le lecteur est sur son début, il est consommé en entier) : le texte de &lt;t&gt; et des
    /// séquences &lt;r&gt;&lt;t&gt;, sans les annotations phonétiques (&lt;rPh&gt;) ni les propriétés de mise en forme.
    /// </summary>
    internal static string ReadRichString(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return "";
        }

        int depth = r.Depth;
        var sb = new StringBuilder();
        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;

            if (r.NodeType == XmlNodeType.Element)
            {
                switch (r.LocalName)
                {
                    case "t":
                        {
                            string part = ListXml.ReadText(r, ListCollector.MaxValueChars + 1);
                            if (sb.Length <= ListCollector.MaxValueChars) sb.Append(part);
                            continue;
                        }

                    case "r":
                        r.Read();   // on descend : <rPr> est ignoré, <t> est lu
                        continue;
                    default:
                        r.Skip();   // rPh (phonétique), phoneticPr, rPr, extLst...
                        continue;
                }
            }

            r.Read();
        }

        r.Read();   // passe la balise de fin
        return ListXml.DecodeXmlEscapes(sb.ToString());
    }

    /// <summary>
    /// Styles de cellules (cellXfs de styles.xml) : sait si un style d'indice donné est un format de date ou d'heure, et quel effet
    /// un autre format de nombre a sur l'affichage (zéros de tête, décimales...). Chargé seulement si la feuille contient un nombre
    /// mis en forme ; une table illisible est simplement ignorée.
    /// </summary>
    private sealed class StyleTable
    {
        private const int MaxStyles = 100_000;

        private readonly ListZipPackage _pkg;
        private readonly ZipArchiveEntry? _entry;
        private bool _loaded;
        private bool[]? _isDate;
        private CellNumberFormat[]? _formats;

        internal StyleTable(ListZipPackage pkg, ZipArchiveEntry? entry)
        {
            _pkg = pkg;
            _entry = entry;
        }

        internal bool IsDate(int style)
        {
            if (!_loaded) Load();
            return _isDate != null && style >= 0 && style < _isDate.Length && _isDate[style];
        }

        /// <summary>Effet du format de nombre du style (jamais null ; <see cref="CellNumberFormat.Plain"/> si inconnu).</summary>
        internal CellNumberFormat Format(int style)
        {
            if (!_loaded) Load();
            return _formats != null && style >= 0 && style < _formats.Length ? _formats[style] : CellNumberFormat.Plain;
        }

        private void Load()
        {
            _loaded = true;
            if (_entry == null) return;

            try
            {
                var formats = new Dictionary<int, string>();
                var classified = new Dictionary<int, CellNumberFormat>();
                var dates = new List<bool>();
                var effects = new List<CellNumberFormat>();
                using XmlReader r = _pkg.OpenXml(_entry);
                bool inCellXfs = false;
                int cellXfsDepth = 0;
                r.Read();
                while (!r.EOF)
                {
                    ListXml.CheckDepth(r);
                    if (r.NodeType == XmlNodeType.Element)
                    {
                        string local = r.LocalName;
                        if (local == "numFmt" && formats.Count < MaxStyles)
                        {
                            if (int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                            {
                                formats[id] = r.GetAttribute("formatCode") ?? "";
                            }
                        }
                        else if (local == "cellXfs")
                        {
                            inCellXfs = !r.IsEmptyElement;
                            cellXfsDepth = r.Depth;
                        }
                        else if (inCellXfs && local == "xf" && dates.Count < MaxStyles)
                        {
                            int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int formatId);
                            formats.TryGetValue(formatId, out string? code);
                            bool date = ListFileXlsx.IsDateFormat(formatId, code);
                            dates.Add(date);
                            if (date)
                            {
                                effects.Add(CellNumberFormat.Plain);   // une date est signalée à part
                            }
                            else
                            {
                                if (!classified.TryGetValue(formatId, out CellNumberFormat? effect))
                                {
                                    effect = ListFileXlsx.ClassifyNumberFormat(formatId, code);
                                    classified[formatId] = effect;
                                }

                                effects.Add(effect);
                            }
                        }
                    }
                    else if (r.NodeType == XmlNodeType.EndElement && inCellXfs && r.LocalName == "cellXfs" && r.Depth == cellXfsDepth)
                    {
                        break;   // la suite (styles nommés, dxfs...) est inutile
                    }

                    r.Read();
                }

                _isDate = dates.ToArray();
                _formats = effects.ToArray();
            }
            catch (XmlException ex) when (!ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
            {
                _isDate = null;   // styles illisibles : on ne signale simplement pas les dates ni les formats
                _formats = null;
            }
        }
    }

    /// <summary>
    /// Examine le format d'affichage d'une cellule numérique RETENUE : un format fait seulement de zéros (« 00000 », « "CH-"0000 »)
    /// est appliqué à un entier positif (c'est ce qu'Excel affiche et ce que « Coller » donnerait) ; une date ou tout autre format qui
    /// change l'affichage est compté pour la remarque et la valeur brute est gardée.
    /// </summary>
    private static string ApplyNumberFormat(StyleTable styles, int style, string text, SheetStats st)
    {
        if (style <= 0) return text;   // le style 0 est « Standard » (inutile de charger les styles)

        if (styles.IsDate(style))
        {
            st.DateCells++;
            return text;
        }

        CellNumberFormat format = styles.Format(style);
        switch (format.Kind)
        {
            case CellFormatKind.ZeroPad:
                {
                    string? padded = ApplyZeroPad(format, text);
                    if (padded != null) return padded;
                    st.FormattedNumbers++;   // décimale, négatif, notation scientifique : l'affichage d'Excel ne se déduit pas
                    return text;
                }

            case CellFormatKind.Other:
                st.FormattedNumbers++;
                return text;

            default:
                return text;
        }
    }

    /// <summary>Quels formats de nombre sont reconnus, et comment ils changent l'affichage d'un entier positif.</summary>
    internal enum CellFormatKind
    {
        /// <summary>Standard, Texte, ou format inconnu : l'affichage est la valeur enregistrée.</summary>
        Plain,

        /// <summary>Zéros seulement (« 00000 », « "CH-"0000 ») : l'entier est complété de zéros à gauche, avec les textes fixes.</summary>
        ZeroPad,

        /// <summary>Tout autre format (décimales, milliers, pourcentage, monétaire, fraction, notation scientifique...).</summary>
        Other,
    }

    /// <summary>Effet d'un format de nombre sur l'affichage.</summary>
    internal sealed class CellNumberFormat
    {
        internal static readonly CellNumberFormat Plain = new(CellFormatKind.Plain, "", 0, "");
        internal static readonly CellNumberFormat Other = new(CellFormatKind.Other, "", 0, "");

        internal CellNumberFormat(CellFormatKind kind, string prefix, int digits, string suffix)
        {
            Kind = kind;
            Prefix = prefix;
            Digits = digits;
            Suffix = suffix;
        }

        internal CellFormatKind Kind { get; }

        /// <summary>Texte fixe affiché avant les chiffres (« CH- »).</summary>
        internal string Prefix { get; }

        /// <summary>Nombre de zéros (chiffres minimum affichés).</summary>
        internal int Digits { get; }

        /// <summary>Texte fixe affiché après les chiffres.</summary>
        internal string Suffix { get; }
    }

    private const int MaxZeroPadDigits = 30;

    /// <summary>
    /// Effet d'un format de nombre. Intégrés : Standard (0) et Texte (49) n'ont aucun effet, « 0 » (1) est un format de zéros, tous les
    /// autres (&lt; 164) changent l'affichage. Personnalisés : seule la première section compte ; zéros contigus + textes fixes
    /// (guillemets, « \x », « _x », caractères littéraux ASCII) = <see cref="CellFormatKind.ZeroPad"/> ; « General » et « @ » = aucun effet ;
    /// le reste (décimales, milliers, %, E+, #, ?, conditions, symboles hors ASCII...) = <see cref="CellFormatKind.Other"/>.
    /// Les formats de date sont écartés AVANT par <see cref="IsDateFormat"/>.
    /// </summary>
    internal static CellNumberFormat ClassifyNumberFormat(int formatId, string? code)
    {
        if (formatId == 0 || formatId == 49) return CellNumberFormat.Plain;
        if (formatId == 1) return new CellNumberFormat(CellFormatKind.ZeroPad, "", 1, "");
        if (formatId < 164) return CellNumberFormat.Other;
        if (string.IsNullOrWhiteSpace(code)) return CellNumberFormat.Plain;   // format inconnu : rien à affirmer

        string trimmed = code.Trim();
        if (trimmed.Equals("General", StringComparison.OrdinalIgnoreCase) || trimmed == "@") return CellNumberFormat.Plain;

        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        int digits = 0;
        bool zerosStarted = false;
        bool zerosEnded = false;

        bool Literal(string text)
        {
            foreach (char c in text)
            {
                if (c < 0x20 || c > 0x7E) return false;   // « € » ou autre : la valeur ne serait plus du Code 128, on ne l'ajoute pas
            }

            if (zerosStarted)
            {
                zerosEnded = true;
                suffix.Append(text);
            }
            else
            {
                prefix.Append(text);
            }

            return true;
        }

        int i = 0;
        int n = code.Length;
        while (i < n)
        {
            char c = code[i];
            if (c == ';') break;   // sections suivantes : nombres négatifs, zéro, texte

            switch (c)
            {
                case '"':
                    {
                        int close = code.IndexOf('"', i + 1);
                        if (close < 0 || !Literal(code.Substring(i + 1, close - i - 1))) return CellNumberFormat.Other;
                        i = close + 1;
                        continue;
                    }

                case '\\':
                    if (i + 1 >= n || !Literal(code[i + 1].ToString())) return CellNumberFormat.Other;
                    i += 2;
                    continue;

                case '_':
                    if (i + 1 >= n || !Literal(" ")) return CellNumberFormat.Other;   // espace de la largeur du caractère suivant
                    i += 2;
                    continue;

                case '[':
                    {
                        int close = code.IndexOf(']', i + 1);
                        if (close < 0) return CellNumberFormat.Other;
                        string inner = code.Substring(i + 1, close - i - 1);
                        if (inner.Length > 0 && (inner[0] == '<' || inner[0] == '>' || inner[0] == '='))
                        {
                            return CellNumberFormat.Other;   // condition : la section dépend de la valeur
                        }

                        if (inner.Length > 0 && inner[0] == '$')
                        {
                            int dash = inner.IndexOf('-');
                            string symbol = dash < 0 ? inner.Substring(1) : inner.Substring(1, dash - 1);   // [$€-407] : le symbole monétaire
                            if (symbol.Length > 0 && !Literal(symbol)) return CellNumberFormat.Other;
                        }

                        i = close + 1;   // [Red], [$-409] : couleur, langue
                        continue;
                    }

                case '0':
                    if (zerosEnded) return CellNumberFormat.Other;   // zéros séparés par un texte (« 00-000 ») : répartition des chiffres non gérée
                    zerosStarted = true;
                    digits++;
                    i++;
                    continue;

                case ' ':
                case '$':
                case '-':
                case '+':
                case '(':
                case ')':
                case ':':
                case '!':
                case '^':
                case '&':
                case '\'':
                case '~':
                case '{':
                case '}':
                case '<':
                case '>':
                case '=':
                    Literal(c.ToString());
                    i++;
                    continue;

                default:
                    return CellNumberFormat.Other;   // # ? . , % / E e @ * lettres, chiffres 1-9...
            }
        }

        if (digits == 0 || digits > MaxZeroPadDigits) return CellNumberFormat.Other;
        return new CellNumberFormat(CellFormatKind.ZeroPad, prefix.ToString(), digits, suffix.ToString());
    }

    /// <summary>
    /// Entier positif écrit comme Excel l'affiche avec un format de zéros. Null si <paramref name="text"/> n'est pas un entier positif
    /// fait de chiffres seulement (décimale, négatif, notation scientifique...) : l'affichage ne se déduit plus.
    /// </summary>
    internal static string? ApplyZeroPad(CellNumberFormat format, string text)
    {
        if (format.Kind != CellFormatKind.ZeroPad || text.Length == 0 || text.Length > 40) return null;

        foreach (char c in text)
        {
            if (c < '0' || c > '9') return null;
        }

        return format.Prefix + text.PadLeft(format.Digits, '0') + format.Suffix;
    }

    /// <summary>Format de date ou d'heure ? (formats intégrés d'Excel, ou format personnalisé contenant j/m/a/h/s hors guillemets et crochets).</summary>
    internal static bool IsDateFormat(int formatId, string? code)
    {
        if ((formatId >= 14 && formatId <= 22) || (formatId >= 27 && formatId <= 36) || (formatId >= 45 && formatId <= 47) || (formatId >= 50 && formatId <= 58))
        {
            return true;
        }

        if (formatId < 164 || string.IsNullOrEmpty(code)) return false;

        bool inQuotes = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\\':
                case '_':
                case '*':
                    i++;   // le caractère suivant est littéral
                    break;
                case '[':
                    {
                        int end = code.IndexOf(']', i + 1);
                        if (end < 0) return false;
                        string inner = code.Substring(i + 1, end - i - 1);
                        // [h], [mm], [ss] : durées ; [Red], [$-409], [>100] : sans rapport avec une date
                        if (inner.Length > 0 && inner.All(ch => ch == 'h' || ch == 'H' || ch == 'm' || ch == 'M' || ch == 's' || ch == 'S')) return true;
                        i = end;
                        break;
                    }

                case 'y':
                case 'Y':
                case 'd':
                case 'D':
                case 'h':
                case 'H':
                case 'm':
                case 'M':
                case 's':
                case 'S':
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Table des chaînes partagées chargée au fil des demandes : une feuille qui ne cite que les premières chaînes ne fait lire
    /// qu'un début de fichier. Plafonnée en nombre d'entrées et en caractères.
    /// </summary>
    private sealed class SharedStringTable : IDisposable
    {
        private readonly XmlReader _reader;
        private readonly List<string> _loaded = new();
        private long _chars;
        private bool _finished;
        private bool _started;

        internal SharedStringTable(ListZipPackage pkg, ZipArchiveEntry entry)
        {
            _reader = pkg.OpenXml(entry);
        }

        public void Dispose() => _reader.Dispose();

        /// <summary>Chaîne d'indice donné, ou null si l'indice n'existe pas.</summary>
        internal string? Get(int index)
        {
            if (index < 0) return null;
            while (_loaded.Count <= index && !_finished) LoadNext();
            return index < _loaded.Count ? _loaded[index] : null;
        }

        private void LoadNext()
        {
            XmlReader r = _reader;
            if (!_started)
            {
                _started = true;
                r.Read();
            }

            while (!r.EOF)
            {
                ListXml.CheckDepth(r);
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                {
                    string s = ReadRichString(r);
                    _chars += s.Length;
                    if (_loaded.Count >= MaxSharedStrings || _chars > MaxSharedStringChars)
                    {
                        throw new ListFileException("Ce classeur contient trop de texte pour être lu : réduisez-le, ou copiez la colonne puis utilisez « Coller ».");
                    }

                    _loaded.Add(s);
                    return;
                }

                r.Read();
            }

            _finished = true;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Références de cellules
    // ------------------------------------------------------------------------------------------------

    /// <summary>Colonne (0 = A) d'une référence « B12 » ; <paramref name="fallback"/> si elle est absente ou invalide.</summary>
    internal static int ParseColumn(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;

        int value = 0;
        int letters = 0;
        foreach (char c in reference)
        {
            int v;
            if (c >= 'A' && c <= 'Z') v = c - 'A' + 1;
            else if (c >= 'a' && c <= 'z') v = c - 'a' + 1;
            else break;

            letters++;
            if (letters > 3) return fallback;
            value = value * 26 + v;
        }

        return letters == 0 || value > 16384 ? fallback : value - 1;
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0 && v <= 1_000_000 ? v : fallback;

    private static long ParseLong(string? s, long fallback)
        => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long v) && v > 0 && v <= 100_000_000 ? v : fallback;
}
