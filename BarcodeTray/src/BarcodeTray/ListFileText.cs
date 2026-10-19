using System;
using System.Collections.Generic;
using System.Text;

namespace BarcodeTray;

/// <summary>
/// Texte brut : décodage des octets (UTF-8 avec ou sans BOM, UTF-16/32 avec BOM, repli Windows-1252), découpage en lignes (.txt),
/// en champs (.csv, avec guillemets à la façon d'Excel / RFC 4180) et lecture du texte collé.
/// Fonctions pures, sans état partagé.
/// </summary>
internal static class ListFileText
{
    private static readonly char[] Cp1252High =
    {
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    };

    /// <summary>Nombre d'enregistrements examinés pour deviner le séparateur d'un .csv.</summary>
    private const int DelimiterSampleRecords = 50;

    // ------------------------------------------------------------------------------------------------
    // Décodage des octets
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Octets en texte. UTF-8 (avec ou sans BOM) si c'est valide, UTF-16 / UTF-32 avec BOM (et UTF-16 sans BOM reconnaissable),
    /// sinon Windows-1252 (l'ANSI des anciens Bloc-notes et d'Excel « CSV »). Refuse les contenus binaires (<see cref="ListFileException"/>).
    /// </summary>
    internal static string Decode(byte[] data, int length)
    {
        ReadOnlySpan<byte> all = data.AsSpan(0, length);

        // Octets de fin sans signification : NUL de remplissage, Ctrl+Z (marque de fin des vieux fichiers DOS).
        int end = length;
        while (end > 0 && (all[end - 1] == 0 || all[end - 1] == 0x1A)) end--;

        // Un fichier UTF-16 / UTF-32 se termine légitimement par des NUL : on ne rogne que pour les encodages d'un octet.
        if (HasBom(all, 0xFF, 0xFE, 0x00, 0x00))
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false).GetString(data, 4, length - 4);
        }

        if (HasBom(all, 0x00, 0x00, 0xFE, 0xFF))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false).GetString(data, 4, length - 4);
        }

        if (HasBom(all, 0xFF, 0xFE))
        {
            return CheckText(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false).GetString(data, 2, (length - 2) & ~1));
        }

        if (HasBom(all, 0xFE, 0xFF))
        {
            return CheckText(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false).GetString(data, 2, (length - 2) & ~1));
        }

        int skip = HasBom(all, 0xEF, 0xBB, 0xBF) ? 3 : 0;
        ReadOnlySpan<byte> body = all.Slice(skip, Math.Max(0, end - skip));

        if (end == 0)
        {
            // Rien que des NUL (ou des Ctrl+Z) : fichier vide ou corrompu, pas du texte.
            throw new ListFileException(
                "Ce fichier n'est pas un fichier texte (il ne contient que des octets vides). Ouvrez-le dans son programme d'origine (Excel, Word...) "
                + "et enregistrez-le en .xlsx / .docx, ou copiez la liste puis utilisez « Coller ».");
        }

        int nul = body.Count((byte)0);
        if (nul > 0)
        {
            // UTF-16 sans BOM : les octets de poids fort (tous nuls pour du texte latin) occupent une position sur deux.
            // On examine les octets bruts (les NUL de fin d'un fichier UTF-16 sont de vrais caractères).
            ReadOnlySpan<byte> raw = all.Slice(skip);
            int sample = Math.Min(raw.Length, 4096) & ~1;
            int even = 0;
            int odd = 0;
            for (int i = 0; i < sample; i++)
            {
                if (raw[i] != 0) continue;
                if ((i & 1) == 0) even++;
                else odd++;
            }

            int half = sample / 2;
            if (half >= 2 && odd * 10 >= half * 9 && even * 20 <= half)
            {
                return CheckText(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false)
                    .GetString(data, skip, (length - skip) & ~1));
            }

            if (half >= 2 && even * 10 >= half * 9 && odd * 20 <= half)
            {
                return CheckText(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false)
                    .GetString(data, skip, (length - skip) & ~1));
            }

            throw new ListFileException(
                "Ce fichier n'est pas un fichier texte (contenu binaire). Ouvrez-le dans son programme d'origine (Excel, Word...) "
                + "et enregistrez-le en .xlsx / .docx, ou copiez la liste puis utilisez « Coller ».");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data, skip, end - skip);
        }
        catch (DecoderFallbackException)
        {
            text = DecodeWindows1252(data, skip, end - skip);
        }

        return CheckText(text);
    }

    private static bool HasBom(ReadOnlySpan<byte> d, params byte[] bom)
    {
        if (d.Length < bom.Length) return false;
        for (int i = 0; i < bom.Length; i++)
        {
            if (d[i] != bom[i]) return false;
        }

        return true;
    }

    /// <summary>Windows-1252 : l'ISO-8859-1 avec, de 0x80 à 0x9F, les lettres et signes de la table Windows (€, guillemets typographiques...).</summary>
    internal static string DecodeWindows1252(byte[] data, int offset, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            byte b = data[offset + i];
            chars[i] = b >= 0x80 && b <= 0x9F ? Cp1252High[b - 0x80] : (char)b;
        }

        return new string(chars);
    }

    /// <summary>Refuse un « texte » plein de caractères de contrôle (fichier binaire lu à tort comme du texte).</summary>
    private static string CheckText(string text)
    {
        int controls = 0;
        foreach (char c in text)
        {
            bool control = (c < 0x20 && c != '\t' && c != '\n' && c != '\r' && c != '\f') || c == '\u007F';
            if (control) controls++;
        }

        if (controls > 0 && (long)controls * 50 > text.Length)
        {
            throw new ListFileException(
                "Ce fichier n'est pas un fichier texte (contenu binaire). Ouvrez-le dans son programme d'origine (Excel, Word...) "
                + "et enregistrez-le en .xlsx / .docx, ou copiez la liste puis utilisez « Coller ».");
        }

        return text;
    }

    // ------------------------------------------------------------------------------------------------
    // .txt : une valeur par ligne
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Une valeur par ligne (CRLF, LF ou CR), ligne entière sans découpage ni guillemets.
    /// Retourne le nombre de lignes non vides lues et, parmi elles, combien contiennent une tabulation (colonnes d'un export Excel).
    /// </summary>
    internal static (int Lines, int WithTab) ReadLines(string text, ListCollector collector)
    {
        int n = text.Length;
        int i = n > 0 && text[0] == '\uFEFF' ? 1 : 0;
        int lines = 0;
        int withTab = 0;
        while (i < n)
        {
            int rel = text.AsSpan(i).IndexOfAny('\r', '\n');
            int stop = rel < 0 ? n : i + rel;
            if (stop == i)
            {
                collector.AddEmpty(1);
            }
            else
            {
                string line = text.Substring(i, stop - i);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines++;
                    if (line.Trim().Contains('\t')) withTab++;   // tabulation intérieure = séparateur de colonnes
                }

                if (!collector.AddRaw(line)) return (lines, withTab);
            }

            if (stop >= n) return (lines, withTab);
            i = stop + (text[stop] == '\r' && stop + 1 < n && text[stop + 1] == '\n' ? 2 : 1);
        }

        return (lines, withTab);
    }

    // ------------------------------------------------------------------------------------------------
    // .csv et texte collé : première cellule de chaque ligne
    // ------------------------------------------------------------------------------------------------

    /// <summary>Séparateur à utiliser quand aucun n'est reconnu : un seul champ par ligne.</summary>
    internal const int NoDelimiter = -1;

    /// <summary>
    /// Devine le séparateur d'un .csv : « ; », tabulation puis « , » (dans cet ordre de préférence) si au moins 90 % des premiers
    /// enregistrements ont le même nombre de champs, supérieur à 1 (un fichier un peu irrégulier reste lisible). Sinon
    /// <see cref="NoDelimiter"/> (liste à une colonne : des virgules isolées dans des textes ne sont pas prises pour des séparateurs).
    /// </summary>
    internal static int DetectDelimiter(string text)
    {
        foreach (char candidate in new[] { ';', '\t', ',' })
        {
            List<int>? counts = FieldCounts(text, candidate, DelimiterSampleRecords);
            if (counts == null || counts.Count == 0) continue;

            var frequency = new Dictionary<int, int>();
            int mode = 0;
            int modeCount = 0;
            foreach (int c in counts)
            {
                frequency.TryGetValue(c, out int seen);
                frequency[c] = ++seen;
                if (seen > modeCount || (seen == modeCount && c > mode))
                {
                    mode = c;
                    modeCount = seen;
                }
            }

            if (mode >= 2 && (long)modeCount * 10 >= (long)counts.Count * 9) return candidate;
        }

        return NoDelimiter;
    }

    /// <summary>
    /// Directive d'Excel « sep=; » sur la première ligne d'un .csv : donne le séparateur et la position où commencent les données.
    /// </summary>
    internal static bool TryReadSepDirective(string text, out int delimiter, out int dataStart)
    {
        delimiter = NoDelimiter;
        dataStart = 0;
        int i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        if (text.Length < i + 5 || string.CompareOrdinal(text, i, "sep=", 0, 4) != 0) return false;

        char c = text[i + 4];
        if (c == '\r' || c == '\n' || c == '"') return false;
        int next = i + 5;
        if (next < text.Length && text[next] != '\r' && text[next] != '\n') return false;

        delimiter = c;
        dataStart = SkipTerminator(text, next);
        return true;
    }

    /// <summary>
    /// Lit la cellule d'indice <paramref name="column"/> (0 = la PREMIÈRE) de chaque ligne. Retourne vrai si les cellules SUIVANTES
    /// contenaient du texte (elles sont ignorées) ; les cellules d'avant ne comptent pas.
    /// Règles d'Excel : champ entre guillemets (« "" » = un guillemet, séparateurs et sauts de ligne internes conservés) ;
    /// un guillemet qui n'ouvre pas vraiment un champ (jamais refermé, ou suivi de texte) est un caractère ordinaire.
    /// Une ligne qui n'a pas assez de cellules compte comme une ligne vide.
    /// </summary>
    internal static bool ReadFirstFields(string text, int delimiter, ListCollector collector, int column = 0)
    {
        int n = text.Length;
        int i = n > 0 && text[0] == '\uFEFF' ? 1 : 0;
        bool otherColumns = false;
        long budget = QuoteBudget(n);

        while (i < n)
        {
            string? value = null;
            int index = 0;
            while (true)
            {
                bool wanted = index == column;
                string? field = ScanField(text, ref i, delimiter, capture: wanted, ref budget, out bool hasContent, out _);
                if (wanted) value = field;
                else if (index > column && hasContent) otherColumns = true;

                if (delimiter >= 0 && i < n && text[i] == delimiter)
                {
                    i++;
                    index++;
                    continue;
                }

                break;
            }

            if (!collector.AddRaw(value)) return otherColumns;

            i = SkipTerminator(text, i);
        }

        return otherColumns;
    }

    /// <summary>
    /// Nombre de champs des premiers enregistrements non vides pour ce séparateur. Null si un champ commence par un guillemet qui
    /// n'ouvre pas un vrai champ entre guillemets : avec le bon séparateur, les guillemets d'un .csv sont toujours bien formés.
    /// </summary>
    private static List<int>? FieldCounts(string text, int delimiter, int maxRecords)
    {
        var counts = new List<int>();
        int n = text.Length;
        int i = n > 0 && text[0] == '\uFEFF' ? 1 : 0;
        long budget = QuoteBudget(n);

        while (i < n && counts.Count < maxRecords)
        {
            int fields = 0;
            bool any = false;
            while (true)
            {
                ScanField(text, ref i, delimiter, capture: false, ref budget, out bool hasContent, out bool malformed);
                if (malformed) return null;
                fields++;
                any |= hasContent;
                if (i < n && text[i] == delimiter)
                {
                    i++;
                    continue;
                }

                break;
            }

            i = SkipTerminator(text, i);
            if (any) counts.Add(fields);
        }

        return counts;
    }

    /// <summary>Plafond du travail de recherche des guillemets fermants (protège d'un fichier fait pour être lent).</summary>
    private static long QuoteBudget(int length) => 8L * length + 1_000_000;

    private static int SkipTerminator(string text, int i)
    {
        int n = text.Length;
        if (i >= n) return n;
        if (text[i] == '\r' && i + 1 < n && text[i + 1] == '\n') return i + 2;
        return i + 1;   // CR ou LF
    }

    /// <summary>
    /// Avance <paramref name="i"/> jusqu'à la fin du champ (séparateur, fin de ligne ou fin du texte, non consommés).
    /// Retourne le contenu si <paramref name="capture"/>.
    /// </summary>
    private static string? ScanField(string text, ref int i, int delimiter, bool capture, ref long quoteBudget, out bool hasContent, out bool malformedQuote)
    {
        int n = text.Length;
        hasContent = false;
        malformedQuote = false;
        if (i >= n) return capture ? "" : null;

        if (text[i] == '"' && quoteBudget > 0)
        {
            int j = i + 1;
            bool escapes = false;
            int closing = -1;
            while (j < n)
            {
                int k = text.IndexOf('"', j);
                if (k < 0)
                {
                    j = n;
                    break;
                }

                if (k + 1 < n && text[k + 1] == '"')
                {
                    escapes = true;
                    j = k + 2;
                    continue;
                }

                closing = k;
                break;
            }

            quoteBudget -= Math.Max(1, (closing >= 0 ? closing + 1 : j) - i);   // travail réellement fait (protège d'un fichier fait pour être lent)

            if (closing >= 0)
            {
                int after = closing + 1;
                bool clean = after >= n || text[after] == '\r' || text[after] == '\n' || (delimiter >= 0 && text[after] == delimiter);
                if (clean)
                {
                    int innerStart = i + 1;
                    int innerLength = closing - innerStart;
                    hasContent = HasContent(text, innerStart, innerLength);
                    i = after;
                    if (!capture) return null;
                    string inner = text.Substring(innerStart, innerLength);
                    return escapes ? inner.Replace("\"\"", "\"") : inner;
                }
            }
            // Pas de vrai champ entre guillemets : on relit comme un champ ordinaire.
            malformedQuote = true;
        }

        int start = i;
        ReadOnlySpan<char> rest = text.AsSpan(i);
        int len = delimiter >= 0 ? rest.IndexOfAny('\r', '\n', (char)delimiter) : rest.IndexOfAny('\r', '\n');
        i = len < 0 ? n : i + len;
        hasContent = HasContent(text, start, i - start);
        return capture ? text.Substring(start, i - start) : null;
    }

    private static bool HasContent(string text, int start, int length)
    {
        int end = start + length;
        for (int k = start; k < end; k++)
        {
            char c = text[k];
            if (!char.IsWhiteSpace(c) && c != '\u200B' && c != '\uFEFF') return true;
        }

        return false;
    }
}

