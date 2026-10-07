using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Tests du presse-papier Windows. Ils REMPLACENT le contenu du presse-papier de la machine qui les exécute.
/// Si le presse-papier n'est pas utilisable (session sans bureau, verrou d'un autre programme...), les tests
/// sont ignorés ("Skipped") et non en échec.
/// </summary>
public class ClipboardServiceTests
{
    private sealed record ClipboardOutcome(
        string? UnavailableReason,
        bool HasBitmap,
        bool HasPng,
        string? BitmapDecodedText,
        Size? BitmapSize,
        byte[]? PngBytes,
        bool SourceStillUsable);

    [SkippableFact]
    public void SetBarcodeImage_PutsBothABitmapAndAPngOnTheClipboard()
    {
        const string text = "CLIP-TEST-123";

        byte[]? expectedPng = null;
        Size? expectedSize = null;
        ClipboardOutcome outcome = Sta.Run(() =>
        {
            using Bitmap bitmap = BarcodeRenderer.Render(text, new AppSettings());
            byte[] png = BarcodeRenderer.ToPng(bitmap);
            expectedPng = png;
            expectedSize = bitmap.Size;

            try
            {
                ClipboardService.SetBarcodeImage(bitmap, png);
            }
            catch (InvalidOperationException ex)
            {
                return new ClipboardOutcome("SetBarcodeImage : " + ex.Message, false, false, null, null, null, true);
            }

            IDataObject? data = GetDataObjectWithRetry();
            if (data == null)
            {
                return new ClipboardOutcome("Clipboard.GetDataObject() n'a rien renvoyé.", false, false, null, null, null, true);
            }

            bool hasBitmap = data.GetDataPresent(DataFormats.Bitmap);
            bool hasPng = data.GetDataPresent("PNG");

            string? decoded = null;
            Size? bitmapSize = null;
            if (hasBitmap && data.GetData(DataFormats.Bitmap) is Bitmap fromClipboard)
            {
                using (fromClipboard)
                {
                    bitmapSize = fromClipboard.Size;
                    decoded = BarcodeDecoding.Decode(fromClipboard);
                }
            }

            byte[]? pngBytes = null;
            if (hasPng && data.GetData("PNG") is Stream stream)
            {
                using var copy = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(copy);
                pngBytes = copy.ToArray();
            }

            // L'appelant reste propriétaire du Bitmap : le service ne doit pas l'avoir libéré.
            bool stillUsable;
            try
            {
                stillUsable = bitmap.Width > 0 && bitmap.Height > 0 && bitmap.GetPixel(0, 0).A == 255;
            }
            catch (Exception)
            {
                stillUsable = false;
            }

            return new ClipboardOutcome(null, hasBitmap, hasPng, decoded, bitmapSize, pngBytes, stillUsable);
        });

        Skip.If(outcome.UnavailableReason != null, "Presse-papier indisponible sur ce poste : " + outcome.UnavailableReason);

        Assert.True(outcome.HasBitmap, "Le format Bitmap (Word, Excel, Paint) est absent du presse-papier.");
        Assert.True(outcome.HasPng, "Le format PNG (préféré par Office) est absent du presse-papier.");
        Assert.Equal(text, outcome.BitmapDecodedText);
        Assert.Equal(expectedSize, outcome.BitmapSize);
        Assert.NotNull(expectedPng);
        Assert.NotNull(outcome.PngBytes);
        // Windows peut arrondir la taille du bloc mémoire du presse-papier : quelques octets nuls à la fin sont tolérés
        // (un lecteur PNG s'arrête au chunk IEND).
        byte[] clipboardPng = outcome.PngBytes!;
        Assert.True(clipboardPng.Length >= expectedPng!.Length, "Flux PNG tronqué dans le presse-papier.");
        Assert.True(clipboardPng.Length - expectedPng.Length <= 64, "Trop d'octets en trop dans le flux PNG du presse-papier.");
        Assert.Equal(expectedPng, clipboardPng.Take(expectedPng.Length).ToArray());
        Assert.All(clipboardPng.Skip(expectedPng.Length), b => Assert.Equal(0, b));
        Assert.True(outcome.SourceStillUsable, "Le Bitmap de l'appelant ne doit pas être libéré par le service.");
    }

    [SkippableFact]
    public void SetBarcodeImage_CanBeCalledRepeatedly_LastImageWins()
    {
        string?[] decoded = Sta.Run(() =>
        {
            var results = new string?[2];
            string[] texts = { "FIRST-1", "SECOND-2" };
            for (int i = 0; i < texts.Length; i++)
            {
                using Bitmap bitmap = BarcodeRenderer.Render(texts[i], new AppSettings());
                try
                {
                    ClipboardService.SetBarcodeImage(bitmap, BarcodeRenderer.ToPng(bitmap));
                }
                catch (InvalidOperationException)
                {
                    return new string?[] { "INDISPONIBLE", null };
                }
            }

            IDataObject? data = GetDataObjectWithRetry();
            if (data != null && data.GetData(DataFormats.Bitmap) is Bitmap fromClipboard)
            {
                using (fromClipboard)
                {
                    results[1] = BarcodeDecoding.Decode(fromClipboard);
                }
            }

            return results;
        });

        Skip.If(decoded[0] == "INDISPONIBLE", "Presse-papier indisponible sur ce poste.");

        Assert.Equal("SECOND-2", decoded[1]);
    }

    [SkippableFact]
    public void SetText_RoundTrips()
    {
        const string text = "Diagnostic copié : imprimante « Brother QL-800 » – OK";

        (string? unavailable, string? back) = Sta.Run(() =>
        {
            try
            {
                ClipboardService.SetText(text);
            }
            catch (InvalidOperationException ex)
            {
                return ((string?)ex.Message, (string?)null);
            }

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        return ((string?)null, Clipboard.GetText());
                    }
                }
                catch (ExternalException)
                {
                    // presse-papier momentanément occupé
                }

                Thread.Sleep(100);
            }

            return ((string?)null, (string?)null);
        });

        Skip.If(unavailable != null, "Presse-papier indisponible sur ce poste : " + unavailable);

        Assert.Equal(text, back);
    }

    [Fact]
    public void SetBarcodeImage_RejectsMissingArguments()
    {
        using var bitmap = new Bitmap(10, 10);

        Assert.ThrowsAny<ArgumentException>(() => ClipboardService.SetBarcodeImage(null!, new byte[] { 1 }));
        Assert.ThrowsAny<ArgumentException>(() => ClipboardService.SetBarcodeImage(bitmap, Array.Empty<byte>()));
        Assert.ThrowsAny<ArgumentException>(() => ClipboardService.SetBarcodeImage(bitmap, null!));
    }

    private static IDataObject? GetDataObjectWithRetry()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                IDataObject? data = Clipboard.GetDataObject();
                if (data != null)
                {
                    return data;
                }
            }
            catch (ExternalException)
            {
                // presse-papier momentanément occupé
            }

            Thread.Sleep(100);
        }

        return null;
    }
}
