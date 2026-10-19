using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Code128;

namespace BarcodeTray;

/// <summary>Gravité du constat sur une ligne de la fenêtre « Codes-barres en série ».</summary>
internal enum BarcodeRowSeverity
{
    /// <summary>Rien à signaler.</summary>
    Ok,

    /// <summary>Imprimable, mais à vérifier (barres très fines, code-barres réduit pour tenir sur l'étiquette).</summary>
    Warning,

    /// <summary>Non imprimable (texte vide, caractère interdit, trop long).</summary>
    Invalid,
}

/// <summary>
/// Constat sur le texte d'une ligne, établi SANS dessiner d'image (seul le nombre de symboles Code 128 est calculé) :
/// texte rogné, erreur éventuelle (français), barres trop fines ou code-barres réduit à l'impression.
/// </summary>
/// <param name="Text">Texte rogné, tel qu'il sera encodé et imprimé.</param>
/// <param name="Error">Message d'erreur (français) si la ligne ne peut pas être imprimée, sinon null.</param>
/// <param name="SymbolCount">Nombre de symboles Code 128 (départ, données, contrôle et arrêt compris) ; 0 si invalide.</param>
/// <param name="ModulePx">Largeur retenue de la barre la plus fine, en points ; 0 si invalide.</param>
/// <param name="ThinBars">Vrai si la barre la plus fine a dû être réduite à 1 point (lecture peu fiable).</param>
/// <param name="Scaled">Vrai si le code-barres reste trop large pour le ruban même à 1 point : l'impression le réduit.</param>
/// <param name="Hint">Remarque orange (français) pour <paramref name="ThinBars"/> ou <paramref name="Scaled"/>, sinon null.</param>
internal sealed record BarcodeRowAnalysis(
    string Text, string? Error, int SymbolCount, int ModulePx, bool ThinBars, bool Scaled, string? Hint)
{
    /// <summary>La ligne peut être imprimée.</summary>
    internal bool IsValid => Error == null;

    internal BarcodeRowSeverity Severity
        => Error != null ? BarcodeRowSeverity.Invalid : (ThinBars || Scaled ? BarcodeRowSeverity.Warning : BarcodeRowSeverity.Ok);

    /// <summary>Le message à montrer pour cette ligne (erreur, sinon remarque), ou null.</summary>
    internal string? Message => Error ?? Hint;
}

/// <summary>Première ligne qui ne peut pas être imprimée, avec le message (français) à montrer.</summary>
internal sealed record BarcodeRowProblem(int Row, string Message);

/// <summary>Décompte des constats d'une liste (pour le résumé et le compte rendu).</summary>
/// <param name="Total">Nombre de lignes.</param>
/// <param name="Invalid">Lignes non imprimables.</param>
/// <param name="Thin">Lignes à barres très fines (hors lignes réduites).</param>
/// <param name="Scaled">Lignes réduites pour tenir sur l'étiquette.</param>
/// <param name="Duplicates">Lignes dont le texte est déjà apparu plus haut dans la liste.</param>
internal readonly record struct BarcodeListStats(int Total, int Invalid, int Thin, int Scaled, int Duplicates)
{
    internal int Warnings => Thin + Scaled;
}

/// <summary>
/// Logique PURE de la fenêtre « Codes-barres en série » (aucune dépendance à Windows Forms ni à GDI+) : analyse des lignes sans
/// dessiner d'image, résumé, textes des messages, choix de la première ligne invalide, état des colonnes, traitement du
/// glisser-déposer. Le déroulement d'une série (<see cref="BatchLogic.Run"/>), le choix des lignes (<see cref="BatchLogic.ChooseRows"/>)
/// et la disponibilité des boutons (<see cref="BatchLogic.ComputeButtons"/>) sont ceux de la fenêtre des étiquettes de plans de
/// châssis, réutilisés tels quels. <see cref="BarcodeBatchForm"/> ne fait que l'afficher.
/// </summary>
internal static class BarcodeBatchLogic
{
    /// <summary>Longueur maximale d'un texte (comme la zone de saisie de la fenêtre principale).</summary>
    internal const int MaxTextLength = 200;

    /// <summary>Largeur maximale d'une image Code 128 (reflète la limite de <see cref="BarcodeRenderer"/>).</summary>
    internal const int MaxImageWidthPx = 30_000;

