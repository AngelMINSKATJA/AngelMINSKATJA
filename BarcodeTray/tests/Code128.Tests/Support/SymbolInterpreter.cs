namespace Code128.Tests.Support;

/// <summary>
/// A tiny Code 128 *decoder state machine* working on symbol values (no bars involved),
/// written straight from the symbology rules and independent of the encoder:
///
///   value  set A         set B         set C
///   0..63  ' '..'_'      ' '..'?'      "00".."63"
///   64..95 NUL..US       '@'..DEL      "64".."95"
///   96,97  FNC3, FNC2    FNC3, FNC2    "96","97"
///   98     SHIFT         SHIFT         "98"
///   99     Code C        Code C        "99"
///   100    Code B        FNC4          Code B
///   101    FNC4          Code A        Code A
///   102    FNC1          FNC1          FNC1
///   103..105 Start A/B/C   106 Stop
///
/// (set B: value v is the char 32+v for 0..95; set A: v&lt;64 -> 32+v, 64..95 -> v-64.)
/// </summary>
internal static class SymbolInterpreter
{
    internal enum Set { A, B, C }

    internal enum Kind
    {
        Data,       // emitted one or two chars
        Switch,     // 99/100/101
        Shift,      // 98
        Function,   // FNC1..FNC4 (never produced by our encoder)
        Invalid,    // not allowed in this state
    }

    internal readonly record struct State(Set Set, bool ShiftActive);

    internal readonly record struct StepResult(Kind Kind, State Next, string Emitted);

    /// <summary>State after a start symbol (103..105).</summary>
    internal static State StartState(int startValue) => startValue switch
    {
        103 => new State(Set.A, false),
        104 => new State(Set.B, false),
        105 => new State(Set.C, false),
        _ => throw new ArgumentOutOfRangeException(nameof(startValue)),
    };

    /// <summary>Applies one non-start, non-stop symbol (0..102).</summary>
    internal static StepResult Step(State state, int value)
    {
        if (value < 0 || value > 102)
        {
            return new StepResult(Kind.Invalid, state, "");
        }

        // The effective set for a data symbol (SHIFT flips A <-> B for exactly one symbol).
        Set effective = state.Set;
        if (state.ShiftActive)
        {
            effective = state.Set == Set.A ? Set.B : Set.A;
        }
        var cleared = new State(state.Set, false);

        if (state.Set == Set.C)
        {
            if (value <= 99)
            {
                return new StepResult(Kind.Data, state, value.ToString("D2"));
            }
            return value switch
            {
                100 => new StepResult(Kind.Switch, new State(Set.B, false), ""),
                101 => new StepResult(Kind.Switch, new State(Set.A, false), ""),
                _ => new StepResult(Kind.Function, state, ""),   // 102 = FNC1
            };
        }

        // Sets A / B (possibly shifted).
        if (value <= 95)
        {
            char c = effective == Set.A
                ? (char)(value < 64 ? value + 32 : value - 64)
                : (char)(value + 32);
            return new StepResult(Kind.Data, cleared, c.ToString());
        }

        if (state.ShiftActive)
        {
            // After SHIFT only a data symbol may follow.
            return new StepResult(Kind.Invalid, state, "");
        }

        switch (value)
        {
            case 96:
            case 97:
            case 102:
                return new StepResult(Kind.Function, state, "");
            case 98:
                return new StepResult(Kind.Shift, new State(state.Set, true), "");
            case 99:
                return new StepResult(Kind.Switch, new State(Set.C, false), "");
            case 100:
                return state.Set == Set.A
                    ? new StepResult(Kind.Switch, new State(Set.B, false), "")
                    : new StepResult(Kind.Function, state, "");   // FNC4 in set B
            default: // 101
                return state.Set == Set.B
                    ? new StepResult(Kind.Switch, new State(Set.A, false), "")
                    : new StepResult(Kind.Function, state, "");   // FNC4 in set A
        }
    }

    internal sealed record Decoded(
        string? Text, string? Error, int Switches, int Shifts, int DataSymbols, int FunctionSymbols);

    /// <summary>
    /// Fully validates and decodes a symbol list: Start ... checksum, Stop. Error is null when
    /// everything is consistent (start in 103..105, stop last, correct checksum, no stray
    /// start/stop in the middle, no dangling SHIFT, no function symbols).
    /// </summary>
    internal static Decoded Decode(IReadOnlyList<int> symbols)
    {
        static Decoded Fail(string error) => new(null, error, 0, 0, 0, 0);

        if (symbols.Count < 4)
        {
            return Fail("fewer than 4 symbols");
        }
        int start = symbols[0];
        if (start < 103 || start > 105)
        {
            return Fail($"first symbol {start} is not a start symbol");
        }
        if (symbols[^1] != 106)
        {
            return Fail($"last symbol {symbols[^1]} is not the stop symbol");
        }

        // checksum
        long sum = start;
        for (int k = 1; k < symbols.Count - 2; k++)
        {
            sum += (long)k * symbols[k];
        }
        int expected = (int)(sum % 103);
        int actual = symbols[^2];
        if (actual != expected)
        {
            return Fail($"checksum is {actual}, expected {expected}");
        }

        var text = new System.Text.StringBuilder();
        State state = StartState(start);
        int switches = 0, shifts = 0, data = 0, functions = 0;
        for (int k = 1; k < symbols.Count - 2; k++)
        {
            StepResult r = Step(state, symbols[k]);
            switch (r.Kind)
            {
                case Kind.Invalid:
                    return Fail($"symbol {symbols[k]} at index {k} is invalid in state {state}");
                case Kind.Switch:
                    switches++;
                    break;
                case Kind.Shift:
                    shifts++;
                    break;
                case Kind.Function:
                    functions++;
                    break;
                default:
                    data++;
                    break;
            }
            text.Append(r.Emitted);
            state = r.Next;
        }
        if (state.ShiftActive)
        {
            return Fail("SHIFT is not followed by a data symbol");
        }
        return new Decoded(text.ToString(), null, switches, shifts, data, functions);
    }
}
