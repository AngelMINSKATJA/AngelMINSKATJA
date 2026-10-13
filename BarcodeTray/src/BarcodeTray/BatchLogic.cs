using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BarcodeTray;

/// <summary>État d'une ligne (une étiquette) dans la fenêtre « Étiquettes en série ».</summary>
internal enum RowState
{
    /// <summary>Pas encore imprimée (ou modifiée depuis).</summary>
    Pending,

    /// <summary>En cours d'envoi à l'imprimante.</summary>
    Printing,

    /// <summary>Envoyée à l'imprimante.</summary>
    Done,

    /// <summary>Échec d'impression (message d'erreur affiché).</summary>
    Failed,

    /// <summary>Windows n'a pas confirmé l'envoi à temps : l'étiquette est peut-être sortie.</summary>
    Uncertain,

    /// <summary>Arrêtée entre deux copies : seule une partie des copies demandées est sortie.</summary>
    Partial,
}

/// <summary>Quelles lignes imprimer.</summary>
internal enum PrintMode
{
    /// <summary>Toute la liste.</summary>
    All,

    /// <summary>Seulement les lignes sélectionnées.</summary>
    Selection,

    /// <summary>De la première ligne sélectionnée jusqu'à la fin de la liste (reprise après un arrêt ou un échec).</summary>
    FromSelection,
}

/// <summary>Nature d'un message d'état (couleur d'affichage).</summary>
internal enum BatchTone
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Colonne fautive d'une ligne invalide.</summary>
internal enum LabelField
{
    Type,
    Plan,
}

/// <summary>Première ligne qui ne peut pas être imprimée, avec le message (français) à montrer.</summary>
internal sealed record RowProblem(int Row, LabelField Field, string Message);

/// <summary>Comment une série d'impressions s'est terminée.</summary>
internal enum BatchEnd
{
    /// <summary>Toutes les étiquettes demandées ont été envoyées.</summary>
    Completed,

    /// <summary>L'utilisateur a cliqué sur « Arrêter » : arrêt avant l'envoi suivant.</summary>
    Stopped,

    /// <summary>Pause volontaire après la première étiquette (le pilote a imposé sa propre longueur de papier) : à vérifier avant de continuer.</summary>
    Paused,

    /// <summary>Un envoi a échoué : la série s'est arrêtée sur cette ligne.</summary>
    Failed,

    /// <summary>Windows n'a pas confirmé un envoi dans le délai : la série s'est arrêtée, la ligne est incertaine.</summary>
    TimedOut,
}

/// <summary>
/// Changement d'état d'une ligne pendant une série (pour l'affichage : colonne « État », barre de progression, texte).
/// <see cref="Position"/> : rang de la ligne dans CETTE série (1 à <see cref="TotalRows"/>) ; <see cref="Row"/> : indice dans la liste.
/// </summary>
internal readonly record struct BatchEvent(
    int Row,
    RowState State,
    int Position,
    int TotalRows,
    int Copy,
    int Copies,
    int CopiesDone,
    int SheetsDone,
    int TotalSheets);

/// <summary>Bilan d'une série d'impressions.</summary>
/// <param name="End">Comment elle s'est terminée.</param>
/// <param name="RowsDone">Lignes dont toutes les copies ont été envoyées.</param>
/// <param name="SheetsDone">Impressions envoyées (étiquettes x copies).</param>
/// <param name="TotalRows">Lignes demandées.</param>
/// <param name="TotalSheets">Impressions demandées.</param>
/// <param name="ResumeRow">Indice (dans la liste) de la ligne où reprendre ; null si tout est terminé.</param>
/// <param name="ResumeCopiesDone">Copies déjà envoyées de cette ligne (0 sauf arrêt ou échec entre deux copies).</param>
/// <param name="Error">Message d'erreur (français) en cas d'échec.</param>
/// <param name="Orphan">Envoi resté sans réponse après le délai : il tourne encore en arrière-plan.</param>
internal sealed record BatchOutcome(
    BatchEnd End,
    int RowsDone,
    int SheetsDone,
    int TotalRows,
    int TotalSheets,
    int? ResumeRow,
    int ResumeCopiesDone,
    string? Error,
    Task? Orphan);

/// <summary>Disponibilité des commandes de la fenêtre.</summary>
internal readonly record struct BatchButtonStates(
    bool Paste,
    bool Clear,
    bool Copies,
    bool Printer,
    bool Confirm,
    bool PrintAll,
    bool PrintSelection,
    bool PrintFromSelection,
    bool Stop,
    bool GridEditable);

