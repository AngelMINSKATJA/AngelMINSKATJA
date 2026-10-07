using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using BarcodeTray.Tests.Support;
using Code128;
using Xunit;

namespace BarcodeTray.Tests;

public class BarcodeRendererTests
{
    // Textes variés : lettres, chiffres (jeu C), mélanges (changements de jeu), ponctuation, espaces, longs.
    public static TheoryData<string> SampleTexts => new()
    {
        "A",
        "a",
        "12",
        "12345",
        "1234567890",
        "Hello",
        "Hello World",
        "ABC-123",
        "SN:0001/2024",
        "Order 2024-000123 / Lot 45",
        "abc def GHI jkl",
        "a1B2c3D4e5F6g7H8i9J0",
        "~!@#$%^&*()_+{}|:\"<>?",
        @"[]\;',./`-=",
        "0123456789012345678901234567890123456789",
        "The quick brown fox jumps over the lazy dog 0123456789",
        new string('A', 60),
        string.Concat(Enumerable.Repeat("0123456789", 10)),
        string.Concat(Enumerable.Repeat("Ab1-", 30)),
        new string('Z', 200),
        "Un texte nettement plus long que d'habitude, avec des chiffres 1234567890123456 et de la ponctuation ; OK ?",
    };

    private static AppSettings DefaultSettings() => new();

    private static int SymbolCount(string text) => Code128Encoder.Encode(text.Trim()).Symbols.Count;

    // ---------------------------------------------------------------- relecture par un décodeur indépendant

    [Theory]
    [MemberData(nameof(SampleTexts))]
    public void Render_DecodesBackToTheSameText(string text)
    {
        using Bitmap bitmap = BarcodeRenderer.Render(text, DefaultSettings());

        string? decoded = BarcodeDecoding.Decode(bitmap);

        Assert.Equal(text, decoded);
    }

    [Theory]
    [MemberData(nameof(SampleTexts))]
    public void Png_RoundTrip_DecodesBackToTheSameText(string text)
    {
        using Bitmap bitmap = BarcodeRenderer.Render(text, DefaultSettings());
        byte[] png = BarcodeRenderer.ToPng(bitmap);

        using var stream = new MemoryStream(png);
        using var reloaded = new Bitmap(stream);

        Assert.Equal(bitmap.Width, reloaded.Width);
        Assert.Equal(bitmap.Height, reloaded.Height);
        Assert.Equal(text, BarcodeDecoding.Decode(reloaded));
    }

    [Fact]
    public void Render_TrimsSurroundingWhitespace()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("   Hello 42  ", DefaultSettings());

        Assert.Equal("Hello 42", BarcodeDecoding.Decode(bitmap));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Render_DecodesWithAnyPreferredModuleWidth(int modulePx)
    {
        var settings = new AppSettings { ModulePx = modulePx, MaxWidthPx = 5000 };

        using Bitmap bitmap = BarcodeRenderer.Render("MODULE-" + modulePx, settings);

        Assert.Equal("MODULE-" + modulePx, BarcodeDecoding.Decode(bitmap));
    }

    [Theory]
    [InlineData(203)]
    [InlineData(300)]
    [InlineData(600)]
    public void Render_DecodesAtOtherResolutions(int dpi)
    {
        var settings = new AppSettings { Dpi = dpi, MaxWidthPx = (int)(696 * (dpi / 300.0)), BarHeightPx = 100 };

        using Bitmap bitmap = BarcodeRenderer.Render("DPI-" + dpi, settings);

        Assert.Equal("DPI-" + dpi, BarcodeDecoding.Decode(bitmap));
        Assert.Equal(dpi, bitmap.HorizontalResolution, 0.5);
        Assert.Equal(dpi, bitmap.VerticalResolution, 0.5);
    }

    // ---------------------------------------------------------------- dimensions et métadonnées

    [Fact]
    public void Render_WidthNeverExceedsMaxWidth_ForUpTo58Symbols()
    {
        var settings = DefaultSettings();
        int checkedCases = 0;

        for (int length = 1; length <= 120; length++)
        {
            foreach (string text in new[]
                     {
                         new string('A', length),                       // jeu B pur
                         string.Concat(Enumerable.Repeat("7", length)), // chiffres (jeu C)
                         string.Concat(Enumerable.Repeat("a1", length)).Substring(0, length), // alternance
                     })
            {
                if (SymbolCount(text) > 58)
                {
                    continue;
                }

                using Bitmap bitmap = BarcodeRenderer.Render(text, settings);
                Assert.True(
                    bitmap.Width <= settings.MaxWidthPx,
                    $"« {text} » ({SymbolCount(text)} symboles) : largeur {bitmap.Width} px > {settings.MaxWidthPx} px.");
                checkedCases++;
            }
        }

        Assert.True(checkedCases > 100, "Le test n'a vérifié presque aucun cas.");
    }