/// <summary>
/// Reconnaît les numéros et puces de liste que Word écrit devant chaque ligne copiée (« 1.<tab>AAA-1 », « •<tab>AAA-1 ») : dans le
/// texte du presse-papiers ce ne sont PLUS de la mise en forme mais une première cellule, qu'il ne faut pas prendre pour le code.
/// </summary>
internal static class ListMarkers
{
    /// <summary>Puces usuelles (Word en utilise plusieurs selon la police) ; les caractères de la zone privée (Symbol, Wingdings : U+F0B7...) en sont aussi.</summary>
    private const string Bullets = "•·▪■□○◦●‣⁃◆◇►▶➢✓✔§-–—*";

    /// <summary>
    /// Vrai si au moins 90 % des valeurs sont des numéros ou des puces de liste. <paramref name="skipFirstLine"/> : la première valeur
    /// est un titre de colonne, elle ne compte pas.
    /// </summary>
    internal static bool AreListMarkers(IReadOnlyList<string> values, bool skipFirstLine)
    {
        int start = skipFirstLine ? 1 : 0;
        int total = values.Count - start;
        if (total <= 0) return false;

        int markers = 0;
        for (int i = start; i < values.Count; i++)
        {
            if (IsMarker(values[i])) markers++;
        }

        return (long)markers * 10 >= (long)total * 9;
    }

