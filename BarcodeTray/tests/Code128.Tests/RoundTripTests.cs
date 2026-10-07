using Code128.Tests.Support;

namespace Code128.Tests;

/// <summary>
/// Encode -> raster (10-module quiet zones, 3 px per module) -> ZXing.Net Code128Reader.
/// </summary>
public class RoundTripTests
{
    private static void AssertRoundTrip(string text, int scale = ZXingOracle.DefaultScale)
    {
        var barcode = Code128Encoder.Encode(text);
        Assert.Equal(11 * (barcode.Symbols.Count - 1) + 13, barcode.Modules.Count);

        // 1) symbol level, independent state machine (also validates checksum/start/stop)
        var decoded = SymbolInterpreter.Decode(barcode.Symbols);
        Assert.True(decoded.Error == null, $"\"{ReferenceEncoder.Escape(text)}\": symbol check failed: {decoded.Error}");
        Assert.Equal(text, decoded.Text);
        Assert.Equal(0, decoded.FunctionSymbols);

        // 2) pixel level, independent decoder
        string? zx = ZXingOracle.RoundTrip(barcode, scale);
        Assert.True(zx != null, $"ZXing could not decode \"{ReferenceEncoder.Escape(text)}\" (symbols {string.Join(",", barcode.Symbols)})");
        Assert.True(
            string.Equals(text, zx, StringComparison.Ordinal),
            $"ZXing decoded \"{ReferenceEncoder.Escape(zx!)}\" instead of \"{ReferenceEncoder.Escape(text)}\" (symbols {string.Join(",", barcode.Symbols)})");
    }

    private const string Mixed60A = "The Quick Brown Fox 1234567890 jumps/over: the lazy_dog -99!";
    private const string Mixed60B = "Mixed: abc DEF 0123456789 \t tab, line\nbreak, 000111 {}[]()<>";

    public static IEnumerable<object[]> HandPicked()
    {
        string[] samples =
        {
            // single chars
            "A", "a", "Z", "0", "9", " ", "~", "-", "#",
            // digits: 2 .. 8, odd and even
            "12", "00", "99", "123", "1234", "12345", "123456", "1234567", "12345678",
            "000000", "0000000", "99999999",
            // long digit runs, odd and even
            "123456789012345678901", "1234567890123456789012",
            "1234567890123456789012345678901234567890", "12345678901234567890123456789012345678901",
            new string('0', 50), new string('7', 51),
            // typical label content
            "ABC-123", "ABC-1234", "REF 2024-000123", "SN: 0123456789", "ORDER#1042/B",
            "https://example.com/a?b=c&d=e", "user@example.com", "Hello, World!",
            // digits then letters / letters then digits
            "12ab", "123ab", "1234ab", "12345ab", "123456abcdef", "1234567890ABCDEF",
            "ab12", "ab123", "ab1234", "ab12345", "abcdef123456", "ABCDEF1234567890",
            "A1B2C3D4", "1A2B3C4D", "12A34B56C78", "123A456B789",
            // spaces
            "  ", "a b c", " leading", "trailing ", "a  b   c    d", "12 34 56 78",
            // case / control mixes that need SHIFT or switches to A
            "ab\tcd", "ABC\nDEF", "line1\r\nline2", "\u0001\u0002a\u0003\u0004", "\u0001A", "\u0001a",
            "a\u0001a\u0001a\u0001a", "A\u0001A\u0001A", "\0", "\0\0\0", "x\0y", "\u001f", "\u007f", "\u007f\u007f",
            "\u0001ABab", "ab\u0001AB", "lower\u001bUPPER\u007fmixed",
            "1234\t5678", "12\t34", "12\tab34", "\t1234", "1234\t",
            // 60-char mixed string
            Mixed60A,
            Mixed60B,
        };
        foreach (string s in samples)
        {
            yield return new object[] { s };
        }
    }

