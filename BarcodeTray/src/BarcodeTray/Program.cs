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
    private const string AppTitle = "Code-barres Code 128";
    private const string MutexName = @"Local\BarcodeTray.SingleInstance";
    private const string ShowEventName = @"Local\BarcodeTray.Show";
    private const int MaxErrorDialogs = 3;

    // Champs statiques : ils doivent rester référencés pendant toute la vie du processus,
    // sinon le ramasse-miettes libérerait le Mutex et la protection « une seule instance » disparaîtrait.
    private static Mutex? _singleInstanceMutex;
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

        bool startInTray = HasFlag(args, "--tray");
        Log.Info("Démarrage (version " + typeof(Program).Assembly.GetName().Version + ", arguments : "
                 + (args.Length == 0 ? "(aucun)" : string.Join(" ", args)) + ", exécutable : "
                 + (Environment.ProcessPath ?? "?") + ").");

        // 1) Une seule instance par session Windows.
        OpenShowEvent();
        if (!TryBecomeFirstInstance())
        {
            if (!startInTray)
            {
                SignalFirstInstance();
            }

            Log.Info("Une instance est déjà active : fermeture de celle-ci.");
            return 0;
        }

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
                WriteError("Usage : BarcodeTray.exe --selftest <fichier.png>");
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
