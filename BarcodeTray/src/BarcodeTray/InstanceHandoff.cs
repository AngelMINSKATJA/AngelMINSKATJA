using System;
using System.Threading;

namespace BarcodeTray;

/// <summary>Ce que fait un programme qui vient de se lancer alors qu'un autre est déjà actif dans la session Windows.</summary>
internal enum SecondInstanceAction
{
    /// <summary>Lancé par le démarrage automatique (--tray) : on se ferme sans bruit, comme toujours.</summary>
    ExitSilently,

    /// <summary>Une instance de cette version tourne : on lui demande d'afficher sa fenêtre, puis on se ferme.</summary>
    ShowRunningWindow,

    /// <summary>
    /// Une instance d'une ANCIENNE version tourne (BarcodeTray.exe) : on le dit à l'utilisateur et on redirige le démarrage
    /// automatique vers ce nouvel exécutable, puis on se ferme. Sans cela, le nouvel exécutable disparaîtrait sans rien dire
    /// pendant que la fenêtre de l'ancien programme s'afficherait, et l'ancien reviendrait à chaque ouverture de session.
    /// </summary>
    ExplainOlderVersion,
}

/// <summary>
/// Reconnaissance « ancienne version / même version » entre deux exécutables. Le mutex et l'événement « une seule
/// instance » gardent volontairement leur ancien nom (Program : une ancienne et une nouvelle version se reconnaissent comme
/// « la même application », jamais deux icônes) ; l'ancienne version ne sait pas qu'elle est ancienne. Chaque version à partir
/// de la 3 crée donc en plus un mutex « marqueur » que l'ancienne version ne crée pas : un programme qui trouve l'instance
/// active SANS ce marqueur sait qu'il s'agit d'une ancienne version.
/// </summary>
internal static class InstanceHandoff
{
    /// <summary>
    /// Nom du mutex marqueur, créé par toute version 3 ou plus récente (une version future doit continuer à le créer).
    /// Différent du mutex et de l'événement « une seule instance » (ces noms-là ne changent pas).
    /// </summary>
    internal const string RunningMarkerName = @"Local\BarcodeTray.v3.Running";

    /// <summary>
    /// Nombre d'essais et pause (ms) entre deux essais quand le marqueur est introuvable : la première instance le crée
    /// immédiatement après le mutex « une seule instance », mais deux lancements simultanés pourraient se croiser dans cet
    /// intervalle ; on laisse donc quelques instants avant de conclure à une ancienne version.
    /// </summary>
    internal const int ProbeAttempts = 4;
    internal const int ProbePauseMs = 75;

    /// <summary>
    /// Décision pure. <paramref name="runningInstanceHasMarker"/> n'est évalué que si l'utilisateur a lancé le programme lui-même
    /// (inutile, et lent, au démarrage automatique).
    /// </summary>
    internal static SecondInstanceAction Decide(bool startInTray, Func<bool> runningInstanceHasMarker)
    {
        ArgumentNullException.ThrowIfNull(runningInstanceHasMarker);
        if (startInTray)
        {
            return SecondInstanceAction.ExitSilently;
        }

        return runningInstanceHasMarker() ? SecondInstanceAction.ShowRunningWindow : SecondInstanceAction.ExplainOlderVersion;
    }

    /// <summary>
    /// Crée le marqueur (non possédé) et le renvoie : l'appelant le garde ouvert jusqu'à la fin du processus.
    /// Null si Windows refuse (le programme fonctionne quand même : seule la détection d'une ancienne version est perdue).
    /// </summary>
    internal static Mutex? CreateMarker(string markerName)
    {
        try
        {
            return new Mutex(false, markerName);
        }
        catch (Exception ex)
        {
            Log.Warn("Marqueur de version impossible à créer : " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Vrai si un marqueur de ce nom existe. En cas de doute (accès refusé, erreur inattendue) on répond vrai : mieux vaut
    /// l'ancien comportement (afficher la fenêtre de l'instance active) qu'un faux message « ancienne version ».
    /// </summary>
    internal static bool IsMarkerPresent(string markerName, int attempts, int pauseMs)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(pauseMs);
            }

            try
            {
                if (Mutex.TryOpenExisting(markerName, out Mutex? marker))
                {
                    marker.Dispose();
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return true; // il existe, mais créé dans un autre contexte de sécurité
            }
            catch (Exception ex)
            {
                Log.Warn("Recherche du marqueur de version impossible : " + ex.Message);
                return true;
            }
        }

        return false;
    }

    /// <summary>Message affiché quand une ancienne version est encore ouverte (français, pour un non-informaticien).</summary>
    internal static string OlderVersionMessage()
    {
        return "Une ancienne version du logiciel (« Code-barres Code 128 », fichier BarcodeTray.exe) est encore ouverte : "
               + "elle empêche de lancer celle-ci.\n\n"
               + "Faites un clic droit sur son icône près de l'horloge (sous Windows 11, cliquez d'abord sur le « ^ »), "
               + "choisissez Quitter, puis relancez « " + AppInfo.FileBaseName + ".exe ».\n\n"
               + "Vos réglages et votre imprimante sont conservés.";
    }
}