    /// <summary>Nombre maximal de miniatures gardées en mémoire (les lignes affichées plus les dernières vues).</summary>
    internal const int ThumbnailCacheCapacity = 96;

    /// <summary>Texte maximal montré d'un code-barres dans un message (le reste est remplacé par « … »).</summary>
    internal const int MaxDisplayedTextLength = 24;

    internal static readonly string AppTitle = AppInfo.WindowTitle("Codes-barres en série");

    internal const string NoCodesText = "Aucun code-barres : collez une colonne copiée depuis Excel ou Word, ou ouvrez un fichier.";

    internal const string HintText = "Collez une liste ou ouvrez un fichier pour commencer.";

    internal const string NoClipboardTextMessage =
        "Le presse-papiers ne contient pas de texte. Dans Excel, Word ou le Bloc-notes, sélectionnez la liste, "
        + "copiez-la (Ctrl+C), puis recommencez.";

    internal const string ClipboardBusyMessage =
        "Le presse-papiers est occupé par un autre programme : patientez un instant puis réessayez.";

    internal const string FallbackFileFilter =
        "Listes de codes (*.txt;*.csv;*.xlsx;*.docx)|*.txt;*.csv;*.xlsx;*.docx|Tous les fichiers (*.*)|*.*";

    internal const string CopyImageMenuText = "Copier le code-barres (image)";

    internal const string CopyTextMenuText = "Copier le texte";

    private const string ScaledHint =
        "Texte très long : le code-barres sera réduit pour tenir sur l'étiquette (barres de 1 point) — la lecture peut échouer. "
        + "Raccourcissez le texte.";

    // ------------------------------------------------------------------ analyse d'une ligne

    private static string Count(int n, string singular, string plural)
        => n.ToString(CultureInfo.InvariantCulture) + " " + (n <= 1 ? singular : plural);

    /// <summary>
    /// Largeur préférée de la barre la plus fine et largeur maximale de l'image, après validation des réglages (comme le fait
    /// <see cref="BarcodeRenderer.Render(string, AppSettings)"/>). À calculer une fois pour toute une liste.
    /// </summary>
    internal static (int PreferredModulePx, int MaxWidthPx) ModuleLimits(AppSettings? settings)
    {
        AppSettings s = (settings ?? new AppSettings()).Clone();
        s.Validate();
        return (s.ModulePx, s.MaxWidthPx);
    }

    /// <summary>Largeur totale (en modules) d'un code-barres de <paramref name="symbolCount"/> symboles, zones de silence comprises.</summary>
    internal static long TotalModules(int symbolCount)
        => 11L * (Math.Max(1, symbolCount) - 1) + 13 + 2L * BarcodeRenderer.QuietZoneModulesUsed;

    /// <summary>
    /// Analyse un texte comme le ferait <see cref="BarcodeRenderer.Render(string, AppSettings, out int)"/> mais sans rien dessiner :
    /// validation (<see cref="BarcodeRenderer.ValidateText"/>), nombre de symboles (<see cref="Code128Encoder.Encode"/>), largeur
    /// de barre retenue (<see cref="BarcodeRenderer.ChooseModulePx"/>) et avertissements (<see cref="BarcodeRenderer.ThinBarsWarning"/>).
    /// Ne lève jamais d'exception.
    /// </summary>
    internal static BarcodeRowAnalysis AnalyzeRow(string? text, int preferredModulePx, int maxWidthPx)
    {
        string t = (text ?? string.Empty).Trim();
        string? error = BarcodeRenderer.ValidateText(t);
        if (error == null && t.Length > MaxTextLength)
        {
            error = "Texte trop long (" + Count(t.Length, "caractère", "caractères") + ") : " + Count(MaxTextLength, "caractère", "caractères")
                    + " au maximum.";
        }

        if (error != null)
        {
            return new BarcodeRowAnalysis(t, error, 0, 0, false, false, null);
        }

        int symbols;
        try
        {
            symbols = Code128Encoder.Encode(t).Symbols.Count;
        }
        catch (ArgumentException ex)
        {
            return new BarcodeRowAnalysis(t, string.IsNullOrWhiteSpace(ex.Message) ? "Texte non pris en charge par Code 128." : ex.Message,
                0, 0, false, false, null);
        }

        int modulePx = BarcodeRenderer.ChooseModulePx(symbols, preferredModulePx, maxWidthPx);
        long minWidth = TotalModules(symbols) * modulePx;
        if (minWidth > MaxImageWidthPx)
        {
            return new BarcodeRowAnalysis(t, "Le texte est trop long pour être converti en code-barres.", 0, 0, false, false, null);
        }

        string? thin = BarcodeRenderer.ThinBarsWarning(modulePx, preferredModulePx);
        // Même critère que la fenêtre principale : l'image reste plus large que le ruban même avec des barres de 1 point.
        bool scaled = minWidth > maxWidthPx * 1.02;
        string? hint = scaled ? ScaledHint : thin;
        return new BarcodeRowAnalysis(t, null, symbols, modulePx, thin != null, scaled, hint);
    }