    [Theory]
    [MemberData(nameof(HandPicked))]
    public void Hand_picked_strings_round_trip_through_ZXing(string text) => AssertRoundTrip(text);

    [Fact]
    public void The_two_mixed_samples_really_have_60_chars()
    {
        Assert.Equal(60, Mixed60A.Length);
        Assert.Equal(60, Mixed60B.Length);
    }

    [Fact]
    public void Every_printable_ASCII_char_individually_round_trips()
    {
        for (int c = 32; c <= 126; c++)
        {
            AssertRoundTrip(((char)c).ToString());
        }
    }

    [Fact]
    public void Every_ASCII_char_0_to_127_individually_round_trips()
    {
        for (int c = 0; c <= 127; c++)
        {
            AssertRoundTrip(((char)c).ToString());
        }
    }

    [Fact]
    public void All_128_ASCII_chars_in_one_string_round_trip_ascending_descending_and_interleaved()
    {
        string asc = string.Concat(Enumerable.Range(0, 128).Select(i => (char)i));
        AssertRoundTrip(asc);
        AssertRoundTrip(new string(asc.Reverse().ToArray()));
        // alternate low controls and high lowercase region to force many shifts / switches
        AssertRoundTrip(string.Concat(Enumerable.Range(0, 32).Select(i => $"{(char)i}{(char)(96 + i)}")));
    }

    [Fact]
    public void Every_two_digit_pair_round_trips_alone_and_inside_text()
    {
        for (int v = 0; v < 100; v++)
        {
            string pair = v.ToString("D2");
            AssertRoundTrip(pair);
            AssertRoundTrip(pair + pair);
            AssertRoundTrip("x" + pair + pair + "y");
        }
        // all 100 pairs back to back (200 digits) in set C
        AssertRoundTrip(string.Concat(Enumerable.Range(0, 100).Select(v => v.ToString("D2"))));
    }

    [Fact]
    public void Digit_runs_of_every_length_1_to_30_alone_and_between_letters_round_trip()
    {
        for (int len = 1; len <= 30; len++)
        {
            string digits = string.Concat(Enumerable.Range(0, len).Select(i => (char)('0' + (i * 7 + len) % 10)));
            AssertRoundTrip(digits);
            AssertRoundTrip("ab" + digits);
            AssertRoundTrip(digits + "ab");
            AssertRoundTrip("ab" + digits + "cd");
            AssertRoundTrip("\u0001" + digits + "\u0002");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void Round_trip_works_for_several_module_scales(int scale)
    {
        foreach (string s in new[] { "A", "123456", "Hello, World!", "ab\tcd", "12345abc", "\u0001\u0002a" })
        {
            AssertRoundTrip(s, scale);
        }
    }

    [Fact]
    public void Oracle_sanity_corrupting_a_module_makes_ZXing_fail_or_differ()
    {
        // Guards against an oracle that "decodes" anything: damage the bars and require a miss.
        foreach (string text in new[] { "Hello, World!", "123456", "ab\tcd" })
        {
            var b = Code128Encoder.Encode(text);
            string? ok = ZXingOracle.RoundTrip(b);
            Assert.Equal(text, ok);

            // flip a module in the middle of the first data symbol, then in the checksum area
            foreach (int idx in new[] { 11 + 3, b.Modules.Count - 13 - 11 + 4 })
            {
                var damaged = b.Modules.ToArray();
                damaged[idx] = !damaged[idx];
                string? decoded = ZXingOracle.Decode(ZXingOracle.Rasterize(damaged));
                Assert.NotEqual(text, decoded);
            }
        }
    }

    [Fact]
    public void Oracle_sanity_wrong_text_is_not_reported()
    {
        string? decoded = ZXingOracle.RoundTrip(Code128Encoder.Encode("ABC-123"));
        Assert.Equal("ABC-123", decoded);
        Assert.NotEqual("ABC-124", decoded);
    }
}
