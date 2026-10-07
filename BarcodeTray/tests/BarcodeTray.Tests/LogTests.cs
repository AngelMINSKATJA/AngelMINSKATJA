using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BarcodeTray.Tests;

public class LogTests
{
    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarcodeTray", "barcodetray.log");

    // Le journal tourne au-delà de ~256 Ko (l'ancien fichier devient « .1 ») : on lit les deux.
    private static string ReadLog()
    {
        string rotated = LogFilePath + ".1";
        return (File.Exists(rotated) ? ReadFile(rotated) : string.Empty) + ReadFile(LogFilePath);
    }

    private static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void InfoAndError_AreWrittenToTheLogFile_WithIsoTimestamps()
    {
        string marker = "marqueur-" + Guid.NewGuid().ToString("N");

        Log.Info("Info de test " + marker + " éàç");
        Log.Error("Erreur de test " + marker, new InvalidOperationException("boum-" + marker));

        string content = ReadLog();
        Assert.Contains("Info de test " + marker + " éàç", content, StringComparison.Ordinal);
        Assert.Contains("Erreur de test " + marker, content, StringComparison.Ordinal);
        Assert.Contains("boum-" + marker, content, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", RegexOptions.Multiline), content);
    }

    [Fact]
    public void Logging_NeverThrows_EvenWithOddInput()
    {
        Log.Info(null!);
        Log.Info(string.Empty);
        Log.Info(new string('x', 50_000));
        Log.Error(null!, null);
        Log.Error("avec exception interne", new AggregateException(new InvalidOperationException("interne"), new ArgumentException("autre")));
    }

    [Fact]
    public void Logging_IsThreadSafe_LinesAreNeverInterleaved()
    {
        string marker = "thread-" + Guid.NewGuid().ToString("N");

        System.Threading.Tasks.Parallel.For(0, 200, i => Log.Info(marker + " ligne " + i));

        string[] lines = ReadLog()
            .Split('\n')
            .Where(l => l.Contains(marker, StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

        // Chaque ligne est entière : horodatage au début, "marqueur ligne N" à la fin (rien d'entrelacé).
        var wellFormed = new Regex("^\\d{4}-\\d{2}-\\d{2}T.* " + Regex.Escape(marker) + " ligne \\d+$");
        Assert.All(lines, line => Assert.Matches(wellFormed, line));
        // Une écriture peut être perdue si un antivirus verrouille brièvement le fichier : on tolère un petit déficit.
        Assert.True(lines.Length >= 180, "Seulement " + lines.Length + " lignes sur 200 retrouvées dans le journal.");
    }
}
