namespace Code128;

/// <summary>
/// Result of encoding a text as Code 128.
/// </summary>
public sealed class Code128Barcode
{
    public Code128Barcode(string text, IReadOnlyList<int> symbols, IReadOnlyList<bool> modules)
    {
        Text = text;
        Symbols = symbols;
        Modules = modules;
    }

    /// <summary>The text that was encoded (unchanged).</summary>
    public string Text { get; }

    /// <summary>
    /// Symbol values (0..106) in print order: Start (103..105), data/code-switch symbols,
    /// checksum, Stop (106).
    /// </summary>
    public IReadOnlyList<int> Symbols { get; }

    /// <summary>
    /// Bar/space pattern WITHOUT quiet zones, one entry per module: true = bar, false = space.
    /// Length = 11 * (Symbols.Count - 1) + 13 (the Stop symbol is 13 modules wide).
    /// </summary>
    public IReadOnlyList<bool> Modules { get; }
}

public static class Code128Encoder
{
    /// <summary>Minimum quiet zone required on each side, in modules.</summary>
    public const int QuietZoneModules = 10;

    /// <summary>Symbol value of the Stop character.</summary>
    public const int StopSymbol = 106;

    // ---- encoder internals -------------------------------------------------------------------
    //
    // Optimal encoding = shortest path through the states (position, active code set).
    // Cost of a path is the pair (number of symbols, number of "special" symbols), compared
    // lexicographically and packed into one long: symbols in the high 32 bits, specials in the low
    // 32 bits.  "Special" = code-switch symbol (99/100/101) or SHIFT (98).  So:
    //   1st criterion: fewest symbols,
    //   2nd criterion: fewest switches (code switches + shifts),
    //   3rd criterion (exact ties only): prefer set B, then A, then C -- implemented by the order
    //   in which candidates are examined while walking the optimal path (first best wins).
    // In particular for an odd-length digit run the lone digit comes first and Code C starts
    // after it (e.g. "12345" => Start B, '1', Code C, "23", "45"), as ISO/GS1 recommend.

    private const int SetA = 0;
    private const int SetB = 1;
    private const int SetC = 2;

    private const long OneSymbol = 1L << 32;
    private const long OneSpecial = 1L;
    private const long SwitchCost = OneSymbol + OneSpecial;   // a code-switch symbol
    private const long Impossible = long.MaxValue / 4;

    // Examination order for exact ties: B first, then A, then C.
    private static readonly int[] TieOrder = { SetB, SetA, SetC };

    private enum Move
    {
        None,          // not possible
        Single,        // one data symbol for one char, in the active set
        ShiftSingle,   // SHIFT symbol + one data symbol (char of the "other" A/B set)
        Pair,          // one data symbol for two digits (set C)
    }

