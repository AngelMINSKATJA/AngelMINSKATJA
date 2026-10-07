using System.Reflection;
using ZXing.OneD;

namespace Code128.Tests;

public class PatternTableTests
{
    private static IReadOnlyList<int> W(int value) => Code128Patterns.Widths[value];

    [Fact]
    public void Table_has_107_entries()
    {
        Assert.Equal(107, Code128Patterns.Count);
        Assert.Equal(107, Code128Patterns.Widths.Count);
    }

    [Fact]
    public void Every_data_and_start_symbol_has_six_elements_summing_to_11()
    {
        for (int v = 0; v <= 105; v++)
        {
            var w = W(v);
            Assert.True(w.Count == 6, $"symbol {v}: {w.Count} elements instead of 6");
            Assert.True(w.Sum() == 11, $"symbol {v}: widths sum to {w.Sum()} instead of 11");
        }
    }

    [Fact]
    public void Every_element_is_between_1_and_4_modules()
    {
        for (int v = 0; v <= 106; v++)
        {
            foreach (int width in W(v))
            {
                Assert.InRange(width, 1, 4);
            }
        }
    }

    [Fact]
    public void Stop_symbol_has_seven_elements_summing_to_13_and_is_2331112()
    {
        var stop = W(Code128Encoder.StopSymbol);
        Assert.Equal(7, stop.Count);
        Assert.Equal(13, stop.Sum());
        Assert.Equal(new[] { 2, 3, 3, 1, 1, 1, 2 }, stop.ToArray());
    }

    [Fact]
    public void All_107_patterns_are_distinct()
    {
        var seen = new Dictionary<string, int>();
        for (int v = 0; v <= 106; v++)
        {
            string key = string.Concat(W(v));
            Assert.True(seen.TryAdd(key, v), $"symbols {seen.GetValueOrDefault(key)} and {v} share pattern {key}");
        }
    }

    [Fact]
    public void Bars_of_every_symbol_add_up_to_an_even_module_count()
    {
        // Code 128 symbol characters have 3 bars + 3 spaces whose bar widths total an even number
        // (the parity property that makes the symbology self-checking). The stop pattern has 4 bars.
        for (int v = 0; v <= 105; v++)
        {
            var w = W(v);
            int bars = w[0] + w[2] + w[4];
            Assert.True(bars % 2 == 0, $"symbol {v}: bar widths total {bars} (odd)");
        }
        var stop = W(106);
        Assert.Equal(0, (stop[0] + stop[2] + stop[4] + stop[6]) % 2);
    }

    [Fact]
    public void Special_symbols_have_the_published_patterns()
    {
        // Values taken from the symbology tables: Start A/B/C, FNC1, Shift, Code A/B/C, FNC2, FNC3.
        Assert.Equal("211412", string.Concat(W(103)));
        Assert.Equal("211214", string.Concat(W(104)));
        Assert.Equal("211232", string.Concat(W(105)));
        Assert.Equal("411131", string.Concat(W(Code128Patterns.FNC1)));
        Assert.Equal("411311", string.Concat(W(Code128Patterns.Shift)));
        Assert.Equal("113141", string.Concat(W(Code128Patterns.CodeC)));
        Assert.Equal("114131", string.Concat(W(Code128Patterns.CodeB)));
        Assert.Equal("311141", string.Concat(W(Code128Patterns.CodeA)));
        Assert.Equal("411113", string.Concat(W(Code128Patterns.FNC2)));
        Assert.Equal("114311", string.Concat(W(Code128Patterns.FNC3)));
        // space and a few anchors of the data range
        Assert.Equal("212222", string.Concat(W(0)));   // ' '
        Assert.Equal("111422", string.Concat(W(64)));  // 'at sign' in set B, NUL in set A
        Assert.Equal("114113", string.Concat(W(95)));
    }

