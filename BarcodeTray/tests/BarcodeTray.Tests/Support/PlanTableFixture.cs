using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BarcodeTray.Tests.Support;

/// <summary>
/// Le tableau « Plan SE » du client, reconstitué d'après la capture d'écran : en-tête
/// <c>Plan SE | HB1 (20) | HBA (20) | HB2 (8) | VF1 (24) | VF2 (36)</c>, une ligne par plan (« 2020Q7E 22061 »)
/// et une croix dans la colonne du type. Tableau filtré : les lignes masquées ne sont pas copiées.
/// Les mêmes numéros reviennent sous deux types (22120, 22151, 22103).
/// <para>Fonctions pures (aucune dépendance à System.Drawing) : utilisables sous Linux comme sous Windows.</para>
/// </summary>
internal static class PlanTableFixture
{
    /// <summary>Noms de types attendus après normalisation (la colonne HBA est vide dans le tableau réel).</summary>
    internal static readonly string[] Types = { "HB1", "HBA", "HB2", "VF1", "VF2" };

    /// <summary>Cellules d'en-tête telles que copiées par Excel (le nombre entre parenthèses ne fait pas partie du type).</summary>
    internal static readonly string[] Headers = { "HB1 (20)", "HBA (20)", "HB2 (8)", "VF1 (24)", "VF2 (36)" };

    /// <summary>Une ligne du tableau : le n° de plan (sans « 2020Q7E ») et le type où se trouve la croix.</summary>
    internal readonly record struct Row(string Type, string Plan);

    /// <summary>Les lignes du tableau, dans l'ordre d'affichage.</summary>
    internal static IReadOnlyList<Row> Rows { get; } = BuildRows();

    /// <summary>Étiquettes attendues de l'analyse : une par croix, dans l'ordre du tableau.</summary>
    internal static IReadOnlyList<LabelItem> ExpectedItems { get; } = Rows.Select(r => new LabelItem(r.Type, r.Plan)).ToList();

    private static List<Row> BuildRows()
    {
        var rows = new List<Row>();

        // Bloc HB1 : 22061 .. 22104, puis 22120 (ligne plus haute dans le tableau).
        for (int plan = 22061; plan <= 22104; plan++) rows.Add(new Row("HB1", plan.ToString()));
        rows.Add(new Row("HB1", "22120"));

        // Bloc HB2 : 22110 .. 22121, 22151, 22123 .. 22125, 22103, 21970, 21969 (22120, 22151 et 22103 existent aussi ailleurs).
        foreach (int plan in new[] { 22110, 22111, 22112, 22113, 22114, 22115, 22116, 22117, 22118, 22119, 22120, 22121, 22151, 22123, 22124, 22125, 22103, 21970, 21969 })
            rows.Add(new Row("HB2", plan.ToString()));

        // Bloc VF1 : 22148 .. 22162 avec des trous.
        foreach (int plan in new[] { 22148, 22149, 22150, 22151, 22152, 22154, 22156, 22157, 22158, 22160, 22161, 22162 })
            rows.Add(new Row("VF1", plan.ToString()));

        // Bloc VF2 : 22168 .. 22178, 22180, 22181.
        for (int plan = 22168; plan <= 22178; plan++) rows.Add(new Row("VF2", plan.ToString()));
        rows.Add(new Row("VF2", "22180"));
        rows.Add(new Row("VF2", "22181"));

        return rows;
    }

    /// <summary>Variantes du texte du presse-papiers (par défaut : le texte « Excel » le plus réaliste).</summary>
    internal sealed class Options
    {
        /// <summary>Fin de ligne entre les lignes (Excel : CRLF).</summary>
        public string NewLine { get; init; } = "\r\n";

        /// <summary>Termine le texte par une fin de ligne (Excel le fait toujours).</summary>
        public bool FinalNewLine { get; init; } = true;

        /// <summary>Les cellules vides de fin de ligne sont présentes (tabulations de fin), comme dans le vrai presse-papiers.</summary>
        public bool TrailingTabs { get; init; } = true;

        /// <summary>L'en-tête « HB1 (20) » est copié entre guillemets avec un saut de ligne interne (« "HB1\n(20)" »).</summary>
        public bool QuotedHeaderWithNewLine { get; init; } = true;

        /// <summary>Quelques lignes ont une espace insécable (U+00A0) et/ou une croix en minuscule, ou entourée d'espaces.</summary>
        public bool Quirks { get; init; } = true;

        /// <summary>Ajoute un BOM (U+FEFF) en tête du texte.</summary>
        public bool Bom { get; init; }

        /// <summary>Lignes vides ajoutées avant l'en-tête (sélection commençant par une ligne vide).</summary>
        public int LeadingEmptyRows { get; init; }

        /// <summary>Lignes vides ajoutées à la fin (en plus de la fin de ligne finale).</summary>
        public int TrailingEmptyRows { get; init; }
    }

    /// <summary>Fabrique le texte tel que le copierait Excel pour la sélection complète (en-tête compris).</summary>
    internal static string BuildExcelText(Options? options = null)
    {
        Options o = options ?? new Options();
        var sb = new StringBuilder();
        if (o.Bom) sb.Append('﻿');

        // Une ligne vide copiée par Excel sur 6 colonnes = 5 tabulations.
        for (int k = 0; k < o.LeadingEmptyRows; k++) sb.Append(o.TrailingTabs ? "\t\t\t\t\t" : "").Append(o.NewLine);

        var lines = new List<string>();

        string[] headerCells = Headers.ToArray();
        if (o.QuotedHeaderWithNewLine)
        {
            headerCells[0] = "\"HB1\n(20)\"";
            headerCells[3] = "\"VF1\n(24)\"";
        }

        lines.Add("Plan SE\t" + string.Join("\t", headerCells));

        for (int r = 0; r < Rows.Count; r++)
        {
            Row row = Rows[r];
            string first = "2020Q7E " + row.Plan;
            string cross = "X";

            if (o.Quirks)
            {
                if (r % 7 == 3) first = "2020Q7E " + row.Plan;       // espace insécable entre le préfixe et le n°
                if (r % 11 == 5) cross = "x";                              // croix en minuscule
                if (r % 13 == 8) cross = "X ";                             // espace après la croix
                if (r % 17 == 9) cross = " X";                        // espace insécable avant la croix
                if (r % 19 == 4) first += " ";                        // espace insécable de fin
            }

            int column = Array.IndexOf(Types, row.Type);
            var cells = new string[1 + Types.Length];
            for (int c = 0; c < cells.Length; c++) cells[c] = "";
            cells[0] = first;
            cells[1 + column] = cross;

            string line = string.Join("\t", cells);
            if (!o.TrailingTabs) line = line.TrimEnd('\t');
            lines.Add(line);
        }

        sb.Append(string.Join(o.NewLine, lines));
        if (o.FinalNewLine) sb.Append(o.NewLine);

        for (int k = 0; k < o.TrailingEmptyRows; k++) sb.Append(o.TrailingTabs ? "\t\t\t\t\t" : "").Append(o.NewLine);

        return sb.ToString();
    }
}
