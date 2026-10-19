using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BarcodeTray;

/// <summary>
/// Erreur « attendue » de lecture d'une liste : le message est en français, destiné à l'utilisateur tel quel.
/// Elle ne sort jamais de <see cref="ListFileReader"/> (elle devient <see cref="ListReadResult.Error"/>).
/// </summary>
internal sealed class ListFileException : Exception
{
    internal ListFileException(string message) : base(message)
    {
    }
}

/// <summary>Nettoyage d'une valeur lue (cellule, ligne, paragraphe) avant d'en faire un texte de code-barres.</summary>
internal static class ListClean
{
    /// <summary>
    /// Espaces insécables (U+00A0, U+202F...), espaces typographiques, tabulations et sauts de ligne deviennent UNE espace ;
    /// caractères de largeur nulle, BOM, trait d'union conditionnel et caractères de contrôle sont supprimés ; les bords sont rognés.
    /// Rien n'est réduit à l'intérieur (un code peut contenir plusieurs espaces) : « A  B » reste « A  B ».
    /// </summary>
    internal static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // Chemin rapide : ASCII imprimable sans espace en bordure (le cas courant).
        bool plain = true;
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            if (ch < 0x20 || ch > 0x7E)
            {
                plain = false;
                break;
            }
        }

        if (plain && s[0] != ' ' && s[s.Length - 1] != ' ') return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (Classify(c))
            {
                case CharKind.Keep:
                    sb.Append(c);
                    break;
                case CharKind.Space:
                    if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;   // CRLF = une seule espace
                    sb.Append(' ');
                    break;
            }
        }

        int start = 0;
        int end = sb.Length;
        while (start < end && sb[start] == ' ') start++;
        while (end > start && sb[end - 1] == ' ') end--;
        return end - start == sb.Length ? sb.ToString() : sb.ToString(start, end - start);
    }

    private enum CharKind
    {
        Keep,
        Space,
        Drop,
    }

    private static CharKind Classify(char c)
    {
        if (c >= 0x20 && c <= 0x7E) return CharKind.Keep;
        if (c < 0x20)
        {
            // tabulation, saut de ligne, tabulation verticale, saut de page, retour chariot
            return c >= '\t' && c <= '\r' ? CharKind.Space : CharKind.Drop;
        }

        if (c == '\u007F') return CharKind.Drop;
        if (c < 0x00A0) return c == '\u0085' ? CharKind.Space : CharKind.Drop;   // C1 : NEL = espace, le reste est supprimé
        if (c == '\u00A0') return CharKind.Space;                                    // espace insécable
        if (c == '\u00AD') return CharKind.Drop;                                     // trait d'union conditionnel
        if (c == '\u1680' || c == '\u202F' || c == '\u205F' || c == '\u3000') return CharKind.Space;
        if (c >= '\u2000' && c <= '\u200A') return CharKind.Space;                   // espaces typographiques (dont U+2007 insécable)
        if (c == '\u2028' || c == '\u2029') return CharKind.Space;
        if (c == '\u180E') return CharKind.Drop;
        if (c >= '\u200B' && c <= '\u200F') return CharKind.Drop;                    // largeur nulle, marques directionnelles
        if (c >= '\u202A' && c <= '\u202E') return CharKind.Drop;
        if (c >= '\u2060' && c <= '\u2064') return CharKind.Drop;
        if (c >= '\u2066' && c <= '\u2069') return CharKind.Drop;
        if (c == '\uFEFF') return CharKind.Drop;                                     // BOM / espace insécable de largeur nulle
        if (c >= '\uFE00' && c <= '\uFE0F') return CharKind.Drop;                    // sélecteurs de variante
        return CharKind.Keep;
    }
}

/// <summary>Constats faits par un lecteur sur une valeur (pour les remarques affichées après la lecture).</summary>
[Flags]
internal enum ListValueFlags
{
    None = 0,

    /// <summary>La valeur contenait un saut de ligne à l'intérieur du texte (Alt+Entrée dans une cellule Excel, champ CSV sur plusieurs lignes).</summary>
    LineBreak = 1,

