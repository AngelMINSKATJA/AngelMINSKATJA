using Code128.Tests.Support;

namespace Code128.Tests;

/// <summary>
/// Cross-checks the symbol count (and number of switches/shifts) of the encoder against the
/// naive uniform-cost search of ReferenceEncoder.
/// </summary>
public class OptimalityTests
{
    internal static string? CheckOne(string text)
    {
        var barcode = Code128Encoder.Encode(text);
        var decoded = SymbolInterpreter.Decode(barcode.Symbols);
        if (decoded.Error != null)
        {
            return $"\"{ReferenceEncoder.Escape(text)}\": {decoded.Error} [{string.Join(",", barcode.Symbols)}]";
        }
        if (!string.Equals(decoded.Text, text, StringComparison.Ordinal))
        {
            return $"\"{ReferenceEncoder.Escape(text)}\": symbols decode to \"{ReferenceEncoder.Escape(decoded.Text!)}\"";
        }
        if (decoded.FunctionSymbols != 0)
        {
            return $"\"{ReferenceEncoder.Escape(text)}\": function symbols produced";
        }
        var best = ReferenceEncoder.Minimum(text);
        int symbolsBeforeChecksum = barcode.Symbols.Count - 2;
        int specials = decoded.Switches + decoded.Shifts;
        if (symbolsBeforeChecksum != best.Symbols)
        {
            return $"\"{ReferenceEncoder.Escape(text)}\": {symbolsBeforeChecksum} symbols, optimum is {best.Symbols} [{string.Join(",", barcode.Symbols)}]";
        }
        if (specials != best.Specials)
        {
            return $"\"{ReferenceEncoder.Escape(text)}\": {specials} switches/shifts, optimum is {best.Specials} [{string.Join(",", barcode.Symbols)}]";
        }
        return null;
    }

    [Fact]
    public void Reference_search_agrees_with_hand_computed_lengths()
    {
        // sanity of the reference itself (symbols before checksum, switches+shifts)
        Assert.Equal(new ReferenceEncoder.Optimum(2, 0), ReferenceEncoder.Minimum("A"));
        Assert.Equal(new ReferenceEncoder.Optimum(2, 0), ReferenceEncoder.Minimum("12"));
        Assert.Equal(new ReferenceEncoder.Optimum(3, 0), ReferenceEncoder.Minimum("1234"));
        Assert.Equal(new ReferenceEncoder.Optimum(4, 0), ReferenceEncoder.Minimum("123"));
        Assert.Equal(new ReferenceEncoder.Optimum(5, 1), ReferenceEncoder.Minimum("12345"));
        Assert.Equal(new ReferenceEncoder.Optimum(6, 1), ReferenceEncoder.Minimum("AB1234"));
        Assert.Equal(new ReferenceEncoder.Optimum(7, 1), ReferenceEncoder.Minimum("ab\tcd"));
        Assert.Equal(new ReferenceEncoder.Optimum(5, 0), ReferenceEncoder.Minimum("12AB"));
    }

    [Fact]
    public void Every_one_char_and_two_char_ASCII_string_is_optimal()
    {
        var failures = new List<string>();
        for (int a = 0; a < 128; a++)
        {
            string one = ((char)a).ToString();
            string? f = CheckOne(one);
            if (f != null) failures.Add(f);
            for (int b = 0; b < 128; b++)
            {
                f = CheckOne(one + (char)b);
                if (f != null) failures.Add(f);
            }
        }
        Assert.True(failures.Count == 0, failures.Count + " failures, e.g.\n" + string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void Exhaustive_small_alphabet_up_to_length_6_is_optimal()
    {
        // digits (pairing!), a char valid in A and B, a B-only char, an A-only char, DEL (B-only).
        char[] alphabet = { '0', '7', 'A', 'a', '\u0001', '\u007f' };
        var failures = new List<string>();
        var current = new List<string> { "" };
        int checkedCount = 0;
        for (int length = 1; length <= 6; length++)
        {
            var next = new List<string>(current.Count * alphabet.Length);
            foreach (string prefix in current)
            {
                foreach (char c in alphabet)
                {
                    next.Add(prefix + c);
                }
            }
            current = next;
            foreach (string s in current)
            {
                string? f = CheckOne(s);
                checkedCount++;
                if (f != null) failures.Add(f);
            }
        }
        Assert.Equal(6 + 36 + 216 + 1296 + 7776 + 46656, checkedCount);
        Assert.True(failures.Count == 0, failures.Count + " failures, e.g.\n" + string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void Digit_heavy_strings_are_optimal_around_every_switch_threshold()
    {
        var failures = new List<string>();
        string[] contexts = { "", "a", "A", "\u0001", "ab", "\u0001\u0002" };
        foreach (string pre in contexts)
        {
            foreach (string post in contexts)
            {
                for (int digits = 0; digits <= 14; digits++)
                {
                    string text = pre + new string('5', digits) + post;
                    if (text.Length == 0) continue;
                    string? f = CheckOne(text);
                    if (f != null) failures.Add(f);
                }
            }
        }
        Assert.True(failures.Count == 0, failures.Count + " failures, e.g.\n" + string.Join("\n", failures.Take(10)));
    }
}
