using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BarcodeTray;

/// <summary>
/// Dépose l'image (ou du texte) dans le presse-papier Windows. À appeler depuis le thread d'interface (STA).
/// </summary>
public static class ClipboardService
{
    private const int RetryTimes = 10;
    private const int RetryDelayMs = 100;

    /// <summary>
    /// Place l'image dans le presse-papier sous deux formes à la fois : bitmap classique (Word, Excel, Paint...)
    /// et flux « PNG » (Office le préfère : il conserve la taille physique grâce à la résolution).
    /// Lève <see cref="InvalidOperationException"/> (message en français) si le presse-papier est inaccessible.
    /// </summary>
    public static void SetBarcodeImage(Bitmap bitmap, byte[] png)
    {
        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        if (png == null || png.Length == 0)
        {
            throw new ArgumentException("Image PNG vide.", nameof(png));
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, bitmap);

            // Ce flux ne doit PAS être libéré ici : il doit rester valide jusqu'à ce que
            // le presse-papier ait pris sa copie (copy: true ci-dessous, copie faite avant le retour).
            var pngStream = new MemoryStream(png);
            data.SetData("PNG", false, pngStream);

            Clipboard.SetDataObject(data, true, RetryTimes, RetryDelayMs);
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            Log.Error("Presse-papier : écriture de l'image impossible.", ex);
            throw new InvalidOperationException(BuildFriendlyMessage(ex), ex);
        }
    }

    /// <summary>Place un texte brut (Unicode) dans le presse-papier.</summary>
    public static void SetText(string text)
    {
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, text ?? string.Empty);
            Clipboard.SetDataObject(data, true, RetryTimes, RetryDelayMs);
        }
        catch (Exception ex) when (IsClipboardFailure(ex))
        {
            Log.Error("Presse-papier : écriture du texte impossible.", ex);
            throw new InvalidOperationException(BuildFriendlyMessage(ex), ex);
        }
    }

    private static bool IsClipboardFailure(Exception ex)
    {
        return ex is ExternalException
               || ex is System.Threading.ThreadStateException
               || ex is InvalidOperationException
               || ex is System.ComponentModel.Win32Exception
               || ex is OutOfMemoryException
               || ex is UnauthorizedAccessException;
    }

    private static string BuildFriendlyMessage(Exception ex)
    {
        if (ex is System.Threading.ThreadStateException)
        {
            return "Le presse-papier n'est pas accessible depuis ce contexte. Relancez l'application.";
        }

        return "Le presse-papier est occupé ou inaccessible (une autre application l'utilise peut-être). "
               + "Patientez un instant puis réessayez.";
    }
}
