using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;

namespace BarcodeTray;

/// <summary>Valeurs lues dans une liste (fichier ou texte collé) : un texte de code-barres par entrée.</summary>
internal sealed class ListReadResult
{
    internal ListReadResult(IReadOnlyList<string> values, IReadOnlyList<string> warnings, string? error, string source, string? skippedTitle = null)
    {
        Values = values;
        Warnings = warnings;
        Error = error;
        Source = source;
        SkippedTitle = skippedTitle;
    }

    /// <summary>Valeurs, dans l'ordre de la liste, déjà nettoyées (espaces insécables remplacés, bords supprimés), sans entrée vide.</summary>
    internal IReadOnlyList<string> Values { get; }

    /// <summary>Remarques (français) : lignes vides ignorées, limite atteinte, feuilles ignorées, etc.</summary>
    internal IReadOnlyList<string> Warnings { get; }

    /// <summary>Message d'erreur (français) si rien d'exploitable n'a été lu (fichier illisible, format non pris en charge...), sinon null.</summary>
    internal string? Error { get; }

    /// <summary>Provenance lisible : « fichier « codes.xlsx » », « presse-papiers »...</summary>
    internal string Source { get; }

    /// <summary>
    /// Texte de la première ligne retirée parce que l'option « première ligne = titre » était demandée (null : rien n'a été retiré).
    /// Sert à dire à l'écran QUELLE ligne a disparu ; ne va jamais dans le journal.
    /// </summary>
    internal string? SkippedTitle { get; }
}

/// <summary>
/// Lecture d'une liste de textes à convertir en codes-barres, depuis un fichier .txt, .csv, .xlsx (Excel) ou .docx (Word),
/// ou depuis du texte collé. Aucune bibliothèque externe : les .xlsx et .docx sont des archives ZIP de fichiers XML.
/// Ne lève jamais d'exception (toute erreur est rendue dans <see cref="ListReadResult.Error"/>).
/// <para>Règles de lecture (une valeur = un code-barres) :</para>
/// <list type="bullet">
/// <item>.txt : une valeur par ligne, la ligne entière (aucun découpage, aucun guillemet interprété).</item>
/// <item>.csv : première cellule de chaque ligne ; séparateur « ; », tabulation ou « , » reconnu tout seul, guillemets RFC 4180.</item>
/// <item>.xlsx : première feuille visible, première colonne contenant des valeurs, lignes/colonnes masquées ou filtrées ignorées.</item>
/// <item>.docx : un paragraphe non vide = une valeur ; dans un tableau, seulement la première cellule de chaque ligne.</item>
/// <item>texte collé : première cellule (avant la première tabulation) de chaque ligne, guillemets d'Excel ; si cette première cellule
/// n'est qu'un numéro ou une puce de liste Word (« 1. », « • »...) sur presque toutes les lignes, c'est la cellule suivante.</item>
/// </list>
/// <para>
/// Chaque valeur est nettoyée (espaces insécables en espaces, caractères invisibles et de contrôle supprimés, bords rognés ;
/// rien n'est réduit à l'intérieur). Les entrées vides sont écartées. <c>skipFirstLine</c> retire la PREMIÈRE entrée non vide
/// après nettoyage (le titre de colonne) et la rend dans <see cref="ListReadResult.SkippedTitle"/>. Au plus <see cref="MaxValues"/> valeurs sont gardées.
/// </para>
/// <para>
/// Remarques (<see cref="ListReadResult.Warnings"/>) : autres colonnes ignorées, lignes vides, nombres en notation scientifique
/// (« 5,9012E+12 » : Excel a abrégé le code, des chiffres sont perdus), sauts de ligne remplacés par une espace, formats d'affichage
/// Excel non reproduits, tabulations ou sauts de ligne dans un paragraphe Word, colonnes d'un tableau Word ignorées.
/// </para>
/// <para>
/// Sécurité : fichier plafonné à <see cref="MaxFileBytes"/>, archives lues sans extraction sur disque, octets décompressés comptés
/// pour de vrai (50 Mo par partie, 200 Mo au total, 30 s), DTD/XXE interdites, lecture XML en flux.
/// </para>
/// </summary>
internal static class ListFileReader
{
    /// <summary>Nombre maximal de valeurs gardées (au-delà : avertissement).</summary>
    internal const int MaxValues = 5000;