/// <summary>
/// Logique PURE de la fenêtre « Étiquettes en série » (aucune dépendance à Windows Forms) : textes, validation des lignes,
/// choix des lignes à imprimer, disponibilité des boutons et déroulement d'une série (<see cref="Run"/>). Tout ce qui peut être
/// testé sans imprimante ni interface est ici ; <see cref="BatchForm"/> ne fait que l'afficher.
/// </summary>
internal static class BatchLogic
{
    /// <summary>Nombre minimal de copies par étiquette.</summary>
    internal const int MinCopies = 1;

    /// <summary>Nombre maximal de copies par étiquette.</summary>
    internal const int MaxCopies = 99;

    /// <summary>Délai (ms) au-delà duquel un envoi sans réponse de Windows arrête la série.</summary>
    internal const int DefaultTimeoutMs = 60_000;

    /// <summary>Nombre maximal de types détaillés dans le résumé (les autres sont comptés ensemble).</summary>
    internal const int MaxSummaryTypes = 12;

    // Marges supposées de la QL-800 sur du 62 mm (centièmes de pouce) : 6 de chaque côté, environ 3 mm en haut et en bas.
    internal const float NominalMarginXUnits = 6f;
    internal const float NominalMarginTopUnits = 11.67f;
    internal const float NominalMarginBottomUnits = 12f;

    internal const string NoLabelsText = "Aucune étiquette : collez le tableau copié depuis Excel.";

    internal const string EmptyTypeName = "(sans type)";

    // ------------------------------------------------------------------ textes

    internal static int ClampCopies(int copies) => Math.Clamp(copies, MinCopies, MaxCopies);

    private static string Count(int n, string singular, string plural)
        => n.ToString(CultureInfo.InvariantCulture) + " " + (n <= 1 ? singular : plural);

    /// <summary>
    /// « 88 étiquettes — HB1 : 45, HB2 : 19, VF1 : 12, VF2 : 14 » (types dans l'ordre de première apparition, insensible à la casse).
    /// Avec plusieurs copies : « 88 étiquettes x 2 copies = 176 impressions — ... ».
    /// </summary>
    internal static string BuildSummary(IReadOnlyList<LabelItem> items, int copies = 1)
    {
        if (items == null || items.Count == 0)
        {
            return NoLabelsText;
        }

        copies = ClampCopies(copies);
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (LabelItem item in items)
        {
            string key = (item?.Type ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                key = EmptyTypeName;
            }

            if (counts.TryGetValue(key, out int n))
            {
                counts[key] = n + 1;
            }
            else
            {
                counts[key] = 1;
                order.Add(key);
            }
        }

        var sb = new StringBuilder();
        sb.Append(Count(items.Count, "étiquette", "étiquettes"));
        if (copies > 1)
        {
            sb.Append(" x ").Append(copies.ToString(CultureInfo.InvariantCulture)).Append(" copies = ")
              .Append((items.Count * copies).ToString(CultureInfo.InvariantCulture)).Append(" impressions");
        }

        sb.Append(" — ");
        int shown = Math.Min(order.Count, MaxSummaryTypes);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(order[i]).Append(" : ").Append(counts[order[i]].ToString(CultureInfo.InvariantCulture));
        }

        if (order.Count > shown)
        {
            int others = order.Count - shown;
            sb.Append(", … (").Append(others.ToString(CultureInfo.InvariantCulture)).Append(others == 1 ? " autre type)" : " autres types)");
        }

