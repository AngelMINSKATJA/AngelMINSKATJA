using System.Collections.Concurrent;
using Code128.Tests.Support;

namespace Code128.Tests;

public class FuzzTests
{
    private const int Iterations = 25_000;
    private const int Seed = 128_128;

    /// <summary>Random ASCII strings (length 1..40), biased towards digit runs.</summary>
    internal static List<string> Generate(int count, int seed)
    {
        var rng = new Random(seed);
        var list = new List<string>(count);
        var sb = new System.Text.StringBuilder();
        for (int n = 0; n < count; n++)
        {
            int target = rng.Next(1, 41);
            sb.Clear();
            int style = rng.Next(100);
            if (style < 15)
            {
                // pure digits
                while (sb.Length < target) sb.Append((char)('0' + rng.Next(10)));
            }
            else if (style < 25)
            {
                // fully random ASCII 0..127
                while (sb.Length < target) sb.Append((char)rng.Next(128));
            }
            else
            {
                // segments of different kinds, digit runs preferred
                while (sb.Length < target)
                {
                    int kind = rng.Next(100);
                    if (kind < 45)
                    {
                        int run = rng.Next(1, 13);
                        for (int i = 0; i < run; i++) sb.Append((char)('0' + rng.Next(10)));
                    }
                    else if (kind < 60)
                    {
                        int run = rng.Next(1, 6);
                        for (int i = 0; i < run; i++) sb.Append((char)rng.Next('A', 'Z' + 1));
                    }
                    else if (kind < 72)
                    {
                        int run = rng.Next(1, 6);
                        for (int i = 0; i < run; i++) sb.Append((char)rng.Next('a', 'z' + 1));
                    }
                    else if (kind < 80)
                    {
                        int run = rng.Next(1, 4);
                        for (int i = 0; i < run; i++) sb.Append((char)rng.Next(32, 48));   // space and punctuation
                    }
                    else if (kind < 90)
                    {
                        int run = rng.Next(1, 3);
                        for (int i = 0; i < run; i++) sb.Append((char)rng.Next(0, 32));    // controls (set A only)
                    }
                    else
                    {
                        int run = rng.Next(1, 3);
                        for (int i = 0; i < run; i++) sb.Append((char)rng.Next(96, 128));  // 96..127 (set B only)
                    }
                }
            }
            if (sb.Length > target) sb.Length = target;
            list.Add(sb.ToString());
        }
        return list;
    }

    [Fact]
    public void Generator_is_deterministic_and_covers_the_intended_space()
    {
        var a = Generate(2000, Seed);
        var b = Generate(2000, Seed);
        Assert.Equal(a, b);
        Assert.True(a.All(s => s.Length is >= 1 and <= 40));
        Assert.True(a.All(s => s.All(c => c < 128)));
        Assert.True(a.Count(s => s.All(char.IsAsciiDigit)) > 150);                  // digit-only strings
        Assert.True(a.Count(s => s.Any(c => c < 32)) > 200);                         // control chars
        Assert.True(a.Count(s => s.Any(c => c >= 96)) > 200);                        // 96..127
        Assert.True(a.Count(s => System.Text.RegularExpressions.Regex.IsMatch(s, "[0-9]{6,}")) > 300);
    }

    [Fact]
    public void Fuzz_25000_random_strings_decode_with_ZXing_and_are_optimal()
    {
        var inputs = Generate(Iterations, Seed);
        var failures = new ConcurrentBag<(int Index, string Message)>();

        Parallel.For(0, inputs.Count, i =>
        {
            string text = inputs[i];
            try
            {
                var b = Code128Encoder.Encode(text);
                var sym = b.Symbols;

                if (b.Text != text) throw new Exception("Text property differs");
                if (sym.Count < 4) throw new Exception("too few symbols");
                if (sym[0] < 103 || sym[0] > 105) throw new Exception($"first symbol {sym[0]} is not a start symbol");
                if (sym[^1] != 106) throw new Exception($"last symbol {sym[^1]} is not stop");
                for (int k = 1; k < sym.Count - 1; k++)
                {
                    if (sym[k] < 0 || sym[k] > 105) throw new Exception($"symbol {sym[k]} out of range at {k}");
                }
                if (b.Modules.Count != 11 * (sym.Count - 1) + 13) throw new Exception("module count formula violated");
                if (!b.Modules[0] || !b.Modules[^1]) throw new Exception("barcode must begin and end with a bar");

                // independent checksum calculation
                long sum = sym[0];
                for (int k = 1; k < sym.Count - 2; k++) sum += (long)k * sym[k];
                if (sym[^2] != (int)(sum % 103)) throw new Exception($"bad checksum {sym[^2]}, expected {sum % 103}");

                // symbol-level decode + optimality vs the naive reference
                string? problem = OptimalityTests.CheckOne(text);
                if (problem != null) throw new Exception(problem);

                // pixel-level decode with ZXing
                string? zx = ZXingOracle.RoundTrip(b);
                if (zx == null) throw new Exception("ZXing could not decode");
                if (!string.Equals(zx, text, StringComparison.Ordinal))
                    throw new Exception($"ZXing decoded \"{ReferenceEncoder.Escape(zx)}\"");
            }
            catch (Exception ex)
            {
                failures.Add((i, $"#{i} \"{ReferenceEncoder.Escape(text)}\": {ex.Message}"));
            }
        });

        var ordered = failures.OrderBy(f => f.Index).Select(f => f.Message).ToList();
        Assert.True(ordered.Count == 0, ordered.Count + " failures, first ones:\n" + string.Join("\n", ordered.Take(10)));
    }

    [Fact]
    public void Fuzz_encoding_is_stable_across_repeated_calls()
    {
        foreach (string text in Generate(2000, Seed + 1))
        {
            var a = Code128Encoder.Encode(text);
            var b = Code128Encoder.Encode(text);
            Assert.True(a.Symbols.SequenceEqual(b.Symbols));
            Assert.True(a.Modules.SequenceEqual(b.Modules));
        }
    }

    [Fact]
    public void Fuzz_random_non_ASCII_text_is_rejected()
    {
        var rng = new Random(Seed + 2);
        for (int n = 0; n < 2000; n++)
        {
            int len = rng.Next(1, 20);
            var chars = new char[len];
            for (int i = 0; i < len; i++) chars[i] = (char)rng.Next(0, 128);
            int bad = rng.Next(len);
            chars[bad] = (char)rng.Next(128, 0x3000);
            string text = new string(chars);
            Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(text));
            Assert.False(Code128Encoder.IsEncodable(text, out char first));
            Assert.True(first >= 128);
            // first offender reported must be the first one in the string
            Assert.Equal(text.First(c => c >= 128), first);
        }
    }
}
