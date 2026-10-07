namespace Code128.Tests;

/// <summary>
/// Hand-computed symbol sequences. Checksum = (start + sum(position * value)) mod 103.
/// Set B value = ASCII - 32; set A: control char c -> c + 64; set C: two digits -> their value.
/// </summary>
public class KnownVectorTests
{
    private static void AssertSymbols(string text, params int[] expected)
    {
        var actual = Code128Encoder.Encode(text).Symbols;
        Assert.True(
            expected.SequenceEqual(actual),
            $"\"{Support.ReferenceEncoder.Escape(text)}\": expected [{string.Join(",", expected)}] but got [{string.Join(",", actual)}]");
    }

    [Fact]
    public void Single_letter_A_is_start_B_then_value_33()
    {
        // 'A' = 65 - 32 = 33 ; checksum = (104 + 1*33) mod 103 = 137 mod 103 = 34
        AssertSymbols("A", 104, 33, 34, 106);
    }

    [Fact]
    public void Single_space_is_start_B_value_0()
    {
        // checksum = 104 mod 103 = 1
        AssertSymbols(" ", 104, 0, 1, 106);
    }

    [Fact]
    public void Plain_text_uses_set_B_only()
    {
        // H=40 e=69 l=76 l=76 o=79 ; 104 + 40 + 2*69 + 3*76 + 4*76 + 5*79 = 1209 ; 1209 mod 103 = 76
        AssertSymbols("Hello", 104, 40, 69, 76, 76, 79, 76, 106);
    }

    [Fact]
    public void Two_digits_use_set_C_from_the_start()
    {
        // 105 + 1*12 = 117 ; 117 mod 103 = 14
        AssertSymbols("12", 105, 12, 14, 106);
        // 105 + 0 = 105 ; mod 103 = 2
        AssertSymbols("00", 105, 0, 2, 106);
    }

    [Fact]
    public void Four_digits_are_two_pairs_in_set_C()
    {
        // 105 + 12 + 2*34 = 185 ; 185 mod 103 = 82
        AssertSymbols("1234", 105, 12, 34, 82, 106);
    }

    [Fact]
    public void Six_digits_are_three_pairs_in_set_C()
    {
        // 105 + 12 + 68 + 3*56 = 353 ; 353 mod 103 = 44
        AssertSymbols("123456", 105, 12, 34, 56, 44, 106);
    }

    [Fact]
    public void Three_digits_stay_in_set_B_because_C_would_need_a_switch_and_a_lone_digit()
    {
        // B: start + 3 chars = 4 symbols; C: start + pair + Code B + digit = 4 symbols but 1 switch.
        // '1' = 17, '2' = 18, '3' = 19 ; 104 + 17 + 36 + 57 = 214 ; 214 mod 103 = 8
        AssertSymbols("123", 104, 17, 18, 19, 8, 106);
    }

    [Fact]
    public void Odd_digit_run_puts_the_lone_digit_first_then_switches_to_C()
    {
        // Start B, '1', Code C, "23", "45" : 104 + 17 + 2*99 + 3*23 + 4*45 = 568 ; mod 103 = 53
        // (the all-C-first alternative Start C,"12","34",Code B,'5' has the same length and
        //  the same number of switches; the tie is broken in favour of set B)
        AssertSymbols("12345", 104, 17, 99, 23, 45, 53, 106);
    }

    [Fact]
    public void Digits_before_letters_start_in_C_and_switch_to_B()
    {
        // 105 + 12 + 2*34 + 3*100 + 4*65 + 5*66 = 1075 ; mod 103 = 45
        AssertSymbols("1234ab", 105, 12, 34, 100, 65, 66, 45, 106);
        // 105 + 12 + 68 + 300 + 4*33 = 617 ; mod 103 = 102 (checksum value 102 = FNC1's value)
        AssertSymbols("1234A", 105, 12, 34, 100, 33, 102, 106);
    }

    [Fact]
    public void Letters_before_digits_switch_from_B_to_C()
    {
        // 104 + 33 + 2*34 + 3*99 + 4*12 + 5*34 = 720 ; 720 mod 103 = 102
        AssertSymbols("AB1234", 104, 33, 34, 99, 12, 34, 102, 106);
        // "ABC-" + 4 digits: B-only would need 9 symbols, switching to C needs 8.
        // 104 + 33 + 68 + 105 + 4*13 + 5*99 + 6*12 + 7*34 = 1167 ; mod 103 = 34
        AssertSymbols("ABC-1234", 104, 33, 34, 35, 13, 99, 12, 34, 34, 106);
    }

    [Fact]
    public void Short_digit_run_between_letters_is_not_worth_a_switch()
    {
        // "12AB" in B = 5 symbols (+ checksum + stop); via C = 5 symbols too but with a switch.
        // 104 + 17 + 2*18 + 3*33 + 4*34 = 392 ; mod 103 = 83
        AssertSymbols("12AB", 104, 17, 18, 33, 34, 83, 106);
        // "ABC-123" stays in B as well. 104+33+68+105+52+85+108+133 = 688 ; mod 103 = 70
        AssertSymbols("ABC-123", 104, 33, 34, 35, 13, 17, 18, 19, 70, 106);
    }