        return sb.ToString();
    }

    /// <summary>Texte de la colonne « État » : « », « en cours… », « ✔ imprimée », « ✖ échec », « ? à vérifier »...</summary>
    internal static string StateText(RowState state, int copiesDone = 0, int copies = 1)
    {
        bool several = copies > 1 && copiesDone > 0;
        string detail = several
            ? " (" + copiesDone.ToString(CultureInfo.InvariantCulture) + "/" + copies.ToString(CultureInfo.InvariantCulture) + " copies)"
            : string.Empty;
        return state switch
        {
            RowState.Printing => "en cours…",
            RowState.Done => "✔ imprimée",
            RowState.Failed => "✖ échec" + detail,
            RowState.Uncertain => "? à vérifier" + detail,
            RowState.Partial => "arrêtée" + (several ? detail : string.Empty),
            _ => string.Empty,
        };
    }

    /// <summary>« Étiquette 12 / 88 (HB1 22072)… » (avec « , copie 2 / 3 » s'il y a plusieurs copies).</summary>
    internal static string BuildProgressText(int position, int total, int copy, int copies, string type, string plan)
    {
        var sb = new StringBuilder("Étiquette ");
        sb.Append(position.ToString(CultureInfo.InvariantCulture)).Append(" / ").Append(total.ToString(CultureInfo.InvariantCulture));
        sb.Append(" (").Append((type ?? string.Empty).Trim()).Append(' ').Append((plan ?? string.Empty).Trim()).Append(')');
        if (copies > 1)
        {
            sb.Append(", copie ").Append(copy.ToString(CultureInfo.InvariantCulture)).Append(" / ").Append(copies.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Question posée avant d'imprimer plusieurs étiquettes.</summary>
    internal static string BuildConfirmText(int labelCount, int copies, string printer)
    {
        copies = ClampCopies(copies);
        string what = Count(labelCount, "étiquette", "étiquettes");
        if (copies > 1)
        {
            what += " en " + copies.ToString(CultureInfo.InvariantCulture) + " copies";
            if (labelCount > 1)
            {
                what += " (" + (labelCount * copies).ToString(CultureInfo.InvariantCulture) + " impressions au total)";
            }
        }

        return "Imprimer " + what + " sur « " + printer + " » ?";
    }

    /// <summary>
    /// Question posée quand parmi les <paramref name="labelCount"/> étiquettes à imprimer, <paramref name="doneCount"/> sont déjà
    /// marquées « ✔ imprimée » (risque de doublon). Sans étiquette déjà imprimée : la simple confirmation
    /// (<see cref="BuildConfirmText"/>). Si TOUTES sont déjà imprimées : question Oui/Non à réponse unique. Sinon le texte explique les
    /// trois réponses d'une boîte Oui / Non / Annuler (Oui : tout, Non : seulement les pas encore imprimées, Annuler : rien).
    /// </summary>
    internal static string BuildReprintText(int labelCount, int doneCount, int copies, string printer)
    {
        doneCount = Math.Clamp(doneCount, 0, Math.Max(0, labelCount));
        if (doneCount == 0)
        {
            return BuildConfirmText(labelCount, copies, printer);
        }

        string mark = "« " + StateText(RowState.Done) + " »";
        string nl = Environment.NewLine;
        if (doneCount >= labelCount)
        {
            string subject = labelCount == 1
                ? "Cette étiquette est déjà marquée " + mark + " : l'imprimer de nouveau la fera sortir en double."
                : "Ces " + labelCount.ToString(CultureInfo.InvariantCulture) + " étiquettes sont déjà marquées " + mark
                  + " : les imprimer de nouveau les fera sortir en double.";
            return subject + nl + nl + BuildConfirmText(labelCount, copies, printer);
        }

        int pending = labelCount - doneCount;
        string already = doneCount == 1
            ? "1 étiquette est déjà marquée " + mark + "."
            : doneCount.ToString(CultureInfo.InvariantCulture) + " étiquettes sont déjà marquées " + mark + ".";
        string yes = doneCount == 1
            ? "Oui : tout imprimer (celle déjà imprimée sortira en double)."
            : "Oui : tout imprimer (les " + doneCount.ToString(CultureInfo.InvariantCulture) + " déjà imprimées sortiront en double).";
        string no = pending == 1
            ? "Non : imprimer seulement l'étiquette pas encore imprimée."
            : "Non : imprimer seulement les " + pending.ToString(CultureInfo.InvariantCulture) + " étiquettes pas encore imprimées.";
        return BuildConfirmText(labelCount, copies, printer) + nl + nl
               + "Attention : " + already + nl + nl
               + yes + nl + no + nl + "Annuler : ne rien imprimer.";
    }

    /// <summary>Nombre de lignes de <paramref name="rows"/> déjà marquées « imprimée » (<see cref="RowState.Done"/>).</summary>
    internal static int CountDone(IReadOnlyList<RowState> states, IEnumerable<int> rows)
    {
        if (states == null || rows == null)
        {
            return 0;
        }

        return rows.Count(r => r >= 0 && r < states.Count && states[r] == RowState.Done);
    }

    /// <summary>Les lignes de <paramref name="rows"/> qui ne sont pas encore marquées « imprimée » (même ordre).</summary>
    internal static IReadOnlyList<int> ExcludeDone(IReadOnlyList<RowState> states, IEnumerable<int> rows)
    {
        if (rows == null)
        {
            return Array.Empty<int>();
        }

        return rows.Where(r => states == null || r < 0 || r >= states.Count || states[r] != RowState.Done).ToArray();
    }

    /// <summary>
    /// Texte d'état après un collage : erreur (rouge), remarques (orange) ou simple compte rendu (vert).
    /// <paramref name="pendingNotice"/> : explication à donner quand l'impression est bloquée (un envoi ou une préparation du papier
    /// est resté sans réponse) : « Imprimer tout » est alors grisé, le message vert qui y invite serait trompeur ; le texte est orange.
    /// </summary>
    internal static (string Text, BatchTone Tone) BuildPasteStatus(PlanTableParseResult result, string? pendingNotice = null)
    {
        if (result == null)
        {
            return ("Le tableau collé n'a pas pu être analysé.", BatchTone.Error);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return (result.Error!, BatchTone.Error);
        }

        if (result.Items.Count == 0)
        {
            return ("Aucune étiquette trouvée dans le tableau collé.", BatchTone.Error);
        }

        string head = Count(result.Items.Count, "étiquette lue", "étiquettes lues") + " dans le tableau.";
        bool blocked = !string.IsNullOrWhiteSpace(pendingNotice);
        if (result.Warnings.Count == 0)
        {
            return blocked
                ? (head + " " + pendingNotice!.Trim(), BatchTone.Warning)
                : (head + " Vérifiez la liste, puis cliquez sur « Imprimer tout ».", BatchTone.Success);
        }

        // La consigne d'abord : l'écran coupe les messages trop longs par la fin.
        return (head + (blocked ? " " + pendingNotice!.Trim() : string.Empty) + " Remarques : "
                + string.Join(" ", result.Warnings.Select(w => w.Trim())), BatchTone.Warning);
    }

    // ------------------------------------------------------------------ envois en retard (délai dépassé)

    /// <summary>Explication quand un envoi qui a dépassé le délai tourne encore : l'impression reste bloquée.</summary>
    internal const string LateSendBlockedMessage =
        "Un envoi précédent n'a pas encore été confirmé par Windows : vérifiez la file d'impression avant de réessayer.";

    /// <summary>Explication quand la préparation du papier a dépassé le délai et tourne encore (aucune étiquette n'est partie).</summary>
    internal const string LatePrepareBlockedMessage =
        "La préparation du papier demandée à l'imprimante n'a pas encore abouti (rien n'a été envoyé) : patientez, "
        + "l'impression redeviendra possible dès qu'elle aura répondu.";

    /// <summary>Pourquoi l'impression est bloquée : un envoi d'étiquette (<paramref name="labelSent"/>) ou la préparation du papier.</summary>
    internal static string BuildLateBlockedMessage(bool labelSent) => labelSent ? LateSendBlockedMessage : LatePrepareBlockedMessage;

    /// <summary>Message quand l'imprimante n'a pas répondu à la préparation du papier dans le délai : RIEN n'a été envoyé.</summary>
    internal static string BuildPrepareTimeoutMessage(string printer, int timeoutMs)
        => "L'imprimante « " + printer + " » n'a pas répondu à la préparation du papier en "
           + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + " s. Rien n'a été envoyé. "
           + "Attendez sa réponse (un message vous préviendra) avant de relancer l'impression, et vérifiez qu'elle est allumée et branchée.";

    /// <summary>
    /// Message quand un envoi (ou une préparation du papier) resté sans réponse se termine enfin. Pour une préparation
    /// (<paramref name="labelSent"/> faux) aucune étiquette n'est partie et aucune ligne n'est marquée « ? » : on le dit, on ne
    /// promet surtout pas une étiquette à venir.
    /// </summary>
    /// <param name="labelSent">Vrai si l'attente concernait l'envoi d'une étiquette.</param>
    /// <param name="printer">Nom de l'imprimante.</param>
    /// <param name="errorMessage">Message de l'échec ; null si l'opération a abouti.</param>
    /// <param name="tone">Couleur du message.</param>
    internal static string BuildLateFinishedMessage(bool labelSent, string printer, string? errorMessage, out BatchTone tone)
    {
        bool failed = errorMessage != null;
        tone = failed ? BatchTone.Error : BatchTone.Info;
        if (labelSent)
        {
            return failed
                ? "L'envoi en attente a échoué : " + errorMessage
                : "L'envoi en attente est arrivé à l'imprimante « " + printer + " » : l'étiquette doit sortir. "
                  + "Vérifiez-la avant de reprendre (ligne marquée « ? »).";
        }

        return failed
            ? "La préparation du papier sur « " + printer + " » a fini par échouer : " + EndWithPeriod(errorMessage!) + " Rien n'a été envoyé."
            : "La préparation du papier sur « " + printer + " » a fini par aboutir : rien n'est parti, vous pouvez relancer l'impression.";
    }

    /// <summary>Message final (et sa couleur) d'une série d'impressions.</summary>
    /// <param name="outcome">Bilan de la série.</param>
    /// <param name="items">La liste d'étiquettes (pour nommer la ligne en cause à l'écran).</param>
    /// <param name="copies">Copies par étiquette.</param>
    /// <param name="printer">Nom de l'imprimante.</param>
    /// <param name="timeoutMs">Délai d'attente par envoi.</param>
    /// <param name="pauseNote">Explication affichée en cas de pause volontaire (<see cref="BatchEnd.Paused"/>).</param>
    /// <param name="tone">Couleur du message.</param>
    internal static string BuildOutcomeMessage(
        BatchOutcome outcome, IReadOnlyList<LabelItem> items, int copies, string printer, int timeoutMs, string? pauseNote, out BatchTone tone)
    {
        copies = ClampCopies(copies);
        string labels = Count(outcome.RowsDone, "étiquette", "étiquettes");
        string ofTotal = outcome.RowsDone.ToString(CultureInfo.InvariantCulture) + " sur " + outcome.TotalRows.ToString(CultureInfo.InvariantCulture);
        string row = outcome.ResumeRow.HasValue ? DescribeRow(items, outcome.ResumeRow.Value) : string.Empty;
        string resume = ResumeInstruction(outcome.ResumeRow, false);
        string partial = outcome.ResumeCopiesDone > 0
            ? " Cette ligne n'a reçu que " + outcome.ResumeCopiesDone.ToString(CultureInfo.InvariantCulture) + " copie"
              + (outcome.ResumeCopiesDone > 1 ? "s" : string.Empty) + " sur " + copies.ToString(CultureInfo.InvariantCulture)
              + " : la reprise les imprimera tous à nouveau."
            : string.Empty;

        switch (outcome.End)
        {
            case BatchEnd.Completed:
            {
                tone = BatchTone.Success;
                string sent = outcome.TotalRows == 1 ? "1 étiquette envoyée" : outcome.TotalRows.ToString(CultureInfo.InvariantCulture) + " étiquettes envoyées";
                string extra = copies > 1
                    ? " (" + outcome.SheetsDone.ToString(CultureInfo.InvariantCulture) + " impressions)"
                    : string.Empty;
                return "✔ " + sent + extra + " à « " + printer + " ».";
            }

            case BatchEnd.Stopped:
                tone = BatchTone.Warning;
                return "Impression arrêtée : " + ofTotal + " étiquettes envoyées à « " + printer + " ». "
                       + "Pour reprendre, " + resume + "." + partial;

            case BatchEnd.Paused:
                tone = BatchTone.Warning;
                // La consigne d'abord : la remarque du pilote est longue et l'écran coupe les messages trop longs par la fin.
                return "Première étiquette envoyée, puis pause : vérifiez-la. Si elle vous convient, " + resume + "."
                       + (string.IsNullOrWhiteSpace(pauseNote) ? string.Empty : " " + pauseNote!.Trim());

            case BatchEnd.Failed:
                tone = BatchTone.Error;
                return "Échec à la " + row + " : " + EndWithPeriod(outcome.Error ?? "erreur inconnue") + " "
                       + ofTotal + " étiquettes envoyées. Corrigez le problème, puis " + resume + "." + partial;

            case BatchEnd.TimedOut:
                tone = BatchTone.Error;
                return "Windows n'a pas confirmé l'envoi de la " + row + " en " + (timeoutMs / 1000).ToString(CultureInfo.InvariantCulture)
                       + " s… vérifiez la file d'impression avant de réessayer : l'étiquette est peut-être sortie (la ligne est marquée « ? »). "
                       + ofTotal + " étiquettes envoyées avant celle-ci. Pour reprendre, " + ResumeInstruction(outcome.ResumeRow, true) + "." + partial;

            default:
                tone = BatchTone.Info;
                return labels + " envoyées.";
        }
    }

    /// <summary>« sélectionnez la ligne 13 et cliquez sur « Imprimer à partir de la sélection » ».</summary>
    private static string ResumeInstruction(int? row, bool unsure)
    {
        if (!row.HasValue)
        {
            return string.Empty;
        }

        return "sélectionnez la ligne " + (row.Value + 1).ToString(CultureInfo.InvariantCulture)
               + (unsure ? " (ou la suivante si l'étiquette est sortie)" : string.Empty)
               + " et cliquez sur « Imprimer à partir de la sélection »";
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

    /// <summary>« ligne 13 (HB1 22073) ».</summary>
    internal static string DescribeRow(IReadOnlyList<LabelItem> items, int row)
    {
        string name = string.Empty;
        if (items != null && row >= 0 && row < items.Count)
        {
            LabelItem item = items[row];
            name = " (" + (item.Type ?? string.Empty).Trim() + " " + (item.Plan ?? string.Empty).Trim() + ")";
        }

        return "ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + name;
    }

    // ------------------------------------------------------------------ lignes

    /// <summary>
    /// Première ligne (dans l'ordre de <paramref name="rows"/>) dont le type ou le n° de plan ne peut pas être imprimé, ou null.
    /// Message : « Ligne 5 : Le champ « N° de plan » est vide. ».
    /// </summary>
    internal static RowProblem? FindInvalidRow(IReadOnlyList<LabelItem> items, IEnumerable<int> rows)
    {
        foreach (int row in rows)
        {
            if (row < 0 || row >= items.Count)
            {
                continue;
            }

            LabelItem item = items[row];
            string? error = PlanLabelRenderer.ValidateField(item.Type, PlanLabelRenderer.TypeFieldName);
            if (error != null)
            {
                return new RowProblem(row, LabelField.Type, "Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " : " + error);
            }

            error = PlanLabelRenderer.ValidateField(item.Plan, PlanLabelRenderer.PlanFieldName);
            if (error != null)
            {
                return new RowProblem(row, LabelField.Plan, "Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " : " + error);
            }
        }

        return null;
    }

    /// <summary>Indices (croissants, sans doublon) des lignes à imprimer selon le mode et la sélection.</summary>
    internal static IReadOnlyList<int> ChooseRows(PrintMode mode, int rowCount, IEnumerable<int> selected)
    {
        if (rowCount <= 0)
        {
            return Array.Empty<int>();
        }

        if (mode == PrintMode.All)
        {
            return Enumerable.Range(0, rowCount).ToArray();
        }

        int[] valid = (selected ?? Enumerable.Empty<int>()).Where(r => r >= 0 && r < rowCount).Distinct().OrderBy(r => r).ToArray();
        if (valid.Length == 0)
        {
            return Array.Empty<int>();
        }

        return mode == PrintMode.Selection
            ? valid
            : Enumerable.Range(valid[0], rowCount - valid[0]).ToArray();
    }

    /// <summary>Message quand le mode demandé ne donne aucune ligne.</summary>
    internal static string NothingToPrintMessage(PrintMode mode)
        => mode == PrintMode.All
            ? "Aucune étiquette à imprimer : collez d'abord le tableau copié depuis Excel."
            : "Sélectionnez d'abord une ou plusieurs lignes dans la liste.";

    /// <summary>
    /// Première ligne à partir de <paramref name="start"/> qui n'est pas encore imprimée (pour proposer la reprise), ou null.
    /// </summary>
    internal static int? NextRowToPrint(IReadOnlyList<RowState> states, int start = 0)
    {
        for (int i = Math.Max(0, start); i < states.Count; i++)
        {
            if (states[i] != RowState.Done)
            {
                return i;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ boutons

    /// <summary>
    /// Disponibilité des commandes. Pendant une impression, seul « Arrêter » (tant que l'arrêt n'est pas demandé) est actif.
    /// Tant qu'un envoi trop lent n'a pas abouti (<paramref name="latePending"/>), aucune impression ne peut partir
    /// (elle sortirait en double) mais on peut préparer la liste.
    /// </summary>
    internal static BatchButtonStates ComputeButtons(int rowCount, int selectedCount, bool printing, bool stopRequested, bool latePending)
    {
        bool idle = !printing;
        bool canPrint = idle && !latePending;
        return new BatchButtonStates(
            Paste: idle,
            Clear: idle && rowCount > 0,
            Copies: idle,
            Printer: idle,
            Confirm: idle,
            PrintAll: canPrint && rowCount > 0,
            PrintSelection: canPrint && selectedCount > 0,
            PrintFromSelection: canPrint && selectedCount > 0,
            Stop: printing && !stopRequested,
            GridEditable: idle);
    }

    // ------------------------------------------------------------------ texte et longueur réelle de la page

    /// <summary>
    /// Débordement vertical toléré (mm) entre le bas de l'encre et le bas de la zone imprimable : en dessous, le rognage n'est pas
    /// visible (lissage des bords) et on n'arrête pas toute une série pour si peu.
    /// </summary>
    internal const double VerticalToleranceMm = 0.3;

    /// <summary>
    /// Vérifie que le bas du texte (type et n° de plan) reste dans la page réelle du pilote : le pilote peut imposer une page plus
    /// courte que <c>BatchLabelLengthMm</c> (<c>PaperName</c> d'une étiquette prédécoupée de 29 mm, par exemple), et le texte, placé
    /// à une hauteur fixe depuis le haut, serait alors rogné en silence sur toutes les étiquettes. Renvoie null si tout tient,
    /// sinon un message (français) qui dit quoi régler.
    /// </summary>
    /// <param name="typeInkPx">Encre du type, en points de l'image (voir <see cref="PlanLabelLayout.TypeInkPx"/>).</param>
    /// <param name="planInkPx">Encre du n° de plan, en points de l'image.</param>
    /// <param name="dpi">Résolution de l'image.</param>
    /// <param name="pageHeightUnits">Hauteur réelle de la page, centièmes de pouce.</param>
    /// <param name="printableAreaUnits">Zone imprimable annoncée par le pilote (centièmes de pouce) ; vide ou illisible : seule la page compte.</param>
    internal static string? CheckTextFitsPageHeight(
        RectangleF typeInkPx, RectangleF planInkPx, int dpi, int pageHeightUnits, RectangleF printableAreaUnits)
    {
        if (dpi <= 0 || pageHeightUnits <= 0)
        {
            return null;
        }

        double inkBottom = 0.0;
        foreach (RectangleF ink in new[] { typeInkPx, planInkPx })
        {
            if (!ink.IsEmpty && float.IsFinite(ink.Bottom))
            {
                inkBottom = Math.Max(inkBottom, ink.Bottom);
            }
        }

        double perUnit = dpi / 100.0;
        double pageBottom = pageHeightUnits * perUnit;
        double limit = pageBottom;
        bool usable = float.IsFinite(printableAreaUnits.X) && float.IsFinite(printableAreaUnits.Y)
                      && float.IsFinite(printableAreaUnits.Width) && float.IsFinite(printableAreaUnits.Height)
                      && printableAreaUnits.Width > 0 && printableAreaUnits.Height > 0;
        if (usable)
        {
            limit = Math.Min(limit, ((double)printableAreaUnits.Y + printableAreaUnits.Height) * perUnit);
        }

        double toleranceMm = VerticalToleranceMm;
        double pxToMm = 25.4 / dpi;
        if (inkBottom * pxToMm <= limit * pxToMm + toleranceMm)
        {
            return null;
        }

        return "La zone imprimable de l'étiquette s'arrête à " + MmText(limit * pxToMm) + " mm du haut (étiquette de "
               + MmText(pageBottom * pxToMm) + " mm) mais le texte descend jusqu'à " + MmText(inkBottom * pxToMm)
               + " mm : le bas du texte serait coupé. Rien n'a été envoyé. Si PaperName est renseigné dans settings.json, videz-le "
               + "(la longueur est alors celle de BatchLabelLengthMm) ; sinon augmentez BatchLabelLengthMm ou remontez le texte "
               + "(BatchPlanBaselineMm).";
    }

    /// <summary>Millimètres avec une décimale au plus et la virgule française : « 25,9 », « 32 ».</summary>
    private static string MmText(double mm)
        => mm.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');

    // ------------------------------------------------------------------ aperçu

    /// <summary>
    /// Zone imprimable supposée pour l'aperçu (centièmes de pouce, origine = coin du papier) : 6 de chaque côté,
    /// 11,67 en haut et 12 en bas, comme mesuré sur le pilote de la QL-800 pour du ruban 62 mm.
    /// </summary>
    internal static RectangleF PreviewPrintableArea(int pageWidthUnits, int pageHeightUnits)
    {
        float width = Math.Max(1f, pageWidthUnits - 2 * NominalMarginXUnits);
        float height = Math.Max(1f, pageHeightUnits - NominalMarginTopUnits - NominalMarginBottomUnits);
        return new RectangleF(NominalMarginXUnits, NominalMarginTopUnits, width, height);
    }

    // ------------------------------------------------------------------ déroulement d'une série

    /// <summary>
    /// Envoie les étiquettes <paramref name="rows"/> une par une (<paramref name="copies"/> impressions chacune) en s'arrêtant au
    /// premier incident. Un travail d'impression par copie : une coupe chacune, et rien n'est jamais réessayé tout seul
    /// (risque de doublon).
    /// <list type="bullet">
    /// <item><description>L'arrêt demandé (<paramref name="stopRequested"/>) est pris en compte AVANT chaque envoi, jamais pendant.</description></item>
    /// <item><description>Un envoi qui lève une exception arrête la série (<see cref="BatchEnd.Failed"/>, ligne <c>Failed</c>).</description></item>
    /// <item><description>Un envoi sans réponse après <paramref name="timeoutMs"/> arrête la série (<see cref="BatchEnd.TimedOut"/>, ligne <c>Uncertain</c>) ;
    /// la tâche est rendue dans <see cref="BatchOutcome.Orphan"/> (elle tourne encore) et n'est jamais annulée.</description></item>
    /// <item><description><paramref name="pauseAfterRows"/> &gt; 0 : pause volontaire une fois ce nombre de lignes terminées (s'il en reste).</description></item>
    /// </list>
    /// Les notifications <paramref name="report"/> sont émises depuis le contexte qui appelle (le thread d'interface pour la fenêtre) ;
    /// le travail d'impression lui-même doit s'exécuter ailleurs (<paramref name="printSheet"/> renvoie une tâche déjà démarrée).
    /// </summary>
    /// <param name="rows">Indices (dans la liste) des lignes à imprimer, dans l'ordre.</param>
    /// <param name="copies">Copies par ligne (1 à 99).</param>
    /// <param name="printSheet">Envoie UNE copie : (ligne, numéro de la copie à partir de 1). Lève une exception en cas d'échec.</param>
    /// <param name="stopRequested">Vrai quand l'utilisateur a demandé l'arrêt.</param>
    /// <param name="report">Reçoit chaque changement d'état d'une ligne.</param>
    /// <param name="timeoutMs">Délai d'attente par envoi (0 ou moins : illimité).</param>
    /// <param name="pauseAfterRows">Nombre de lignes après lesquelles faire une pause (0 : jamais).</param>
    internal static async Task<BatchOutcome> Run(
        IReadOnlyList<int> rows,
        int copies,
        Func<int, int, Task> printSheet,
        Func<bool> stopRequested,
        Action<BatchEvent> report,
        int timeoutMs,
        int pauseAfterRows = 0)
    {
        copies = ClampCopies(copies);
        int totalRows = rows.Count;
        int totalSheets = totalRows * copies;
        int rowsDone = 0;
        int sheetsDone = 0;

        BatchOutcome End(BatchEnd end, int? resumeRow, int resumeCopies, string? error = null, Task? orphan = null)
            => new(end, rowsDone, sheetsDone, totalRows, totalSheets, resumeRow, resumeCopies, error, orphan);

        void Report(int row, RowState state, int position, int copy, int copiesDone)
        {
            try
            {
                report(new BatchEvent(row, state, position, totalRows, copy, copies, copiesDone, sheetsDone, totalSheets));
            }
            catch
            {
                // L'affichage ne doit jamais interrompre une série d'impressions.
            }
        }

        for (int i = 0; i < totalRows; i++)
        {
            int row = rows[i];
            int position = i + 1;

            for (int copy = 1; copy <= copies; copy++)
            {
                if (stopRequested())
                {
                    if (copy > 1)
                    {
                        Report(row, RowState.Partial, position, copy, copy - 1);
                    }

                    return End(BatchEnd.Stopped, row, copy - 1);
                }

                Report(row, RowState.Printing, position, copy, copy - 1);

                Task work;
                try
                {
                    work = printSheet(row, copy) ?? Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    work = Task.FromException(ex);
                }

                using var cts = new CancellationTokenSource();
                if (timeoutMs > 0)
                {
                    Task finished = await Task.WhenAny(work, Task.Delay(timeoutMs, cts.Token));
                    if (!ReferenceEquals(finished, work))
                    {
                        Report(row, RowState.Uncertain, position, copy, copy - 1);
                        return End(BatchEnd.TimedOut, row, copy - 1, orphan: work);
                    }

                    cts.Cancel();
                }

                try
                {
                    await work;
                }
                catch (Exception ex)
                {
                    Report(row, RowState.Failed, position, copy, copy - 1);
                    return End(BatchEnd.Failed, row, copy - 1, DescribeError(ex));
                }

                sheetsDone++;
            }

            rowsDone++;
            Report(row, RowState.Done, position, copies, copies);

            if (pauseAfterRows > 0 && rowsDone == pauseAfterRows && i + 1 < totalRows)
            {
                return End(BatchEnd.Paused, rows[i + 1], 0);
            }
        }

        return End(BatchEnd.Completed, null, 0);
    }

    /// <summary>Message (français) d'une exception d'envoi : celui de LabelPrinter tel quel, « Erreur inattendue » sinon.</summary>
    internal static string DescribeError(Exception ex)
    {
        Exception e = ex is AggregateException agg && agg.InnerExceptions.Count > 0 ? agg.InnerExceptions[0] : ex;
        string message = (e.Message ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            message = e.GetType().Name;
        }

        return e is InvalidOperationException || e is ArgumentException
            ? message
            : "Erreur inattendue : " + message;
    }
}
