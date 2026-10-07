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

    /// <summary>
    /// True when every char of <paramref name="text"/> is encodable (ASCII 0..127).
    /// When false, <paramref name="firstInvalid"/> is the first offending char.
    /// </summary>
    public static bool IsEncodable(string text, out char firstInvalid)
        => throw new NotImplementedException();

    /// <summary>
    /// Encodes <paramref name="text"/> with the minimum number of symbols (code sets A/B/C,
    /// switches and shifts chosen optimally). Throws <see cref="ArgumentException"/> if the text
    /// is null, empty or contains a char outside ASCII 0..127.
    /// </summary>
    public static Code128Barcode Encode(string text)
        => throw new NotImplementedException();
}
