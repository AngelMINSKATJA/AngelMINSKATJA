using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BarcodeTray;

/// <summary>
/// Fenêtre principale : texte, [Générer], imprimante, [Imprimer], démarrage automatique, état.
/// Elle ne se ferme jamais vraiment (sauf <see cref="AllowExit"/> ou fin de session Windows) :
/// fermer, Échap ou réduire la masque dans la zone de notification.
/// </summary>
internal sealed class MainForm : Form
{
    private const string AppTitle = "Code-barres Code 128";
    private const string HintText =
        "Saisissez un texte, puis cliquez sur « Générer » pour le copier dans le presse-papier, "
        + "ou sur « Imprimer » pour imprimer l'étiquette.";
    private const int MaxStatusLength = 400;
    private const int PrintTimeoutMs = 60_000;

    private enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    private static readonly Color InfoColor = SystemColors.GrayText;
    private static readonly Color SuccessColor = Color.FromArgb(0, 110, 40);
    private static readonly Color WarningColor = Color.FromArgb(160, 80, 0);
    private static readonly Color ErrorColor = Color.FromArgb(192, 0, 0);

    private readonly AppSettings _settings;
    private readonly TextBox _txtText = new();
    private readonly Button _btnGenerate = new();
    private readonly ComboBox _cmbPrinter = new();
    private readonly Button _btnPrint = new();
    private readonly CheckBox _chkStartup = new();
    private readonly Label _lblStatus = new();
    private readonly ToolTip _toolTip = new();

    private Icon? _formIcon;
    private bool _loadingPrinters;
    private bool _syncingStartup;
    private bool _printing;
    private bool _printPending; // un envoi qui a dépassé le délai tourne encore en arrière-plan
    private bool _restoring;

    public MainForm(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        BuildUi();
    }

    /// <summary>Vrai quand la fermeture doit être réelle (« Quitter »).</summary>
    public bool AllowExit { get; set; }

    /// <summary>Imprimante actuellement sélectionnée dans la liste (null si aucune).</summary>
    public string? SelectedPrinter => _cmbPrinter.SelectedItem as string;

