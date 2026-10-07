using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BarcodeTray;

/// <summary>
/// Réglages de l'application : %APPDATA%\BarcodeTray\settings.json.
/// Lecture tolérante (fichier absent, JSON invalide, valeur de mauvais type : valeurs par défaut + journal,
/// jamais d'exception), écriture atomique (fichier temporaire puis remplacement).
/// Seul un fichier ABSENT est un « premier lancement » : un fichier présent mais illisible garde
/// <see cref="FirstRunDone"/> à vrai et lève <see cref="LoadFailed"/> (le démarrage automatique choisi par
/// l'utilisateur n'est pas remis en route, et le fichier abîmé n'est pas écrasé tant que rien n'est modifié).
/// </summary>
public sealed class AppSettings
{
    private const string DefaultFontName = "Arial";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly PropertyInfo[] SettableProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>Nom exact de l'imprimante choisie (null = choix automatique).</summary>
    public string? PrinterName { get; set; }

    /// <summary>Vrai une fois le premier lancement traité (démarrage automatique activé une fois).</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>Résolution de l'image générée (points par pouce).</summary>
    public int Dpi { get; set; } = 300;

    /// <summary>Largeur préférée de la barre la plus fine, en pixels à <see cref="Dpi"/>.</summary>
    public int ModulePx { get; set; } = 3;

    /// <summary>Largeur imprimable maximale en pixels (QL-800, ruban 62 mm : 696 points à 300 dpi).</summary>
    public int MaxWidthPx { get; set; } = 696;

    /// <summary>Hauteur des barres en pixels (150 px = 12,7 mm à 300 dpi).</summary>
    public int BarHeightPx { get; set; } = 150;

    public string FontName { get; set; } = DefaultFontName;

    public float FontSizePt { get; set; } = 11f;

    /// <summary>Largeur du ruban en mm, utilisée pour choisir le format de papier du pilote.</summary>
    public int LabelWidthMm { get; set; } = 62;

    /// <summary>Longueur minimale de l'étiquette en mm.</summary>
    public int MinLabelLengthMm { get; set; } = 15;

    /// <summary>Nom exact d'un format de papier du pilote à utiliser tel quel (null = automatique).</summary>
    public string? PaperName { get; set; }

    /// <summary>
    /// Vrai (par défaut) : avant d'imprimer, l'état de la file Windows est vérifié (hors connexion, capot ouvert,
    /// plus de ruban...) et l'impression est refusée si l'étiquette ne pouvait pas sortir. À mettre à faux si Windows
    /// affiche un état erroné qui bloque l'impression à tort.
    /// </summary>
    public bool CheckPrinterStatus { get; set; } = true;

    /// <summary>
    /// Vrai si le fichier de réglages existait mais n'a pas pu être lu en entier (JSON invalide, valeur de mauvais
    /// type, fichier verrouillé...). Interne : ni lu ni écrit dans le JSON.
    /// </summary>
    internal bool LoadFailed { get; private set; }

