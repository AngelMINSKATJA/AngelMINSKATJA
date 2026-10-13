using System;
using System.Collections.Generic;
using System.Text;

namespace BarcodeTray;

/// <summary>Une étiquette à imprimer : type de châssis (en haut à gauche) et numéro de plan (grand, centré).</summary>
public sealed record LabelItem(string Type, string Plan);

/// <summary>Résultat de l'analyse d'un tableau collé depuis Excel.</summary>
public sealed class PlanTableParseResult
{
    public PlanTableParseResult(IReadOnlyList<LabelItem> items, IReadOnlyList<string> types, IReadOnlyList<string> warnings, string? error)
    {
        Items = items;
        Types = types;
        Warnings = warnings;
        Error = error;
    }

    /// <summary>Étiquettes, dans l'ordre des lignes du tableau (une par croix).</summary>
    public IReadOnlyList<LabelItem> Items { get; }

    /// <summary>Types de châssis trouvés dans l'en-tête, dans l'ordre des colonnes.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>Remarques (français) : cellules ignorées, lignes sans croix, etc.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Message d'erreur (français) si rien d'exploitable n'a été trouvé, sinon null.</summary>
    public string? Error { get; }
}

/// <summary>
/// Analyse d'un tableau copié depuis Excel (texte séparé par des tabulations).
/// <para>
/// Mode « matrice » (le tableau de plans) : la première ligne non vide est l'en-tête (« Plan SE », « HB1 (20) », « HBA (20) »…),
/// puis une ligne par plan avec une croix dans la colonne du type de châssis ; une étiquette par croix.
/// Mode « liste » (repli, sans aucune croix) : « Type &lt;tab&gt; N° de plan » sur chaque ligne.
/// </para>
/// <para>Cette classe est pure (aucune dépendance Windows, aucun état partagé) et ne lève jamais d'exception.</para>
/// </summary>
public static class PlanTableParser
{
    /// <summary>Garde-fou : nombre maximal d'étiquettes lues dans un tableau.</summary>
    public const int MaxLabels = 5000;

    /// <summary>Garde-fou : nombre maximal de colonnes lues (les suivantes sont ignorées).</summary>
    public const int MaxColumns = 200;

    /// <summary>Garde-fou : nombre maximal de lignes non vides lues (protège la mémoire contre un texte démesuré).</summary>
    public const int MaxRows = 50000;

    private const int MaxExamples = 3;
    private const int MaxExampleLength = 24;

    private const string EmptyMessage =
        "Le presse-papiers est vide : dans Excel, sélectionnez le tableau AVEC sa ligne d'en-tête (Plan SE, HB1, HBA...), copiez-le (Ctrl+C), puis recommencez.";

    private const string NoHeaderMessage =
        "Aucune ligne d'en-tête reconnue (Plan SE, HB1, HBA...) : sélectionnez le tableau AVEC sa ligne d'en-tête puis recopiez-le (Ctrl+C).";

    private const string NoCrossMessage =
        "Aucune croix (X) trouvée : sélectionnez le tableau AVEC sa ligne d'en-tête (Plan SE, HB1, HBA...) puis recopiez-le (Ctrl+C)";

    private const string MarksWithoutPlanMessage =
        "Aucune étiquette exploitable : les croix trouvées n'ont pas de n° de plan dans la première colonne. Sélectionnez le tableau AVEC sa colonne « Plan SE » puis recopiez-le (Ctrl+C).";

    private const string UnexpectedMessage =
        "Le tableau collé n'a pas pu être analysé. Recopiez-le depuis Excel (Ctrl+C) puis recommencez.";

