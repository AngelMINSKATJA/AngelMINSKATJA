using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BarcodeTray;

/// <summary>
/// Cœur de l'application : icône dans la zone de notification + fenêtre principale.
/// Aucune fenêtre n'est affichée à la création (pas de scintillement avec --tray) ; la poignée de la fenêtre
/// principale n'est créée qu'au premier affichage.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string TrayTip = "Code-barres Code 128";
    private const int DiagnosticsTimeoutMs = 25_000;

    // Un clic sur l'icône désactive d'abord la fenêtre (le focus passe à la barre des tâches) puis arrive quelques
    // dizaines de millisecondes plus tard : une désactivation aussi récente signifie « elle était au premier plan ».
    private const long ClickAfterDeactivationMs = 500;

    private static TrayApplicationContext? _current;

    private readonly AppSettings _settings;
    private readonly Control _marshal;
    private readonly MainForm _form;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Font? _boldFont;
    private readonly Icon? _trayIcon;
    private bool _disposed;
    private bool _exiting;
    private bool _stayActiveHintShown;
    private long _formDeactivatedTick = -1; // Environment.TickCount64 de la dernière désactivation (-1 = jamais)

    public TrayApplicationContext(AppSettings settings, bool startHidden)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // Contrôle invisible dont la poignée est créée ICI, sur le thread d'interface : il sert à ramener sur ce
        // thread les appels venant d'ailleurs (signal de la 2e instance, fin de session).
        _marshal = new Control();
        _ = _marshal.Handle;

        _form = new MainForm(settings);
        _form.HiddenByUser += OnFormHiddenByUser;
        _form.Deactivate += (_, _) => _formDeactivatedTick = Environment.TickCount64;
        _form.FormClosed += OnFormClosed;

        _menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Ouvrir");
        _boldFont = new Font(openItem.Font, FontStyle.Bold);
        openItem.Font = _boldFont;
        openItem.Click += (_, _) => Safe("Ouvrir", ShowMainWindowCore);

        _startupItem = new ToolStripMenuItem("Lancer au démarrage de Windows");
        _startupItem.Click += (_, _) => Safe("Démarrage automatique", ToggleStartup);

        var diagItem = new ToolStripMenuItem("Copier le diagnostic d'impression");
        diagItem.Click += OnCopyDiagnosticsClick;

        var exitItem = new ToolStripMenuItem("Quitter");
        exitItem.Click += (_, _) => Safe("Quitter", ExitApplication);

        _menu.Items.Add(openItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(diagItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => Safe("Menu", () => _startupItem.Checked = StartupRegistration.IsEnabled());

        _trayIcon = LoadAppIcon(SystemInformation.SmallIconSize);
        _notifyIcon = new NotifyIcon
        {
            Text = TrayTip,
            Icon = _trayIcon ?? SystemIcons.Application,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += OnNotifyMouseClick;
        _notifyIcon.MouseDoubleClick += OnNotifyMouseDoubleClick;
        _notifyIcon.Visible = true;

        SystemEvents.SessionEnded += OnSessionEnded;
        _current = this;

        if (settings.LoadFailed)
        {
            // Affichage différé : la boucle de messages doit tourner pour que la bulle apparaisse.
            _marshal.BeginInvoke(new Action(() => ShowBalloon(
                "Réglages illisibles",
                "Le fichier settings.json n'a pas pu être lu : les valeurs par défaut sont utilisées. "
                + "Une copie est gardée sous le nom settings.json.invalide dans " + AppSettings.DirectoryPath + ".",
                ToolTipIcon.Warning)));
        }

        if (!startHidden)
        {
            // Affichage différé : exécuté dès que la boucle de messages tourne.
            _marshal.BeginInvoke(new Action(ShowMainWindowCore));
        }
    }

    /// <summary>La fenêtre principale (pour les tests).</summary>
    internal MainForm Window => _form;

    /// <summary>
    /// Affiche la fenêtre principale au premier plan. Peut être appelée depuis n'importe quel thread
    /// (par exemple le thread qui attend le signal de la deuxième instance) : l'appel est renvoyé,
    /// sans attente, vers le thread d'interface.
    /// </summary>
    public void ShowMainWindow()
    {
        try
        {
            if (_disposed || _marshal.IsDisposed || !_marshal.IsHandleCreated)
            {
                return;
            }

            if (_marshal.InvokeRequired)
            {
                _marshal.BeginInvoke(new Action(ShowMainWindowCore));
            }
            else
            {
                ShowMainWindowCore();
            }
        }
        catch (ObjectDisposedException)
        {
            // fermeture en cours
        }
        catch (InvalidOperationException)
        {
            // poignée détruite pendant l'appel : fermeture en cours
        }
        catch (Exception ex)
        {
            Log.Error("Affichage de la fenêtre demandé impossible.", ex);
        }
    }

    /// <summary>
    /// Retire l'icône de la zone de notification en urgence (plantage). Utilisable depuis n'importe quel thread ;
    /// ne lève jamais d'exception.
    /// </summary>
    internal static void EmergencyCleanup()
    {
        try
        {
            TrayApplicationContext? current = _current;
            if (current != null && !current._disposed)
            {
                current._notifyIcon.Visible = false;
            }
        }
        catch
        {
            // dernier recours : rien d'autre à faire
        }
    }

    /// <summary>
    /// Charge l'icône intégrée (ressource « app.ico »). Renvoie null si elle est absente ou illisible.
    /// Sans taille : taille par défaut du système.
    /// </summary>
    internal static Icon? LoadAppIcon(Size? size = null)
    {
        try
        {
            using Stream? stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("app.ico");
            if (stream == null)
            {
                Log.Warn("Ressource d'icône « app.ico » introuvable : icône système utilisée.");
                return null;
            }

            if (size.HasValue)
            {
                try
                {
                    return new Icon(stream, size.Value);
                }
                catch (Exception ex)
                {
                    // Taille demandée refusée : on retente avec la taille par défaut du système.
                    Log.Warn("Icône « app.ico » à la taille " + size.Value + " impossible (" + ex.Message + ") : taille par défaut.");
                    stream.Position = 0;
                }
            }

            return new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Error("Icône « app.ico » illisible : icône système utilisée.", ex);
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                SystemEvents.SessionEnded -= OnSessionEnded;
            }
            catch
            {
                // ignoré
            }

            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }

            // Icône d'abord : aucune icône fantôme ne doit rester dans la zone de notification.
            try
            {
                _notifyIcon.Visible = false;
            }
            catch
            {
                // ignoré
            }

            try
            {
                _notifyIcon.Dispose();
            }
            catch
            {
                // ignoré
            }

            try
            {
                _menu.Dispose();
            }
            catch
            {
                // ignoré
            }

            try
            {
                _form.AllowExit = true;
                _form.Dispose();
            }
            catch
            {
                // ignoré
            }

            try
            {
                _marshal.Dispose();
            }
            catch
            {
                // ignoré
            }

            _boldFont?.Dispose();
            _trayIcon?.Dispose();
        }

        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ actions (thread d'interface)

    private void ShowMainWindowCore()
    {
        if (_disposed || _exiting)
        {
            return;
        }

        Safe("Afficher la fenêtre", _form.ShowAndFocus);
    }

    private void ToggleMainWindow()
    {
        if (_disposed || _exiting)
        {
            return;
        }

        long sinceDeactivated = _formDeactivatedTick < 0 ? long.MaxValue : Environment.TickCount64 - _formDeactivatedTick;
        bool isActive = ReferenceEquals(Form.ActiveForm, _form);
        if (ShouldHideOnTrayClick(_form.Visible, _form.WindowState, isActive, sinceDeactivated))
        {
            _form.HideToTray(false);
        }
        else
        {
            _form.ShowAndFocus();
        }
    }

    /// <summary>
    /// Un clic gauche sur l'icône masque la fenêtre seulement si elle était visible ET au premier plan juste avant
    /// le clic. Visible mais cachée derrière Word ou Excel (cas courant : on colle, puis on revient), le clic doit la
    /// ramener devant, pas la faire disparaître.
    /// </summary>
    /// <param name="visible">La fenêtre est affichée.</param>
    /// <param name="state">État de la fenêtre.</param>
    /// <param name="formIsActive">La fenêtre est la fenêtre active de l'application à cet instant.</param>
    /// <param name="msSinceDeactivated">Millisecondes depuis la dernière désactivation de la fenêtre.</param>
    internal static bool ShouldHideOnTrayClick(bool visible, FormWindowState state, bool formIsActive, long msSinceDeactivated)
    {
        if (!visible || state == FormWindowState.Minimized)
        {
            return false;
        }

        return formIsActive || (msSinceDeactivated >= 0 && msSinceDeactivated <= ClickAfterDeactivationMs);
    }

    private void ToggleStartup()
    {
        bool enable = !StartupRegistration.IsEnabled();
        bool ok = enable ? StartupRegistration.Enable() : StartupRegistration.Disable();
        if (!ok)
        {
            ShowBalloon(
                "Démarrage automatique",
                "Impossible de modifier le lancement au démarrage de Windows (accès refusé par le poste ?).",
                ToolTipIcon.Warning);
        }

        _startupItem.Checked = StartupRegistration.IsEnabled();
        _form.RefreshStartupCheckbox();
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Log.Info("Fermeture de l'application.");
        _form.AllowExit = true;
        try
        {
            _notifyIcon.Visible = false; // l'icône disparaît tout de suite
        }
        catch
        {
            // ignoré
        }

        ExitThread();
    }

    private async void OnCopyDiagnosticsClick(object? sender, EventArgs e)
    {
        try
        {
            string? printer = _form.SelectedPrinter ?? _settings.PrinterName;
            AppSettings snapshot = _settings.Clone();

            Task<string> work = Task.Run(() =>
            {
                string? name = printer;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = LabelPrinter.PickDefaultPrinter(
                        LabelPrinter.GetPrinters(), snapshot.PrinterName, LabelPrinter.GetSystemDefaultPrinter());
                }

                return LabelPrinter.BuildDiagnostics(name, snapshot);
            });

            Task finished = await Task.WhenAny(work, Task.Delay(DiagnosticsTimeoutMs));
            string text;
            if (finished == work)
            {
                text = await work;
            }
            else
            {
                Log.Error("Diagnostic : l'énumération des imprimantes n'a pas répondu à temps.");
                text = "=== Diagnostic d'impression - Code-barres Code 128 ===" + Environment.NewLine
                       + "Diagnostic incomplet : Windows n'a pas répondu à la liste des imprimantes en "
                       + DiagnosticsTimeoutMs / 1000 + " s." + Environment.NewLine + Environment.NewLine
                       + snapshot.ToJson();
            }

            if (_disposed)
            {
                return;
            }

            ClipboardService.SetText(text);
            ShowBalloon(
                "Diagnostic copié",
                "Le diagnostic d'impression est dans le presse-papier : collez-le (Ctrl+V) dans un message.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Copie du diagnostic impossible.", ex);
            ShowBalloon("Diagnostic impossible", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// La fenêtre a été fermée pour de vrai (Gestionnaire des tâches « Fin de tâche », taskkill sans /f,
    /// CloseMainWindow, fin de session...) : elle est détruite, donc l'application doit s'arrêter aussi. Sinon l'icône
    /// resterait dans la zone de notification avec une fenêtre morte que plus rien ne peut rouvrir.
    /// Les fermetures « ordinaires » (X, Alt+F4, Échap) sont annulées par la fenêtre elle-même et n'arrivent jamais ici.
    /// </summary>
    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (_exiting || _disposed)
        {
            return;
        }

        Log.Info("Fenêtre fermée (" + e.CloseReason + ") : arrêt de l'application.");
        Safe("Fermeture", ExitApplication);
    }

    /// <summary>Une seule fois par session : explique que le programme reste actif quand on ferme la fenêtre.</summary>
    private void OnFormHiddenByUser(object? sender, EventArgs e)
    {
        if (_stayActiveHintShown || _disposed || _exiting)
        {
            return;
        }

        _stayActiveHintShown = true;
        ShowBalloon(
            TrayTip,
            "Le programme reste actif dans la zone de notification. Cliquez sur son icône pour rouvrir la fenêtre ; "
            + "clic droit puis « Quitter » pour le fermer.",
            ToolTipIcon.Info);
    }

    private void OnNotifyMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Safe("Clic sur l'icône", ToggleMainWindow);
        }
    }

    private void OnNotifyMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Safe("Double-clic sur l'icône", ShowMainWindowCore);
        }
    }

    /// <summary>
    /// Appelé par Windows sur son propre thread (pas celui de l'interface). SessionEnded = notification FINALE
    /// (WM_ENDSESSION, la session se termine vraiment). SessionEnding, elle, n'est qu'une demande (WM_QUERYENDSESSION)
    /// que Word, Excel ou l'utilisateur peuvent encore refuser : s'arrêter à ce stade ferait disparaître l'icône
    /// d'une session qui continue.
    /// </summary>
    private void OnSessionEnded(object sender, SessionEndedEventArgs e)
    {
        try
        {
            Log.Info("Fin de session Windows : fermeture.");
            if (!_disposed && !_marshal.IsDisposed && _marshal.IsHandleCreated)
            {
                IAsyncResult result = _marshal.BeginInvoke(new Action(ExitApplication));
                result.AsyncWaitHandle.WaitOne(2000);
            }
        }
        catch
        {
            // la session se termine : on ne peut rien faire de plus
        }
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            if (!_disposed)
            {
                _notifyIcon.ShowBalloonTip(4000, title, text, icon);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Notification impossible : " + ex.Message);
        }
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(what + " : erreur inattendue.", ex);
        }
    }
}