    [Fact]
    public void Single_control_char_in_lowercase_text_uses_SHIFT_to_set_A()
    {
        // 'a'=65 'b'=66 SHIFT=98 TAB(9)->9+64=73 'c'=67 'd'=68
        // 104 + 65 + 2*66 + 3*98 + 4*73 + 5*67 + 6*68 = 1630 ; mod 103 = 85
        AssertSymbols("ab\tcd", 104, 65, 66, 98, 73, 67, 68, 85, 106);
    }

    [Fact]
    public void Single_lowercase_letter_in_control_text_uses_SHIFT_to_set_B()
    {
        // Start A, SOH(1)->65, STX(2)->66, SHIFT, 'a'->65, ETX->67, EOT->68
        // 103 + 65 + 2*66 + 3*98 + 4*65 + 5*67 + 6*68 = 1597 ; mod 103 = 52
        AssertSymbols("\u0001\u0002a\u0003\u0004", 103, 65, 66, 98, 65, 67, 68, 52, 106);
    }

    [Fact]
    public void Control_char_first_starts_in_set_A()
    {
        // 103 + 65 + 2*33 = 234 ; mod 103 = 28
        AssertSymbols("\u0001A", 103, 65, 33, 28, 106);
        // NUL -> 64 ; 103 + 64 = 167 ; mod 103 = 64
        AssertSymbols("\0", 103, 64, 64, 106);
        // 'A','B' exist in A too, so no shift: 103 + 65 + 2*33 + 3*34 = 336 ; mod 103 = 27
        AssertSymbols("\u0001AB", 103, 65, 33, 34, 27, 106);
    }

    [Fact]
    public void DEL_is_only_in_set_B_and_checksum_96_is_fine()
    {
        // 127 - 32 = 95 ; 104 + 95 = 199 ; mod 103 = 96
        AssertSymbols("\u007f", 104, 95, 96, 106);
    }

    [Fact]
    public void Tie_between_starting_in_A_or_B_prefers_start_B()
    {
        // "\u0001a": Start A,SOH,Code B,'a'  and  Start B,SHIFT,SOH,'a'  have the same length and the
        // same number of special symbols -> the tie goes to set B (Start B = 104).
        // 104 + 98 + 2*65 + 3*65 = 527 ; mod 103 = 12
        AssertSymbols("\u0001a", 104, 98, 65, 65, 12, 106);
    }

    [Fact]
    public void Tie_between_shift_and_code_switch_prefers_the_code_switch_into_B()
    {
        // "\u0001\u0002a" (two controls force Start A): SHIFT+'a' and Code B+'a' cost the same
        // (2 symbols, 1 special) -> Code B (100), i.e. the encoder ends in set B.
        // 103 + 65 + 2*66 + 3*100 + 4*65 = 860 ; mod 103 = 36
        AssertSymbols("\u0001\u0002a", 103, 65, 66, 100, 65, 36, 106);
    }

    [Fact]
    public void Tie_between_switching_early_or_late_prefers_switching_to_B_early()
    {
        // "\u0001\u0002ABab": Start A is forced; Code B before "AB" or before "ab" costs the same,
        // set B is preferred, so the switch happens as early as possible.
        // 103 + 65 + 2*66 + 3*100 + 4*33 + 5*34 + 6*65 + 7*66 = 1754 ; mod 103 = 3
        AssertSymbols("\u0001\u0002ABab", 103, 65, 66, 100, 33, 34, 65, 66, 3, 106);
    }

    [Fact]
    public void Barcode_exposes_the_original_text()
    {
        const string text = "Ref-2024/001";
        var b = Code128Encoder.Encode(text);
        Assert.Equal(text, b.Text);
        Assert.Equal(11 * (b.Symbols.Count - 1) + 13, b.Modules.Count);
    }

    [Fact]
    public void Encoding_is_deterministic_and_results_are_read_only()
    {
        var a = Code128Encoder.Encode("Deterministic 0123456789 \t");
        var b = Code128Encoder.Encode("Deterministic 0123456789 \t");
        Assert.Equal(a.Symbols, b.Symbols);
        Assert.Equal(a.Modules, b.Modules);
        Assert.True(((ICollection<int>)a.Symbols).IsReadOnly);
        Assert.True(((ICollection<bool>)a.Modules).IsReadOnly);
    }

    [Fact]
    public void Modules_of_A_match_the_published_pattern()
    {
        // Check the first symbol (Start B = widths 2 1 1 2 1 4) and the stop (2 3 3 1 1 1 2)
        // explicitly, module by module.
        var m = Code128Encoder.Encode("A").Modules;
        Assert.Equal(11 * 3 + 13, m.Count);
        // Start B: bar bar | space | bar | space space | bar | space space space space
        bool[] startB = { true, true, false, true, false, false, true, false, false, false, false };
        Assert.Equal(startB, m.Take(11).ToArray());
        // Stop: bar bar | s s s | b b b | s | b | s | b b
        bool[] stop = { true, true, false, false, false, true, true, true, false, true, false, true, true };
        Assert.Equal(stop, m.Skip(m.Count - 13).ToArray());
    }
}