    /// <summary>Cellules qui comptent comme une croix (comparaison insensible à la casse, après nettoyage).</summary>
    private static readonly HashSet<string> Marks = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "×", "✓", "✔", "1", "oui", "o", "yes", "y", "ｘ", "Ｘ",
    };

    /// <summary>Croix « sans ambiguïté » : sert à reconnaître une ligne de plan copiée par erreur à la place de l'en-tête.</summary>
    private static readonly HashSet<string> StrongMarks = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "×", "✓", "✔", "ｘ", "Ｘ",
    };

    // ------------------------------------------------------------------------------------------------
    // API publique
    // ------------------------------------------------------------------------------------------------

    /// <summary>Analyse le texte copié depuis Excel. Ne lève jamais d'exception : en cas de problème, <see cref="PlanTableParseResult.Error"/> est renseigné.</summary>
    public static PlanTableParseResult Parse(string? text)
    {
        try
        {
            return ParseCore(text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail(UnexpectedMessage);
        }
    }

    /// <summary>« 2020Q7E 22061 » devient « 22061 » (chiffres de fin ; à défaut, dernier mot).</summary>
    public static string ExtractPlanNumber(string? cell)
    {
        string s = CleanCell(cell);
        if (s.Length == 0) return "";

        int start = s.Length;
        while (start > 0 && IsAsciiDigit(s[start - 1])) start--;
        if (start < s.Length) return s.Substring(start);

        int space = s.LastIndexOf(' ');
        return space < 0 ? s : s.Substring(space + 1);
    }

    /// <summary>« HB1 (20) » devient « HB1 » (espaces normalisés, parenthèse finale retirée).</summary>
    public static string NormalizeTypeName(string? header)
    {
        string s = CleanCell(header);
        if (s.Length == 0) return "";

        string stripped = StripTrailingGroup(s);
        // « (20) » seul : on garde le texte tel quel plutôt que de perdre la colonne.
        return stripped.Length == 0 ? s : stripped;
    }

    // ------------------------------------------------------------------------------------------------
    // Analyse
    // ------------------------------------------------------------------------------------------------

    private static PlanTableParseResult ParseCore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail(EmptyMessage);

        List<List<string>> rows = SplitTable(text, out bool columnsTruncated, out bool rowsTruncated);
        if (rows.Count == 0) return Fail(EmptyMessage);

        var warnings = new List<string>();
        if (columnsTruncated)
            warnings.Add($"Tableau trop large : seules les {MaxColumns} premières colonnes ont été lues.");
        if (rowsTruncated)
            warnings.Add($"Tableau trop long : seules les {MaxRows} premières lignes ont été lues.");

        // Première ligne non vide = en-tête : colonne 0 = titre de la colonne des plans, les autres cellules non vides = types.
        List<string> header = rows[0];
        var typeColumns = new List<(int Column, string Name)>();
        for (int j = 1; j < header.Count; j++)
        {
            if (header[j].Length > 0) typeColumns.Add((j, NormalizeTypeName(header[j])));
        }

        if (typeColumns.Count == 0 || LooksLikePlanRow(header)) return Fail(NoHeaderMessage);

        // --- Mode matrice -------------------------------------------------------------------------
        var items = new List<LabelItem>();
        var ignoredCells = new Tally();
        var rowsWithoutCross = new Tally();
        int rowsWithoutPlan = 0;
        int marksSeen = 0;
        bool capped = false;

        for (int r = 1; r < rows.Count && !capped; r++)
        {
            List<string> row = rows[r];
            string plan = row.Count > 0 ? ExtractPlanNumber(row[0]) : "";
            int rowMarks = 0;

            foreach ((int column, string name) in typeColumns)
            {
                if (column >= row.Count) break;      // les colonnes sont triées : les suivantes sont toutes absentes
                string cell = row[column];
                if (cell.Length == 0) continue;

                if (!IsMark(cell))
                {
                    ignoredCells.Add(cell);
                    continue;
                }

                rowMarks++;
                marksSeen++;
                if (plan.Length == 0) continue;

                if (items.Count >= MaxLabels)
                {
                    capped = true;
                    break;
                }

                items.Add(new LabelItem(name, plan));
            }

            if (rowMarks > 0)
            {
                if (plan.Length == 0) rowsWithoutPlan++;
            }
            else if (plan.Length > 0)
            {
                rowsWithoutCross.Add(plan);
            }
        }

        if (marksSeen == 0)
        {
            // Aucune croix : peut-être une simple liste « Type <tab> Plan ».
            PlanTableParseResult? list = TryParseList(rows, warnings);
            return list ?? Fail(NoCrossMessage);
        }

        if (items.Count == 0) return Fail(MarksWithoutPlanMessage);

        if (ignoredCells.Count > 0)
            warnings.Add($"{ignoredCells.Count} cellule(s) non vide(s) ignorée(s) : {ignoredCells.Describe()}");
        if (rowsWithoutCross.Count > 0)
            warnings.Add($"{rowsWithoutCross.Count} ligne(s) sans croix ignorée(s) : {rowsWithoutCross.Describe()}");
        if (rowsWithoutPlan > 0)
            warnings.Add($"{rowsWithoutPlan} ligne(s) avec une croix mais sans n° de plan ignorée(s).");

        AddItemWarnings(items, warnings, capped);

        var types = new List<string>();
        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((int _, string name) in typeColumns)
        {
            if (knownTypes.Add(name)) types.Add(name);
        }

        return new PlanTableParseResult(items, types, warnings, null);
    }

    /// <summary>
    /// Mode liste : chaque ligne non vide est « Type &lt;tab&gt; Plan ». Renvoie null si le texte n'a pas cette forme.
    /// </summary>
    private static PlanTableParseResult? TryParseList(List<List<string>> rows, List<string> warnings)
    {
        foreach (List<string> row in rows)
        {
            if (row.Count < 2 || row[0].Length == 0 || row[1].Length == 0) return null;
        }

        // Première ligne d'allure « titre » : ignorée. Soit sa 2e cellule n'a aucun chiffre (ex. « Type / Plan »),
        // soit c'est un en-tête de tableau (« Plan SE / HB1 / HBA… » : titre sans chiffre suivi d'au moins deux types) copié sans ses lignes.
        int first = HasDigit(rows[0][1]) && !LooksLikeMatrixHeader(rows[0]) ? 0 : 1;

        var items = new List<LabelItem>();
        var ignoredCells = new Tally();
        bool anyPlanWithDigit = false;
        bool capped = false;

        for (int r = first; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            string plan = ExtractPlanNumber(row[1]);
            if (HasDigit(plan)) anyPlanWithDigit = true;

            if (items.Count >= MaxLabels)
            {
                capped = true;
                break;
            }

            items.Add(new LabelItem(NormalizeTypeName(row[0]), plan));

            for (int j = 2; j < row.Count; j++)
            {
                if (row[j].Length > 0) ignoredCells.Add(row[j]);
            }
        }

        // Sans aucun numéro contenant un chiffre, ce n'est pas une liste de plans (et on ne fabrique pas d'étiquettes au hasard).
        if (items.Count == 0 || !anyPlanWithDigit) return null;

        if (ignoredCells.Count > 0)
            warnings.Add($"{ignoredCells.Count} cellule(s) non vide(s) ignorée(s) : {ignoredCells.Describe()}");

        AddItemWarnings(items, warnings, capped);

        var types = new List<string>();
        var knownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LabelItem item in items)
        {
            if (knownTypes.Add(item.Type)) types.Add(item.Type);
        }

        return new PlanTableParseResult(items, types, warnings, null);
    }

    /// <summary>En-tête de tableau : première cellule sans chiffre (« Plan SE ») suivie d'au moins deux autres cellules non vides.</summary>
    private static bool LooksLikeMatrixHeader(List<string> row)
    {
        if (row.Count < 3 || row[0].Length == 0 || HasDigit(row[0])) return false;

        int others = 0;
        for (int j = 1; j < row.Count; j++)
        {
            if (row[j].Length > 0) others++;
        }

        return others >= 2;
    }

    /// <summary>Remarques communes aux deux modes : n° de plan sans chiffre, doublons, plafond d'étiquettes.</summary>
    private static void AddItemWarnings(List<LabelItem> items, List<string> warnings, bool capped)
    {
        var suspicious = new Tally();
        var duplicates = new Tally();
        var seen = new HashSet<LabelItem>();

        foreach (LabelItem item in items)
        {
            if (!HasDigit(item.Plan)) suspicious.Add(item.Plan);
            if (!seen.Add(item)) duplicates.Add(item.Type + " " + item.Plan);
        }

        if (suspicious.Count > 0)
            warnings.Add($"{suspicious.Count} n° de plan sans chiffre (à vérifier) : {suspicious.Describe()}");
        if (duplicates.Count > 0)
            warnings.Add($"{duplicates.Count} étiquette(s) en double (même type et même n° de plan, conservées) : {duplicates.Describe()}");
        if (capped)
            warnings.Add($"Plus de {MaxLabels} étiquettes dans le tableau : seules les {MaxLabels} premières sont conservées.");
    }

    private static PlanTableParseResult Fail(string message)
        => new(Array.Empty<LabelItem>(), Array.Empty<string>(), Array.Empty<string>(), message);

    /// <summary>Vrai si la cellule (déjà nettoyée) est une croix.</summary>
    private static bool IsMark(string cleaned) => Marks.Contains(cleaned);

    /// <summary>
    /// La première ligne copiée est une ligne de plan et non l'en-tête (l'utilisateur a oublié la ligne « Plan SE… ») :
    /// la première colonne contient un n° de plan et une autre cellule est une croix franche.
    /// Sans cette vérification, la croix serait prise pour un « type » nommé « X ».
    /// </summary>
    private static bool LooksLikePlanRow(List<string> header)
    {
        if (header.Count < 2 || !HasDigit(header[0])) return false;
        for (int j = 1; j < header.Count; j++)
        {
            if (StrongMarks.Contains(header[j])) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------
    // Découpage du texte (règles « Excel » : tabulations, guillemets, CRLF/LF/CR)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Découpe le texte en lignes de cellules déjà nettoyées (<see cref="CleanCell"/>). Les lignes entièrement vides sont supprimées,
    /// les cellules vides de fin de ligne aussi (les colonnes restent alignées : une cellule absente se lit comme vide).
    /// </summary>
    internal static List<List<string>> SplitTable(string text, out bool columnsTruncated, out bool rowsTruncated)
    {
        columnsTruncated = false;
        rowsTruncated = false;

        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        int n = text.Length;
        int i = n > 0 && text[0] == '\uFEFF' ? 1 : 0;   // BOM éventuel

        while (true)
        {
            string cell = CleanCell(ReadField(text, ref i, sb));
            if (row.Count < MaxColumns) row.Add(cell);
            else if (cell.Length > 0) columnsTruncated = true;

            bool endOfRow;
            if (i >= n)
            {
                endOfRow = true;
            }
            else if (text[i] == '\t')
            {
                i++;
                endOfRow = false;
            }
            else
            {
                // CR, LF ou CRLF
                char c = text[i++];
                if (c == '\r' && i < n && text[i] == '\n') i++;
                endOfRow = true;
            }

            if (!endOfRow) continue;

            while (row.Count > 0 && row[row.Count - 1].Length == 0) row.RemoveAt(row.Count - 1);
            if (row.Count > 0)
            {
                if (rows.Count >= MaxRows)
                {
                    rowsTruncated = true;
                    return rows;
                }

                rows.Add(row);
                row = new List<string>();
            }

            if (i >= n) break;
        }

        return rows;
    }

    /// <summary>
    /// Lit un champ à partir de <paramref name="i"/> et s'arrête sur la tabulation, le saut de ligne ou la fin du texte.
    /// Champ entre guillemets : « "" » = un guillemet, les tabulations et sauts de ligne internes sont conservés.
    /// Un guillemet d'ouverture jamais refermé est pris pour un guillemet ordinaire (le reste du tableau n'est pas avalé).
    /// </summary>
    private static string ReadField(string text, ref int i, StringBuilder sb)
    {
        int n = text.Length;

        if (i < n && text[i] == '"')
        {
            sb.Clear();
            int j = i + 1;
            bool closed = false;
            while (j < n)
            {
                char c = text[j];
                if (c == '"')
                {
                    if (j + 1 < n && text[j + 1] == '"')
                    {
                        sb.Append('"');
                        j += 2;
                        continue;
                    }

                    closed = true;
                    j++;
                    break;
                }

                sb.Append(c);
                j++;
            }

            if (closed)
            {
                // Tolérance : du texte collé juste après le guillemet de fermeture est gardé tel quel (Excel n'en produit pas).
                while (j < n && text[j] != '\t' && text[j] != '\r' && text[j] != '\n')
                {
                    sb.Append(text[j]);
                    j++;
                }

                i = j;
                return sb.ToString();
            }
            // Pas de fermeture : on relit le champ comme un champ ordinaire.
        }

        int start = i;
        while (i < n && text[i] != '\t' && text[i] != '\r' && text[i] != '\n') i++;
        return text.Substring(start, i - start);
    }

    // ------------------------------------------------------------------------------------------------
    // Nettoyage de texte
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Nettoie une cellule : toutes les espaces (y compris l'espace insécable U+00A0, les sauts de ligne et tabulations) deviennent
    /// une espace simple, les suites d'espaces sont réduites, les extrémités rognées, et les caractères invisibles
    /// (espace de largeur nulle, BOM, sélecteurs de variante, caractères de contrôle) sont supprimés.
    /// </summary>
    internal static string CleanCell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (IsInvisible(c)) continue;

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsInvisible(char c)
        => char.IsControl(c)
           || c == '\u00AD'                          // trait d'union conditionnel
           || (c >= '\u200B' && c <= '\u200F')       // espace de largeur nulle, marques directionnelles
           || (c >= '\u202A' && c <= '\u202E')
           || (c >= '\u2060' && c <= '\u2064')
           || c == '\uFEFF'                          // BOM / espace insécable de largeur nulle
           || c == '\uFE0E' || c == '\uFE0F';        // sélecteurs de variante (« ✔️ »)

    /// <summary>« HB1 (20) » : retire le groupe entre parenthèses qui termine le texte (parenthèses équilibrées uniquement).</summary>
    private static string StripTrailingGroup(string s)
    {
        char last = s[s.Length - 1];
        if (last != ')' && last != '）') return s;

        int depth = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            char c = s[i];
            if (c == ')' || c == '）')
            {
                depth++;
            }
            else if (c == '(' || c == '（')
            {
                depth--;
                if (depth == 0) return s.Substring(0, i).TrimEnd();
            }
        }

        return s;
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    private static bool HasDigit(string s)
    {
        foreach (char c in s)
        {
            if (IsAsciiDigit(c)) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------------------------------------
    // Exemples cités dans les remarques
    // ------------------------------------------------------------------------------------------------

    /// <summary>Compte des cas et garde quelques exemples distincts pour les citer dans une remarque.</summary>
    private sealed class Tally
    {
        private readonly List<string> _examples = new();
        private bool _more;

        public int Count { get; private set; }

        public void Add(string example)
        {
            Count++;
            if (_examples.Contains(example)) return;
            if (_examples.Count < MaxExamples) _examples.Add(example);
            else _more = true;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _examples.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                string e = _examples[i];
                if (e.Length > MaxExampleLength)
                {
                    int cut = char.IsHighSurrogate(e[MaxExampleLength - 1]) ? MaxExampleLength - 1 : MaxExampleLength;
                    e = e.Substring(0, cut) + "…";
                }

                sb.Append("« ").Append(e).Append(" »");
            }

            if (_more) sb.Append(" …");
            return sb.ToString();
        }
    }
}