    /// <summary>%APPDATA%\BarcodeTray</summary>
    public static string DirectoryPath
    {
        get
        {
            string baseDir = string.Empty;
            try
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
                // repli ci-dessous
            }

            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, "BarcodeTray");
        }
    }

    /// <summary>%APPDATA%\BarcodeTray\settings.json</summary>
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>Charge les réglages. Ne lève jamais d'exception : en cas de problème, valeurs par défaut.</summary>
    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>Enregistre les réglages. Ne lève jamais d'exception (l'échec est journalisé).</summary>
    public void Save() => TrySave();

    /// <summary>Ramène les valeurs absurdes dans des bornes raisonnables.</summary>
    public void Validate()
    {
        Dpi = Math.Clamp(Dpi, 72, 1200);
        ModulePx = Math.Clamp(ModulePx, 1, 10);
        MaxWidthPx = Math.Clamp(MaxWidthPx, 64, 20000);
        BarHeightPx = Math.Clamp(BarHeightPx, 10, 5000);

        FontName = string.IsNullOrWhiteSpace(FontName) ? DefaultFontName : FontName.Trim();
        if (float.IsNaN(FontSizePt) || float.IsInfinity(FontSizePt))
        {
            FontSizePt = 11f;
        }

        FontSizePt = Math.Clamp(FontSizePt, 4f, 72f);

        LabelWidthMm = Math.Clamp(LabelWidthMm, 10, 300);
        MinLabelLengthMm = Math.Clamp(MinLabelLengthMm, 0, 1000);

        PrinterName = NullIfBlank(PrinterName);
        PaperName = NullIfBlank(PaperName);
    }

    /// <summary>Copie indépendante (pour travailler sans risque depuis un autre thread).</summary>
    internal AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Représentation JSON indentée (pour le diagnostic).</summary>
    internal string ToJson()
    {
        try
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }
        catch (Exception ex)
        {
            return "(sérialisation impossible : " + ex.Message + ")";
        }
    }

    internal static AppSettings LoadFrom(string path)
    {
        AppSettings result;
        try
        {
            if (!File.Exists(path))
            {
                Log.Info("Pas de fichier de réglages (" + path + ") : valeurs par défaut.");
                result = new AppSettings();
            }
            else
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                result = Parse(json, path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des réglages impossible (" + path + ") : valeurs par défaut.", ex);
            TryKeepCorruptCopy(path);
            result = new AppSettings { LoadFailed = true };
        }

        if (result.LoadFailed)
        {
            // Le fichier existe : ce n'est PAS un premier lancement. Sans cela, le programme réactiverait le
            // démarrage automatique (même décoché par l'utilisateur) et écraserait le fichier abîmé par les valeurs par défaut.
            result.FirstRunDone = true;
        }

        result.Validate();
        return result;
    }

    internal bool TrySave() => TrySaveTo(FilePath);

    internal bool TrySaveTo(string path)
    {
        string? tmp = null;
        try
        {
            Validate();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch (Exception replaceEx)
                {
                    // Remplacement refusé (antivirus, verrou...) : copie directe en dernier recours.
                    Log.Warn("File.Replace a échoué (" + replaceEx.Message + "), copie directe.");
                    File.Copy(tmp, path, true);
                    File.Delete(tmp);
                }
            }
            else
            {
                File.Move(tmp, path);
            }

            tmp = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement des réglages impossible (" + path + ").", ex);
            return false;
        }
        finally
        {
            if (tmp != null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignoré
                }
            }
        }
    }

    private static AppSettings Parse(string json, string path)
    {
        var result = new AppSettings();
        if (string.IsNullOrWhiteSpace(json))
        {
            Log.Warn("Fichier de réglages vide (" + path + ") : valeurs par défaut.");
            result.LoadFailed = true;
            return result;
        }

        var docOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        // JsonException (JSON invalide) remonte à LoadFrom, qui journalise et garde une copie du fichier.
        using JsonDocument doc = JsonDocument.Parse(json, docOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            Log.Warn("Le fichier de réglages n'est pas un objet JSON (" + path + ") : valeurs par défaut.");
            result.LoadFailed = true;
            TryKeepCorruptCopy(path);
            return result;
        }

        bool unreadable = false;
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            PropertyInfo? target = SettableProperties.FirstOrDefault(
                p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                continue; // propriété inconnue : ignorée
            }

            try
            {
                object? value = prop.Value.Deserialize(target.PropertyType, JsonOptions);
                if (value != null || !target.PropertyType.IsValueType)
                {
                    target.SetValue(result, value);
                }
            }
            catch (Exception ex)
            {
                // Une valeur illisible ne doit pas faire perdre les autres réglages.
                Log.Warn("Réglage « " + prop.Name + " » illisible (" + ex.Message + ") : valeur par défaut conservée.");
                unreadable = true;
            }
        }

        if (unreadable)
        {
            result.LoadFailed = true;
            TryKeepCorruptCopy(path);
        }

        return result;
    }

    /// <summary>
    /// Garde une copie du fichier abîmé (settings.json.invalide). Une copie déjà conservée n'est jamais écrasée :
    /// un contenu différent va dans settings.json.invalide-AAAAMMJJ-HHMMSS, un contenu déjà conservé n'est pas recopié.
    /// </summary>
    private static void TryKeepCorruptCopy(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1_000_000)
            {
                return;
            }

            byte[] content = File.ReadAllBytes(path);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                foreach (string existing in Directory.GetFiles(directory, Path.GetFileName(path) + ".invalide*"))
                {
                    if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(content))
                    {
                        return; // déjà conservé
                    }
                }
            }

            string target = path + ".invalide";
            if (File.Exists(target))
            {
                target += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            }

            File.WriteAllBytes(target, content);
        }
        catch
        {
            // ignoré
        }
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
