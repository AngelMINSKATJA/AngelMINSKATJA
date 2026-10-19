using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BarcodeTray;

/// <summary>
/// Lecture d'un document Word (.docx) en flux (XmlReader, jamais de DOM) : chaque paragraphe non vide du corps est une valeur,
/// dans l'ordre du document ; dans un tableau, seule la PREMIÈRE cellule de chaque ligne compte (tableaux imbriqués ignorés).
/// <para>
/// Texte d'un paragraphe = concaténation des &lt;w:t&gt; de ses exécutions (un mot coupé en plusieurs exécutions est recollé) ;
/// tabulation et saut de ligne deviennent une espace, le trait d'union insécable un « - ». Sont traversés : zones de contenu
/// (&lt;w:sdt&gt;, sauf celles qui n'affichent que leur texte indicatif), insertions suivies (&lt;w:ins&gt;), liens, champs.
/// Sont ignorés : suppressions suivies (&lt;w:del&gt;, &lt;w:delText&gt;), codes de champ (&lt;w:instrText&gt;), annotations phonétiques (&lt;w:rt&gt;), propriétés de mise en forme,
/// en-têtes, pieds de page, notes. Pour &lt;mc:AlternateContent&gt; seul le premier &lt;mc:Choice&gt; est lu (le &lt;mc:Fallback&gt; répète le
/// même contenu). Un paragraphe imbriqué dans un autre (zone de texte) devient une valeur à part, juste après son paragraphe hôte.
/// </para>
/// <para>
/// Remarques (comme pour .txt, .csv et .xlsx) : des paragraphes dont les parties sont séparées par une tabulation ou un saut de ligne
/// (Maj+Entrée) sont lus en un seul texte, et les cellules d'un tableau autres que la première sont ignorées : le lecteur les compte
/// pour que l'écran le dise, au lieu de laisser croire que la liste est complète.
/// </para>
/// </summary>
internal static class ListFileDocx
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string WordNsStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private const int TextCap = ListCollector.MaxValueChars + 1;

    /// <summary>Un paragraphe en cours de lecture (les paragraphes peuvent s'imbriquer via une zone de texte).</summary>
    private sealed class Paragraph
    {
        internal readonly StringBuilder Text = new();
        internal List<string>? Nested;

        private bool _hasText;          // du texte non blanc a déjà été lu
        private bool _separatorSeen;    // une tabulation ou un saut de ligne a suivi ce texte

        /// <summary>Du texte non blanc suit une tabulation ou un saut de ligne : le paragraphe contient plusieurs « morceaux ».</summary>
        internal bool Columns { get; private set; }

        internal void Append(string s)
        {
            if (Text.Length < TextCap) Text.Append(s);
            if (!string.IsNullOrWhiteSpace(s))
            {
                if (_separatorSeen) Columns = true;
                _hasText = true;
            }
        }

        /// <summary>Tabulation ou saut de ligne : devient une espace.</summary>
        internal void Separator()
        {
            Append(" ");
            if (_hasText) _separatorSeen = true;
        }
    }

    /// <summary>Constats faits pendant la lecture (pour les remarques).</summary>
    private sealed class DocxStats
    {
        internal int TabbedParagraphs;
        internal int ExtraCellRows;
    }

    internal static void Read(ListZipPackage pkg, ListCollector collector, List<string>? warnings = null)
    {
        string? part = pkg.FindMainPart("word/document.xml");
        ZipArchiveEntry? entry = part == null ? null : pkg.Find(part);
        if (entry == null)
        {
            throw NotADocument(pkg);
        }

        using XmlReader r = pkg.OpenXml(entry);
        if (r.MoveToContent() != XmlNodeType.Element || r.LocalName != "document")
        {
            throw NotADocument(pkg);   // la partie principale de l'archive n'est pas un document Word (classeur renommé...)
        }

        var stats = new DocxStats();
        ReadBody(r, collector, stats);

        if (warnings != null)
        {
            if (stats.TabbedParagraphs > 0)
            {
                warnings.Add($"{stats.TabbedParagraphs} paragraphe(s) contiennent des tabulations ou des sauts de ligne (Maj+Entrée) : le paragraphe entier sert de "
                             + "texte du code-barres, ses parties sont séparées par une espace. Pour un code par ligne, utilisez la touche Entrée seule.");
            }

            if (stats.ExtraCellRows > 0)
            {
                warnings.Add($"Seule la première colonne des tableaux a été lue : les autres colonnes ({stats.ExtraCellRows} ligne(s) de tableau concernée(s)) sont ignorées.");
            }
        }
    }

    private static ListFileException NotADocument(ListZipPackage pkg)
    {
        if (pkg.Has("xl/workbook.xml"))
        {
            return new ListFileException("Ce fichier est un classeur Excel, pas un document Word : changez son extension en .xlsx (ou rechargez-le comme classeur).");
        }

        if (pkg.Has("content.xml") && pkg.Has("mimetype"))
        {
            return new ListFileException("Ce fichier est au format OpenDocument (.odt / .ods) : ouvrez-le puis enregistrez-le en .docx / .xlsx.");
        }

        return new ListFileException("Ce fichier n'est pas un document Word (.docx) valide : la partie « word/document.xml » est introuvable. Ouvrez-le dans Word puis enregistrez-le de nouveau en .docx.");
    }

    private static bool IsWord(string ns) => ns == WordNs || ns == WordNsStrict;

    private static void ReadBody(XmlReader r, ListCollector collector, DocxStats stats)
    {
        var paragraphs = new Stack<Paragraph>();
        var choiceTaken = new Stack<bool>();       // un <mc:Choice> a-t-il déjà été pris dans cet <mc:AlternateContent> ?
        var placeholder = new Stack<bool>();       // la zone de contenu n'affiche-t-elle que son texte indicatif ?
        bool inTable = false;
        bool rowExtraCounted = false;
        int cell = 0;

        r.Read();
        while (!r.EOF)
        {
            ListXml.CheckDepth(r);

            if (r.NodeType == XmlNodeType.Element)
            {
                string ns = r.NamespaceURI;
                string local = r.LocalName;

                if (ns == McNs)
                {
                    if (local == "AlternateContent")
                    {
                        if (!r.IsEmptyElement) choiceTaken.Push(false);
                    }
                    else if (local == "Choice")
                    {
                        if (choiceTaken.Count > 0)
                        {
                            if (choiceTaken.Peek())
                            {
                                r.Skip();
                                continue;
                            }

                            choiceTaken.Pop();
                            choiceTaken.Push(true);
                        }
                    }
                    else if (local == "Fallback" && choiceTaken.Count > 0 && choiceTaken.Peek())
                    {
                        r.Skip();   // répète le contenu du <mc:Choice> déjà lu
                        continue;
                    }

                    r.Read();
                    continue;
                }

                if (IsWord(ns))
                {
                    switch (local)
                    {
                        case "p":
                            if (r.IsEmptyElement)
                            {
                                if (paragraphs.Count == 0) collector.AddEmpty(1);
                                r.Read();
                                continue;
                            }

                            paragraphs.Push(new Paragraph());
                            break;

                        case "t":
                            {
                                string text = ListXml.ReadText(r, TextCap);
                                if (paragraphs.Count > 0) paragraphs.Peek().Append(text);
                                continue;
                            }

                        case "tab":
                        case "br":
                        case "cr":
                        case "ptab":
                            if (paragraphs.Count > 0) paragraphs.Peek().Separator();
                            break;

                        case "noBreakHyphen":
                            if (paragraphs.Count > 0) paragraphs.Peek().Append("-");
                            break;

                        case "tbl":
                            if (inTable)
                            {
                                r.Skip();   // tableau imbriqué : ignoré
                                continue;
                            }

                            inTable = true;
                            cell = 0;
                            break;

                        case "tr":
                            cell = 0;
                            rowExtraCounted = false;
                            break;

                        case "tc":
                            cell++;
                            if (cell > 1)
                            {
                                // seule la première cellule de la ligne compte ; les autres sont sautées, mais on note celles qui avaient du texte
                                if (CellHasText(r) && !rowExtraCounted)
                                {
                                    rowExtraCounted = true;
                                    stats.ExtraCellRows++;
                                }

                                continue;
                            }

                            break;

                        case "sdt":
                            if (!r.IsEmptyElement) placeholder.Push(false);
                            break;

                        case "sdtPr":
                            {
                                bool showsPlaceholder = ListXml.SubtreeContains(r, "showingPlcHdr", e => !IsFalse(ListXml.AttributeByLocalName(e, "val")));
                                if (showsPlaceholder && placeholder.Count > 0)
                                {
                                    placeholder.Pop();
                                    placeholder.Push(true);
                                }

                                continue;
                            }

                        case "sdtContent":
                            if (placeholder.Count > 0 && placeholder.Peek())
                            {
                                r.Skip();   // « Cliquez ici pour entrer du texte. » : pas une valeur
                                continue;
                            }

                            break;

                        case "pPr":
                        case "rPr":
                        case "tblPr":
                        case "tblGrid":
                        case "trPr":
                        case "tcPr":
                        case "sectPr":
                        case "sdtEndPr":
                        case "del":
                        case "moveFrom":
                        case "instrText":
                        case "delText":
                        case "fldData":
                        case "rt":   // annotation phonétique (ruby) : le texte de base seul est lu
                            r.Skip();
                            continue;
                    }
                }

                r.Read();
                continue;
            }

            if (r.NodeType == XmlNodeType.EndElement)
            {
                string ns = r.NamespaceURI;
                string local = r.LocalName;
                if (ns == McNs)
                {
                    if (local == "AlternateContent" && choiceTaken.Count > 0) choiceTaken.Pop();
                }
                else if (IsWord(ns))
                {
                    switch (local)
                    {
                        case "p":
                            if (paragraphs.Count > 0 && !EndParagraph(paragraphs, collector, stats)) return;
                            break;
                        case "tbl":
                            inTable = false;
                            break;
                        case "sdt":
                            if (placeholder.Count > 0) placeholder.Pop();
                            break;
                    }
                }
            }

            r.Read();
        }
    }

    /// <summary>Termine le paragraphe courant. Faux : la limite de valeurs est atteinte.</summary>
    private static bool EndParagraph(Stack<Paragraph> paragraphs, ListCollector collector, DocxStats stats)
    {
        Paragraph p = paragraphs.Pop();
        string text = p.Text.ToString();
        if (p.Columns && text.Trim().Length > 0) stats.TabbedParagraphs++;

        if (paragraphs.Count > 0)
        {
            // Paragraphe d'une zone de texte : valeur à part, rangée après le paragraphe hôte.
            Paragraph host = paragraphs.Peek();
            host.Nested ??= new List<string>();
            if (text.Length > 0) host.Nested.Add(text);
            if (p.Nested != null) host.Nested.AddRange(p.Nested);
            return true;
        }

        if (!collector.AddRaw(text)) return false;
        if (p.Nested != null)
        {
            foreach (string nested in p.Nested)
            {
                if (!collector.AddRaw(nested)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parcourt une cellule de tableau qu'on ne lit pas (le lecteur est sur &lt;w:tc&gt;, elle est consommée en entier) et dit si elle
    /// contenait du texte (les suppressions suivies et les codes de champ ne comptent pas).
    /// </summary>
    private static bool CellHasText(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return false;
        }

        int depth = r.Depth;
        bool found = false;
        r.Read();
        while (!r.EOF)
        {
            ListXml.CheckDepth(r);
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) break;

            if (r.NodeType == XmlNodeType.Element && IsWord(r.NamespaceURI))
            {
                switch (r.LocalName)
                {
                    case "t":
                        if (!string.IsNullOrWhiteSpace(ListXml.ReadText(r, 64))) found = true;
                        continue;   // ReadText a déjà avancé
                    case "del":
                    case "moveFrom":
                    case "instrText":
                    case "delText":
                        r.Skip();
                        continue;
                }
            }

            r.Read();
        }

        r.Read();   // passe </w:tc>
        return found;
    }

    /// <summary>Valeur « faux » d'un attribut booléen Word (w:val="0", "false", "off").</summary>
    private static bool IsFalse(string? value)
    {
        if (value == null) return false;
        string v = value.Trim();
        return v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase);
    }
}
