using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BarcodeTray;

/// <summary>Une relation d'un fichier .rels (identifiant, type, cible interne).</summary>
internal readonly record struct ListRelationship(string Id, string Type, string Target);

/// <summary>
/// Accès en lecture seule, plafonné et sans extraction sur disque, à un paquet Office (.xlsx / .docx = archive ZIP) :
/// nombre d'entrées plafonné AVANT l'ouverture, noms normalisés (les entrées « ../ », absolues ou à lecteur sont ignorées),
/// octets décompressés comptés pour de vrai (<see cref="ListLimitedStream"/>).
/// </summary>
internal sealed class ListZipPackage : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _map;
    private readonly ListBudget _budget;

    private ListZipPackage(ZipArchive zip, Dictionary<string, ZipArchiveEntry> map, ListBudget budget)
    {
        _zip = zip;
        _map = map;
        _budget = budget;
    }

    /// <summary>Budget partagé par toutes les parties ouvertes de ce paquet.</summary>
    internal ListBudget Budget => _budget;

    /// <summary>Nombre d'entrées utilisables (hors dossiers et noms dangereux).</summary>
    internal int EntryCount => _map.Count;

    /// <summary>Ouvre l'archive. Lève <see cref="InvalidDataException"/> (archive illisible) ou <see cref="ListFileException"/> (trop d'entrées).</summary>
    internal static ListZipPackage Open(byte[] data, int length, ListBudget budget)
    {
        int? declared = DeclaredEntryCount(data, length);
        if (declared.HasValue && declared.Value > ListBudget.MaxEntries)
        {
            throw new ListFileException("Cette archive contient un nombre anormal de fichiers : lecture refusée (fichier piégé ou endommagé).");
        }

        var stream = new MemoryStream(data, 0, length, writable: false);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        try
        {
            if (zip.Entries.Count > ListBudget.MaxEntries)
            {
                throw new ListFileException("Cette archive contient un nombre anormal de fichiers : lecture refusée (fichier piégé ou endommagé).");
            }

            var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string? name = NormalizeName(entry.FullName);
                if (name == null) continue;               // dossier, chemin absolu hors paquet, « .. » : jamais utilisé
                map.TryAdd(name, entry);                  // doublon de nom : la première entrée gagne
            }

            return new ListZipPackage(zip, map, budget);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public void Dispose() => _zip.Dispose();

    /// <summary>Vrai si la partie existe (nom normalisé, insensible à la casse).</summary>
    internal bool Has(string partName) => Find(partName) != null;

    /// <summary>Noms des parties (normalisés) : pour reconnaître un autre type de document.</summary>
    internal bool HasAny(Func<string, bool> predicate)
    {
        foreach (string name in _map.Keys)
        {
            if (predicate(name)) return true;
        }

        return false;
    }

    internal ZipArchiveEntry? Find(string partName)
    {
        string? name = NormalizeName(partName);
        if (name == null) return null;
        if (_map.TryGetValue(name, out ZipArchiveEntry? entry)) return entry;

        // Cible de relation encodée en pourcentage (« My%20Sheet.xml ») alors que l'entrée ne l'est pas.
        if (name.IndexOf('%') >= 0)
        {
            string unescaped = SafeUnescape(name);
            if (_map.TryGetValue(unescaped, out entry)) return entry;
        }

        return null;
    }

    /// <summary>Ouvre une partie en lecture plafonnée (octets réellement lus).</summary>
    /// <param name="entry">Partie à ouvrir.</param>
    /// <param name="limit">Plafond de cette partie (<see cref="ListBudget.MaxEntryBytes"/> par défaut ; une feuille de classeur a le sien).</param>
    internal Stream Open(ZipArchiveEntry entry, long limit = ListBudget.MaxEntryBytes)
    {
        // La taille annoncée par l'archive ne prouve rien (elle peut mentir) : seul le comptage des octets réellement lus fait foi.
        // Une feuille de 100 Mo dont on n'a besoin que du début reste donc lisible ; une bombe est coupée au plafond.
        return new ListLimitedStream(entry.Open(), limit, _budget);
    }

    /// <summary>Ouvre un lecteur XML sûr sur une partie.</summary>
    internal XmlReader OpenXml(ZipArchiveEntry entry, long limit = ListBudget.MaxEntryBytes) => ListXml.Create(Open(entry, limit));

    /// <summary>Relations d'un fichier .rels (liste vide s'il n'existe pas). Les cibles externes sont ignorées.</summary>
    internal List<ListRelationship> ReadRelationships(string relsPart)
    {
        var result = new List<ListRelationship>();
        ZipArchiveEntry? entry = Find(relsPart);
        if (entry == null) return result;

        using XmlReader r = OpenXml(entry);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
            if (string.Equals(r.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;

            string? id = r.GetAttribute("Id");
            string? type = r.GetAttribute("Type");
            string? target = r.GetAttribute("Target");
            if (id == null || type == null || string.IsNullOrEmpty(target)) continue;
            if (result.Count >= 2000) break;   // garde-fou
            result.Add(new ListRelationship(id, type, target));
        }

        return result;
    }

    /// <summary>
    /// Partie principale du document (relation « officeDocument » de _rels/.rels, sinon le nom habituel).
    /// Null si elle n'existe pas dans l'archive.
    /// </summary>
    internal string? FindMainPart(string fallback)
    {
        foreach (ListRelationship rel in ReadRelationships("_rels/.rels"))
        {
            if (!rel.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) continue;
            string? part = ResolveTarget("", rel.Target);
            if (part != null && Has(part)) return part;
        }

        return Has(fallback) ? fallback : null;
    }

    /// <summary>Chemin du fichier de relations d'une partie : « xl/workbook.xml » donne « xl/_rels/workbook.xml.rels ».</summary>
    internal static string RelsPathFor(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "_rels/" + part + ".rels" : part.Substring(0, slash) + "/_rels/" + part.Substring(slash + 1) + ".rels";
    }

    /// <summary>Dossier d'une partie (« xl/workbook.xml » donne « xl »).</summary>
    internal static string DirectoryOf(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "" : part.Substring(0, slash);
    }

    /// <summary>
    /// Cible d'une relation (relative au dossier de la partie, ou absolue « /xl/... ») en nom de partie normalisé ;
    /// null si elle sort de l'archive (« ../../ ») ou est invalide.
    /// </summary>
    internal static string? ResolveTarget(string baseDirectory, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        string t = target.Replace('\\', '/');
        int cut = t.IndexOfAny(new[] { '#', '?' });
        if (cut >= 0) t = t.Substring(0, cut);
        if (t.Length == 0) return null;

        string full = t[0] == '/' || baseDirectory.Length == 0 ? t : baseDirectory + "/" + t;
        return NormalizeName(full);
    }

    /// <summary>
    /// Nom de partie normalisé : « \ » devient « / », segments « . » et vides supprimés, « .. » remonte d'un dossier.
    /// Null pour un dossier, un chemin qui remonte au-dessus de la racine, un nom à lecteur (« C: ») ou contenant un caractère nul.
    /// </summary>
    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.IndexOf('\0') >= 0) return null;

        string n = name.Replace('\\', '/');
        if (n[n.Length - 1] == '/') return null;

        var parts = new List<string>();
        foreach (string segment in n.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;   // sort de l'archive
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (segment.IndexOf(':') >= 0) return null;   // « C:\... » ou flux de données alternatif
            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    private static string SafeUnescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }

    /// <summary>
    /// Nombre d'entrées annoncé par le répertoire central (lu dans l'enregistrement de fin, y compris ZIP64), sans rien allouer.
    /// Null si l'enregistrement n'est pas trouvé (la bibliothèque produira alors sa propre erreur).
    /// </summary>
    internal static int? DeclaredEntryCount(byte[] d, int length)
    {
        if (length < 22) return null;
        int lowest = Math.Max(0, length - 22 - 65535);
        for (int i = length - 22; i >= lowest; i--)
        {
            if (d[i] != 0x50 || d[i + 1] != 0x4B || d[i + 2] != 0x05 || d[i + 3] != 0x06) continue;

            int total = d[i + 10] | (d[i + 11] << 8);
            if (total != 0xFFFF) return total;

            // ZIP64 : le localisateur est juste avant l'enregistrement de fin (20 octets).
            int loc = i - 20;
            if (loc >= 0 && d[loc] == 0x50 && d[loc + 1] == 0x4B && d[loc + 2] == 0x06 && d[loc + 3] == 0x07)
            {
                long off = BitConverter.ToInt64(d, loc + 8);
                if (off >= 0 && off + 56 <= length && d[off] == 0x50 && d[off + 1] == 0x4B && d[off + 2] == 0x06 && d[off + 3] == 0x06)
                {
                    long total64 = BitConverter.ToInt64(d, (int)off + 32);
                    return total64 > int.MaxValue ? int.MaxValue : (int)Math.Max(0, total64);
                }
            }

            return total;
        }

        return null;
    }
}