    /// <summary>Taille maximale d'un fichier accepté.</summary>
    internal const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Extensions prises en charge (minuscules, avec le point).</summary>
    internal static readonly IReadOnlyList<string> SupportedExtensions = new[] { ".txt", ".csv", ".xlsx", ".docx" };

    /// <summary>Filtre pour la boîte « Ouvrir » (français).</summary>
    internal static string FileDialogFilter { get; } =
        "Listes de codes (*.txt;*.csv;*.xlsx;*.docx)|*.txt;*.csv;*.xlsx;*.docx"
        + "|Texte (*.txt)|*.txt"
        + "|CSV (*.csv)|*.csv"
        + "|Classeur Excel (*.xlsx)|*.xlsx"
        + "|Document Word (*.docx)|*.docx"
        + "|Tous les fichiers (*.*)|*.*";

    private const string ClipboardSource = "presse-papiers";

    private static readonly HashSet<string> LegacyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsb", ".xlsm", ".xlt", ".xltx", ".xltm", ".ods", ".numbers",
        ".doc", ".dot", ".dotx", ".docm", ".rtf", ".odt", ".wps", ".pages",
    };

    private static int _unexpectedFailures;

    /// <summary>
    /// Nombre d'erreurs INATTENDUES (bogues) depuis le démarrage : une lecture qui échoue normalement (fichier endommagé, format
    /// inconnu...) ne compte pas. Sert aux tests (un fichier hostile ne doit jamais provoquer d'erreur inattendue).
    /// </summary>
    internal static int UnexpectedFailureCount => Volatile.Read(ref _unexpectedFailures);

    private enum Kind
    {
        Text,
        Csv,
        Xlsx,
        Docx,
    }

    // ------------------------------------------------------------------------------------------------
    // API
    // ------------------------------------------------------------------------------------------------

    /// <summary>Lit un fichier. <paramref name="skipFirstLine"/> : ignore la première entrée (titre de colonne).</summary>
    internal static ListReadResult ReadFile(string path, bool skipFirstLine)
    {
        string source = SourceFor(path);
        Kind? kind = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Fail(source, "Aucun fichier n'a été indiqué.");
            }

            if (Directory.Exists(path)) return Fail(source, "C'est un dossier, pas un fichier : choisissez un fichier .txt, .csv, .xlsx ou .docx.");

            string? extensionError = Classify(ExtensionOf(path), out Kind k);
            if (extensionError != null) return Fail(source, extensionError);
            kind = k;

            var info = new FileInfo(path);
            if (!info.Exists) return Fail(source, "Fichier introuvable : il a peut-être été déplacé ou supprimé.");
            if (info.Length > MaxFileBytes) return Fail(source, TooBigMessage());

            // Partage permissif : Excel / Word gardent souvent le fichier ouvert (lecture seule pour les autres).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            return Load(stream, k, source, skipFirstLine);
        }
        catch (Exception ex)
        {
            return FromException(ex, source, kind);
        }
    }

    /// <summary>Lit un flux dont <paramref name="fileName"/> donne l'extension (pour les tests et le glisser-déposer).</summary>
    internal static ListReadResult ReadStream(Stream stream, string fileName, bool skipFirstLine)
    {
        string source = SourceFor(fileName);
        Kind? kind = null;
        try
        {
            if (stream == null) return Fail(source, "Aucun fichier n'a été indiqué.");

            string? extensionError = Classify(ExtensionOf(fileName), out Kind k);
            if (extensionError != null) return Fail(source, extensionError);
            kind = k;

            return Load(stream, k, source, skipFirstLine);
        }
        catch (Exception ex)
        {
            return FromException(ex, source, kind);
        }
    }

    /// <summary>Lit du texte collé (colonne Excel, lignes Word, Bloc-notes) : une valeur par ligne, première colonne si tabulations.</summary>
    internal static ListReadResult ReadText(string? text, bool skipFirstLine, string source = ClipboardSource)
    {
        source = string.IsNullOrWhiteSpace(source) ? ClipboardSource : source;
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Fail(source, string.Equals(source, ClipboardSource, StringComparison.OrdinalIgnoreCase)
                    ? "Le presse-papiers est vide : copiez d'abord la liste (une colonne Excel, des lignes Word ou Bloc-notes) avec Ctrl+C, puis recommencez."
                    : $"Aucune valeur trouvée dans {source} : le texte est vide.");
            }

            if (text.Length > MaxFileBytes)
            {
                return Fail(source, "Le texte collé est beaucoup trop volumineux : réduisez la liste (5000 lignes au plus).");
            }

            var collector = new ListCollector(skipFirstLine);
            var warnings = new List<string>();
            bool otherColumns = ListFileText.ReadFirstFields(text, '\t', collector);
            if (otherColumns && ListMarkers.AreListMarkers(collector.Values, skipFirstLine))
            {
                // Word copie « 1.<tab>AAA-1 » (numérotation automatique) ou « •<tab>AAA-1 » (puces) : le vrai texte est dans la 2e cellule.
                var second = new ListCollector(skipFirstLine);
                bool moreColumns = ListFileText.ReadFirstFields(text, '\t', second, column: 1);
                if (second.Count > 0 && (long)second.Count * 10 >= (long)collector.Count * 9)
                {
                    collector = second;
                    warnings.Add("La première colonne du texte collé ne contenait que des numéros ou des puces (« 1. », « • »… : numérotation automatique de Word) : "
                                 + "c'est la deuxième colonne qui a été lue" + (moreColumns ? ", les suivantes sont ignorées." : "."));
                    return collector.Build(source, warnings, "Aucune valeur trouvée dans le texte collé : il ne contient que des lignes vides.");
                }
            }

            if (otherColumns)
            {
                warnings.Add("Seule la première colonne a été lue : les autres colonnes du texte collé sont ignorées.");
            }

            return collector.Build(source, warnings, "Aucune valeur trouvée dans le texte collé : il ne contient que des lignes vides.");
        }
        catch (Exception ex)
        {
            return FromException(ex, source, null);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Lecture
    // ------------------------------------------------------------------------------------------------

    private static ListReadResult Load(Stream stream, Kind kind, string source, bool skipFirstLine)
    {
        (byte[] buffer, int length) = ReadAllBounded(stream);
        if (length == 0) throw new ListFileException("Aucune valeur trouvée : le fichier est vide.");

        var collector = new ListCollector(skipFirstLine);
        var warnings = new List<string>();
        string emptyMessage;

        switch (kind)
        {
            case Kind.Text:
            case Kind.Csv:
                {
                    ThrowIfForeignText(buffer, length);
                    string text = ListFileText.Decode(buffer, length);
                    if (kind == Kind.Text)
                    {
                        (int lines, int withTab) = ListFileText.ReadLines(text, collector);
                        if (withTab >= 2 && withTab * 2 >= lines)
                        {
                            warnings.Add("Les lignes contiennent des tabulations (colonnes) : la ligne entière sert de texte du code-barres. "
                                         + "Pour ne garder que la première colonne, enregistrez le fichier en .csv ou copiez la colonne puis utilisez « Coller ».");
                        }
                    }
                    else
                    {
                        int delimiter;
                        if (ListFileText.TryReadSepDirective(text, out delimiter, out int dataStart))
                        {
                            text = text.Substring(dataStart);   // « sep=; » : directive d'Excel, pas une valeur
                        }
                        else
                        {
                            delimiter = ListFileText.DetectDelimiter(text);
                        }

                        if (ListFileText.ReadFirstFields(text, delimiter, collector))
                        {
                            warnings.Add("Seule la première colonne a été lue : les autres colonnes du fichier sont ignorées.");
                        }
                    }

                    emptyMessage = "Aucune valeur trouvée dans ce fichier : il est vide ou ne contient que des lignes vides.";
                    break;
                }

            case Kind.Xlsx:
                using (ListZipPackage pkg = OpenPackage(buffer, length, ".xlsx"))
                {
                    ListFileXlsx.Read(pkg, collector, warnings);
                }

                emptyMessage = "Aucune valeur trouvée dans la première feuille du classeur : elle est vide, ses lignes sont masquées ou ses cellules ne contiennent pas de texte.";
                break;

            default:
                using (ListZipPackage pkg = OpenPackage(buffer, length, ".docx"))
                {
                    ListFileDocx.Read(pkg, collector, warnings);
                }

                emptyMessage = "Aucune valeur trouvée dans ce document Word : le corps du document ne contient aucun paragraphe de texte (en-têtes, pieds de page et notes sont ignorés).";
                break;
        }

        return collector.Build(source, warnings, emptyMessage);
    }

    /// <summary>Refuse un « fichier texte » qui est en réalité un autre format (Office, PDF, image, archive ZIP...).</summary>
    private static void ThrowIfForeignText(byte[] buffer, int length)
    {
        string? foreign = ListFileSniffer.DescribeForeign(buffer, length);
        if (foreign != null) throw new ListFileException(foreign);

        if (ListFileSniffer.IsZip(buffer, length))
        {
            throw new ListFileException(
                "Ce fichier est une archive ZIP ou un document Office renommé : ouvrez-le dans Excel ou Word, "
                + "ou donnez-lui l'extension .xlsx / .docx si c'en est un.");
        }
    }

    private static ListZipPackage OpenPackage(byte[] buffer, int length, string expectedExtension)
    {
        if (ListFileSniffer.IsOle2(buffer, length))
        {
            throw new ListFileException(ListFileSniffer.DescribeOle2(buffer, length));
        }

        string? foreign = ListFileSniffer.DescribeForeign(buffer, length);
        if (foreign != null) throw new ListFileException(foreign);

        try
        {
            return ListZipPackage.Open(buffer, length, new ListBudget());
        }
        catch (InvalidDataException) when (ListFileSniffer.LooksLikeText(buffer, length))
        {
            throw new ListFileException(
                $"Ce fichier {expectedExtension} n'est pas un vrai document Office : il contient du texte. "
                + "Renommez-le en .csv ou .txt, ou ouvrez-le dans Excel / Word et enregistrez-le en .xlsx / .docx.");
        }
    }

    /// <summary>Lit tout le flux, sans jamais dépasser <see cref="MaxFileBytes"/> en mémoire.</summary>
    private static (byte[] Buffer, int Length) ReadAllBounded(Stream stream)
    {
        if (!stream.CanRead) throw new ListFileException("Le fichier ne peut pas être lu.");

        long hint = 0;
        if (stream.CanSeek)
        {
            hint = Math.Max(0, stream.Length - stream.Position);
            if (hint > MaxFileBytes) throw new ListFileException(TooBigMessage());
        }

        int capacity = (int)Math.Min(Math.Max(hint + 1, 4096), MaxFileBytes + 1);
        byte[] buffer = new byte[capacity];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                long next = Math.Min((long)buffer.Length * 2, MaxFileBytes + 1);
                Array.Resize(ref buffer, (int)next);
            }

            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read <= 0) break;

            length += read;
            if (length > MaxFileBytes) throw new ListFileException(TooBigMessage());
        }

        return (buffer, length);
    }

    // ------------------------------------------------------------------------------------------------
    // Extensions
    // ------------------------------------------------------------------------------------------------

    /// <summary>Nom du fichier sans son dossier (l'antislash comme la barre oblique séparent les dossiers, quelle que soit la plateforme).</summary>
    private static string FileNameOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut < 0 ? path : path.Substring(cut + 1);
    }

    private static string ExtensionOf(string? name)
    {
        string file = FileNameOf(name);
        int dot = file.LastIndexOf('.');
        return dot < 0 || dot == file.Length - 1 ? "" : file.Substring(dot).ToLowerInvariant();
    }

    /// <summary>Type de lecture d'une extension, ou un message d'erreur (français).</summary>
    private static string? Classify(string extension, out Kind kind)
    {
        kind = Kind.Text;
        switch (extension)
        {
            case ".txt":
                return null;
            case ".csv":
                kind = Kind.Csv;
                return null;
            case ".xlsx":
                kind = Kind.Xlsx;
                return null;
            case ".docx":
                kind = Kind.Docx;
                return null;
        }

        if (LegacyExtensions.Contains(extension))
        {
            return $"Le format « {extension} » n'est pas pris en charge : ouvrez le fichier puis enregistrez-le en .xlsx / .docx "
                   + "(Fichier > Enregistrer sous), ou copiez la liste (Ctrl+C) et utilisez « Coller ».";
        }

        string what = extension.Length == 0 ? "Ce fichier n'a pas d'extension reconnue" : $"Le format « {extension} » n'est pas pris en charge";
        return what + " : formats acceptés .txt, .csv, .xlsx (Excel) et .docx (Word). Pour tout autre fichier, copiez la liste (Ctrl+C) et utilisez « Coller ».";
    }

    private static string SourceFor(string? name)
    {
        string file = FileNameOf(name);
        return file.Length == 0 ? "fichier" : $"fichier « {file} »";
    }

    // ------------------------------------------------------------------------------------------------
    // Erreurs
    // ------------------------------------------------------------------------------------------------

    private static string TooBigMessage()
        => $"Le fichier est trop volumineux (maximum {MaxFileBytes / 1024 / 1024} Mo). Réduisez la liste, ou copiez-la (Ctrl+C) et utilisez « Coller ».";

    private static ListReadResult Fail(string source, string message)
        => new(Array.Empty<string>(), Array.Empty<string>(), message, source);

    private static ListReadResult FromException(Exception ex, string source, Kind? kind)
    {
        string message;
        switch (ex)
        {
            case ListFileException known:
                message = known.Message;
                break;
            case FileNotFoundException:
            case DirectoryNotFoundException:
                message = "Fichier introuvable : il a peut-être été déplacé ou supprimé.";
                break;
            case UnauthorizedAccessException:
                message = "Accès refusé à ce fichier (droits insuffisants). Copiez-le dans un autre dossier puis réessayez.";
                break;
            case PathTooLongException:
                message = "Le chemin de ce fichier est trop long : déplacez-le dans un dossier plus court.";
                break;
            case IOException io when IsSharingViolation(io):
                message = "Ce fichier est utilisé par un autre programme (Excel ou Word est peut-être ouvert dessus) : "
                          + "fermez-le ou faites-en une copie, puis réessayez.";
                break;
            case IOException:
                message = "Ce fichier n'a pas pu être lu (erreur d'accès). Vérifiez qu'il est accessible, puis réessayez.";
                break;
            case InvalidDataException:
            case NotSupportedException:
                message = Corrupt(kind);
                break;
            case XmlException xml:
                message = xml.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                    ? "Ce fichier contient une déclaration DTD, interdite par sécurité : lecture refusée."
                    : Corrupt(kind);
                break;
            case OutOfMemoryException:
                message = "Mémoire insuffisante pour lire ce fichier : réduisez la liste, ou copiez-la (Ctrl+C) et utilisez « Coller ».";
                break;
            default:
                Interlocked.Increment(ref _unexpectedFailures);
                Log.Error("Lecture d'une liste : erreur inattendue (" + ex.GetType().Name + ")", ex);
                message = "Ce fichier n'a pas pu être lu (erreur inattendue). Copiez la liste (Ctrl+C) et utilisez « Coller » à la place.";
                break;
        }

        return Fail(source, message);
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code == 32 || code == 33;   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    private static string Corrupt(Kind? kind) => kind switch
    {
        Kind.Xlsx => "Ce classeur Excel est illisible : le fichier est endommagé ou protégé par un mot de passe. "
                     + "Ouvrez-le dans Excel et enregistrez-le de nouveau en .xlsx, ou copiez la colonne (Ctrl+C) et utilisez « Coller ».",
        Kind.Docx => "Ce document Word est illisible : le fichier est endommagé ou protégé par un mot de passe. "
                     + "Ouvrez-le dans Word et enregistrez-le de nouveau en .docx, ou copiez les lignes (Ctrl+C) et utilisez « Coller ».",
        _ => "Ce fichier est illisible ou endommagé. Copiez la liste (Ctrl+C) et utilisez « Coller » à la place.",
    };
}
