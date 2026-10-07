namespace Code128.Tests;

public class ValidationTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("ABC-123")]
    [InlineData(" ")]
    [InlineData("~")]
    [InlineData("\u007f")]          // DEL is ASCII 127
    [InlineData("\0")]              // NUL is ASCII 0
    [InlineData("tab\there\r\n")]
    public void IsEncodable_accepts_ascii(string text)
    {
        Assert.True(Code128Encoder.IsEncodable(text, out char bad));
        Assert.Equal('\0', bad);
    }

    [Fact]
    public void IsEncodable_accepts_all_128_ascii_chars()
    {
        string all = string.Concat(Enumerable.Range(0, 128).Select(i => (char)i));
        Assert.True(Code128Encoder.IsEncodable(all, out _));
    }

    [Theory]
    [InlineData("é", 'é')]
    [InlineData("€", '€')]
    [InlineData("\u0080", '\u0080')]
    [InlineData("abcé", 'é')]
    [InlineData("a€bé", '€')]            // the FIRST offender is reported
    [InlineData("ok \u00A0 ok", '\u00A0')]   // no-break space
    [InlineData("日本", '日')]
    public void IsEncodable_rejects_non_ascii_and_reports_the_first_offender(string text, char expected)
    {
        Assert.False(Code128Encoder.IsEncodable(text, out char bad));
        Assert.Equal(expected, bad);
    }

    [Fact]
    public void IsEncodable_handles_null_and_empty_without_throwing()
    {
        Assert.False(Code128Encoder.IsEncodable(null!, out char bad));
        Assert.Equal('\0', bad);
        // empty text has no offending char (Encode itself still rejects it)
        Assert.True(Code128Encoder.IsEncodable("", out _));
    }

    [Fact]
    public void IsEncodable_rejects_surrogate_pairs()
    {
        Assert.False(Code128Encoder.IsEncodable("a\U0001F600b", out char bad));
        Assert.True(char.IsHighSurrogate(bad));
    }

    [Fact]
    public void Encode_null_throws_ArgumentException()
        => Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(null!));

    [Fact]
    public void Encode_empty_throws_ArgumentException()
        => Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(""));

    [Theory]
    [InlineData("é")]
    [InlineData("€")]
    [InlineData("\u0080")]
    [InlineData("ÿ")]
    [InlineData("abc\u0080def")]
    [InlineData("12é34")]
    [InlineData("\U0001F600")]
    public void Encode_non_ascii_throws_ArgumentException(string text)
        => Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(text));

    [Fact]
    public void Encode_accepts_whitespace_only_text()
    {
        // The library does not trim: " " is a perfectly valid Code 128 payload (the app trims).
        Assert.Equal(new[] { 104, 0, 1, 106 }, Code128Encoder.Encode(" ").Symbols.ToArray());
    }

    [Fact]
    public void Encode_handles_long_input()
    {
        string text = new string('9', 1001) + "x" + new string('7', 999);
        var b = Code128Encoder.Encode(text);
        Assert.Equal(11 * (b.Symbols.Count - 1) + 13, b.Modules.Count);
        var d = Support.SymbolInterpreter.Decode(b.Symbols);
        Assert.Null(d.Error);
        Assert.Equal(text, d.Text);
        // 1001 digits (odd): 'x' ... : lone digit first then pairs: 1 + 500 pairs, then 'x', then 999 digits
        Assert.True(b.Symbols.Count < text.Length / 2 + 20);
    }
}