/// <summary>Outils XML communs : lecteur sûr (pas de DTD, pas de résolution externe) et lecture de texte plafonnée.</summary>
internal static class ListXml
{
    /// <summary>Profondeur maximale d'imbrication acceptée (un document réel en a quelques dizaines).</summary>
    internal const int MaxDepth = 400;

    // Au moins le plafond d'octets cumulé (200 Mo) : c'est le comptage d'octets, avec son message clair, qui doit couper en premier.
    private const long MaxCharacters = 220_000_000;

    /// <summary>Lecteur XML en flux, sûr : DTD interdite (XXE, « milliards de rires »), aucun résolveur, document borné.</summary>
    internal static XmlReader Create(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,     // sans effet avec DTD interdite ; explicite par prudence
            MaxCharactersInDocument = MaxCharacters,
            CheckCharacters = false,           // tolère un caractère de contrôle écrit tel quel (le nettoyage le retire)
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };
        return XmlReader.Create(stream, settings);
    }

    /// <summary>Refuse une imbrication démesurée.</summary>
    internal static void CheckDepth(XmlReader r)
    {
        if (r.Depth > MaxDepth)
        {
            throw new ListFileException("Ce fichier contient une structure XML anormalement imbriquée : lecture refusée (fichier piégé ou endommagé).");
        }
    }

    [ThreadStatic]
    private static StringBuilder? _scratch;

    [ThreadStatic]
    private static char[]? _chunk;

    /// <summary>
    /// Lit le texte d'un élément (le lecteur est sur son début) et le consomme en entier, fin comprise : on se retrouve sur le nœud suivant.
    /// Les éléments enfants sont ignorés ; le texte au-delà de <paramref name="cap"/> caractères est lu (par morceaux, sans jamais
    /// construire un texte géant en mémoire) mais jeté.
    /// </summary>
    internal static string ReadText(XmlReader r, int cap)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return "";
        }

        int depth = r.Depth;
        StringBuilder sb = _scratch ??= new StringBuilder(256);
        sb.Clear();
        char[] chunk = _chunk ??= new char[2048];

        r.Read();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;

            switch (r.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (sb.Length < cap)
                    {
                        if (r.CanReadValueChunk)
                        {
                            int n;
                            while ((n = r.ReadValueChunk(chunk, 0, chunk.Length)) > 0)
                            {
                                if (sb.Length < cap) sb.Append(chunk, 0, Math.Min(n, cap - sb.Length));
                            }
                        }
                        else
                        {
                            string v = r.Value;
                            sb.Append(v, 0, Math.Min(v.Length, cap - sb.Length));
                        }
                    }

                    r.Read();
                    break;
                case XmlNodeType.Element:
                    r.Skip();
                    break;
                default:
                    r.Read();
                    break;
            }
        }

        r.Read();   // passe la balise de fin
        string result = sb.ToString();
        if (sb.Capacity > 64 * 1024) _scratch = null;   // ne garde pas un gros tampon en mémoire
        return result;
    }

    /// <summary>Parcourt le sous-arbre de l'élément courant (consommé en entier) et dit s'il contient un élément de ce nom local.</summary>
    internal static bool SubtreeContains(XmlReader r, string localName, Func<XmlReader, bool>? accept = null)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return false;
        }

        int depth = r.Depth;
        bool found = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;
            if (r.NodeType == XmlNodeType.Element && r.LocalName == localName && (accept == null || accept(r))) found = true;
            if (r.Depth > MaxDepth) CheckDepth(r);
        }

        r.Read();
        return found;
    }

    /// <summary>Attribut booléen OOXML : « 1 », « true », « on » (insensible à la casse) ; absent = faux.</summary>
    internal static bool IsTrue(string? value)
    {
        if (value == null) return false;
        string v = value.Trim();
        return v == "1"
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Valeur d'un attribut désigné par son nom local, quel que soit son espace de noms (null s'il est absent).</summary>
    internal static string? AttributeByLocalName(XmlReader r, string localName)
    {
        if (!r.HasAttributes) return null;
        string? result = null;
        if (r.MoveToFirstAttribute())
        {
            do
            {
                if (r.LocalName == localName)
                {
                    result = r.Value;
                    break;
                }
            }
            while (r.MoveToNextAttribute());

            r.MoveToElement();
        }

        return result;
    }

    /// <summary>
    /// Décode les échappements Excel « _xHHHH_ » (caractère de contrôle écrit en toutes lettres dans le texte d'une cellule) :
    /// « _x000D_ » est un retour chariot ; « _x005F_x000D_ » est le texte littéral « _x000D_ ».
    /// </summary>
    internal static string DecodeXmlEscapes(string s)
    {
        int first = s.IndexOf("_x", StringComparison.Ordinal);
        if (first < 0) return s;

        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '_' && i + 6 < s.Length && s[i + 1] == 'x' && s[i + 6] == '_' && IsHex4(s, i + 2))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                i += 7;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsHex4(string s, int start)
    {
        for (int k = start; k < start + 4; k++)
        {
            char c = s[k];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }

        return true;
    }
}