    /// <summary>La valeur est un nombre écrit en notation scientifique (5,9012E+12), même sans signe devant l'exposant (cellule numérique Excel).</summary>
    Scientific = 2,
}

/// <summary>
/// Accumulateur commun aux lecteurs : nettoie, écarte les valeurs vides (en comptant celles qui sont ENTRE deux valeurs),
/// applique la limite de <see cref="ListFileReader.MaxValues"/> et l'option « ignorer la première ligne ».
/// </summary>
internal sealed class ListCollector
{
    /// <summary>Longueur au-delà de laquelle une valeur est tronquée (elle ne peut de toute façon plus devenir un code-barres).</summary>
    internal const int MaxValueChars = 10_000;

    private readonly List<string> _values = new();
    private readonly int _limit;
    private readonly bool _skipFirst;
    private long _interiorEmpty;
    private int _pendingEmpty;
    private int _longValues;
    private int _scientific;
    private int _lineBreaks;
    private ListValueFlags _firstFlags;

    internal ListCollector(bool skipFirstLine)
    {
        _skipFirst = skipFirstLine;
        // On garde une entrée de plus quand la première est un titre qui sera retiré.
        _limit = ListFileReader.MaxValues + (skipFirstLine ? 1 : 0);
    }

    /// <summary>Nombre de valeurs (non vides) retenues jusqu'ici.</summary>
    internal int Count => _values.Count;

    /// <summary>Vrai dès qu'une valeur de plus que la limite a été rencontrée (inutile de lire la suite).</summary>
    internal bool Truncated { get; private set; }

    /// <summary>Valeurs retenues (avant retrait éventuel du titre).</summary>
    internal IReadOnlyList<string> Values => _values;

    /// <summary>Repart de zéro (le lecteur Excel change de colonne).</summary>
    internal void Reset()
    {
        _values.Clear();
        _interiorEmpty = 0;
        _pendingEmpty = 0;
        _longValues = 0;
        _scientific = 0;
        _lineBreaks = 0;
        _firstFlags = ListValueFlags.None;
        Truncated = false;
    }

    /// <summary>Ajoute une valeur brute (nettoyée ici). Faux : la limite est dépassée, arrêtez la lecture.</summary>
    internal bool AddRaw(string? raw)
    {
        string clean = ListClean.Clean(raw);
        if (clean.Length == 0)
        {
            AddEmpty(1);
            return true;
        }

        return Add(clean, HasInnerLineBreak(raw) ? ListValueFlags.LineBreak : ListValueFlags.None);
    }

    /// <summary>
    /// Vrai si le texte brut contient un saut de ligne AU MILIEU (les bords sont rognés de toute façon) : le nettoyage le remplace par
    /// une espace, ce que l'utilisateur doit savoir (« A\nB » devient « A B »).
    /// </summary>
    internal static bool HasInnerLineBreak(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return raw.AsSpan().Trim().IndexOfAny(LineBreakChars) >= 0;
    }

    private static readonly SearchValues<char> LineBreakChars = SearchValues.Create("\n\r\u000B\u000C\u0085\u2028\u2029");

    /// <summary>
    /// Nombre en notation scientifique tel qu'Excel l'affiche quand une colonne est trop étroite (« 5,9012E+12 », « 5.9012E+12 »,
    /// « 1,23E-05 ») : chiffres, fraction facultative (point ou virgule), « E », un SIGNE obligatoire, exposant. Le signe évite de prendre
    /// pour un nombre une référence comme « 4E5 » ou « 12E3 ».
    /// </summary>
    internal static bool IsScientificNotation(string? s)
    {
        if (s == null || s.Length < 4) return false;

        int n = s.Length;
        int i = 0;
        if (s[i] == '+' || s[i] == '-') i++;

        int start = i;
        while (i < n && s[i] >= '0' && s[i] <= '9') i++;
        if (i == start) return false;

        if (i < n && (s[i] == '.' || s[i] == ','))
        {
            i++;
            int fraction = i;
            while (i < n && s[i] >= '0' && s[i] <= '9') i++;
            if (i == fraction) return false;
        }

        if (i >= n || (s[i] != 'E' && s[i] != 'e')) return false;
        i++;
        if (i >= n || (s[i] != '+' && s[i] != '-')) return false;
        i++;

        int exponent = i;
        while (i < n && s[i] >= '0' && s[i] <= '9') i++;
        return i == n && i > exponent;
    }

