using System;
using System.IO;
using System.Linq;
using System.Text;

namespace BarcodeTray.Tests.Support;

/// <summary>
/// %APPDATA%\BarcodeTray\settings.json n'est pas redirigeable : les tests utilisent donc le vrai emplacement,
/// après avoir mis de côté tout fichier "settings.json*" existant, et le remettent en place à la fin
/// (important pour quelqu'un qui lancerait les tests sur son propre poste).
/// </summary>
internal sealed class SettingsSandbox : IDisposable
{
    private readonly string _directory = AppSettings.DirectoryPath;
    private readonly string _backupDirectory;
    private readonly bool _directoryExisted;

    internal SettingsSandbox()
    {
        _directoryExisted = Directory.Exists(_directory);
        Directory.CreateDirectory(_directory);
        _backupDirectory = Path.Combine(_directory, ".testbackup-" + Guid.NewGuid().ToString("N"));

        string[] existing = Directory.GetFiles(_directory, "settings.json*");
        if (existing.Length > 0)
        {
            Directory.CreateDirectory(_backupDirectory);
            foreach (string file in existing)
            {
                File.Move(file, Path.Combine(_backupDirectory, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>Chemin du fichier de réglages réel de l'application.</summary>
    internal string SettingsPath => AppSettings.FilePath;

    internal void DeleteSettingsFile()
    {
        if (File.Exists(SettingsPath))
        {
            File.Delete(SettingsPath);
        }
    }

    internal void WriteRaw(string content) => File.WriteAllText(SettingsPath, content, new UTF8Encoding(false));

    internal void WriteRawBytes(byte[] content) => File.WriteAllBytes(SettingsPath, content);

    internal string[] FilesInDirectory() =>
        Directory.GetFiles(_directory).Select(Path.GetFileName).OfType<string>().ToArray();

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.GetFiles(_directory, "settings.json*"))
            {
                File.Delete(file);
            }

            if (Directory.Exists(_backupDirectory))
            {
                foreach (string file in Directory.GetFiles(_backupDirectory))
                {
                    File.Move(file, Path.Combine(_directory, Path.GetFileName(file)));
                }

                Directory.Delete(_backupDirectory, true);
            }

            // Ne laisse pas de dossier vide si les tests l'ont créé eux-mêmes.
            if (!_directoryExisted && Directory.GetFileSystemEntries(_directory).Length == 0)
            {
                Directory.Delete(_directory);
            }
        }
        catch
        {
            // Le nettoyage ne doit pas masquer le résultat du test.
        }
    }
}