    [Fact]
    public void Symbol_constants_are_the_standard_values()
    {
        Assert.Equal(96, Code128Patterns.FNC3);
        Assert.Equal(97, Code128Patterns.FNC2);
        Assert.Equal(98, Code128Patterns.Shift);
        Assert.Equal(99, Code128Patterns.CodeC);
        Assert.Equal(100, Code128Patterns.CodeB);
        Assert.Equal(101, Code128Patterns.CodeA);
        Assert.Equal(102, Code128Patterns.FNC1);
        Assert.Equal(103, Code128Patterns.StartA);
        Assert.Equal(104, Code128Patterns.StartB);
        Assert.Equal(105, Code128Patterns.StartC);
        Assert.Equal(106, Code128Patterns.Stop);
        Assert.Equal(Code128Encoder.StopSymbol, Code128Patterns.Stop);
        Assert.Equal(10, Code128Encoder.QuietZoneModules);
    }

    /// <summary>
    /// Cross-check against ZXing.Net's own table (read by reflection from the pinned 0.16.9
    /// package): an independent source for all 107 entries, including the ones the encoder never
    /// emits (FNC1/2/3).
    /// </summary>
    [Fact]
    public void Table_matches_the_table_inside_ZXing()
    {
        FieldInfo? field = typeof(Code128Reader).GetField(
            "CODE_PATTERNS", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(field != null, "ZXing.Net 0.16.9 Code128Reader.CODE_PATTERNS not found (package changed?)");
        var theirs = (int[][])field!.GetValue(null)!;
        Assert.Equal(107, theirs.Length);
        for (int v = 0; v <= 106; v++)
        {
            Assert.True(
                theirs[v].SequenceEqual(W(v)),
                $"symbol {v}: ours {string.Concat(W(v))} vs ZXing {string.Concat(theirs[v])}");
        }
    }

    [Fact]
    public void Modules_are_exactly_the_concatenated_symbol_patterns()
    {
        // Re-derive the run lengths of every 11-module chunk (13 for Stop) and compare with the table.
        var texts = new List<string> { "A", "12", "1234", "Hello, World!", "ab\tcd", "\u007f", "12345abc\u0001" };
        texts.AddRange(FuzzTests.Generate(400, 99));
        foreach (string text in texts)
        {
            var b = Code128Encoder.Encode(text);
            int pos = 0;
            for (int k = 0; k < b.Symbols.Count; k++)
            {
                int size = k == b.Symbols.Count - 1 ? 13 : 11;
                var runs = new List<int>();
                bool current = true;     // every symbol starts with a bar
                int len = 0;
                for (int m = pos; m < pos + size; m++)
                {
                    if (b.Modules[m] == current) { len++; }
                    else { runs.Add(len); current = !current; len = 1; }
                }
                runs.Add(len);
                Assert.True(
                    runs.SequenceEqual(W(b.Symbols[k])),
                    $"\"{Support.ReferenceEncoder.Escape(text)}\" symbol #{k} (value {b.Symbols[k]}): runs {string.Join("", runs)} vs table {string.Concat(W(b.Symbols[k]))}");
                pos += size;
            }
            Assert.Equal(b.Modules.Count, pos);
        }
    }

    [Fact]
    public void Barcode_modules_start_with_a_bar_end_with_the_stop_bar_and_have_max_run_4()
    {
        foreach (string text in new[] { "A", "12", "1234", "Hello, World!", "ab\tcd", "\u007f" })
        {
            var b = Code128Encoder.Encode(text);
            Assert.True(b.Modules[0]);            // start symbol begins with a bar
            Assert.True(b.Modules[^1]);           // stop pattern ends with its 2-module bar
            Assert.True(b.Modules[^2]);
            Assert.False(b.Modules[^3]);          // ... preceded by a 1-module space
            int run = 1, longest = 1;
            for (int i = 1; i < b.Modules.Count; i++)
            {
                run = b.Modules[i] == b.Modules[i - 1] ? run + 1 : 1;
                longest = Math.Max(longest, run);
            }
            Assert.True(longest <= 4, $"{text}: run of {longest} equal modules");
        }
    }
}