    /// <summary>Analyse chaque texte de la liste (même ordre). Une entrée null compte comme un texte vide.</summary>
    internal static List<BarcodeRowAnalysis> AnalyzeAll(IReadOnlyList<string?> texts, AppSettings? settings)
    {
        (int preferred, int max) = ModuleLimits(settings);
        var result = new List<BarcodeRowAnalysis>(texts.Count);
        foreach (string? text in texts)
        {
            result.Add(AnalyzeRow(text, preferred, max));
        }

        return result;
    }

    // ------------------------------------------------------------------ décomptes et résumé

    /// <summary>Nombre de lignes de texte non vide déjà apparu plus haut (comparaison exacte : un code-barres est sensible à la casse).</summary>
    internal static int CountDuplicates(IReadOnlyList<BarcodeRowAnalysis> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;
        foreach (BarcodeRowAnalysis row in rows)
        {
            if (row.Text.Length > 0 && !seen.Add(row.Text))
            {
                duplicates++;
            }
        }

        return duplicates;
    }

    /// <summary>Décompte des constats de la liste.</summary>
    internal static BarcodeListStats ComputeStats(IReadOnlyList<BarcodeRowAnalysis> rows)
    {
        int invalid = 0;
        int thin = 0;
        int scaled = 0;
        foreach (BarcodeRowAnalysis row in rows)
        {
            if (!row.IsValid)
            {
                invalid++;
            }
            else if (row.Scaled)
            {
                scaled++;
            }
            else if (row.ThinBars)
            {
                thin++;
            }
        }

        return new BarcodeListStats(rows.Count, invalid, thin, scaled, CountDuplicates(rows));
    }

    /// <summary>
    /// « 88 codes-barres — 2 invalides, 3 à barres fines, 1 réduit pour tenir, 4 doublons » (ou « — tous valides »).
    /// Avec plusieurs copies : « 88 codes-barres x 2 copies = 176 impressions — ... ».
    /// </summary>
    internal static string BuildSummary(IReadOnlyList<BarcodeRowAnalysis> rows, int copies = 1)
    {
        if (rows == null || rows.Count == 0)
        {
            return NoCodesText;
        }

        copies = BatchLogic.ClampCopies(copies);
        BarcodeListStats stats = ComputeStats(rows);
        var sb = new StringBuilder();
        sb.Append(Count(rows.Count, "code-barres", "codes-barres"));
        if (copies > 1)
        {
            sb.Append(" x ").Append(copies.ToString(CultureInfo.InvariantCulture)).Append(" copies = ")
              .Append((rows.Count * copies).ToString(CultureInfo.InvariantCulture)).Append(" impressions");
        }

        var details = new List<string>();
        if (stats.Invalid > 0)
        {
            details.Add(stats.Invalid.ToString(CultureInfo.InvariantCulture) + (stats.Invalid == 1 ? " invalide" : " invalides"));
        }

        if (stats.Thin > 0)
        {
            details.Add(stats.Thin.ToString(CultureInfo.InvariantCulture) + " à barres fines");
        }

        if (stats.Scaled > 0)
        {
            details.Add(stats.Scaled.ToString(CultureInfo.InvariantCulture) + (stats.Scaled == 1 ? " réduit pour tenir" : " réduits pour tenir"));
        }

        if (stats.Duplicates > 0)
        {
            details.Add(Count(stats.Duplicates, "doublon", "doublons"));
        }

        sb.Append(" — ");
        sb.Append(details.Count == 0 ? (rows.Count == 1 ? "valide" : "tous valides") : string.Join(", ", details));
        return sb.ToString();
    }

    /// <summary>Couleur du résumé : rouge s'il y a des lignes invalides, orange pour les remarques, sinon neutre.</summary>
    internal static BatchTone SummaryTone(IReadOnlyList<BarcodeRowAnalysis> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return BatchTone.Info;
        }

