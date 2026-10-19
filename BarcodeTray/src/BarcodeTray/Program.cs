using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BarcodeTray;

internal static class Program
{
    private const string AppTitle = AppInfo.DisplayName;

    // Noms techniques : ils NE suivent PAS le nom affiché (AppInfo). Une ancienne et une nouvelle version du programme
    // doivent se reconnaître comme « la même application » (une seule instance par session), voir AppInfo.
    private const string MutexName = @"Local\BarcodeTray.SingleInstance";
    private const string ShowEventName = @"Local\BarcodeTray.Show";
    private const int MaxErrorDialogs = 3;

    // Champs statiques : ils doivent rester référencés pendant toute la vie du processus,
    // sinon le ramasse-miettes libérerait le Mutex et la protection « une seule instance » disparaîtrait.
    private static Mutex? _singleInstanceMutex;
    private static Mutex? _runningMarker;
    private static EventWaitHandle? _showEvent;

    private static bool _inErrorDialog;
    private static int _errorDialogCount;

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Log.Error("Erreur fatale au démarrage.", ex);
            TryShowError("Le programme n'a pas pu démarrer :\n\n" + ex.Message
                         + "\n\nDétail dans le journal :\n" + Log.FilePath);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length > 0 && IsFlag(args[0], "--selftest"))
        {
            return RunSelfTest(args);
        }

        if (args.Length > 0 && IsFlag(args[0], "--selftest-label"))
        {
            return RunSelfTestLabel(args);
        }

        bool startInTray = HasFlag(args, "--tray");
        Log.Info("Démarrage de " + AppInfo.DisplayName + " (version " + typeof(Program).Assembly.GetName().Version + ", arguments : "
                 + (args.Length == 0 ? "(aucun)" : string.Join(" ", args)) + ", exécutable : "
                 + (Environment.ProcessPath ?? "?") + ").");

        // 1) Une seule instance par session Windows.
        OpenShowEvent();
        if (!TryBecomeFirstInstance())
        {
            return HandleSecondInstance(startInTray);
        }

        // Marqueur « version 3 ou plus récente » (voir InstanceHandoff), créé tout de suite après le mutex : une ancienne
        // version ne le crée pas, ce qui permet à un programme lancé plus tard de la reconnaître.
        _runningMarker = InstanceHandoff.CreateMarker(InstanceHandoff.RunningMarkerName);

        // 2) Réglages et premier lancement.
        AppSettings settings = AppSettings.Load();
        if (settings.LoadFailed)
        {
            // Fichier présent mais illisible : ce n'est pas un premier lancement. On ne réactive pas le démarrage
            // automatique (l'utilisateur l'a peut-être décoché) et on n'écrase pas le fichier abîmé ; la bulle
            // d'information est affichée par TrayApplicationContext.
            Log.Warn("Réglages illisibles : valeurs par défaut utilisées, démarrage automatique et fichier laissés tels quels.");
            StartupRegistration.EnsurePathCurrent();
        }
        else if (!settings.FirstRunDone && StartupRegistration.IsTransientLocation(Environment.ProcessPath))
        {
            // Premier lancement depuis le ZIP / un dossier temporaire : on n'enregistre rien et on ne marque pas le
            // premier lancement comme fait, pour réessayer quand l'exécutable aura été copié dans un dossier permanent.
            Log.Warn("Premier lancement depuis un emplacement temporaire : démarrage automatique reporté.");
        }
        else if (!settings.FirstRunDone)
        {
            bool enabled = StartupRegistration.Enable();
            settings.FirstRunDone = true;
            settings.Save();
            Log.Info("Premier lancement : démarrage automatique " + (enabled ? "activé." : "non activé (échec)."));
        }
        else
        {
            StartupRegistration.EnsurePathCurrent();
        }

        // 3) Interface. L'ordre est imposé par WinForms : avant toute création de fenêtre.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        using (var context = new TrayApplicationContext(settings, startInTray))
        {
            StartShowWatcher(context);
            Application.Run(context);
        }

        Log.Info("Arrêt normal.");
        GC.KeepAlive(_singleInstanceMutex);
        GC.KeepAlive(_runningMarker);
        GC.KeepAlive(_showEvent);
        return 0;
    }

    // ------------------------------------------------------------------ auto-test (CI)

    /// <summary>
    /// --selftest sortie.png : sans interface, génère un code-barres d'exemple avec les réglages par défaut
    /// et l'écrit en PNG. Code 0 si tout va bien, 2 (message sur la sortie d'erreur) sinon.
    /// </summary>
    private static int RunSelfTest(string[] args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                WriteError("Usage : \"" + AppInfo.FileBaseName + ".exe\" --selftest <fichier.png>");
                return 2;
            }

            string outputPath = Path.GetFullPath(args[1]);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settings = new AppSettings();
            using Bitmap bitmap = BarcodeRenderer.Render("BarcodeTray 0123456789", settings);
            byte[] png = BarcodeRenderer.ToPng(bitmap);
            if (png.Length < 100 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
            {
                WriteError("Le PNG généré est invalide.");
                return 2;
            }

            File.WriteAllBytes(outputPath, png);

            using Icon? icon = TrayApplicationContext.LoadAppIcon();
            Log.Info("Auto-test réussi : " + outputPath + " (" + bitmap.Width + "x" + bitmap.Height + " px, "
                     + png.Length + " octets, icône intégrée : " + (icon != null ? "oui" : "NON") + ").");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Auto-test en échec.", ex);
            WriteError("Auto-test en échec : " + ex);
            return 2;
        }
    }

    /// <summary>
    /// --selftest-label sortie.png : sans interface, dessine l'étiquette d'exemple « HB1 » / « 22062 » (page 62 x 40 mm,
    /// zone imprimable du QL-800 : 6 ; 11,67 ; 232 x 134 centièmes de pouce) avec les réglages par défaut et l'écrit en PNG.
    /// Vérifie au passage que le texte est bien dessiné (encre présente, à l'endroit annoncé, dans la zone imprimable).
    /// Code 0 si tout va bien, 2 (message sur la sortie d'erreur) sinon.
    /// </summary>
    internal static int RunSelfTestLabel(string[] args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                WriteError("Usage : \"" + AppInfo.FileBaseName + ".exe\" --selftest-label <fichier.png>");
                return 2;
            }

            string outputPath = Path.GetFullPath(args[1]);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            const int pageWidthUnits = 244;   // 62 mm
            const int pageHeightUnits = 157;  // 40 mm
            var printable = new RectangleF(6f, 11.67f, 232f, 134f);
            var settings = new AppSettings();

            using Bitmap bitmap = PlanLabelRenderer.Render("HB1", "22062", settings, pageWidthUnits, pageHeightUnits, printable, out PlanLabelLayout layout);
            int expectedWidth = PlanLabelRenderer.PageSizePx(pageWidthUnits, settings.Dpi);
            int expectedHeight = PlanLabelRenderer.PageSizePx(pageHeightUnits, settings.Dpi);
            if (bitmap.Width != expectedWidth || bitmap.Height != expectedHeight)
            {
                WriteError("Dimensions inattendues : " + bitmap.Width + "x" + bitmap.Height + " au lieu de " + expectedWidth + "x" + expectedHeight + ".");
                return 2;
            }

            Rectangle ink = PlanLabelRenderer.FindInkBounds(bitmap);
            if (ink.IsEmpty)
            {
                WriteError("L'étiquette générée est vide (aucun texte dessiné).");
                return 2;
            }

            // Le dessin doit correspondre à la mise en page annoncée (à quelques points près : bords lissés)...
            RectangleF expected = RectangleF.Union(layout.TypeInkPx, layout.PlanInkPx);
            const float tolerancePx = 3f;
            if (ink.Left < expected.Left - tolerancePx || ink.Top < expected.Top - tolerancePx
                || ink.Right > expected.Right + tolerancePx || ink.Bottom > expected.Bottom + tolerancePx
                || ink.Left > expected.Left + tolerancePx || ink.Top > expected.Top + tolerancePx
                || ink.Right < expected.Right - tolerancePx || ink.Bottom < expected.Bottom - tolerancePx)
            {
                WriteError("Le texte n'est pas dessiné là où la mise en page l'annonce : encre " + ink + ", attendu " + expected + ".");
                return 2;
            }

            // ... et rester dans la largeur imprimable de l'imprimante.
            float perUnit = settings.Dpi / 100f;
            float printableLeft = printable.X * perUnit;
            float printableRight = (printable.X + printable.Width) * perUnit;
            if (ink.Left < printableLeft - 1f || ink.Right > printableRight + 1f)
            {
                WriteError("Le texte dépasse la zone imprimable (" + ink.Left + " à " + ink.Right + " pour " + printableLeft + " à " + printableRight + ").");
                return 2;
            }

            byte[] png = BarcodeRenderer.ToPng(bitmap);
            if (png.Length < 100 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
            {
                WriteError("Le PNG généré est invalide.");
                return 2;
            }

            File.WriteAllBytes(outputPath, png);

            Log.Info("Auto-test étiquette réussi : " + outputPath + " (" + bitmap.Width + "x" + bitmap.Height + " px, "
                     + png.Length + " octets, police « " + layout.FontName + " », type " + layout.TypePt + " pt, plan "
                     + layout.PlanPt + " pt).");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Auto-test étiquette en échec.", ex);
            WriteError("Auto-test étiquette en échec : " + ex);
            return 2;
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // pas de console (application Windows) : le journal contient l'information
        }
    }

    // ------------------------------------------------------------------ instance unique

    private static void OpenShowEvent()
    {
        try
        {
            // Créé (ou ouvert) AVANT le mutex et conservé jusqu'à la fin du processus : un signal envoyé trop tôt
            // par une deuxième instance n'est donc jamais perdu.
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (Exception ex)
        {
            Log.Error("Événement d'affichage inaccessible.", ex);
            _showEvent = null;
        }
    }

    private static bool TryBecomeFirstInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Le mutex existe, créé dans un autre contexte de sécurité : une instance tourne déjà.
            Log.Warn("Mutex inaccessible (instance existante ?) : " + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // Mieux vaut risquer deux instances que de ne pas démarrer du tout.
            Log.Error("Création du mutex impossible : démarrage sans protection d'instance unique.", ex);
            return true;
        }
    }

    /// <summary>
    /// Une autre instance est déjà active dans cette session. Instance de cette version : on lui demande d'afficher sa fenêtre
    /// (sauf au démarrage automatique). ANCIENNE version (premier lancement du nouvel exécutable alors que l'ancien tourne,
    /// cas normal puisqu'il démarre avec Windows) : on ne réveille pas sa fenêtre, on redirige le démarrage automatique vers
    /// cet exécutable et on explique quoi faire.
    /// </summary>
    private static int HandleSecondInstance(bool startInTray)
    {
        SecondInstanceAction action = InstanceHandoff.Decide(
            startInTray,
            () => InstanceHandoff.IsMarkerPresent(InstanceHandoff.RunningMarkerName, InstanceHandoff.ProbeAttempts, InstanceHandoff.ProbePauseMs));

        switch (action)
        {
            case SecondInstanceAction.ShowRunningWindow:
                SignalFirstInstance();
                break;

            case SecondInstanceAction.ExplainOlderVersion:
                Log.Warn("Une ancienne version du programme est déjà active : démarrage automatique redirigé vers cet exécutable, "
                         + "message affiché, fermeture de celle-ci.");
                StartupRegistration.EnsurePathCurrent(); // sans effet si le démarrage automatique est désactivé ou si l'exe est dans un dossier temporaire
                ShowOlderVersionNotice();
                return 0;
        }

        Log.Info("Une instance est déjà active : fermeture de celle-ci.");
        return 0;
    }

    private static void ShowOlderVersionNotice()
    {
        try
        {
            Application.EnableVisualStyles();
            MessageBox.Show(InstanceHandoff.OlderVersionMessage(), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Warn("Message « ancienne version » impossible à afficher : " + ex.Message);
        }
    }

    private static void SignalFirstInstance()
    {
        try
        {
            // Ce processus vient d'être lancé par l'utilisateur : il peut autoriser l'autre à passer au premier plan.
            AllowSetForegroundWindow(-1); // ASFW_ANY
        }
        catch (Exception ex)
        {
            Log.Warn("AllowSetForegroundWindow impossible : " + ex.Message);
        }

        try
        {
            _showEvent?.Set();
        }
        catch (Exception ex)
        {
            Log.Error("Signal vers la première instance impossible.", ex);
        }
    }

    private static void StartShowWatcher(TrayApplicationContext context)
    {
        EventWaitHandle? showEvent = _showEvent;
        if (showEvent == null)
        {
            return;
        }

        var watcher = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    showEvent.WaitOne();
                }
                catch (Exception ex)
                {
                    Log.Warn("Attente du signal d'affichage interrompue : " + ex.Message);
                    return;
                }

                try
                {
                    context.ShowMainWindow(); // renvoie vers le thread d'interface sans attendre
                }
                catch (Exception ex)
                {
                    Log.Error("Affichage demandé par une autre instance impossible.", ex);
                }
            }
        })
        {
            IsBackground = true, // ne retient jamais la fermeture du processus
            Name = "BarcodeTray.ShowWatcher",
        };
        watcher.Start();
    }

    // ------------------------------------------------------------------ erreurs non gérées

    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        Log.Error("Exception non gérée (thread d'interface).", e.Exception);
        TryShowError("Une erreur inattendue s'est produite :\n\n" + e.Exception.Message
                     + "\n\nL'application continue. Détail dans le journal :\n" + Log.FilePath);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Exception non gérée (fatale : " + e.IsTerminating + ").", e.ExceptionObject as Exception);
        if (e.IsTerminating)
        {
            TrayApplicationContext.EmergencyCleanup(); // pas d'icône fantôme après un plantage
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Exception de tâche non observée.", e.Exception);
        e.SetObserved();
    }

    private static void TryShowError(string message)
    {
        // Jamais plus de quelques boîtes de dialogue, jamais de boîte imbriquée (boucle d'erreurs).
        if (_inErrorDialog || _errorDialogCount >= MaxErrorDialogs)
        {
            return;
        }

        _inErrorDialog = true;
        _errorDialogCount++;
        try
        {
            MessageBox.Show(message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // rien de plus à faire
        }
        finally
        {
            _inErrorDialog = false;
        }
    }

    // ------------------------------------------------------------------ arguments

    private static bool IsFlag(string arg, string name)
    {
        return string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
               || string.Equals(arg, "/" + name.TrimStart('-'), StringComparison.OrdinalIgnoreCase)
               || string.Equals(arg, name.Substring(1), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (IsFlag(arg, name))
            {
                return true;
            }
        }

        return false;
    }
}
