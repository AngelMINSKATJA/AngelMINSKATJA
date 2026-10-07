namespace Code128;

/// <summary>
/// The Code 128 symbol table (ISO/IEC 15417) and the special symbol values.
/// </summary>
/// <remarks>
/// Each entry is the sequence of element widths, in modules, starting with a BAR and then
/// alternating space / bar. Every symbol has 6 elements and is 11 modules wide, except the Stop
/// symbol which has 7 elements and is 13 modules wide (it ends with the 2-module termination bar).
/// </remarks>
internal static class Code128Patterns
{
    internal const int FNC3 = 96;
    internal const int FNC2 = 97;
    internal const int Shift = 98;
    internal const int CodeC = 99;
    internal const int CodeB = 100;   // "Code B" from sets A and C (symbol 100 is FNC4 when in set B)
    internal const int CodeA = 101;   // "Code A" from sets B and C (symbol 101 is FNC4 when in set A)
    internal const int FNC1 = 102;
    internal const int StartA = 103;
    internal const int StartB = 104;
    internal const int StartC = 105;
    internal const int Stop = 106;

    /// <summary>Number of entries in <see cref="Widths"/> (values 0..106).</summary>
    internal const int Count = 107;

    // Index = symbol value. Digits = widths of bar, space, bar, space, bar, space [, bar].
    private static readonly string[] Table =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", //   0..9
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132", //  10..19
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211", //  20..29
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", //  30..39
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", //  40..49
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", //  50..59
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", //  60..69
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111", //  70..79
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", //  80..89
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", //  90..99
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",                              // 100..106
    };

    /// <summary>Element widths of every symbol value 0..106 (bar-first, alternating).</summary>
    internal static readonly IReadOnlyList<IReadOnlyList<int>> Widths = Parse();

    private static IReadOnlyList<IReadOnlyList<int>> Parse()
    {
        var all = new IReadOnlyList<int>[Table.Length];
        for (int v = 0; v < Table.Length; v++)
        {
            string s = Table[v];
            var w = new int[s.Length];
            for (int k = 0; k < s.Length; k++)
            {
                w[k] = s[k] - '0';
            }
            all[v] = Array.AsReadOnly(w);
        }
        return Array.AsReadOnly(all);
    }
}