        BarcodeListStats stats = ComputeStats(rows);
        if (stats.Invalid > 0)
        {
            return BatchTone.Error;
        }

        return stats.Warnings > 0 || stats.Duplicates > 0 ? BatchTone.Warning : BatchTone.Info;
    }

    // ------------------------------------------------------------------ colonne « État »

    /// <summary>
    /// Texte de la colonne « État » : l'état d'impression s'il y en a un (<see cref="BatchLogic.StateText"/>), sinon un constat sur
    /// le texte (« ✖ invalide », « ⚠ barres fines », « ⚠ réduit »), sinon vide.
    /// </summary>
    internal static string StateColumnText(RowState state, int copiesDone, int copies, BarcodeRowAnalysis? analysis)
    {
        if (state != RowState.Pending)
        {
            return BatchLogic.StateText(state, copiesDone, copies);
        }

        if (analysis == null)
        {
            return string.Empty;
        }

        if (!analysis.IsValid)
        {
            return "✖ invalide";
        }

        if (analysis.Scaled)
        {
            return "⚠ réduit";
        }

        return analysis.ThinBars ? "⚠ barres fines" : string.Empty;
    }

    /// <summary>Couleur de la colonne « État » (<see cref="BatchTone.Info"/> : couleur ordinaire).</summary>
    internal static BatchTone StateTone(RowState state, BarcodeRowAnalysis? analysis)
    {
        switch (state)
        {
            case RowState.Done:
                return BatchTone.Success;
            case RowState.Failed:
                return BatchTone.Error;
            case RowState.Uncertain:
            case RowState.Partial:
                return BatchTone.Warning;
            case RowState.Printing:
                return BatchTone.Info;
        }

        return analysis?.Severity switch
        {
            BarcodeRowSeverity.Invalid => BatchTone.Error,
            BarcodeRowSeverity.Warning => BatchTone.Warning,
            _ => BatchTone.Info,
        };
    }

    // ------------------------------------------------------------------ lignes à imprimer

    /// <summary>
    /// Première ligne (dans l'ordre de <paramref name="rows"/>) qui ne peut pas être imprimée, ou null.
    /// Message : « Ligne 5 : Saisissez un texte. ».
    /// </summary>
    internal static BarcodeRowProblem? FindInvalidRow(IReadOnlyList<BarcodeRowAnalysis> analyses, IEnumerable<int> rows)
    {
        foreach (int row in rows)
        {
            if (row < 0 || row >= analyses.Count)
            {
                continue;
            }

            BarcodeRowAnalysis analysis = analyses[row];
            if (!analysis.IsValid)
            {
                return new BarcodeRowProblem(row, "Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " : " + analysis.Error);
            }
        }

        return null;
    }

    /// <summary>Message quand le mode demandé ne donne aucune ligne.</summary>
    internal static string NothingToPrintMessage(PrintMode mode)
        => mode == PrintMode.All
            ? "Aucun code-barres à imprimer : collez ou ouvrez d'abord une liste."
            : "Sélectionnez d'abord une ou plusieurs lignes dans la liste.";

    // ------------------------------------------------------------------ textes de l'impression

    /// <summary>Texte raccourci pour un message : « ABCDEFGHIJKLMNOPQRSTUVW… ».</summary>
    internal static string ShortText(string? text, int max = MaxDisplayedTextLength)
    {
        string t = (text ?? string.Empty).Trim();
        if (max < 2 || t.Length <= max)
        {
            return t;
        }

        // Ne coupe pas une paire de substituts en deux.
        int cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(t[cut - 1]))
        {
            cut--;
        }

        return t.Substring(0, cut) + "…";
    }

    /// <summary>« ligne 13 (ABC-123) ».</summary>
    internal static string DescribeRow(IReadOnlyList<string> texts, int row)
    {
        string name = string.Empty;
        if (texts != null && row >= 0 && row < texts.Count)
        {
            name = " (" + ShortText(texts[row]) + ")";
        }

        return "ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + name;
    }

    /// <summary>« Code-barres 12 / 88 (ABC-123)… » (avec « , copie 2 / 3 » s'il y a plusieurs copies).</summary>
    internal static string BuildProgressText(int position, int total, int copy, int copies, string text)
    {
        var sb = new StringBuilder("Code-barres ");
        sb.Append(position.ToString(CultureInfo.InvariantCulture)).Append(" / ").Append(total.ToString(CultureInfo.InvariantCulture));
        sb.Append(" (").Append(ShortText(text)).Append(')');
        if (copies > 1)
        {
            sb.Append(", copie ").Append(copy.ToString(CultureInfo.InvariantCulture)).Append(" / ").Append(copies.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Question posée avant d'imprimer plusieurs codes-barres.</summary>
    internal static string BuildConfirmText(int count, int copies, string printer)
    {
        copies = BatchLogic.ClampCopies(copies);
        string what = Count(count, "code-barres", "codes-barres");
        if (copies > 1)
        {
            what += " en " + copies.ToString(CultureInfo.InvariantCulture) + " copies";
            if (count > 1)
            {
                what += " (" + (count * copies).ToString(CultureInfo.InvariantCulture) + " impressions au total)";
            }
        }

        return "Imprimer " + what + " sur « " + printer + " » ?";
    }

    /// <summary>
    /// Question posée quand parmi les <paramref name="count"/> codes-barres à imprimer, <paramref name="doneCount"/> sont déjà marqués
    /// « ✔ imprimée » (risque de doublon). Sans ligne déjà imprimée : la simple confirmation (<see cref="BuildConfirmText"/>).
    /// Si TOUTES le sont : question OK / Annuler. Sinon le texte explique les trois réponses Oui / Non / Annuler (Oui : tout,
    /// Non : seulement celles pas encore imprimées, Annuler : rien).
    /// </summary>
    internal static string BuildReprintText(int count, int doneCount, int copies, string printer)
    {
        doneCount = Math.Clamp(doneCount, 0, Math.Max(0, count));
        if (doneCount == 0)
        {
            return BuildConfirmText(count, copies, printer);
        }

        string mark = "« " + BatchLogic.StateText(RowState.Done) + " »";
        string nl = Environment.NewLine;
        if (doneCount >= count)
        {
            string subject = count == 1
                ? "Ce code-barres est déjà marqué " + mark + " : l'imprimer de nouveau le fera sortir en double."
                : "Ces " + count.ToString(CultureInfo.InvariantCulture) + " codes-barres sont déjà marqués " + mark
                  + " : les imprimer de nouveau les fera sortir en double.";
            return subject + nl + nl + BuildConfirmText(count, copies, printer);
        }

        int pending = count - doneCount;
        string already = doneCount == 1
            ? "1 code-barres est déjà marqué " + mark + "."
            : doneCount.ToString(CultureInfo.InvariantCulture) + " codes-barres sont déjà marqués " + mark + ".";
        string yes = doneCount == 1
            ? "Oui : tout imprimer (celui déjà imprimé sortira en double)."
            : "Oui : tout imprimer (les " + doneCount.ToString(CultureInfo.InvariantCulture) + " déjà imprimés sortiront en double).";
        string no = pending == 1
            ? "Non : imprimer seulement le code-barres pas encore imprimé."
            : "Non : imprimer seulement les " + pending.ToString(CultureInfo.InvariantCulture) + " codes-barres pas encore imprimés.";
        return BuildConfirmText(count, copies, printer) + nl + nl
               + "Attention : " + already + nl + nl
               + yes + nl + no + nl + "Annuler : ne rien imprimer.";
    }

    /// <summary>
    /// Remarque sur les lignes IMPRIMÉES de la série qui avaient des barres très fines ou ont été réduites (null : rien à dire).
    /// Les lignes imprimées sont les <paramref name="outcome"/>.RowsDone premières de <paramref name="rows"/>.
    /// </summary>
    internal static string? BuildQualityNote(IReadOnlyList<BarcodeRowAnalysis> analyses, IReadOnlyList<int> rows, BatchOutcome outcome)
    {
        int thin = 0;
        int scaled = 0;
        foreach (int row in rows.Take(Math.Max(0, outcome.RowsDone)))
        {
            if (row < 0 || row >= analyses.Count)
            {
                continue;
            }

            BarcodeRowAnalysis a = analyses[row];
            if (!a.IsValid)
            {
                continue;
            }

            if (a.Scaled)
            {
                scaled++;
            }
            else if (a.ThinBars)
            {
                thin++;
            }
        }

        if (thin == 0 && scaled == 0)
        {
            return null;
        }

        string Subject(int n) => n == 1 ? "1 code-barres" : n.ToString(CultureInfo.InvariantCulture) + " codes-barres";
        string thinClause = thin == 1 ? "a des barres très fines" : "ont des barres très fines";
        string scaledClause = scaled == 1 ? "a été réduit pour tenir sur l'étiquette" : "ont été réduits pour tenir sur l'étiquette";
        string body;
        if (thin > 0 && scaled > 0)
        {
            body = Subject(thin) + " " + thinClause + " et " + scaled.ToString(CultureInfo.InvariantCulture) + " " + scaledClause;
        }
        else if (thin > 0)
        {
            body = Subject(thin) + " " + thinClause;
        }
        else
        {
            body = Subject(scaled) + " " + scaledClause;
        }

        bool single = thin + scaled == 1;
        return "Attention : " + body + " : vérifiez qu'" + (single ? "il se lit" : "ils se lisent") + " bien.";
    }

    /// <summary>Message final (et sa couleur) d'une série d'impressions de codes-barres.</summary>
    /// <param name="outcome">Bilan de la série.</param>
    /// <param name="texts">Les textes de la liste (pour nommer la ligne en cause à l'écran).</param>
    /// <param name="copies">Copies par code-barres.</param>
    /// <param name="printer">Nom de l'imprimante.</param>
    /// <param name="timeoutMs">Délai d'attente par envoi.</param>
    /// <param name="qualityNote">Remarque sur les barres fines ou réduites (<see cref="BuildQualityNote"/>), ajoutée à une série terminée ou arrêtée.</param>
    /// <param name="tone">Couleur du message.</param>
    internal static string BuildOutcomeMessage(
        BatchOutcome outcome, IReadOnlyList<string> texts, int copies, string printer, int timeoutMs, string? qualityNote, out BatchTone tone)
        => BuildOutcomeMessage(outcome, texts, copies, printer, timeoutMs, qualityNote, PrintMode.All, out tone);

    /// <summary>
    /// Idem, en sachant quelle série a été lancée : après une série interrompue sur « Imprimer la sélection », la consigne de reprise
    /// n'est PAS « Imprimer à partir de la sélection » (qui imprimerait toute la fin de la liste) mais « Imprimer la sélection » :
    /// la fenêtre re-sélectionne alors les lignes de la sélection pas encore envoyées (voir <see cref="RemainingRows"/>).
    /// </summary>
    /// <param name="mode">Série qui vient de se terminer.</param>
    internal static string BuildOutcomeMessage(
        BatchOutcome outcome, IReadOnlyList<string> texts, int copies, string printer, int timeoutMs, string? qualityNote, PrintMode mode, out BatchTone tone)
    {
        copies = BatchLogic.ClampCopies(copies);
        string ofTotal = outcome.RowsDone.ToString(CultureInfo.InvariantCulture) + " sur " + outcome.TotalRows.ToString(CultureInfo.InvariantCulture);
        string row = outcome.ResumeRow.HasValue ? DescribeRow(texts, outcome.ResumeRow.Value) : string.Empty;
        string resume = ResumeInstruction(outcome.ResumeRow, false, mode);
        string partial = outcome.ResumeCopiesDone > 0
            ? " Cette ligne n'a reçu que " + outcome.ResumeCopiesDone.ToString(CultureInfo.InvariantCulture) + " copie"
              + (outcome.ResumeCopiesDone > 1 ? "s" : string.Empty) + " sur " + copies.ToString(CultureInfo.InvariantCulture)
              + " : la reprise les imprimera toutes à nouveau."
            : string.Empty;

        switch (outcome.End)
        {
            case BatchEnd.Completed:
            {
                tone = BatchTone.Success;
                string sent = outcome.TotalRows == 1
                    ? "1 code-barres envoyé"
                    : outcome.TotalRows.ToString(CultureInfo.InvariantCulture) + " codes-barres envoyés";
                string extra = copies > 1
                    ? " (" + outcome.SheetsDone.ToString(CultureInfo.InvariantCulture) + " impressions)"
                    : string.Empty;
                string message = "✔ " + sent + extra + " à « " + printer + " ».";
                if (!string.IsNullOrWhiteSpace(qualityNote))
                {
                    message += " " + qualityNote!.Trim();
                    tone = BatchTone.Warning;
                }

                return message;
            }

            case BatchEnd.Stopped:
                tone = BatchTone.Warning;
                return "Impression arrêtée : " + ofTotal + " codes-barres envoyés à « " + printer + " ». "
                       + "Pour reprendre, " + resume + "." + partial
                       + (string.IsNullOrWhiteSpace(qualityNote) ? string.Empty : " " + qualityNote!.Trim());

            case BatchEnd.Paused:
                tone = BatchTone.Warning;
                return "Série en pause : " + ofTotal + " codes-barres envoyés. Pour reprendre, " + resume + ".";

            case BatchEnd.Failed:
                tone = BatchTone.Error;
                return "Échec à la " + row + " : " + EndWithPeriod(outcome.Error ?? "erreur inconnue") + " "
                       + ofTotal + " codes-barres envoyés. Corrigez le problème, puis " + resume + "." + partial;

            case BatchEnd.TimedOut:
                tone = BatchTone.Error;
                return "Windows n'a pas confirmé l'envoi de la " + row + " en " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture)
                       + " s… vérifiez la file d'impression avant de réessayer : l'étiquette est peut-être sortie (la ligne est marquée « ? »). "
                       + ofTotal + " codes-barres envoyés avant celui-ci. Pour reprendre, " + ResumeInstruction(outcome.ResumeRow, true, mode) + "." + partial;

            default:
                tone = BatchTone.Info;
                return Count(outcome.RowsDone, "code-barres envoyé", "codes-barres envoyés") + ".";
        }
    }

    /// <summary>
    /// « sélectionnez la ligne 13 et cliquez sur « Imprimer à partir de la sélection » » ; après « Imprimer la sélection » :
    /// « cliquez sur « Imprimer la sélection » (la ligne 13 et les suivantes de votre sélection sont déjà re-sélectionnées) ».
    /// </summary>
    private static string ResumeInstruction(int? row, bool unsure, PrintMode mode)
    {
        if (!row.HasValue)
        {
            return string.Empty;
        }

        string number = (row.Value + 1).ToString(CultureInfo.InvariantCulture);
        if (mode == PrintMode.Selection)
        {
            return "cliquez sur « Imprimer la sélection » (la ligne " + number + " et les suivantes de votre sélection sont déjà re-sélectionnées"
                   + (unsure ? " ; désélectionnez la ligne " + number + " si son étiquette est sortie" : string.Empty) + ")";
        }

        return "sélectionnez la ligne " + number
               + (unsure ? " (ou la suivante si l'étiquette est sortie)" : string.Empty)
               + " et cliquez sur « Imprimer à partir de la sélection »";
    }

    /// <summary>
    /// Lignes qu'il reste à envoyer dans une série interrompue : à partir de <paramref name="resumeRow"/> (comprise) dans l'ordre de
    /// <paramref name="rows"/>. Si la ligne n'en fait pas partie, elle seule.
    /// </summary>
    /// <param name="rows">Lignes de la série, dans l'ordre d'envoi.</param>
    /// <param name="resumeRow">Ligne où la série s'est arrêtée.</param>
    internal static IReadOnlyList<int> RemainingRows(IReadOnlyList<int> rows, int resumeRow)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] == resumeRow)
            {
                var rest = new List<int>(rows.Count - i);
                for (int k = i; k < rows.Count; k++)
                {
                    rest.Add(rows[k]);
                }

                return rest;
            }
        }

        return new[] { resumeRow };
    }

    private static string EndWithPeriod(string text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return t;
        }

        char last = t[t.Length - 1];
        return last == '.' || last == '!' || last == '?' || last == '…' ? t : t + ".";
    }

    // ------------------------------------------------------------------ chargement de la liste

    /// <summary>
    /// Message d'état après un collage ou l'ouverture d'un fichier : erreur (rouge), remarques ou lignes à corriger (orange), ou
    /// simple compte rendu (vert). <paramref name="pendingNotice"/> : explication à donner quand l'impression est bloquée (un envoi
    /// est resté sans réponse) : « Imprimer tous » est alors grisé, le message vert qui y invite serait trompeur ; le texte est orange.
    /// </summary>
    /// <param name="result">Ce que le lecteur de listes a rendu.</param>
    /// <param name="stats">Décompte des constats sur les valeurs lues (null : pas de décompte).</param>
    /// <param name="pendingNotice">Explication du blocage de l'impression, ou null.</param>
    internal static (string Text, BatchTone Tone) BuildLoadStatus(ListReadResult? result, BarcodeListStats? stats, string? pendingNotice = null)
    {
        if (result == null)
        {
            return ("La liste n'a pas pu être lue.", BatchTone.Error);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return (result.Error!, BatchTone.Error);
        }

        if (result.Values.Count == 0)
        {
            return ("Aucune valeur trouvée dans la liste.", BatchTone.Error);
        }

        string source = string.IsNullOrWhiteSpace(result.Source) ? string.Empty : " (" + result.Source.Trim() + ")";
        string head = Count(result.Values.Count, "code lu", "codes lus") + source + ".";
        if (!string.IsNullOrWhiteSpace(result.SkippedTitle))
        {
            // La ligne retirée est nommée : la case « première ligne = titre » ne doit jamais faire perdre un code sans que cela se voie.
            head += " Titre ignoré : « " + ShortenForMessage(result.SkippedTitle!.Trim()) + " ».";
        }

        bool blocked = !string.IsNullOrWhiteSpace(pendingNotice);
        var remarks = new List<string>();
        if (stats is { Invalid: > 0 } invalid)
        {
            remarks.Add(invalid.Invalid == 1
                ? "1 ligne est à corriger (en rouge) : elle bloquera l'impression."
                : invalid.Invalid.ToString(CultureInfo.InvariantCulture) + " lignes sont à corriger (en rouge) : elles bloqueront l'impression.");
        }

        remarks.AddRange(result.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()));
        if (remarks.Count == 0)
        {
            return blocked
                ? (head + " " + pendingNotice!.Trim(), BatchTone.Warning)
                : (head + " Vérifiez la liste, puis cliquez sur « Imprimer tous ».", BatchTone.Success);
        }

        // La consigne d'abord : l'écran coupe les messages trop longs par la fin.
        return (head + (blocked ? " " + pendingNotice!.Trim() : string.Empty) + " Remarques : " + string.Join(" ", remarks), BatchTone.Warning);
    }

    /// <summary>Texte raccourci pour un message à l'écran (jamais écrit dans le journal).</summary>
    private static string ShortenForMessage(string text)
    {
        const int max = 24;
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    /// <summary>Filtre de la boîte « Ouvrir » : celui du lecteur de listes, à défaut un filtre de secours.</summary>
    internal static string SafeFileFilter()
    {
        try
        {
            string filter = ListFileReader.FileDialogFilter;
            return string.IsNullOrWhiteSpace(filter) ? FallbackFileFilter : filter;
        }
        catch (Exception)
        {
            return FallbackFileFilter;
        }
    }

    /// <summary>
    /// Fichier à charger parmi les éléments déposés sur la fenêtre. Un seul fichier est lu à la fois : plusieurs éléments ou un
    /// dossier donnent un message (orange) à la place. Rien déposé : ni fichier ni message.
    /// </summary>
    /// <param name="paths">Chemins déposés (null ou vide : rien).</param>
    /// <param name="isDirectory">Teste si un chemin est un dossier (null : <see cref="Directory.Exists"/>).</param>
    internal static (string? Path, string? Problem) ClassifyDrop(IReadOnlyList<string>? paths, Func<string, bool>? isDirectory = null)
    {
        string[] items = (paths ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (items.Length == 0)
        {
            return (null, null);
        }

        if (items.Length > 1)
        {
            return (null, "Déposez un seul fichier à la fois (" + items.Length.ToString(CultureInfo.InvariantCulture) + " éléments déposés).");
        }

        isDirectory ??= Directory.Exists;
        bool directory;
        try
        {
            directory = isDirectory(items[0]);
        }
        catch (Exception)
        {
            directory = false;
        }

        return directory
            ? (null, "Déposez un fichier (.txt, .csv, .xlsx ou .docx), pas un dossier.")
            : (items[0], null);
    }

    // ------------------------------------------------------------------ miniatures

    /// <summary>
    /// Taille de la miniature : l'image (<paramref name="srcWidth"/> x <paramref name="srcHeight"/>) réduite pour tenir dans la
    /// cellule (<paramref name="maxWidth"/> x <paramref name="maxHeight"/>) en gardant ses proportions, jamais agrandie, au
    /// moins 1 x 1.
    /// </summary>
    internal static (int Width, int Height) FitSize(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return (1, 1);
        }

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / srcWidth, (double)maxHeight / srcHeight));
        int w = Math.Clamp((int)Math.Round(srcWidth * scale), 1, maxWidth);
        int h = Math.Clamp((int)Math.Round(srcHeight * scale), 1, maxHeight);
        return (w, h);
    }
}