    /// <summary>
    /// True when every char of <paramref name="text"/> is encodable (ASCII 0..127).
    /// When false, <paramref name="firstInvalid"/> is the first offending char.
    /// </summary>
    /// <remarks>
    /// An empty string is "encodable" char-wise (but <see cref="Encode"/> still rejects it).
    /// A null string returns false with <paramref name="firstInvalid"/> = '\0'.
    /// </remarks>
    public static bool IsEncodable(string text, out char firstInvalid)
    {
        firstInvalid = '\0';
        if (text is null)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c > 127)
            {
                firstInvalid = c;
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="text"/> with the minimum number of symbols (code sets A/B/C,
    /// switches and shifts chosen optimally). Throws <see cref="ArgumentException"/> if the text
    /// is null, empty or contains a char outside ASCII 0..127.
    /// </summary>
    public static Code128Barcode Encode(string text)
    {
        if (text is null)
        {
            throw new ArgumentException("The text must not be null.", nameof(text));
        }
        if (text.Length == 0)
        {
            throw new ArgumentException("The text must not be empty.", nameof(text));
        }
        if (!IsEncodable(text, out char bad))
        {
            throw new ArgumentException(
                $"Character U+{(int)bad:X4} is outside ASCII (0..127) and cannot be encoded in Code 128.",
                nameof(text));
        }

        List<int> symbols = PlanSymbols(text);

        // Checksum = (start + sum(position * value)) mod 103, positions counted from 1 after Start.
        long sum = symbols[0];
        for (int k = 1; k < symbols.Count; k++)
        {
            sum = (sum + (long)(k % 103) * symbols[k]) % 103;
        }
        symbols.Add((int)(sum % 103));
        symbols.Add(StopSymbol);

        return new Code128Barcode(
            text,
            Array.AsReadOnly(symbols.ToArray()),
            Array.AsReadOnly(BuildModules(symbols)));
    }

    // ---- optimal planning --------------------------------------------------------------------

    private static List<int> PlanSymbols(string s)
    {
        int n = s.Length;

        // f[i, set] = optimal cost to encode s[i..] when `set` is active before char i.
        var f = new long[n + 1, 3];   // f[n, *] = 0
        for (int i = n - 1; i >= 0; i--)
        {
            long costA = CostOfEncoding(s, f, i, SetA, out _);
            long costB = CostOfEncoding(s, f, i, SetB, out _);
            long costC = CostOfEncoding(s, f, i, SetC, out _);
            long best = Math.Min(costA, Math.Min(costB, costC));
            f[i, SetA] = Math.Min(costA, SwitchCost + best);
            f[i, SetB] = Math.Min(costB, SwitchCost + best);
            f[i, SetC] = Math.Min(costC, SwitchCost + best);
        }

        var symbols = new List<int>(n + 4);

        // Start symbol: no switch penalty, ties resolved by TieOrder (B, A, C).
        int set = -1;
        long bestStart = Impossible;
        foreach (int candidate in TieOrder)
        {
            long cost = CostOfEncoding(s, f, 0, candidate, out _);
            if (cost < bestStart)
            {
                bestStart = cost;
                set = candidate;
            }
        }
        symbols.Add(set switch
        {
            SetA => Code128Patterns.StartA,
            SetB => Code128Patterns.StartB,
            _ => Code128Patterns.StartC,
        });

        int pos = 0;
        while (pos < n)
        {
            int target = -1;
            Move move = Move.None;
            long best = Impossible;
            foreach (int candidate in TieOrder)
            {
                long cost = CostOfEncoding(s, f, pos, candidate, out Move m);
                if (cost >= Impossible)
                {
                    continue;
                }
                if (candidate != set)
                {
                    cost += SwitchCost;
                }
                if (cost < best)
                {
                    best = cost;
                    target = candidate;
                    move = m;
                }
            }

            if (target != set)
            {
                symbols.Add(target switch
                {
                    SetA => Code128Patterns.CodeA,
                    SetB => Code128Patterns.CodeB,
                    _ => Code128Patterns.CodeC,
                });
                set = target;
            }

            char c = s[pos];
            switch (move)
            {
                case Move.Single:
                    symbols.Add(set == SetA ? ValueInSetA(c) : ValueInSetB(c));
                    pos++;
                    break;
                case Move.ShiftSingle:
                    symbols.Add(Code128Patterns.Shift);
                    // Shifted char is interpreted in the other set (A <-> B).
                    symbols.Add(set == SetA ? ValueInSetB(c) : ValueInSetA(c));
                    pos++;
                    break;
                case Move.Pair:
                    symbols.Add((c - '0') * 10 + (s[pos + 1] - '0'));
                    pos += 2;
                    break;
                default:
                    throw new InvalidOperationException("Code 128 planner reached an impossible state.");
            }
        }

        return symbols;
    }

    /// <summary>
    /// Cost of emitting the next symbol(s) for s[i] while <paramref name="set"/> is active
    /// (no switch before it), plus the optimal cost of the remainder.
    /// </summary>
    private static long CostOfEncoding(string s, long[,] f, int i, int set, out Move move)
    {
        char c = s[i];
        switch (set)
        {
            case SetA:
                if (c <= 95)
                {
                    move = Move.Single;
                    return OneSymbol + f[i + 1, SetA];
                }
                // 96..127 only exist in B: SHIFT for this one char.
                move = Move.ShiftSingle;
                return 2 * OneSymbol + OneSpecial + f[i + 1, SetA];

            case SetB:
                if (c >= 32)
                {
                    move = Move.Single;
                    return OneSymbol + f[i + 1, SetB];
                }
                // 0..31 only exist in A: SHIFT for this one char.
                move = Move.ShiftSingle;
                return 2 * OneSymbol + OneSpecial + f[i + 1, SetB];

            default: // SetC: digit pairs only
                if (i + 1 < s.Length && IsDigit(c) && IsDigit(s[i + 1]))
                {
                    move = Move.Pair;
                    return OneSymbol + f[i + 2, SetC];
                }
                move = Move.None;
                return Impossible;
        }
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    // Set A: ' '..'_' (32..95) -> 0..63 ; NUL..US (0..31) -> 64..95.
    private static int ValueInSetA(char c) => c < 32 ? c + 64 : c - 32;

    // Set B: ' '..DEL (32..127) -> 0..95.
    private static int ValueInSetB(char c) => c - 32;

    // ---- modules -----------------------------------------------------------------------------

    private static bool[] BuildModules(List<int> symbols)
    {
        int total = 11 * (symbols.Count - 1) + 13;
        var modules = new bool[total];
        int pos = 0;
        foreach (int symbol in symbols)
        {
            bool bar = true;   // every symbol starts with a bar
            foreach (int width in Code128Patterns.Widths[symbol])
            {
                for (int k = 0; k < width; k++)
                {
                    modules[pos++] = bar;
                }
                bar = !bar;
            }
        }
        if (pos != total)
        {
            throw new InvalidOperationException("Code 128 pattern table is inconsistent.");
        }
        return modules;
    }
}
