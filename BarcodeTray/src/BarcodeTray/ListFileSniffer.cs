using System;

namespace BarcodeTray;

/// <summary>
/// Reconnaît, d'après les premiers octets, un fichier qui n'est pas du format annoncé par son extension
/// (ancien .xls / .doc, fichier Office protégé par mot de passe, PDF, RTF, image, archive ZIP...) pour donner un message utile.
/// </summary>
internal static class ListFileSniffer
{
    private static readonly byte[] Ole2 = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    // « EncryptedPackage » en UTF-16 : nom du flux d'un .xlsx / .docx chiffré par un mot de passe.
    private static readonly byte[] EncryptedPackageName = System.Text.Encoding.Unicode.GetBytes("EncryptedPackage");

    internal static bool IsOle2(byte[] d, int length) => StartsWith(d, length, Ole2);

    internal static bool IsZip(byte[] d, int length)
        => length >= 4 && d[0] == 0x50 && d[1] == 0x4B && ((d[2] == 3 && d[3] == 4) || (d[2] == 5 && d[3] == 6));

    /// <summary>Message d'erreur pour un fichier OLE2 (ancien format Office, ou format récent protégé par mot de passe).</summary>
    internal static string DescribeOle2(byte[] d, int length)
    {
        bool encrypted = d.AsSpan(0, length).IndexOf(EncryptedPackageName) >= 0;
        if (encrypted)
        {
            return "Ce fichier Office est protégé par un mot de passe et ne peut pas être lu. Ouvrez-le dans Excel ou Word, "
                   + "retirez le mot de passe, enregistrez-le en .xlsx / .docx, ou copiez la liste puis utilisez « Coller ».";
        }

        return "Ce fichier est dans l'ancien format Office (.xls / .doc) malgré son extension : "
               + "ouvrez-le dans Excel ou Word, puis enregistrez-le en .xlsx / .docx (Fichier > Enregistrer sous).";
    }

    /// <summary>
    /// Message pour un contenu manifestement d'un autre type (PDF, RTF, image, ancien Office), ou null si rien de reconnu.
    /// </summary>
    internal static string? DescribeForeign(byte[] d, int length)
    {
        if (IsOle2(d, length)) return DescribeOle2(d, length);
        if (StartsWith(d, length, "%PDF-"u8)) return "Ce fichier est un PDF, pas une liste : copiez-en le texte puis utilisez « Coller ».";
        if (StartsWith(d, length, "{\\rtf"u8)) return "Ce fichier est au format RTF : ouvrez-le dans Word, enregistrez-le en .docx puis rechargez-le.";
        if (StartsWith(d, length, new byte[] { 0x89, 0x50, 0x4E, 0x47 }) || StartsWith(d, length, new byte[] { 0xFF, 0xD8, 0xFF }) || StartsWith(d, length, "GIF8"u8))
        {
            return "Ce fichier est une image, pas une liste.";
        }

        return null;
    }

    /// <summary>Vrai si le début du fichier ressemble à du texte (pas d'octet nul ni de contrôle parmi les premiers octets).</summary>
    internal static bool LooksLikeText(byte[] d, int length)
    {
        int n = Math.Min(length, 2048);
        if (n == 0) return false;
        if (n >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return true;   // BOM UTF-16
        for (int i = 0; i < n; i++)
        {
            byte b = d[i];
            if (b < 0x20 && b != '\t' && b != '\n' && b != '\r' && b != '\f') return false;
        }

        return true;
    }

    private static bool StartsWith(byte[] d, int length, ReadOnlySpan<byte> prefix)
        => length >= prefix.Length && d.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