    /// <summary>Note <paramref name="count"/> entrées vides (comptées seulement si elles se retrouvent entre deux valeurs).</summary>
    internal void AddEmpty(int count)
    {
        if (count > 0 && _values.Count > 0)
        {
            _pendingEmpty = (int)Math.Min((long)_pendingEmpty + count, int.MaxValue / 2);
        }
    }

    /// <summary>Ajoute une valeur DÉJÀ nettoyée et non vide. Faux : la limite est dépassée, arrêtez la lecture.</summary>
    /// <param name="clean">Valeur nettoyée.</param>
    /// <param name="flags">Constats du lecteur sur la valeur d'origine (la notation scientifique d'un texte est reconnue ici).</param>
    internal bool Add(string clean, ListValueFlags flags = ListValueFlags.None)
    {
        if (_values.Count >= _limit)
        {
            Truncated = true;
            return false;
        }

        _interiorEmpty += _pendingEmpty;
        _pendingEmpty = 0;

        if ((flags & ListValueFlags.Scientific) == 0 && IsScientificNotation(clean)) flags |= ListValueFlags.Scientific;
        if ((flags & ListValueFlags.Scientific) != 0) _scientific++;
        if ((flags & ListValueFlags.LineBreak) != 0) _lineBreaks++;
        if (_values.Count == 0) _firstFlags = flags;

        if (clean.Length > MaxValueChars)
        {
            // MaxValueChars + 1 caractères : toujours trop long pour un code-barres (la ligne est refusée à l'affichage).
            clean = clean.Substring(0, MaxValueChars + 1);
            _longValues++;
        }

        _values.Add(clean);
        return true;
    }

    /// <summary>Construit le résultat : retire le titre, compose les avertissements, ou l'erreur « Aucune valeur trouvée ».</summary>
    /// <param name="source">Provenance affichée.</param>
    /// <param name="extraWarnings">Avertissements propres au format (ajoutés après les avertissements communs).</param>
    /// <param name="emptyMessage">Message si rien n'est exploitable (commence par « Aucune valeur trouvée »).</param>
    internal ListReadResult Build(string source, List<string> extraWarnings, string emptyMessage)
    {
        var warnings = new List<string>();
        var values = new List<string>(_values);
        bool removedTitle = false;
        string? title = null;
        int scientific = _scientific;
        int lineBreaks = _lineBreaks;
        if (_skipFirst && values.Count > 0)
        {
            title = values[0];
            values.RemoveAt(0);   // le titre = la PREMIÈRE entrée non vide après nettoyage
            removedTitle = true;
            if ((_firstFlags & ListValueFlags.Scientific) != 0) scientific--;     // le titre écarté ne compte pas dans les remarques
            if ((_firstFlags & ListValueFlags.LineBreak) != 0) lineBreaks--;
        }

        if (Truncated)
        {
            warnings.Add($"Seules les {ListFileReader.MaxValues} premières lignes ont été gardées.");
        }

        if (_interiorEmpty > 0)
        {
            warnings.Add($"{_interiorEmpty} ligne(s) vide(s) ignorée(s).");
        }

        if (_longValues > 0)
        {
            warnings.Add($"{_longValues} valeur(s) de plus de {MaxValueChars} caractères tronquée(s) : elles ne peuvent pas devenir un code-barres.");
        }

        if (scientific > 0 && values.Count > 0)
        {
            warnings.Add($"{scientific} nombre(s) en notation scientifique (ex. 5,9012E+12) : Excel a abrégé des nombres trop longs pour la colonne, "
                         + "des chiffres sont perdus. Élargissez la colonne ou formatez la colonne en Texte dans Excel, puis recopiez ou réexportez la liste.");
        }

        if (lineBreaks > 0 && values.Count > 0)
        {
            warnings.Add($"{lineBreaks} valeur(s) contenaient un saut de ligne (Alt+Entrée dans Excel) : il a été remplacé par une espace.");
        }

        warnings.AddRange(extraWarnings);

        if (values.Count == 0)
        {
            string message = emptyMessage;
            if (removedTitle)
            {
                message += " Seule la première ligne (le titre) était présente : décochez « La première ligne est un titre » si c'est une valeur.";
            }

            if (warnings.Count > 0)
            {
                message += " " + string.Join(" ", warnings);
            }

            return new ListReadResult(Array.Empty<string>(), Array.Empty<string>(), message, source);
        }

        // Le titre retiré est rendu avec le résultat : l'écran dit QUELLE ligne a disparu (sans cela, un code perdu passerait inaperçu).
        return new ListReadResult(values, warnings, null, source, removedTitle ? title : null);
    }
}

