using System;
using System.Collections.Generic;

namespace BarcodeTray;

/// <summary>
/// Verrou commun aux fenêtres qui impriment (fenêtre principale, « Étiquettes en série », « Codes-barres en série ») : UNE seule
/// d'entre elles envoie des travaux à une imprimante donnée à la fois. Sans lui, une série de plusieurs milliers de codes-barres
/// pourrait être mélangée, sur le même rouleau, à une autre série ou à un « Imprimer » de la fenêtre principale, et chaque fenêtre
/// marquerait ses lignes « envoyées » sans pouvoir trier les étiquettes ; un envoi resté sans réponse dans une fenêtre
/// n'empêcherait pas les autres de renvoyer sur la même file (étiquettes en double plus tard).
/// <para>
/// Le verrou est pris au moment où la fenêtre commence à imprimer et rendu à la fin de la série, SAUF si un envoi est resté sans
/// réponse : il n'est rendu que lorsque cet envoi tardif s'est terminé. Il est propre à l'imprimante (nom insensible à la casse) :
/// deux imprimantes différentes peuvent travailler en même temps. Classe pure (aucune dépendance à Windows Forms) ; appelée depuis
/// le thread d'interface, protégée par un verrou par prudence.
/// </para>
/// </summary>
internal static class PrintGate
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (object Owner, string OwnerName)> Holds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prend le verrou de <paramref name="printer"/> pour <paramref name="owner"/> (la fenêtre). Faux si une autre fenêtre le tient :
    /// <paramref name="busyOwnerName"/> donne son nom. Reprendre un verrou déjà tenu par le même propriétaire réussit.
    /// </summary>
    /// <param name="printer">Nom de l'imprimante.</param>
    /// <param name="owner">Identité du demandeur (la fenêtre elle-même).</param>
    /// <param name="ownerName">Nom de la fenêtre, tel qu'il sera cité dans le message d'une autre fenêtre.</param>
    /// <param name="busyOwnerName">Nom de la fenêtre qui tient le verrou (faux uniquement).</param>
    internal static bool TryAcquire(string? printer, object owner, string ownerName, out string? busyOwnerName)
    {
        busyOwnerName = null;
        if (string.IsNullOrWhiteSpace(printer))
        {
            return true; // pas d'imprimante nommée : l'appelant refusera de toute façon d'imprimer
        }

        lock (Sync)
        {
            if (Holds.TryGetValue(printer.Trim(), out (object Owner, string OwnerName) held) && !ReferenceEquals(held.Owner, owner))
            {
                busyOwnerName = held.OwnerName;
                return false;
            }

            Holds[printer.Trim()] = (owner, ownerName);
            return true;
        }
    }

    /// <summary>Rend tous les verrous tenus par <paramref name="owner"/> (sans effet s'il n'en tient aucun).</summary>
    internal static void Release(object owner)
    {
        lock (Sync)
        {
            List<string>? mine = null;
            foreach (KeyValuePair<string, (object Owner, string OwnerName)> hold in Holds)
            {
                if (ReferenceEquals(hold.Value.Owner, owner))
                {
                    (mine ??= new List<string>()).Add(hold.Key);
                }
            }

            if (mine != null)
            {
                foreach (string key in mine)
                {
                    Holds.Remove(key);
                }
            }
        }
    }

    /// <summary>Vrai si <paramref name="owner"/> tient au moins un verrou (pour les tests).</summary>
    internal static bool IsHeldBy(object owner)
    {
        lock (Sync)
        {
            foreach ((object holder, string _) in Holds.Values)
            {
                if (ReferenceEquals(holder, owner)) return true;
            }

            return false;
        }
    }

    /// <summary>Message (français) quand une autre fenêtre utilise déjà l'imprimante.</summary>
    internal static string BusyMessage(string? printer, string? busyOwnerName)
    {
        string window = string.IsNullOrWhiteSpace(busyOwnerName) ? "une autre fenêtre du programme" : "la fenêtre « " + busyOwnerName!.Trim() + " »";
        string queue = string.IsNullOrWhiteSpace(printer) ? "L'imprimante" : "L'imprimante « " + printer!.Trim() + " »";
        return queue + " est déjà utilisée par " + window + " (une série en cours, ou un envoi que Windows n'a pas encore confirmé) : "
               + "attendez la fin avant d'imprimer ici, pour ne pas mélanger les étiquettes.";
    }
}
