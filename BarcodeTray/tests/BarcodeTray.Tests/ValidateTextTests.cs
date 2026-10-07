using System;
using System.Linq;
using Xunit;

namespace BarcodeTray.Tests;

public class ValidateTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("      ")]
    [InlineData("\t")]
    [InlineData(" \t \r\n ")]
    public void EmptyOrBlankText_IsRejectedWithAMessage(string? text)
    {
        string? message = BarcodeRenderer.ValidateText(text);

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("0")]
    [InlineData("Hello")]
    [InlineData("Hello World")]
    [InlineData("  padded  ")]                         // espaces de début/fin ignorés
    [InlineData("ABC-123/456_789.0")]
    [InlineData("~!@#$%^&*()_+-=[]{};':\",./<>?\\|`")]  // toute la ponctuation ASCII imprimable
    [InlineData("a b  c   d")]                          // espaces internes conservés
    public void PrintableAscii_IsAccepted(string text)
    {
        Assert.Null(BarcodeRenderer.ValidateText(text));
    }

    [Fact]
    public void WholePrintableAsciiRange_IsAccepted()
    {
        string all = new(Enumerable.Range(0x20, 0x7F - 0x20).Select(c => (char)c).ToArray());

        Assert.Equal(95, all.Length);
        Assert.Null(BarcodeRenderer.ValidateText(all));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(250)]
    public void LongText_IsAcceptedByValidation(int length)
    {
        // La longueur maximale est limitée par la zone de saisie (200), pas par la validation.
        Assert.Null(BarcodeRenderer.ValidateText(new string('A', length)));
    }

    [Theory]
    [InlineData("é", "é")]
    [InlineData("Café", "é")]
    [InlineData("naïve", "ï")]
    [InlineData("Ça va", "Ç")]
    [InlineData("1234€", "€")]
    [InlineData("日本語", "日")]
    [InlineData("ok œuvre", "œ")]
    [InlineData("x – y", "–")]              // tiret demi-cadratin (copier-coller depuis Word)
    [InlineData("« guillemets »", "«")]
    [InlineData("a\u00A0b", "\u00A0")]      // espace insécable au milieu (copier-coller depuis Word/Excel) : pas de l'ASCII
    public void NonAscii_IsRejected_NamingTheFirstBadCharacter(string text, string firstBad)
    {
        string? message = BarcodeRenderer.ValidateText(text);

        Assert.NotNull(message);
        Assert.Contains("ASCII", message, StringComparison.Ordinal);
        if (!char.IsWhiteSpace(firstBad[0]))
        {
            Assert.Contains(firstBad, message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Ref 12\u00A0345", "insécable")]   // espace insécable (Word, Excel) : invisible, donc décrite avec des mots
    [InlineData("1\u202F234", "spéciale")]         // espace fine insécable (séparateur de milliers français)
    [InlineData("a\u2009b", "spéciale")]
    [InlineData("a\u200Bb", "invisible")]          // espace de largeur nulle
    [InlineData("a\u00ADb", "invisible")]          // trait d'union conditionnel
    public void InvisibleCharacters_AreDescribedInWords(string text, string expectedWord)
    {
        string? message = BarcodeRenderer.ValidateText(text);

        Assert.NotNull(message);
        Assert.Contains(expectedWord, message, StringComparison.Ordinal);
        Assert.Contains("ASCII", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Emoji_IsRejected()
    {
        string? message = BarcodeRenderer.ValidateText("ok \U0001F600");

        Assert.NotNull(message);
        Assert.Contains("ASCII", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A\tB")]
    [InlineData("A\nB")]
    [InlineData("A\r\nB")]
    [InlineData("A\u0001B")]
    [InlineData("A\u001FB")]
    [InlineData("A\u007FB")]    // DEL
    [InlineData("A\u0000B")]
    [InlineData("\u0007")]
    public void ControlCharacters_AreRejected(string text)
    {
        string? message = BarcodeRenderer.ValidateText(text);

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Message_IsInFrench_AndMentionsAscii()
    {
        string? message = BarcodeRenderer.ValidateText("é");

        Assert.NotNull(message);
        Assert.Contains("ASCII", message, StringComparison.Ordinal);
        Assert.Contains("Code 128", message, StringComparison.Ordinal);
    }
}