/// <summary>
/// Plafonds communs à la lecture d'un classeur / document (protection contre les archives piégées) :
/// octets RÉELLEMENT décompressés au total et durée.
/// </summary>
internal sealed class ListBudget
{
    /// <summary>Taille décompressée maximale d'une partie (une feuille, la table des chaînes, le document).</summary>
    internal const long MaxEntryBytes = 50L * 1024 * 1024;

    /// <summary>Taille décompressée maximale cumulée.</summary>
    internal const long MaxTotalBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Taille décompressée maximale d'UNE FEUILLE de classeur : tout le plafond cumulé. Une feuille filtrée doit être lue en entier
    /// (les lignes masquées sont sautées une à une) : un export réel de 330 000 lignes pèse plus de 80 Mo de XML pour un fichier de
    /// 14 Mo seulement. Les autres parties gardent <see cref="MaxEntryBytes"/>.
    /// </summary>
    internal const long MaxSheetBytes = MaxTotalBytes;

    /// <summary>Nombre maximal d'entrées d'une archive (un classeur ou un document Word en a quelques dizaines).</summary>
    internal const int MaxEntries = 5000;

    /// <summary>Durée maximale d'une lecture.</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _maxTotal;
    private readonly TimeSpan _maxTime;
    private long _total;

    internal ListBudget()
        : this(MaxTotalBytes, MaxDuration)
    {
    }

    internal ListBudget(long maxTotalBytes, TimeSpan maxTime)
    {
        _maxTotal = maxTotalBytes;
        _maxTime = maxTime;
    }

    /// <summary>Octets décompressés lus jusqu'ici.</summary>
    internal long TotalBytes => _total;

    internal void Consume(int bytes)
    {
        _total += bytes;
        if (_total > _maxTotal)
        {
            throw new ListFileException(
                "Le contenu de ce fichier est démesuré une fois décompressé (fichier piégé ou endommagé) : lecture refusée.");
        }

        CheckTime();
    }

    internal void CheckTime()
    {
        if (_clock.Elapsed > _maxTime)
        {
            throw new ListFileException("La lecture de ce fichier prend trop de temps : elle a été interrompue. Réduisez la liste ou copiez-la puis utilisez « Coller ».");
        }
    }
}

/// <summary>
/// Flux plafonné : compte les octets RÉELLEMENT lus (la taille annoncée par l'archive ne prouve rien) et refuse d'aller au-delà.
/// </summary>
internal sealed class ListLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly ListBudget _budget;
    private long _read;

    internal ListLimitedStream(Stream inner, long limit, ListBudget budget)
    {
        _inner = inner;
        _limit = limit;
        _budget = budget;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Account(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Account(_inner.Read(buffer));

    private int Account(int n)
    {
        if (n > 0)
        {
            _read += n;
            if (_read > _limit)
            {
                throw new ListFileException(
                    "Une partie de ce fichier est démesurée une fois décompressée (fichier piégé ou endommagé) : lecture refusée.");
            }

            _budget.Consume(n);
        }

        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
