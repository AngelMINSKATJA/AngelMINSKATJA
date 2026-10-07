using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BarcodeTray;

/// <summary>
/// Petit journal texte : %LOCALAPPDATA%\BarcodeTray\barcodetray.log.
/// Thread-safe, ne lève JAMAIS d'exception, tourne au-delà d'environ 256 Ko
/// (l'ancien fichier devient barcodetray.log.1).
/// Le texte saisi par l'utilisateur n'est volontairement jamais journalisé (confidentialité).
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 256 * 1024;
    private const string FileName = "barcodetray.log";

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>%LOCALAPPDATA%\BarcodeTray (ou le dossier temporaire si LOCALAPPDATA est indisponible).</summary>
    internal static string DirectoryPath
    {
        get
        {
            string baseDir = string.Empty;
            try
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            catch
            {
                // ignoré : repli ci-dessous
            }

            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, "BarcodeTray");
        }
    }

    internal static string FilePath => Path.Combine(DirectoryPath, FileName);

    internal static void Info(string message) => Write("INFO ", message, null);

    internal static void Warn(string message) => Write("WARN ", message, null);

    internal static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>Dernières lignes du journal (pour le diagnostic). Ne lève jamais d'exception.</summary>
    internal static IReadOnlyList<string> ReadTail(int maxLines)
    {
        var result = new List<string>();
        try
        {
            string path = FilePath;
            if (!File.Exists(path) || maxLines <= 0)
            {
                return result;
            }

            lock (Gate)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var queue = new Queue<string>(maxLines + 1);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    queue.Enqueue(line);
                    if (queue.Count > maxLines)
                    {
                        queue.Dequeue();
                    }
                }

                result.AddRange(queue);
            }
        }
        catch
        {
            // pas de lecture possible : on renvoie ce qu'on a
        }

        return result;
    }

    private static void Write(string level, string? message, Exception? exception)
    {
        try
        {
            var sb = new StringBuilder(256);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            sb.Append(" [").Append(level).Append("] [T").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ");
            sb.Append(message ?? string.Empty);
            if (exception != null)
            {
                sb.AppendLine();
                sb.Append("    ").Append(exception.ToString().Replace("\r\n", "\n").Replace("\n", "\n    "));
            }

            sb.AppendLine();

            lock (Gate)
            {
                string dir = DirectoryPath;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, FileName);
                RotateIfNeeded(path);
                File.AppendAllText(path, sb.ToString(), Utf8NoBom);
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxFileBytes)
            {
                return;
            }

            try
            {
                File.Move(path, path + ".1", true);
            }
            catch
            {
                // Impossible de renommer (verrou ?) : on tronque pour ne pas grossir indéfiniment.
                File.WriteAllText(path, string.Empty, Utf8NoBom);
            }
        }
        catch
        {
            // ignoré
        }
    }
}