    /// <summary>
    /// Affiche la fenêtre (même depuis la barre des tâches réduite ou masquée), la met au premier plan
    /// et place le curseur dans la zone de texte, texte sélectionné. À appeler depuis le thread d'interface.
    /// </summary>
    public void ShowAndFocus()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            RefreshStartupCheckbox();
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du démarrage automatique impossible.", ex);
        }

        try
        {
            RefreshPrinters();
        }
        catch (Exception ex)
        {
            Log.Error("Actualisation des imprimantes impossible.", ex);
        }

        try
        {
            if (!Visible && !_printing && !_printPending)
            {
                SetStatus(HintText, StatusKind.Info); // pas de message périmé à la réouverture
            }

            // Pendant la restauration, un éventuel événement « réduit » ne doit pas masquer à nouveau la fenêtre.
            _restoring = true;
            try
            {
                if (!Visible)
                {
                    Show();
                }

                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }
            }
            finally
            {
                _restoring = false;
            }

            BringToFront();
            Activate();
            _txtText.Focus();
            _txtText.SelectAll();
        }
        catch (Exception ex)
        {
            Log.Error("Affichage de la fenêtre impossible.", ex);
        }
    }

    /// <summary>Masque la fenêtre dans la zone de notification.</summary>
    /// <param name="userInitiated">Vrai si l'utilisateur a fermé, réduit ou quitté la fenêtre (déclenche <see cref="HiddenByUser"/>).</param>
    public void HideToTray(bool userInitiated = true)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            // Si la fenêtre était réduite, l'état « réduit » est conservé ici et corrigé à l'affichage suivant.
            Hide();
        }
        catch (Exception ex)
        {
            Log.Error("Masquage de la fenêtre impossible.", ex);
            return;
        }

        if (userInitiated)
        {
            try
            {
                HiddenByUser?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("Notification du masquage impossible.", ex);
            }
        }
    }

    /// <summary>Déclenché quand l'utilisateur ferme (X, Alt+F4, Échap) ou réduit la fenêtre : elle passe dans la zone de notification.</summary>
    public event EventHandler? HiddenByUser;

    // ------------------------------------------------------------------ construction

    private void BuildUi()
    {
        SuspendLayout();

        // Mise à l'échelle : tailles exprimées pour 96 dpi, Windows les multiplie (125 %, 150 %, 200 %...).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AppTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 270);
        _formIcon = TrayApplicationContext.LoadAppIcon();
        if (_formIcon != null)
        {
            Icon = _formIcon;
        }

        var lblText = new Label
        {
            Text = "Texte à convertir en code-barres :",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 3, 3, 0),
        };

        _txtText.MaxLength = 200;
        _txtText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _txtText.TabIndex = 0;
        _txtText.AccessibleName = "Texte à convertir en code-barres";

        _btnGenerate.Text = "Générer";
        _btnGenerate.AutoSize = false;
        _btnGenerate.Size = new Size(100, 30);
        _btnGenerate.TabIndex = 1;
        _btnGenerate.UseVisualStyleBackColor = true;

        var lblPrinter = new Label
        {
            Text = "Imprimante à étiquettes :",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 12, 3, 0),
        };

        _cmbPrinter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPrinter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _cmbPrinter.TabIndex = 2;
        _cmbPrinter.AccessibleName = "Imprimante à étiquettes";

        _btnPrint.Text = "Imprimer";
        _btnPrint.AutoSize = false;
        _btnPrint.Size = new Size(100, 30);
        _btnPrint.TabIndex = 3;
        _btnPrint.UseVisualStyleBackColor = true;

        _chkStartup.Text = "Lancer au démarrage de Windows";
        _chkStartup.AutoSize = true;
        _chkStartup.Anchor = AnchorStyles.Left;
        _chkStartup.Margin = new Padding(3, 12, 3, 3);
        _chkStartup.TabIndex = 4;

        _lblStatus.AutoSize = false;
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.AutoEllipsis = true;
        _lblStatus.TextAlign = ContentAlignment.TopLeft;
        _lblStatus.Margin = new Padding(3, 12, 3, 3);
        _lblStatus.ForeColor = InfoColor;
        _lblStatus.Text = HintText;
        _lblStatus.UseMnemonic = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        table.Controls.Add(lblText, 0, 0);
        table.SetColumnSpan(lblText, 2);
        table.Controls.Add(_txtText, 0, 1);
        table.Controls.Add(_btnGenerate, 1, 1);
        table.Controls.Add(lblPrinter, 0, 2);
        table.SetColumnSpan(lblPrinter, 2);
        table.Controls.Add(_cmbPrinter, 0, 3);
        table.Controls.Add(_btnPrint, 1, 3);
        table.Controls.Add(_chkStartup, 0, 4);
        table.SetColumnSpan(_chkStartup, 2);
        table.Controls.Add(_lblStatus, 0, 5);
        table.SetColumnSpan(_lblStatus, 2);

        Controls.Add(table);

        AcceptButton = _btnGenerate;

        _toolTip.SetToolTip(_btnGenerate, "Copie le code-barres (image) dans le presse-papier : collez-le avec Ctrl+V dans Word ou Excel.");
        _toolTip.SetToolTip(_btnPrint, "Imprime l'étiquette tout de suite sur l'imprimante choisie.");

        _btnGenerate.Click += OnGenerateClick;
        _btnPrint.Click += OnPrintClick;
        _txtText.TextChanged += OnTextChanged;
        _txtText.KeyDown += OnTextKeyDown;
        _cmbPrinter.DropDown += OnPrinterDropDown;
        _cmbPrinter.SelectedIndexChanged += OnPrinterSelectionChanged;
        _chkStartup.CheckedChanged += OnStartupCheckedChanged;

        ResumeLayout(false);
        PerformLayout();
    }

    // ------------------------------------------------------------------ fermeture / masquage

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool reallyClose = AllowExit
                           || e.CloseReason == CloseReason.WindowsShutDown
                           || e.CloseReason == CloseReason.TaskManagerClosing
                           || e.CloseReason == CloseReason.ApplicationExitCall
                           || e.CloseReason == CloseReason.FormOwnerClosing;
        if (!reallyClose)
        {
            e.Cancel = true;
            HideToTray();
        }

        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible && !AllowExit && !_restoring)
        {
            // Réduire = masquer dans la zone de notification (et ne pas rester « réduit » au prochain affichage).
            HideToTray();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && !_cmbPrinter.DroppedDown)
        {
            HideToTray();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _formIcon?.Dispose();
            _formIcon = null;
        }
    }

    // ------------------------------------------------------------------ événements

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (!_printing && !_printPending)
        {
            SetStatus(HintText, StatusKind.Info);
        }
    }

    private void OnTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.A)
        {
            _txtText.SelectAll();
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }

    private void OnGenerateClick(object? sender, EventArgs e)
    {
        try
        {
            string text = _txtText.Text.Trim();
            string? error = BarcodeRenderer.ValidateText(text);
            if (error != null)
            {
                SetStatus(error, StatusKind.Error);
                _txtText.Focus();
                return;
            }

            using Bitmap bitmap = BarcodeRenderer.Render(text, _settings, out int modulePx);
            byte[] png = BarcodeRenderer.ToPng(bitmap);
            ClipboardService.SetBarcodeImage(bitmap, png);

            Log.Info("Code-barres copié dans le presse-papier (" + text.Length + " caractères, "
                     + bitmap.Width + "x" + bitmap.Height + " px, barre fine " + modulePx + " px).");
            string? warning = BarcodeRenderer.ThinBarsWarning(modulePx, _settings.ModulePx);
            if (warning != null)
            {
                Log.Warn("Génération : " + warning);
            }

            SetStatus(
                "Code-barres copié dans le presse-papier — collez-le avec Ctrl+V." + (warning != null ? " " + warning : string.Empty),
                warning != null ? StatusKind.Warning : StatusKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Génération du code-barres impossible.", ex);
            SetStatus("Échec de la génération : " + ex.Message, StatusKind.Error);
        }
    }

    private async void OnPrintClick(object? sender, EventArgs e)
    {
        if (_printing)
        {
            return;
        }

        if (_printPending)
        {
            SetStatus(
                "Un envoi précédent n'a pas encore été confirmé par Windows : vérifiez la file d'impression avant de réessayer.",
                StatusKind.Error);
            return;
        }

        Bitmap? bitmap = null;
        bool busySet = false;
        try
        {
            string text = _txtText.Text.Trim();
            string? error = BarcodeRenderer.ValidateText(text);
            if (error != null)
            {
                SetStatus(error, StatusKind.Error);
                _txtText.Focus();
                return;
            }

            string? printer = SelectedPrinter;
            if (string.IsNullOrWhiteSpace(printer))
            {
                SetStatus("Aucune imprimante sélectionnée.", StatusKind.Error);
                _cmbPrinter.Focus();
                return;
            }

            _printing = true;
            SetBusy(true);
            busySet = true;
            SetStatus("Envoi de l'étiquette à l'imprimante « " + printer + " »…", StatusKind.Info);

            bitmap = BarcodeRenderer.Render(text, _settings, out int modulePx);
            Bitmap toPrint = bitmap;
            bool tooWide = bitmap.Width > _settings.MaxWidthPx * 1.02; // même à 1 px par barre, trop large pour le ruban
            string? thinWarning = BarcodeRenderer.ThinBarsWarning(modulePx, _settings.ModulePx);
            AppSettings snapshot = _settings.Clone();

            // L'impression part sur un thread de travail : l'interface reste réactive et ne peut pas rester
            // bloquée si le spouleur ne répond plus (au bout de PrintTimeoutMs on rend la main).
            Task work = Task.Run(() => LabelPrinter.Print(toPrint, printer, snapshot));
            Task finished = await Task.WhenAny(work, Task.Delay(PrintTimeoutMs));

            if (finished == work)
            {
                await work; // relance l'exception éventuelle
                Log.Info("Étiquette envoyée à l'imprimante « " + printer + " » (" + text.Length + " caractères, barre fine " + modulePx + " px).");
                string? warning = tooWide
                    ? "Attention : texte très long, code-barres réduit pour tenir sur l'étiquette — vérifiez qu'il se lit bien."
                    : thinWarning;
                if (warning != null)
                {
                    Log.Warn("Impression : " + warning);
                }

                SetStatus(
                    "Étiquette envoyée à l'imprimante « " + printer + " »." + (warning != null ? " " + warning : string.Empty),
                    warning != null ? StatusKind.Warning : StatusKind.Success);
            }
            else
            {
                // Le travail continue en arrière-plan : l'image ne doit être libérée qu'à sa fin, et Imprimer reste
                // bloqué jusque-là (sinon chaque nouveau clic ajouterait un envoi, qui sortirait en double plus tard).
                Bitmap orphan = bitmap;
                bitmap = null;
                _printPending = true;
                _ = work.ContinueWith(t => OnLatePrintFinished(orphan, printer, t), TaskScheduler.Default);

                Log.Error("Impression : pas de confirmation de l'imprimante « " + printer + " » après " + PrintTimeoutMs / 1000 + " s.");
                SetStatus(
                    "L'imprimante « " + printer + " » n'a pas confirmé l'envoi en " + PrintTimeoutMs / 1000 + " s. "
                    + "L'étiquette peut encore sortir : vérifiez la file d'impression de Windows avant de réessayer. "
                    + "Le bouton Imprimer reste inactif tant que Windows n'a pas répondu.",
                    StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Impression impossible.", ex);
            SetStatus("Échec de l'impression : " + ex.Message, StatusKind.Error);
        }
        finally
        {
            bitmap?.Dispose();
            _printing = false;
            if (busySet)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>
    /// Fin d'un envoi qui avait dépassé le délai (thread de travail) : libère l'image, journalise, puis rend la main
    /// au thread d'interface pour réactiver Imprimer et dire ce qui s'est passé.
    /// </summary>
    private void OnLatePrintFinished(Bitmap orphan, string printer, Task finished)
    {
        Exception? error = finished.Exception?.InnerException ?? finished.Exception;
        try
        {
            orphan.Dispose();
        }
        catch
        {
            // ignoré
        }

        if (error != null)
        {
            Log.Error("Impression tardive en échec (imprimante « " + printer + " »).", error);
        }
        else
        {
            Log.Info("Impression tardive terminée (imprimante « " + printer + " »).");
        }

        try
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(() => OnLatePrintUiUpdate(printer, error)));
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
    }

    private void OnLatePrintUiUpdate(string printer, Exception? error)
    {
        _printPending = false;
        if (IsDisposed)
        {
            return;
        }

        UpdatePrintButton();
        if (_printing)
        {
            return; // un autre travail est en cours : son message prime
        }

        if (error == null)
        {
            SetStatus("L'envoi en attente est arrivé à l'imprimante « " + printer + " » : l'étiquette doit sortir.", StatusKind.Info);
        }
        else
        {
            SetStatus("L'envoi en attente a échoué : " + error.Message, StatusKind.Error);
        }
    }

    private void OnPrinterDropDown(object? sender, EventArgs e)
    {
        try
        {
            RefreshPrinters();
        }
        catch (Exception ex)
        {
            Log.Error("Actualisation des imprimantes impossible.", ex);
        }
    }

    private void OnPrinterSelectionChanged(object? sender, EventArgs e)
    {
        if (_loadingPrinters)
        {
            return;
        }

        try
        {
            string? name = SelectedPrinter;
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, _settings.PrinterName, StringComparison.Ordinal))
            {
                _settings.PrinterName = name;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mémorisation de l'imprimante impossible.", ex);
        }
    }

    private void OnStartupCheckedChanged(object? sender, EventArgs e)
    {
        if (_syncingStartup)
        {
            return;
        }

        bool wanted = _chkStartup.Checked;
        bool ok = wanted ? StartupRegistration.Enable() : StartupRegistration.Disable();
        if (!ok)
        {
            SetStatus(
                "Impossible de modifier le lancement au démarrage de Windows (accès refusé par le poste ?).",
                StatusKind.Error);
            RefreshStartupCheckbox(); // revient à l'état réel
        }
    }

    // ------------------------------------------------------------------ outils

    /// <summary>Remet la case « Lancer au démarrage » en accord avec le registre.</summary>
    internal void RefreshStartupCheckbox()
    {
        bool enabled = StartupRegistration.IsEnabled();
        if (_chkStartup.Checked == enabled)
        {
            return;
        }

        _syncingStartup = true;
        try
        {
            _chkStartup.Checked = enabled;
        }
        finally
        {
            _syncingStartup = false;
        }
    }

    /// <summary>
    /// (Ré)énumère les imprimantes ; ne touche à la liste que si elle a changé. La sélection est conservée ;
    /// à défaut, choix automatique (mémorisée, Brother QL-800, imprimante par défaut...).
    /// </summary>
    private void RefreshPrinters()
    {
        IReadOnlyList<string> printers = LabelPrinter.GetPrinters();
        string? current = SelectedPrinter;

        bool unchanged = _cmbPrinter.Items.Count == printers.Count
                         && printers.Select((p, i) => string.Equals(p, _cmbPrinter.Items[i] as string, StringComparison.Ordinal)).All(same => same);
        if (unchanged && (current != null || printers.Count == 0))
        {
            return;
        }

        _loadingPrinters = true;
        _cmbPrinter.BeginUpdate();
        try
        {
            if (!unchanged)
            {
                _cmbPrinter.Items.Clear();
                foreach (string p in printers)
                {
                    _cmbPrinter.Items.Add(p);
                }
            }

            string? target = PickMatch(printers, current)
                             ?? LabelPrinter.PickDefaultPrinter(printers, _settings.PrinterName, LabelPrinter.GetSystemDefaultPrinter());
            _cmbPrinter.SelectedItem = target;
            if (target == null)
            {
                _cmbPrinter.SelectedIndex = -1;
            }

            AdjustDropDownWidth();
        }
        finally
        {
            _cmbPrinter.EndUpdate();
            _loadingPrinters = false;
        }
    }

    private static string? PickMatch(IReadOnlyList<string> printers, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return printers.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    }

    private void AdjustDropDownWidth()
    {
        int widest = 0;
        foreach (object item in _cmbPrinter.Items)
        {
            widest = Math.Max(widest, TextRenderer.MeasureText(Convert.ToString(item) ?? string.Empty, _cmbPrinter.Font).Width);
        }

        int wanted = widest + SystemInformation.VerticalScrollBarWidth + 8;
        _cmbPrinter.DropDownWidth = Math.Max(_cmbPrinter.Width, wanted);
    }

    private void SetBusy(bool busy)
    {
        if (IsDisposed)
        {
            return;
        }

        UseWaitCursor = busy;
        _btnGenerate.Enabled = !busy;
        _cmbPrinter.Enabled = !busy;
        _chkStartup.Enabled = !busy;
        UpdatePrintButton(busy);
    }

    /// <summary>Imprimer n'est actif ni pendant un envoi, ni tant qu'un envoi trop lent n'a pas abouti.</summary>
    internal static bool PrintButtonEnabled(bool busy, bool printPending) => !busy && !printPending;

    private void UpdatePrintButton() => UpdatePrintButton(_printing);

    private void UpdatePrintButton(bool busy)
    {
        _btnPrint.Enabled = PrintButtonEnabled(busy, _printPending);
        _toolTip.SetToolTip(
            _btnPrint,
            _printPending
                ? "Un envoi précédent n'a pas encore été confirmé par Windows : vérifiez la file d'impression avant de réessayer."
                : "Imprime l'étiquette tout de suite sur l'imprimante choisie.");
    }

    private void SetStatus(string message, StatusKind kind)
    {
        if (IsDisposed || _lblStatus.IsDisposed)
        {
            return;
        }

        string shown = message.Length > MaxStatusLength ? message.Substring(0, MaxStatusLength - 1) + "…" : message;
        _lblStatus.Text = shown;
        _lblStatus.ForeColor = kind switch
        {
            StatusKind.Success => SuccessColor,
            StatusKind.Warning => WarningColor,
            StatusKind.Error => ErrorColor,
            _ => InfoColor,
        };
        _toolTip.SetToolTip(_lblStatus, message);
    }
}