    /// <summary>« 1. », « 12) », « (3) », « a. », « iv. », « 1.2. » ou une puce seule.</summary>
    internal static bool IsMarker(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 12) return false;

        if (value.Length == 1)
        {
            char c = value[0];
            return Bullets.IndexOf(c) >= 0 || (c >= '' && c <= '');
        }

        int n = value.Length;
        int i = value[0] == '(' ? 1 : 0;
        int start = i;
        if (i < n && value[i] >= '0' && value[i] <= '9')
        {
            while (i < n && value[i] >= '0' && value[i] <= '9') i++;
            if (i - start > 4) return false;
        }
        else if (i < n && IsAsciiLetter(value[i]))
        {
            while (i < n && IsAsciiLetter(value[i])) i++;
            int length = i - start;
            if (length > 6 || (length > 1 && !IsRomanNumeral(value.Substring(start, length)))) return false;
        }
        else
        {
            return false;
        }

        while (i + 1 < n && value[i] == '.' && value[i + 1] >= '0' && value[i + 1] <= '9')
        {
            i++;
            int levelStart = i;
            while (i < n && value[i] >= '0' && value[i] <= '9') i++;
            if (i - levelStart > 3) return false;
        }

        return i == n - 1 && (value[i] == '.' || value[i] == ')');
    }

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsRomanNumeral(string letters)
    {
        bool lower = char.IsLower(letters[0]);
        foreach (char c in letters)
        {
            if (char.IsLower(c) != lower) return false;
            if ("ivxlcdm".IndexOf(char.ToLowerInvariant(c)) < 0) return false;
        }

        return true;
    }
}
