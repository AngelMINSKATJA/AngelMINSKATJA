using System;
using System.IO;
using Microsoft.Win32;

namespace BarcodeTray;

/// <summary>
/// Lancement automatique à l'ouverture de session Windows, via la clé HKCU\...\Run
/// (utilisateur courant uniquement : aucun droit administrateur nécessaire).
/// Windows (Gestionnaire des tâches, Paramètres, Applications, Démarrage) mémorise en plus un interrupteur par
/// utilisateur dans HKCU\...\Explorer\StartupApproved\Run : s'il est sur « désactivé », la valeur Run est ignorée
/// au démarrage. <see cref="IsEnabled"/> en tient compte et <see cref="Enable"/> le remet sur « activé ».
/// Aucune méthode ne lève d'exception : les échecs sont journalisés et signalés par la valeur renvoyée.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "BarcodeTray";

    private static bool _disabledByWindowsLogged;

    /// <summary>
    /// Vrai si la valeur « BarcodeTray » existe dans la clé Run de l'utilisateur ET que Windows ne l'a pas
    /// désactivée (Gestionnaire des tâches, onglet Démarrage).
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key?.GetValue(ValueName) == null)
            {
                return false;
            }

            return !IsDisabledByWindows();
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du démarrage automatique impossible.", ex);
            return false;
        }
    }

    /// <summary>
    /// Vrai si la valeur binaire StartupApproved est « désactivé ». Windows écrit 12 octets dont le premier vaut 02
    /// ou 06 (activé), 03 ou 07 (désactivé) : un premier octet impair signifie « désactivé ».
    /// </summary>
    internal static bool IsDisabledApproval(object? rawValue)
    {
        return rawValue is byte[] { Length: > 0 } bytes && (bytes[0] & 1) == 1;
    }

    private static bool IsDisabledByWindows()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, false);
            bool disabled = IsDisabledApproval(key?.GetValue(ValueName));
            if (disabled && !_disabledByWindowsLogged)
            {
                _disabledByWindowsLogged = true; // une seule fois : cette méthode est appelée à chaque ouverture de menu
                Log.Warn("Démarrage automatique désactivé dans Windows (Gestionnaire des tâches, onglet Démarrage).");
            }

            return disabled;
        }
        catch (Exception ex)
        {
            Log.Warn("Lecture de StartupApproved impossible : " + ex.Message);
            return false;
        }
    }

    /// <summary>Supprime l'interrupteur « désactivé » de Windows pour notre entrée. Vrai s'il n'en reste aucun.</summary>
    private static bool ClearWindowsDisabledFlag()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, true);
            if (key?.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
                Log.Info("Démarrage automatique : interrupteur « désactivé » de Windows retiré.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Interrupteur de démarrage de Windows non modifiable : " + ex.Message);
            return !IsDisabledByWindows();
        }
    }

    /// <summary>
    /// Active le lancement au démarrage (et remet sur « activé » l'interrupteur de Windows si l'utilisateur l'avait
    /// coupé dans le Gestionnaire des tâches). Renvoie vrai en cas de succès.
    /// </summary>
    public static bool Enable()
    {
        try
        {
            string? command = BuildCommand();
            if (command == null)
            {
                return false;
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                                    ?? throw new InvalidOperationException("Clé de registre Run inaccessible.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
            Log.Info("Démarrage automatique activé : " + command);
            return ClearWindowsDisabledFlag();
        }
        catch (Exception ex)
        {
            Log.Error("Activation du démarrage automatique impossible.", ex);
            return false;
        }
    }

    /// <summary>Désactive le lancement au démarrage. Renvoie vrai en cas de succès (ou si déjà désactivé).</summary>
    public static bool Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                return true;
            }

            key.DeleteValue(ValueName, false);
            Log.Info("Démarrage automatique désactivé.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Désactivation du démarrage automatique impossible.", ex);
            return false;
        }
    }

    /// <summary>
    /// Si le démarrage automatique est activé mais pointe vers un autre emplacement
    /// (programme déplacé ou renommé), réécrit la valeur. Renvoie faux uniquement en cas d'échec.
    /// </summary>
    public static bool EnsurePathCurrent()
    {
        try
        {
            string? expected = BuildCommand();
            if (expected == null)
            {
                return false;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                return true; // rien d'activé
            }

            object? current = key.GetValue(ValueName);
            if (current == null)
            {
                return true; // non activé : on respecte le choix de l'utilisateur
            }

            if (string.Equals(current as string, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            key.SetValue(ValueName, expected, RegistryValueKind.String);
            Log.Info("Démarrage automatique mis à jour : " + expected);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Vérification du démarrage automatique impossible.", ex);
            return false;
        }
    }

    /// <summary>Commande enregistrée : "chemin de l'exe" --tray. Null si le chemin n'est pas exploitable.</summary>
    private static string? BuildCommand()
    {
        // Environment.ProcessPath (jamais Assembly.Location : vide dans un exécutable monofichier).
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            Log.Warn("Chemin de l'exécutable inconnu : démarrage automatique non modifié.");
            return null;
        }

        if (IsTransientLocation(path))
        {
            // Lancé depuis le ZIP ouvert dans l'Explorateur ou depuis un dossier temporaire : cet emplacement
            // disparaîtra, l'enregistrer casserait le démarrage automatique au prochain redémarrage.
            Log.Warn("Exécutable lancé depuis un emplacement temporaire (" + path + ") : démarrage automatique non modifié. "
                     + "Copiez BarcodeTray.exe dans un dossier permanent puis relancez-le.");
            return null;
        }

        return "\"" + path + "\" --tray";
    }

    /// <summary>
    /// Vrai si <paramref name="path"/> se trouve dans le dossier temporaire de l'utilisateur (ce que fait l'Explorateur
    /// quand on ouvre un exécutable directement depuis un ZIP : Temp1_xxx.zip) ou à l'intérieur d'un .zip.
    /// </summary>
    internal static bool IsTransientLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full = path;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // chemin exotique : on garde le texte tel quel
        }

        try
        {
            string temp = Path.GetTempPath();
            if (!string.IsNullOrEmpty(temp) && full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // pas de dossier temporaire exploitable : on se rabat sur les motifs ci-dessous
        }

        return full.Contains("\\Temp1_", StringComparison.OrdinalIgnoreCase)
               || full.Contains(".zip\\", StringComparison.OrdinalIgnoreCase);
    }
}
