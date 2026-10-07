namespace Code128.Tests.Support;

/// <summary>
/// Deliberately naive reference "encoder" used to cross-check the optimality of the real one.
///
/// It does NOT know the encoder's set-selection logic at all. Instead it explores the *decoder*
/// (<see cref="SymbolInterpreter"/>): a node is (chars already produced, decoder state) and an edge
/// is "append ANY symbol value 0..102 / any start symbol", allowed only if the characters that the
/// decoder emits for it are exactly the next characters of the text. A uniform-cost search
/// (Dijkstra) from the empty prefix gives the true minimum.
///
/// Cost of a symbol = SymbolCost (+1 when it is a code switch or SHIFT), so the optimum is
/// lexicographically (fewest symbols, then fewest switches/shifts).
/// </summary>
internal static class ReferenceEncoder
{
    private const long SymbolCost = 1_000_000;

    internal readonly record struct Optimum(int Symbols, int Specials);

    /// <summary>Minimum over all valid symbol streams of (symbols before checksum, switches+shifts).</summary>
    internal static Optimum Minimum(string text)
    {
        int n = text.Length;
        // node = (pos, set, shiftActive); dist keyed by pos * 6 + set * 2 + shift
        var dist = new Dictionary<long, long>();
        var queue = new PriorityQueue<(int Pos, SymbolInterpreter.State State), long>();

        static long Key(int pos, SymbolInterpreter.State s)
            => (long)pos * 6 + (int)s.Set * 2 + (s.ShiftActive ? 1 : 0);

        for (int start = 103; start <= 105; start++)
        {
            var st = SymbolInterpreter.StartState(start);
            long k = Key(0, st);
            dist[k] = SymbolCost;
            queue.Enqueue((0, st), SymbolCost);
        }

        while (queue.TryDequeue(out var node, out long d))
        {
            if (dist[Key(node.Pos, node.State)] != d)
            {
                continue;   // stale queue entry
            }
            if (node.Pos == n && !node.State.ShiftActive)
            {
                return new Optimum((int)(d / SymbolCost), (int)(d % SymbolCost));
            }
            if (node.Pos >= n)
            {
                continue;   // everything consumed but a SHIFT is pending: dead end
            }

            for (int v = 0; v <= 102; v++)
            {
                var r = SymbolInterpreter.Step(node.State, v);
                if (r.Kind == SymbolInterpreter.Kind.Invalid || r.Kind == SymbolInterpreter.Kind.Function)
                {
                    continue;
                }
                int len = r.Emitted.Length;
                if (len > 0 && string.CompareOrdinal(text, node.Pos, r.Emitted, 0, len) != 0)
                {
                    continue;
                }
                if (node.Pos + len > n)
                {
                    continue;
                }
                long cost = SymbolCost + (r.Kind is SymbolInterpreter.Kind.Switch or SymbolInterpreter.Kind.Shift ? 1 : 0);
                long nd = d + cost;
                int np = node.Pos + len;
                long key = Key(np, r.Next);
                if (!dist.TryGetValue(key, out long old) || nd < old)
                {
                    dist[key] = nd;
                    queue.Enqueue((np, r.Next), nd);
                }
            }
        }

        throw new InvalidOperationException("reference search found no encoding for: " + Escape(text));
    }

    /// <summary>Printable form of a test string for assertion messages.</summary>
    internal static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (c >= 32 && c < 127 && c != '\\')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append("\\x").Append(((int)c).ToString("X2"));
            }
        }
        return sb.ToString();
    }
}