    [Fact]
    public void Render_ShortTextUsesThePreferredModuleWidth()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("Hello", DefaultSettings());
        int symbols = SymbolCount("Hello");
        int module = BarcodeRenderer.ChooseModulePx(symbols, 3, 696);

        Assert.Equal(3, module);
        // 11 modules par symbole (13 pour l'arrêt) + au moins 10 modules de silence de chaque côté.
        int minimumWidth = (11 * (symbols - 1) + 13 + 2 * Code128Encoder.QuietZoneModules) * module;
        Assert.True(bitmap.Width >= minimumWidth, $"largeur {bitmap.Width} < minimum {minimumWidth}");
    }

    [Fact]
    public void Render_SetsResolutionTo300Dpi()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("DPI", DefaultSettings());

        Assert.Equal(300f, bitmap.HorizontalResolution, 0.01);
        Assert.Equal(300f, bitmap.VerticalResolution, 0.01);
    }

    [Fact]
    public void ToPng_ProducesAValidPngThatKeepsTheResolution()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("PNG-DPI", DefaultSettings());

        byte[] png = BarcodeRenderer.ToPng(bitmap);

        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());

        using var stream = new MemoryStream(png);
        using Image reloaded = Image.FromStream(stream);
        Assert.Equal(ImageFormat.Png.Guid, reloaded.RawFormat.Guid);
        // Le PNG stocke des pixels par mètre : 11811 px/m = 299,9994 dpi.
        Assert.Equal(300f, reloaded.HorizontalResolution, 0.5);
        Assert.Equal(300f, reloaded.VerticalResolution, 0.5);
    }

    [Fact]
    public void Render_IsOpaque24BitWithWhiteBackground()
    {
        using Bitmap bitmap = BarcodeRenderer.Render("Opaque", DefaultSettings());

        Assert.Equal(PixelFormat.Format24bppRgb, bitmap.PixelFormat);
        Assert.False(Image.IsAlphaPixelFormat(bitmap.PixelFormat));

        BarcodeDecoding.Raster raster = BarcodeDecoding.ToRaster(bitmap);
        Assert.Equal(255, raster.Luma(0, 0));
        Assert.Equal(255, raster.Luma(raster.Width - 1, 0));
        Assert.Equal(255, raster.Luma(0, raster.Height - 1));
        Assert.Equal(255, raster.Luma(raster.Width - 1, raster.Height - 1));
    }

    // ---------------------------------------------------------------- aspect de l'image

    [Fact]
    public void Render_BarsAreCrispWithQuietZonesAndTextBelow()
    {
        const string text = "Hello 123";
        var settings = DefaultSettings();
        using Bitmap bitmap = BarcodeRenderer.Render(text, settings);
        BarcodeDecoding.Raster raster = BarcodeDecoding.ToRaster(bitmap);
        int module = BarcodeRenderer.ChooseModulePx(SymbolCount(text), settings.ModulePx, settings.MaxWidthPx);

        // 1) Lignes contenant au moins un pixel sombre, regroupées en "bandes" consécutives.
        var bands = new List<(int Start, int Length)>();
        int bandStart = -1;
        for (int y = 0; y <= raster.Height; y++)
        {
            bool dark = y < raster.Height && RowHasDarkPixel(raster, y);
            if (dark && bandStart < 0)
            {
                bandStart = y;
            }
            else if (!dark && bandStart >= 0)
            {
                bands.Add((bandStart, y - bandStart));
                bandStart = -1;
            }
        }

        Assert.True(bands.Count >= 2, "Il faut une bande de barres puis au moins une bande de texte.");
        Assert.True(bands[0].Start >= 1, "Marge blanche attendue au-dessus des barres.");
        Assert.Equal(settings.BarHeightPx, bands[0].Length);
        (int lastStart, int lastLength) = bands[^1];
        Assert.True(lastStart >= bands[0].Start + bands[0].Length, "Le texte doit être sous les barres.");
        Assert.True(lastStart + lastLength < raster.Height, "Marge blanche attendue sous le texte.");

        // 2) Sur une ligne au milieu des barres : pixels strictement noirs ou blancs (pas de lissage),
        //    zones de silence d'au moins 10 modules, symétriques, première barre = 2 modules.
        int midRow = bands[0].Start + bands[0].Length / 2;
        for (int x = 0; x < raster.Width; x++)
        {
            Assert.True(raster.IsPureBlackOrWhite(x, midRow), $"pixel ({x},{midRow}) ni noir ni blanc : lissage détecté.");
        }

        int firstDark = Enumerable.Range(0, raster.Width).First(x => raster.Luma(x, midRow) < 128);
        int lastDark = Enumerable.Range(0, raster.Width).Last(x => raster.Luma(x, midRow) < 128);
        Assert.True(firstDark >= Code128Encoder.QuietZoneModules * module,
            $"zone de silence gauche {firstDark} px < {Code128Encoder.QuietZoneModules} modules de {module} px");
        Assert.True(raster.Width - 1 - lastDark >= Code128Encoder.QuietZoneModules * module,
            $"zone de silence droite {raster.Width - 1 - lastDark} px < {Code128Encoder.QuietZoneModules} modules de {module} px");
        Assert.Equal(firstDark, raster.Width - 1 - lastDark);

        int firstBarWidth = 0;
        while (raster.Luma(firstDark + firstBarWidth, midRow) < 128)
        {
            firstBarWidth++;
        }

        Assert.Equal(2 * module, firstBarWidth); // tous les caractères de départ commencent par une barre de 2 modules
    }

    [Fact]
    public void Render_LongTextStaysDecodableEvenWhenWiderThanMaxWidth()
    {
        string text = new('K', 150); // 153 symboles : même à 1 px par module, plus large que 696 px
        using Bitmap bitmap = BarcodeRenderer.Render(text, DefaultSettings());

        Assert.True(bitmap.Width > 696);
        Assert.Equal(text, BarcodeDecoding.Decode(bitmap));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Café")]
    [InlineData("tab\there")]
    public void Render_RejectsTextThatDidNotPassValidation(string text)
    {
        Assert.ThrowsAny<ArgumentException>(() => BarcodeRenderer.Render(text, DefaultSettings()));
    }

    // ---------------------------------------------------------------- texte lisible complet sur les textes longs

    [Theory]
    [InlineData(300, 33, 696, 8, 200.0, 366)]    // texte étroit : largeur minimale (barres + silences)
    [InlineData(300, 33, 696, 8, 350.0, 366)]    // 350 + 16 = 366 : tient pile
    [InlineData(300, 33, 696, 8, 500.0, 516)]    // texte plus large : image élargie juste ce qu'il faut
    [InlineData(300, 33, 696, 8, 501.0, 518)]    // écart avec les barres toujours pair (barres centrées au point près)
    [InlineData(300, 33, 696, 8, 900.0, 696)]    // plafonné à la largeur du ruban
    [InlineData(300, 33, 697, 8, 900.0, 696)]    // plafond impair : on reste sur un écart pair
    [InlineData(800, 33, 696, 8, 900.0, 866)]    // barres déjà plus larges que le ruban : jamais rognées
    public void ChooseCanvasWidth_WidensForTheTextButNeverBeyondTheLimit(
        int barsWidth, int quietPx, int limitWidth, int margin, double textWidth, int expected)
    {
        Assert.Equal(expected, BarcodeRenderer.ChooseCanvasWidth(barsWidth, quietPx, limitWidth, margin, textWidth));
    }

    [Fact]
    public void ChooseCanvasWidth_KeepsBarsCenteredAndQuietZonesAtLeastTheMinimum()
    {
        for (int bars = 200; bars <= 760; bars += 37)
        {
            foreach (int quiet in new[] { 11, 22, 33 })
            {
                for (double text = 50; text <= 1000; text += 13.7)
                {
                    int width = BarcodeRenderer.ChooseCanvasWidth(bars, quiet, 696, 8, text);

                    Assert.Equal(0, (width - bars) % 2);
                    Assert.True((width - bars) / 2 >= quiet, $"silence {(width - bars) / 2} < {quiet} (barres {bars}, texte {text})");
                    Assert.True(width <= Math.Max(bars + 2 * quiet, 696));
                }
            }
        }
    }

    [Fact]
    public void Render_LongText_WidensTheImageSoTheReadableLineIsNotEllipsized()
    {
        // 40 caractères : barres de 1 point (475 points) plus étroites que la ligne de texte, même à 6 pt (~540 points).
        string text = string.Concat(Enumerable.Repeat("Ab1-", 10));
        var settings = DefaultSettings();
        using Bitmap bitmap = BarcodeRenderer.Render(text, settings, out int modulePx);
        BarcodeDecoding.Raster raster = BarcodeDecoding.ToRaster(bitmap);

        Assert.Equal(1, modulePx);
        int barsWidth = Code128Encoder.Encode(text).Modules.Count * modulePx;
        Assert.True(bitmap.Width > barsWidth + 2 * BarcodeRenderer.QuietZoneModulesUsed * modulePx, "L'image devrait être plus large que les barres.");
        Assert.True(bitmap.Width <= settings.MaxWidthPx, $"largeur {bitmap.Width} > {settings.MaxWidthPx}");
        Assert.Equal(text, BarcodeDecoding.Decode(raster));

        // Les barres restent centrées : silences gauche et droit identiques sur une ligne au milieu des barres.
        int midRow = 8 + settings.BarHeightPx / 2;
        int barsLeft = Enumerable.Range(0, raster.Width).First(x => raster.Luma(x, midRow) < 128);
        int barsRight = Enumerable.Range(0, raster.Width).Last(x => raster.Luma(x, midRow) < 128);
        Assert.Equal(barsLeft, raster.Width - 1 - barsRight);

        // La ligne de texte (sous les barres) déborde de chaque côté des barres : le texte est entier, pas rogné.
        int textTop = 8 + settings.BarHeightPx + 8;
        int textLeft = int.MaxValue;
        int textRight = -1;
        for (int y = textTop; y < raster.Height - 8; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                if (raster.Luma(x, y) < 128)
                {
                    textLeft = Math.Min(textLeft, x);
                    textRight = Math.Max(textRight, x);
                }
            }
        }

        Assert.True(textRight >= 0, "Aucun texte dessiné sous les barres.");
        Assert.True(textLeft < barsLeft, $"le texte (x={textLeft}) devrait commencer avant les barres (x={barsLeft})");
        Assert.True(textRight > barsRight, $"le texte (x={textRight}) devrait finir après les barres (x={barsRight})");
    }

    [Theory]
    [MemberData(nameof(SampleTexts))]
    public void Render_QuietZonesAreSymmetric_ForAllSampleTexts(string text)
    {
        var settings = DefaultSettings();
        using Bitmap bitmap = BarcodeRenderer.Render(text, settings);
        BarcodeDecoding.Raster raster = BarcodeDecoding.ToRaster(bitmap);

        int midRow = 8 + settings.BarHeightPx / 2;
        int left = Enumerable.Range(0, raster.Width).First(x => raster.Luma(x, midRow) < 128);
        int right = Enumerable.Range(0, raster.Width).Last(x => raster.Luma(x, midRow) < 128);

        int module = BarcodeRenderer.ChooseModulePx(SymbolCount(text), settings.ModulePx, settings.MaxWidthPx);
        Assert.Equal(left, raster.Width - 1 - right);
        Assert.True(left >= Code128Encoder.QuietZoneModules * module, $"zone de silence de {left} px < {Code128Encoder.QuietZoneModules} modules de {module} px");
    }

    // ---------------------------------------------------------------- barres trop fines (printing-6)

    [Theory]
    [InlineData("Hello", 3)]
    [InlineData("AAAAAAAAAAAAAAAAAAAA", 2)]                       // 20 caractères : 2 points
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 1)]             // 30 caractères : 1 point
    public void Render_ReportsTheModulePxActuallyUsed(string text, int expected)
    {
        using Bitmap bitmap = BarcodeRenderer.Render(text, DefaultSettings(), out int modulePx);

        Assert.Equal(expected, modulePx);
        Assert.Equal(BarcodeRenderer.ChooseModulePx(SymbolCount(text), 3, 696), modulePx);
    }

    [Fact]
    public void ThinBarsWarning_OnlyWhenLengthForcedTheBarsDownToOnePoint()
    {
        string? warning = BarcodeRenderer.ThinBarsWarning(1, 3);

        Assert.NotNull(warning);
        Assert.Contains("fines", warning, StringComparison.Ordinal);
        Assert.Contains("Raccourcissez", warning, StringComparison.Ordinal);
        Assert.Null(BarcodeRenderer.ThinBarsWarning(2, 3));
        Assert.Null(BarcodeRenderer.ThinBarsWarning(3, 3));
        Assert.Null(BarcodeRenderer.ThinBarsWarning(1, 1)); // 1 point choisi volontairement dans les réglages
    }

    private static bool RowHasDarkPixel(BarcodeDecoding.Raster raster, int y)
    {
        for (int x = 0; x < raster.Width; x++)
        {
            if (raster.Luma(x, y) < 128)
            {
                return true;
            }
        }

        return false;
    }
}
