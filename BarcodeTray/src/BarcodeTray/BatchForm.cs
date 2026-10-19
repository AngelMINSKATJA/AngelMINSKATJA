using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BarcodeTray;

/// <summary>
/// Fenêtre « Étiquettes en série — plans de châssis » : on colle un tableau copié depuis Excel, le programme crée une
/// étiquette (type de châssis en haut à gauche, n° de plan en grand dessous) par croix du tableau et les imprime une à une,
/// en silence, sur la Brother QL-800 (un travail d'impression = une coupe par copie).
/// Toute la logique qui peut se tester sans imprimante (textes, validation, choix des lignes, déroulement d'une série, état des
/// boutons) est dans <see cref="BatchLogic"/> ; cette classe ne fait que l'afficher et brancher l'impression.
/// La fenêtre ne se ferme jamais vraiment (sauf <see cref="AllowExit"/> ou fin de session) : fermer ou Échap la masque.
/// </summary>
internal sealed class BatchForm : Form
{
    private const string AppTitle = "Étiquettes en série — plans de châssis";
    private const string HintText = "Collez le tableau copié depuis Excel pour commencer.";
    private const string NoTextMessage =
        "Le presse-papiers ne contient pas de texte. Dans Excel, sélectionnez le tableau AVEC sa ligne d'en-tête "
        + "(Plan SE, HB1, HBA...), copiez-le (Ctrl+C), puis recommencez.";
    private const string ClipboardBusyMessage =
        "Le presse-papiers est occupé par un autre programme : patientez un instant puis réessayez.";
    private const int MaxStatusLength = 520;
    private const int PrintTimeoutMs = BatchLogic.DefaultTimeoutMs;
    private const int PreviewDpi = 200;
    private const int ClipboardAttempts = 5;

    private const int ColNo = 0;
    private const int ColType = 1;
    private const int ColPlan = 2;
    private const int ColState = 3;

    private static readonly Color InfoColor = SystemColors.GrayText;
    private static readonly Color SuccessColor = Color.FromArgb(0, 110, 40);
    private static readonly Color WarningColor = Color.FromArgb(160, 80, 0);
    private static readonly Color ErrorColor = Color.FromArgb(192, 0, 0);

    private readonly AppSettings _settings;
    private readonly Action<AppSettings>? _persist;

    private readonly Label _lblInstructions = new();
    private readonly Button _btnPaste = new();
    private readonly Button _btnClear = new();
    private readonly NumericUpDown _numCopies = new();
    private readonly DataGridView _grid = new();
    private readonly GroupBox _grpPreview = new();
    private readonly PictureBox _preview = new();
    private readonly Label _lblPreviewNote = new();
    private readonly Label _lblSummary = new();
    private readonly ComboBox _cmbPrinter = new();
    private readonly CheckBox _chkConfirm = new();
    private readonly Button _btnPrintAll = new();
    private readonly Button _btnPrintSelection = new();
    private readonly Button _btnPrintFrom = new();
    private readonly Button _btnStop = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _lblStatus = new();
    private readonly ToolTip _toolTip = new();

    private Icon? _formIcon;
    private bool _loadingPrinters;
    private bool _bulkLoading;
    private bool _updatingGrid;
    private bool _refreshScheduled;
    private bool _printing;
    private volatile bool _stopRequested;
    private bool _latePending; // un envoi (ou une préparation du papier) qui a dépassé le délai tourne encore en arrière-plan
    private bool _lateLabelSent; // ce qui tourne encore est l'envoi d'une étiquette (faux : la préparation du papier, rien n'est parti)
    private string? _acknowledgedLengthNote;
    private LabelPrinter.FixedPaper? _preparedPaper; // dernier papier accepté par le pilote : l'aperçu montre la vraie page
    private string? _preparedPrinter;

    /// <param name="settings">Réglages de l'application (partagés avec la fenêtre principale).</param>
    /// <param name="persist">Appelée pour enregistrer les réglages modifiés ici (imprimante, confirmation) ; null : rien n'est enregistré.</param>
    public BatchForm(AppSettings settings, Action<AppSettings>? persist = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _persist = persist;
        BuildUi();
    }

    /// <summary>Vrai quand la fermeture doit être réelle (« Quitter »).</summary>
    public bool AllowExit { get; set; }

    /// <summary>Une série d'impressions est en cours.</summary>
    public bool IsPrinting => _printing;

    /// <summary>Imprimante actuellement sélectionnée dans la liste (null si aucune).</summary>
    public string? SelectedPrinter => _cmbPrinter.SelectedItem as string;

    /// <summary>Nombre d'étiquettes dans la liste.</summary>
    internal int LabelCount => _grid.RowCount;

    /// <summary>Texte du résumé sous la liste.</summary>
    internal string SummaryText => _lblSummary.Text;

    /// <summary>Texte du message d'état.</summary>
    internal string StatusText => _lblStatus.Text;

    /// <summary>Image de l'aperçu (null si rien à montrer).</summary>
    internal Image? PreviewImage => _preview.Image;

    /// <summary>Texte sous l'aperçu (numéro de ligne ou erreur de la ligne).</summary>
    internal string PreviewNoteText => _lblPreviewNote.Text;

    /// <summary>La liste des étiquettes (pour les tests).</summary>
    internal DataGridView Grid => _grid;

    /// <summary>
    /// Pour les tests : remplace les boîtes de question (confirmation, étiquettes déjà imprimées) par cette fonction
    /// (texte, boutons) -> réponse. Null : vraie boîte de dialogue.
    /// </summary>
    internal Func<string, MessageBoxButtons, DialogResult>? PromptHook { get; set; }

    /// <summary>Pourquoi l'impression est bloquée (envoi ou préparation du papier resté sans réponse).</summary>
    private string LateBlockedMessage => BatchLogic.BuildLateBlockedMessage(_lateLabelSent);

    /// <summary>État de la ligne <paramref name="row"/>.</summary>
    internal RowState GetRowState(int row)
        => row >= 0 && row < _grid.RowCount && _grid.Rows[row].Tag is RowState state ? state : RowState.Pending;

    /// <summary>États de toutes les lignes, dans l'ordre de la liste.</summary>
    private List<RowState> GetRowStates()
    {
        var states = new List<RowState>(_grid.RowCount);
        for (int i = 0; i < _grid.RowCount; i++)
        {
            states.Add(GetRowState(i));
        }

        return states;
    }

    /// <summary>
    /// Affiche la fenêtre (même réduite ou masquée), la met au premier plan et actualise la liste des imprimantes.
    /// À appeler depuis le thread d'interface.
    /// </summary>
    public void ShowAndFocus()
    {
        if (IsDisposed)
        {
            return;
        }

        if (!_printing)
        {
            try
            {
                RefreshPrinters(preferSaved: true);
                if (_preparedPaper != null)
                {
                    UpdatePreview(); // l'imprimante choisie a pu changer (fenêtre principale) : l'aperçu suit
                }

                // Réglage « Confirmer avant d'imprimer » partagé avec la fenêtre des codes-barres en série : il a pu y être changé.
                if (_chkConfirm.Checked != _settings.BatchConfirm)
                {
                    _chkConfirm.Checked = _settings.BatchConfirm;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Actualisation des imprimantes impossible.", ex);
            }
        }

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

            BringToFront();
            Activate();
            if (_grid.RowCount == 0)
            {
                _btnPaste.Focus();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Affichage de la fenêtre des étiquettes en série impossible.", ex);
        }
    }

    /// <summary>Masque la fenêtre (elle reste disponible dans le programme).</summary>
    public void HideWindow()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            Hide();
        }
        catch (Exception ex)
        {
            Log.Error("Masquage de la fenêtre des étiquettes en série impossible.", ex);
        }
    }

    // ------------------------------------------------------------------ construction

    private void BuildUi()
    {
        SuspendLayout();

        // Mise à l'échelle : tailles exprimées pour 96 dpi, Windows les multiplie (125 %, 150 %, 200 %...).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = AppTitle;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 600);
        MinimumSize = new Size(760, 560);
        _formIcon = TrayApplicationContext.LoadAppIcon();
        if (_formIcon != null)
        {
            Icon = _formIcon;
        }

        // 1) consigne
        _lblInstructions.Text =
            "Dans Excel : sélectionnez le tableau avec sa ligne d'en-tête (Plan SE, HB1, HBA…), copiez (Ctrl+C), "
            + "puis cliquez sur « Coller depuis Excel »." + Environment.NewLine
            + "Une étiquette est créée pour chaque croix (X) ; le préfixe « 2020Q7E » du n° de plan est retiré.";
        _lblInstructions.AutoSize = false;
        _lblInstructions.Dock = DockStyle.Fill;
        _lblInstructions.Height = 50;
        _lblInstructions.MinimumSize = new Size(0, 50); // hauteur réservée à la consigne (2 à 3 lignes)
        _lblInstructions.Margin = new Padding(3, 0, 3, 6);
        _lblInstructions.UseMnemonic = false;

        // 2) coller / vider / copies
        StyleButton(_btnPaste, "Coller depuis Excel", 0);
        StyleButton(_btnClear, "Vider la liste", 1);

        var lblCopies = new Label
        {
            Text = "Copies par étiquette :",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 0, 3, 0),
        };
        _numCopies.Minimum = BatchLogic.MinCopies;
        _numCopies.Maximum = BatchLogic.MaxCopies;
        _numCopies.Value = BatchLogic.MinCopies;
        _numCopies.Width = 60;
        _numCopies.Anchor = AnchorStyles.Left;
        _numCopies.TextAlign = HorizontalAlignment.Right;
        _numCopies.TabIndex = 2;
        _numCopies.AccessibleName = "Copies par étiquette";

        var copiesPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3),
        };
        copiesPanel.Controls.Add(lblCopies);
        copiesPanel.Controls.Add(_numCopies);
        lblCopies.Margin = new Padding(3, 6, 3, 0);

        var topRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0),
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topRow.Controls.Add(_btnPaste, 0, 0);
        topRow.Controls.Add(_btnClear, 1, 0);
        topRow.Controls.Add(copiesPanel, 3, 0);

        // 3) liste + aperçu
        BuildGrid();
        BuildPreview();

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(_grid, 0, 0);
        content.Controls.Add(_grpPreview, 1, 0);

        _lblSummary.AutoSize = false;
        _lblSummary.Dock = DockStyle.Fill;
        _lblSummary.Height = 22;
        _lblSummary.MinimumSize = new Size(0, 22);
        _lblSummary.AutoEllipsis = true;
        _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
        _lblSummary.Margin = new Padding(3, 6, 3, 0);
        _lblSummary.Font = new Font(Font, FontStyle.Bold);
        _lblSummary.UseMnemonic = false;

        // 5) imprimante + confirmation
        var lblPrinter = new Label
        {
            Text = "Imprimante :",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 0, 3, 0),
        };
        _cmbPrinter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPrinter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _cmbPrinter.TabIndex = 4;
        _cmbPrinter.AccessibleName = "Imprimante à étiquettes";
        _chkConfirm.Text = "Confirmer avant d'imprimer";
        _chkConfirm.AutoSize = true;
        _chkConfirm.Anchor = AnchorStyles.Left;
        _chkConfirm.Checked = _settings.BatchConfirm;
        _chkConfirm.TabIndex = 5;

        var printerRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 0),
        };
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        printerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        printerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        printerRow.Controls.Add(lblPrinter, 0, 0);
        printerRow.Controls.Add(_cmbPrinter, 1, 0);
        printerRow.Controls.Add(_chkConfirm, 2, 0);

        // 6) boutons d'impression
        StyleButton(_btnPrintAll, "Imprimer tout", 6);
        StyleButton(_btnPrintSelection, "Imprimer la sélection", 7);
        StyleButton(_btnPrintFrom, "Imprimer à partir de la sélection", 8);
        StyleButton(_btnStop, "Arrêter", 9);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        actions.Controls.Add(_btnPrintAll);
        actions.Controls.Add(_btnPrintSelection);
        actions.Controls.Add(_btnPrintFrom);
        actions.Controls.Add(_btnStop);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 16;
        _progress.MinimumSize = new Size(0, 16);
        _progress.Minimum = 0;
        _progress.Maximum = 1;
        _progress.Value = 0;
        _progress.Margin = new Padding(3, 8, 3, 0);

        _lblStatus.AutoSize = false;
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.Height = 52;
        _lblStatus.MinimumSize = new Size(0, 52); // trois lignes de message
        _lblStatus.AutoEllipsis = true;
        _lblStatus.TextAlign = ContentAlignment.TopLeft;
        _lblStatus.Margin = new Padding(3, 6, 3, 0);
        _lblStatus.ForeColor = InfoColor;
        _lblStatus.Text = HintText;
        _lblStatus.UseMnemonic = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 8; i++)
        {
            root.RowStyles.Add(i == 2 ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));
        }

        root.Controls.Add(_lblInstructions, 0, 0);
        root.Controls.Add(topRow, 0, 1);
        root.Controls.Add(content, 0, 2);
        root.Controls.Add(_lblSummary, 0, 3);
        root.Controls.Add(printerRow, 0, 4);
        root.Controls.Add(actions, 0, 5);
        root.Controls.Add(_progress, 0, 6);
        root.Controls.Add(_lblStatus, 0, 7);
        Controls.Add(root);

        _toolTip.SetToolTip(_btnPaste, "Colle le tableau copié dans Excel (Ctrl+V) : une étiquette par croix. Remplace la liste actuelle.");
        _toolTip.SetToolTip(_btnClear, "Efface toute la liste.");
        _toolTip.SetToolTip(_numCopies, "Nombre de copies imprimées pour chaque étiquette (1 à 99).");
        _toolTip.SetToolTip(_btnPrintAll, "Imprime toutes les étiquettes de la liste, l'une après l'autre.");
        _toolTip.SetToolTip(_btnPrintSelection, "Imprime seulement les lignes sélectionnées.");
        _toolTip.SetToolTip(_btnPrintFrom, "Imprime de la première ligne sélectionnée jusqu'à la fin de la liste (pour reprendre après un arrêt ou une erreur).");
        _toolTip.SetToolTip(_btnStop, "Arrête la série avant l'envoi de l'étiquette suivante.");
        _toolTip.SetToolTip(_grid, "Double-clic ou F2 pour corriger le type ou le n° de plan ; Suppr pour supprimer les lignes sélectionnées.");

        _btnPaste.Click += (_, _) => PasteFromClipboard();
        _btnClear.Click += OnClearClick;
        _numCopies.ValueChanged += (_, _) => UpdateSummary();
        _cmbPrinter.DropDown += OnPrinterDropDown;
        _cmbPrinter.SelectedIndexChanged += OnPrinterSelectionChanged;
        _chkConfirm.CheckedChanged += OnConfirmCheckedChanged;
        _btnPrintAll.Click += (_, _) => StartPrint(PrintMode.All);
        _btnPrintSelection.Click += (_, _) => StartPrint(PrintMode.Selection);
        _btnPrintFrom.Click += (_, _) => StartPrint(PrintMode.FromSelection);
        _btnStop.Click += OnStopClick;

        ResumeLayout(false);
        PerformLayout();

        UpdateSummary();
        UpdatePreview();
        UpdateButtons();
    }

    private static void StyleButton(Button button, string text, int tabIndex)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(100, 30);
        button.Padding = new Padding(6, 0, 6, 0);
        button.Margin = new Padding(3);
        button.TabIndex = tabIndex;
        button.UseVisualStyleBackColor = true;
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = true;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.StandardTab = true;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.TabIndex = 3;
        _grid.AccessibleName = "Liste des étiquettes";

        var colNo = new DataGridViewTextBoxColumn
        {
            Name = "colNo",
            HeaderText = "N°",
            ReadOnly = true,
            Width = 50,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        colNo.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        colNo.DefaultCellStyle.ForeColor = SystemColors.GrayText;

        var colType = new DataGridViewTextBoxColumn
        {
            Name = "colType",
            HeaderText = "Type",
            Width = 110,
            MaxInputLength = PlanLabelRenderer.MaxFieldLength,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        var colPlan = new DataGridViewTextBoxColumn
        {
            Name = "colPlan",
            HeaderText = "N° de plan",
            Width = 130,
            MaxInputLength = PlanLabelRenderer.MaxFieldLength,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        var colState = new DataGridViewTextBoxColumn
        {
            Name = "colState",
            HeaderText = "État",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 110,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        _grid.Columns.AddRange(colNo, colType, colPlan, colState);

        _grid.CellFormatting += OnGridCellFormatting;
        _grid.CellBeginEdit += OnGridCellBeginEdit;
        _grid.CellValueChanged += OnGridCellValueChanged;
        _grid.SelectionChanged += OnGridSelectionChanged;
        _grid.RowsAdded += (_, _) => ScheduleRefresh();
        _grid.RowsRemoved += (_, _) => ScheduleRefresh();
        _grid.UserDeletingRow += OnGridUserDeletingRow;
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            e.Cancel = false;
        };
    }

    private void BuildPreview()
    {
        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.Gainsboro;
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.TabStop = false;
        _preview.AccessibleName = "Aperçu de l'étiquette";

        _lblPreviewNote.AutoSize = false;
        _lblPreviewNote.Dock = DockStyle.Fill;
        _lblPreviewNote.Height = 54;
        _lblPreviewNote.MinimumSize = new Size(0, 54);
        _lblPreviewNote.AutoEllipsis = true;
        _lblPreviewNote.ForeColor = InfoColor;
        _lblPreviewNote.Margin = new Padding(3, 6, 3, 0);
        _lblPreviewNote.UseMnemonic = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_preview, 0, 0);
        layout.Controls.Add(_lblPreviewNote, 0, 1);

        _grpPreview.Dock = DockStyle.Fill;
        _grpPreview.Padding = new Padding(8, 4, 8, 8);
        _grpPreview.Margin = new Padding(8, 0, 0, 0);
        _grpPreview.Text = PreviewCaption();
        _grpPreview.Controls.Add(layout);
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
            if (_printing)
            {
                SetStatus(
                    "Impression en cours : cliquez sur « Arrêter » (l'envoi en cours se termine), puis fermez la fenêtre.",
                    BatchTone.Warning);
            }
            else
            {
                HideWindow();
            }
        }
        else if (_printing)
        {
            _stopRequested = true; // la série ne démarre aucun nouvel envoi
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool editing = _grid.IsCurrentCellInEditMode;

        if (keyData == Keys.Escape && !_printing && !editing && !_cmbPrinter.DroppedDown)
        {
            HideWindow();
            return true;
        }

        if (keyData == (Keys.Control | Keys.V) && !editing && ActiveControl is not UpDownBase)
        {
            if (!_printing)
            {
                PasteFromClipboard();
            }

            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopRequested = true;
            PrintGate.Release(this);
            _toolTip.Dispose();
            Image? image = _preview.Image;
            _preview.Image = null;
            image?.Dispose();
            _lblSummary.Font?.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _formIcon?.Dispose();
            _formIcon = null;
        }
    }

    // ------------------------------------------------------------------ coller / vider

    /// <summary>Colle le tableau du presse-papiers (remplace la liste, sans rien demander).</summary>
    private void PasteFromClipboard()
    {
        if (_printing || IsDisposed)
        {
            return;
        }

        try
        {
            string? text = ReadClipboardText(out string? problem);
            if (problem != null)
            {
                SetStatus(problem, BatchTone.Error);
                return;
            }

            if (text == null)
            {
                SetStatus(NoTextMessage, BatchTone.Error);
                return;
            }

            LoadTable(text);
        }
        catch (Exception ex)
        {
            Log.Error("Collage du tableau impossible.", ex);
            SetStatus("Échec du collage : " + ex.Message, BatchTone.Error);
        }
    }

    /// <summary>
    /// Analyse <paramref name="text"/> (tableau copié depuis Excel) et, s'il contient des étiquettes, remplace la liste.
    /// En cas d'erreur la liste actuelle est conservée et le message est affiché en rouge.
    /// </summary>
    internal PlanTableParseResult LoadTable(string? text)
    {
        PlanTableParseResult result = PlanTableParser.Parse(text);
        // Tant qu'un envoi (ou une préparation du papier) attend Windows, « Imprimer tout » est grisé : le message le dit ici,
        // une info-bulle sur un bouton inactif ne s'afficherait pas.
        (string message, BatchTone tone) = BatchLogic.BuildPasteStatus(result, _latePending ? LateBlockedMessage : null);
        if (result.Error != null || result.Items.Count == 0)
        {
            SetStatus(message, tone);
            return result;
        }

        LoadItems(result.Items);
        Log.Info("Tableau collé : " + result.Items.Count + " étiquette(s), " + result.Types.Count + " colonne(s) de type, "
                 + result.Warnings.Count + " remarque(s).");
        SetStatus(message, tone);
        return result;
    }

    private static string? ReadClipboardText(out string? problem)
    {
        problem = null;
        for (int attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText) && !Clipboard.ContainsText())
                {
                    return null;
                }

                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (string.IsNullOrEmpty(text))
                {
                    text = Clipboard.GetText();
                }

                return text;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60); // presse-papiers verrouillé un instant par un autre programme
            }
        }

        problem = ClipboardBusyMessage;
        return null;
    }

    private void LoadItems(IReadOnlyList<LabelItem> items)
    {
        _bulkLoading = true;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            var rows = new DataGridViewRow[items.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var row = new DataGridViewRow();
                row.CreateCells(_grid, string.Empty, items[i].Type, items[i].Plan, string.Empty);
                rows[i] = row;
            }

            _grid.Rows.AddRange(rows);
        }
        finally
        {
            _grid.ResumeLayout();
            _bulkLoading = false;
        }

        _acknowledgedLengthNote = null;
        _progress.Maximum = 1;
        _progress.Value = 0;
        OnListChanged();
    }

    private void OnClearClick(object? sender, EventArgs e)
    {
        if (_printing)
        {
            return;
        }

        try
        {
            _grid.EndEdit();
            _bulkLoading = true;
            try
            {
                _grid.Rows.Clear();
            }
            finally
            {
                _bulkLoading = false;
            }

            _progress.Maximum = 1;
            _progress.Value = 0;
            OnListChanged();
            SetStatus(HintText, BatchTone.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Vidage de la liste impossible.", ex);
            SetStatus("Impossible de vider la liste : " + ex.Message, BatchTone.Error);
        }
    }

    // ------------------------------------------------------------------ liste

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex == ColNo && e.RowIndex >= 0)
        {
            e.Value = (e.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
            e.FormattingApplied = true;
        }
    }

    // Ceinture et bretelles : « N° » et « État » ne se modifient jamais (l'état réel d'une ligne est dans Row.Tag, un texte tapé
    // dans la cellule le contredirait), et rien ne se modifie pendant une série.
    private void OnGridCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_printing || e.ColumnIndex == ColNo || e.ColumnIndex == ColState)
        {
            e.Cancel = true;
        }
    }

    private void OnGridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_bulkLoading || _updatingGrid || e.RowIndex < 0 || (e.ColumnIndex != ColType && e.ColumnIndex != ColPlan))
        {
            return;
        }

        // Une étiquette corrigée n'est plus « imprimée » : elle repart à zéro.
        if (GetRowState(e.RowIndex) != RowState.Pending)
        {
            SetRowState(e.RowIndex, RowState.Pending);
        }

        UpdateSummary();
        UpdatePreview();
    }

    private void OnGridSelectionChanged(object? sender, EventArgs e)
    {
        ScheduleRefresh();
    }

    private void OnGridUserDeletingRow(object? sender, DataGridViewRowCancelEventArgs e)
    {
        if (_printing)
        {
            e.Cancel = true;
        }
    }

    /// <summary>Remet à jour résumé, aperçu et boutons.</summary>
    private void OnListChanged()
    {
        if (_bulkLoading || IsDisposed)
        {
            return;
        }

        UpdateSummary();
        UpdatePreview();
        UpdateButtons();
    }

    /// <summary>
    /// Même chose, mais une seule fois par tour de boucle de messages : supprimer 500 lignes d'un coup déclenche 500
    /// événements, il serait absurde de redessiner l'aperçu 500 fois.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_bulkLoading || IsDisposed)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            OnListChanged();
            return;
        }

        if (_refreshScheduled)
        {
            return;
        }

        _refreshScheduled = true;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _refreshScheduled = false;
                OnListChanged();
            }));
        }
        catch (InvalidOperationException)
        {
            _refreshScheduled = false; // poignée détruite : fermeture en cours
        }
    }

    /// <summary>Les étiquettes de la liste, avec les valeurs saisies (espaces aux extrémités retirés).</summary>
    internal IReadOnlyList<LabelItem> GetItems()
    {
        var items = new List<LabelItem>(_grid.RowCount);
        for (int i = 0; i < _grid.RowCount; i++)
        {
            DataGridViewRow row = _grid.Rows[i];
            items.Add(new LabelItem(CellText(row, ColType), CellText(row, ColPlan)));
        }

        return items;
    }

    private static string CellText(DataGridViewRow row, int column)
        => Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    private List<int> SelectedRowIndexes()
    {
        var indexes = new List<int>(_grid.SelectedRows.Count);
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            indexes.Add(row.Index);
        }

        indexes.Sort();
        return indexes;
    }

    internal void SetRowState(int row, RowState state, int copiesDone = 0, int copies = 1)
    {
        if (row < 0 || row >= _grid.RowCount)
        {
            return;
        }

        _updatingGrid = true;
        try
        {
            DataGridViewRow gridRow = _grid.Rows[row];
            gridRow.Tag = state;
            DataGridViewCell cell = gridRow.Cells[ColState];
            cell.Value = BatchLogic.StateText(state, copiesDone, copies);
            cell.Style.ForeColor = state switch
            {
                RowState.Done => SuccessColor,
                RowState.Failed => ErrorColor,
                RowState.Uncertain => WarningColor,
                RowState.Partial => WarningColor,
                _ => Color.Empty,
            };
        }
        finally
        {
            _updatingGrid = false;
        }
    }

    private void SelectRow(int row, int column)
    {
        if (row < 0 || row >= _grid.RowCount)
        {
            return;
        }

        try
        {
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[row].Cells[column];
            _grid.Rows[row].Selected = true;
            EnsureRowVisible(row);
        }
        catch (Exception ex)
        {
            Log.Warn("Sélection de la ligne impossible : " + ex.Message);
        }
    }

    private void EnsureRowVisible(int row)
    {
        try
        {
            if (row < 0 || row >= _grid.RowCount)
            {
                return;
            }

            int first = _grid.FirstDisplayedScrollingRowIndex;
            int visible = _grid.DisplayedRowCount(false);
            if (first < 0 || row < first || row >= first + Math.Max(1, visible) - 1)
            {
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - Math.Max(1, visible) / 2);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Défilement de la liste impossible : " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ résumé, aperçu, boutons

    private void UpdateSummary()
    {
        if (IsDisposed)
        {
            return;
        }

        string text = BatchLogic.BuildSummary(GetItems(), (int)_numCopies.Value);
        _lblSummary.Text = text;
        _toolTip.SetToolTip(_lblSummary, text);
    }

    /// <summary>
    /// Papier à montrer dans l'aperçu : celui que le pilote a réellement accepté pour l'imprimante choisie (connu après une
    /// première impression : la longueur peut différer de BatchLabelLengthMm, p. ex. avec PaperName), sinon null (réglages).
    /// </summary>
    private LabelPrinter.FixedPaper? KnownPaper()
        => _preparedPaper != null && string.Equals(_preparedPrinter, SelectedPrinter, StringComparison.Ordinal) ? _preparedPaper : null;

    private string PreviewCaption()
    {
        LabelPrinter.FixedPaper? paper = KnownPaper();
        int widthMm = paper != null ? (int)Math.Round(LabelPrinter.UnitsToMm(paper.PageWidthUnits)) : _settings.LabelWidthMm;
        int lengthMm = paper != null ? (int)Math.Round(LabelPrinter.UnitsToMm(paper.PageHeightUnits)) : _settings.BatchLabelLengthMm;
        return "Aperçu (" + widthMm.ToString(CultureInfo.InvariantCulture) + " x " + lengthMm.ToString(CultureInfo.InvariantCulture) + " mm)";
    }

    /// <summary>Ligne montrée dans l'aperçu : la première ligne sélectionnée, à défaut la première de la liste.</summary>
    private int PreviewRow()
    {
        if (_grid.RowCount == 0)
        {
            return -1;
        }

        List<int> selected = SelectedRowIndexes();
        return selected.Count > 0 ? selected[0] : 0;
    }

    private void UpdatePreview()
    {
        if (IsDisposed)
        {
            return;
        }

        _grpPreview.Text = PreviewCaption();
        int row = PreviewRow();
        if (row < 0)
        {
            SetPreviewImage(null);
            SetPreviewNote("Collez un tableau pour voir l'étiquette.", InfoColor);
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            AppSettings s = _settings.Clone();
            s.Validate();
            s.Dpi = PreviewDpi;
            DataGridViewRow gridRow = _grid.Rows[row];
            LabelPrinter.FixedPaper? paper = KnownPaper();
            int pageWidth = paper?.PageWidthUnits ?? LabelPrinter.MmToUnits(s.LabelWidthMm);
            int pageHeight = paper?.PageHeightUnits ?? LabelPrinter.MmToUnits(s.BatchLabelLengthMm);
            RectangleF area = paper != null && LabelPrinter.IsUsableArea(paper.PrintableArea)
                ? paper.PrintableArea
                : BatchLogic.PreviewPrintableArea(pageWidth, pageHeight);

            bitmap = PlanLabelRenderer.Render(CellText(gridRow, ColType), CellText(gridRow, ColPlan), s, pageWidth, pageHeight, area);
            DrawPrintableFrame(bitmap, area, PreviewDpi);
            SetPreviewImage(bitmap);
            bitmap = null;
            SetPreviewNote("Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture)
                           + ". Les pointillés marquent la zone imprimable de la QL-800.", InfoColor);
        }
        catch (ArgumentException ex)
        {
            // Champ vide, trop long, caractère non imprimable : le message est déjà en français.
            SetPreviewImage(null);
            SetPreviewNote("Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " : " + ex.Message, ErrorColor);
        }
        catch (Exception ex)
        {
            Log.Error("Aperçu de l'étiquette impossible.", ex);
            SetPreviewImage(null);
            SetPreviewNote("Aperçu indisponible : " + ex.Message, ErrorColor);
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private static void DrawPrintableFrame(Bitmap bitmap, RectangleF areaUnits, int dpi)
    {
        float k = dpi / 100f;
        using Graphics g = Graphics.FromImage(bitmap);
        using var pen = new Pen(Color.FromArgb(120, 120, 120), Math.Max(1f, k * 0.5f)) { DashStyle = DashStyle.Dot };
        g.DrawRectangle(pen, areaUnits.X * k, areaUnits.Y * k, areaUnits.Width * k, areaUnits.Height * k);
    }

    private void SetPreviewImage(Image? image)
    {
        Image? old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    private void SetPreviewNote(string text, Color color)
    {
        _lblPreviewNote.Text = text;
        _lblPreviewNote.ForeColor = color;
        _toolTip.SetToolTip(_lblPreviewNote, text);
    }

    /// <summary>Active ou désactive les commandes selon l'état (voir <see cref="BatchLogic.ComputeButtons"/>).</summary>
    private void UpdateButtons()
    {
        if (IsDisposed)
        {
            return;
        }

        BatchButtonStates s = BatchLogic.ComputeButtons(_grid.RowCount, _grid.SelectedRows.Count, _printing, _stopRequested, _latePending);
        _btnPaste.Enabled = s.Paste;
        _btnClear.Enabled = s.Clear;
        _numCopies.Enabled = s.Copies;
        _cmbPrinter.Enabled = s.Printer;
        _chkConfirm.Enabled = s.Confirm;
        _btnPrintAll.Enabled = s.PrintAll;
        _btnPrintSelection.Enabled = s.PrintSelection;
        _btnPrintFrom.Enabled = s.PrintFromSelection;
        _btnStop.Enabled = s.Stop;

        // Surtout PAS _grid.ReadOnly : passer le tableau en lecture seule remet à « modifiable » les colonnes N° et État (leur
        // réglage propre est effacé) et rien ne le rétablit au retour à false. On ne touche donc qu'aux colonnes Type et N° de
        // plan ; N° et État restent en lecture seule en permanence (voir BuildGrid).
        bool editable = s.GridEditable;
        foreach (int column in new[] { ColType, ColPlan })
        {
            if (_grid.Columns[column].ReadOnly == editable)
            {
                _grid.Columns[column].ReadOnly = !editable;
            }
        }

        _grid.AllowUserToDeleteRows = editable;

        _toolTip.SetToolTip(_btnPrintAll, _latePending ? LateBlockedMessage : "Imprime toutes les étiquettes de la liste, l'une après l'autre.");
    }

    // ------------------------------------------------------------------ impression

    private void OnStopClick(object? sender, EventArgs e)
    {
        if (!_printing)
        {
            return;
        }

        _stopRequested = true;
        UpdateButtons();
        SetStatus("Arrêt demandé : la série s'arrêtera avant l'étiquette suivante (l'envoi en cours se termine).", BatchTone.Warning);
    }

    private async void StartPrint(PrintMode mode)
    {
        try
        {
            await PrintAsync(mode);
        }
        catch (Exception ex)
        {
            Log.Error("Série : erreur inattendue.", ex);
            SetStatus("Échec de l'impression : " + ex.Message, BatchTone.Error);
            EndBusy();
        }
    }

    /// <summary>Valide puis imprime les lignes choisies (appelée par les boutons ; interne pour les tests).</summary>
    internal async Task PrintAsync(PrintMode mode)
    {
        if (_printing || IsDisposed)
        {
            return;
        }

        if (_latePending)
        {
            SetStatus(LateBlockedMessage, BatchTone.Error);
            return;
        }

        if (_grid.IsCurrentCellInEditMode && !_grid.EndEdit())
        {
            SetStatus("Terminez d'abord la modification de la cellule (Entrée ou Échap).", BatchTone.Warning);
            return;
        }

        IReadOnlyList<LabelItem> items = GetItems();
        IReadOnlyList<int> rows = BatchLogic.ChooseRows(mode, items.Count, SelectedRowIndexes());
        if (rows.Count == 0)
        {
            SetStatus(BatchLogic.NothingToPrintMessage(mode), BatchTone.Warning);
            return;
        }

        RowProblem? problem = BatchLogic.FindInvalidRow(items, rows);
        if (problem != null)
        {
            SelectRow(problem.Row, problem.Field == LabelField.Type ? ColType : ColPlan);
            SetStatus(problem.Message, BatchTone.Error);
            return;
        }

        string? printer = SelectedPrinter;
        if (string.IsNullOrWhiteSpace(printer))
        {
            SetStatus("Aucune imprimante sélectionnée.", BatchTone.Error);
            _cmbPrinter.Focus();
            return;
        }

        int copies = BatchLogic.ClampCopies((int)_numCopies.Value);

        // Des lignes déjà marquées « ✔ imprimée » dans la série à lancer (typiquement « Imprimer tout » après un arrêt, un échec ou
        // la pause de la première étiquette) : on ne les réimprime jamais sans le dire, même si la confirmation est décochée.
        IReadOnlyList<RowState> states = GetRowStates();
        int alreadyDone = BatchLogic.CountDone(states, rows);
        if (alreadyDone > 0)
        {
            bool allDone = alreadyDone >= rows.Count;
            DialogResult answer = Ask(
                BatchLogic.BuildReprintText(rows.Count, alreadyDone, copies, printer),
                allDone ? MessageBoxButtons.OKCancel : MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2); // réponse par défaut : « Non » (seulement les pas encore imprimées) ou « Annuler »
            if (!allDone && answer == DialogResult.No)
            {
                rows = BatchLogic.ExcludeDone(states, rows);
            }
            else if (answer != (allDone ? DialogResult.OK : DialogResult.Yes))
            {
                SetStatus("Impression annulée.", BatchTone.Info);
                return;
            }
        }
        else if (_chkConfirm.Checked && rows.Count * copies > 1)
        {
            DialogResult answer = Ask(
                BatchLogic.BuildConfirmText(rows.Count, copies, printer),
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.OK)
            {
                SetStatus("Impression annulée.", BatchTone.Info);
                return;
            }
        }

        // Une seule fenêtre à la fois envoie des travaux à cette imprimante (sinon les étiquettes de deux séries se mélangent sur le rouleau).
        if (!PrintGate.TryAcquire(printer, this, AppTitle, out string? busyWindow))
        {
            SetStatus(PrintGate.BusyMessage(printer, busyWindow), BatchTone.Warning);
            return;
        }

        int sheets = rows.Count * copies;
        _printing = true;
        _stopRequested = false;
        _progress.Maximum = Math.Max(1, sheets);
        _progress.Value = 0;
        UpdateButtons();

        AppSettings snapshot = _settings.Clone();
        snapshot.Validate();
        int lengthMm = snapshot.BatchLabelLengthMm;
        Log.Info("Série : " + rows.Count + " étiquette(s), " + copies + " copie(s) chacune, imprimante « " + printer
                 + " », longueur " + lengthMm + " mm.");

        // 1) Le papier est préparé UNE fois (sans imprimer) : format du pilote + longueur demandée, zone imprimable vérifiée.
        SetStatus("Préparation du papier sur « " + printer + " »…", BatchTone.Info);
        Task<LabelPrinter.FixedPaper> prepare = Task.Run(() => LabelPrinter.PrepareFixedPaper(printer, snapshot, lengthMm));
        Task prepared = await Task.WhenAny(prepare, Task.Delay(PrintTimeoutMs));
        if (!ReferenceEquals(prepared, prepare))
        {
            Log.Error("Série : le pilote n'a pas répondu à la préparation du papier en " + PrintTimeoutMs / 1000 + " s.");
            TrackOrphan(prepare, printer, labelSent: false);
            EndBusy();
            SetStatus(BatchLogic.BuildPrepareTimeoutMessage(printer, PrintTimeoutMs), BatchTone.Error);
            return;
        }

        LabelPrinter.FixedPaper paper;
        try
        {
            paper = await prepare;
        }
        catch (Exception ex)
        {
            Log.Error("Série : préparation du papier impossible.", ex);
            EndBusy();
            SetStatus("Échec de l'impression : " + ex.Message, BatchTone.Error);
            return;
        }

        _preparedPaper = paper;
        _preparedPrinter = printer;
        UpdatePreview(); // l'aperçu montre désormais la vraie page du pilote (sa longueur peut différer de BatchLabelLengthMm)

        // La page réelle du pilote peut être plus courte que prévu (PaperName d'une étiquette prédécoupée...) : le texte, placé à
        // hauteur fixe, serait rogné en silence sur toutes les étiquettes. On refuse la série avant d'en gâcher une seule.
        string? fitProblem = CheckFirstLabelFits(items[rows[0]], snapshot, paper);
        if (fitProblem != null)
        {
            Log.Error("Série refusée : le texte dépasse la page réelle du pilote (page " + paper.PageHeightUnits + " /100 pouce, zone imprimable "
                      + paper.PrintableArea.ToString() + ").");
            EndBusy();
            SetStatus(fitProblem, BatchTone.Error);
            return;
        }

        // Si le pilote impose sa propre longueur, les étiquettes sortiraient trop longues : on s'arrête après la première
        // pour que l'utilisateur la regarde avant de gâcher le rouleau (une seule fois par réglage de papier).
        string? lengthNote = paper.LengthHonored ? null : paper.Description;
        int pauseAfterRows = 0;
        if (lengthNote != null && rows.Count > 1 && !string.Equals(_acknowledgedLengthNote, lengthNote, StringComparison.Ordinal))
        {
            pauseAfterRows = 1;
        }

        // 2) Les étiquettes partent une par une, chacune sur un thread de travail, avec un délai par envoi.
        LabelPrinter.FixedPaper fixedPaper = paper;
        Task PrintSheet(int row, int copy)
        {
            LabelItem item = items[row];
            return Task.Run(() => PrintOne(item, printer, snapshot, fixedPaper));
        }

        // Les lignes de CETTE série repartent à zéro (les autres, déjà imprimées, gardent leur « ✔ »). Pas avant : si la préparation du
        // papier échoue ou si le texte ne tient pas, rien n'est parti et les marques existantes doivent rester.
        foreach (int r in rows)
        {
            SetRowState(r, RowState.Pending);
        }

        BatchOutcome outcome = await BatchLogic.Run(
            rows,
            copies,
            PrintSheet,
            () => _stopRequested || IsDisposed,
            e => ApplyBatchEvent(e, items),
            PrintTimeoutMs,
            pauseAfterRows);

        if (outcome.Orphan != null)
        {
            TrackOrphan(outcome.Orphan, printer, labelSent: true);
        }

        if (outcome.End == BatchEnd.Paused)
        {
            _acknowledgedLengthNote = lengthNote;
        }

        LogOutcome(outcome, printer);
        EndBusy();

        string message = BatchLogic.BuildOutcomeMessage(outcome, items, copies, printer, PrintTimeoutMs, lengthNote, out BatchTone tone);
        if (outcome.End == BatchEnd.Completed && lengthNote != null)
        {
            message += " Attention : " + lengthNote;
            tone = BatchTone.Warning;
        }

        if (outcome.ResumeRow.HasValue)
        {
            SelectRow(outcome.ResumeRow.Value, ColPlan);
        }

        SetStatus(message, tone);
    }

    /// <summary>
    /// Dessine la première étiquette de la série (sans rien envoyer) et contrôle que son texte tient dans la page réelle du
    /// pilote (voir <see cref="BatchLogic.CheckTextFitsPageHeight"/>). Null : tout tient (ou contrôle impossible : l'impression
    /// elle-même signalera un vrai problème de dessin).
    /// </summary>
    private static string? CheckFirstLabelFits(LabelItem item, AppSettings settings, LabelPrinter.FixedPaper paper)
    {
        try
        {
            using Bitmap page = PlanLabelRenderer.Render(
                item.Type, item.Plan, settings, paper.PageWidthUnits, paper.PageHeightUnits, paper.PrintableArea, out PlanLabelLayout layout);
            return BatchLogic.CheckTextFitsPageHeight(layout.TypeInkPx, layout.PlanInkPx, layout.Dpi, paper.PageHeightUnits, paper.PrintableArea);
        }
        catch (Exception ex)
        {
            Log.Warn("Série : contrôle de la hauteur du texte impossible : " + ex.Message);
            return null;
        }
    }

    /// <summary>Pose une question à l'utilisateur (ou appelle <see cref="PromptHook"/> dans les tests).</summary>
    private DialogResult Ask(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        => PromptHook != null
            ? PromptHook(text, buttons)
            : MessageBox.Show(this, text, AppTitle, buttons, icon, defaultButton);

    /// <summary>Dessine et envoie UNE copie (thread de travail : aucun accès à l'interface).</summary>
    private static void PrintOne(LabelItem item, string printer, AppSettings settings, LabelPrinter.FixedPaper paper)
    {
        using Bitmap page = PlanLabelRenderer.Render(
            item.Type, item.Plan, settings, paper.PageWidthUnits, paper.PageHeightUnits, paper.PrintableArea);
        LabelPrinter.PrintFullPage(page, printer, settings, paper);
    }

    private void ApplyBatchEvent(BatchEvent e, IReadOnlyList<LabelItem> items)
    {
        if (IsDisposed)
        {
            return;
        }

        SetRowState(e.Row, e.State, e.CopiesDone, e.Copies);
        _progress.Maximum = Math.Max(1, e.TotalSheets);
        _progress.Value = Math.Clamp(e.SheetsDone, 0, _progress.Maximum);
        if (e.State == RowState.Printing && !_stopRequested)
        {
            LabelItem item = items[e.Row];
            SetStatus(BatchLogic.BuildProgressText(e.Position, e.TotalRows, e.Copy, e.Copies, item.Type, item.Plan), BatchTone.Info);
            EnsureRowVisible(e.Row);
        }
    }

    // Le journal ne contient que des comptes : jamais les types ni les numéros de plan.
    private static void LogOutcome(BatchOutcome outcome, string printer)
    {
        string counts = outcome.RowsDone + " / " + outcome.TotalRows + " étiquette(s), " + outcome.SheetsDone + " / "
                        + outcome.TotalSheets + " impression(s)";
        switch (outcome.End)
        {
            case BatchEnd.Completed:
                Log.Info("Série terminée sur « " + printer + " » : " + counts + ".");
                break;
            case BatchEnd.Failed:
                Log.Error("Série interrompue sur « " + printer + " » à la ligne " + ((outcome.ResumeRow ?? -1) + 1) + " : " + counts
                          + ". " + outcome.Error);
                break;
            case BatchEnd.TimedOut:
                Log.Error("Série interrompue sur « " + printer + " » : pas de confirmation de Windows à la ligne "
                          + ((outcome.ResumeRow ?? -1) + 1) + " après " + PrintTimeoutMs / 1000 + " s (" + counts + ").");
                break;
            default:
                Log.Info("Série " + (outcome.End == BatchEnd.Paused ? "en pause" : "arrêtée") + " sur « " + printer + " » : " + counts + ".");
                break;
        }
    }

    /// <summary>Fin de la série : l'interface redevient disponible (les envois trop lents bloquent encore l'impression).</summary>
    private void EndBusy()
    {
        _printing = false;
        ReleasePrintGate();
        UpdateButtons();
    }

    /// <summary>Rend le verrou d'impression commun aux fenêtres, sauf si un envoi sans réponse tourne encore (il le garde jusqu'à sa fin).</summary>
    private void ReleasePrintGate()
    {
        if (!_printing && !_latePending)
        {
            PrintGate.Release(this);
        }
    }

    /// <summary>
    /// Un envoi (ou la préparation du papier) n'a pas répondu à temps et tourne encore en arrière-plan : toute nouvelle
    /// impression est bloquée jusqu'à sa fin (sinon chaque clic ajouterait un envoi qui sortirait en double plus tard).
    /// </summary>
    /// <param name="orphan">La tâche restée sans réponse.</param>
    /// <param name="printer">Imprimante concernée.</param>
    /// <param name="labelSent">Vrai si c'est l'envoi d'une étiquette ; faux si c'est la préparation du papier (rien n'est parti :
    /// le message de fin ne doit pas promettre une étiquette qui ne sortira jamais).</param>
    private void TrackOrphan(Task orphan, string printer, bool labelSent)
    {
        _latePending = true;
        _lateLabelSent = labelSent;
        UpdateButtons();
        _ = orphan.ContinueWith(t => OnLateFinished(printer, t, labelSent), TaskScheduler.Default);
    }

    private void OnLateFinished(string printer, Task finished, bool labelSent)
    {
        Exception? error = finished.Exception?.InnerException ?? finished.Exception;
        string what = labelSent ? "envoi tardif" : "préparation tardive du papier";
        if (error != null)
        {
            Log.Error("Série : " + what + " en échec (imprimante « " + printer + " »).", error);
        }
        else
        {
            Log.Info("Série : " + what + " terminé" + (labelSent ? string.Empty : "e") + " (imprimante « " + printer + " »).");
        }

        try
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(() => OnLateUiUpdate(printer, error, labelSent)));
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

    private void OnLateUiUpdate(string printer, Exception? error, bool labelSent)
    {
        _latePending = false;
        ReleasePrintGate();
        if (IsDisposed)
        {
            return;
        }

        UpdateButtons();
        if (_printing)
        {
            return; // une autre série est en cours : son message prime
        }

        SetStatus(BatchLogic.BuildLateFinishedMessage(labelSent, printer, error?.Message, out BatchTone tone), tone);
    }

    // ------------------------------------------------------------------ imprimante, réglages

    private void OnPrinterDropDown(object? sender, EventArgs e)
    {
        try
        {
            RefreshPrinters(preferSaved: false);
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
                Persist();
            }

            // L'aperçu montre le papier connu de l'imprimante choisie (ou les réglages si elle n'a pas encore servi).
            if (_preparedPaper != null)
            {
                UpdatePreview();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mémorisation de l'imprimante impossible.", ex);
        }
    }

    private void OnConfirmCheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_settings.BatchConfirm != _chkConfirm.Checked)
            {
                _settings.BatchConfirm = _chkConfirm.Checked;
                Persist();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mémorisation de la confirmation impossible.", ex);
        }
    }

    private void Persist()
    {
        try
        {
            _persist?.Invoke(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement des réglages impossible.", ex);
        }
    }

    /// <summary>
    /// (Ré)énumère les imprimantes ; ne touche à la liste que si elle a changé. La sélection est conservée ; à l'ouverture
    /// (<paramref name="preferSaved"/>) on reprend l'imprimante mémorisée (elle a pu être changée dans la fenêtre principale) ;
    /// à défaut, choix automatique (Brother QL-800, imprimante par défaut...).
    /// </summary>
    private void RefreshPrinters(bool preferSaved)
    {
        IReadOnlyList<string> printers = LabelPrinter.GetPrinters();
        string? current = SelectedPrinter;
        string? saved = PickMatch(printers, _settings.PrinterName);
        string? wanted = preferSaved ? saved ?? PickMatch(printers, current) : PickMatch(printers, current);

        bool unchanged = _cmbPrinter.Items.Count == printers.Count
                         && printers.Select((p, i) => string.Equals(p, _cmbPrinter.Items[i] as string, StringComparison.Ordinal)).All(same => same);
        if (unchanged && wanted != null && string.Equals(wanted, current, StringComparison.Ordinal))
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

            string? target = wanted
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

    // ------------------------------------------------------------------ état

    private void SetStatus(string message, BatchTone tone)
    {
        if (IsDisposed || _lblStatus.IsDisposed)
        {
            return;
        }

        string shown = message.Length > MaxStatusLength ? message.Substring(0, MaxStatusLength - 1) + "…" : message;
        _lblStatus.Text = shown;
        _lblStatus.ForeColor = tone switch
        {
            BatchTone.Success => SuccessColor,
            BatchTone.Warning => WarningColor,
            BatchTone.Error => ErrorColor,
            _ => InfoColor,
        };
        _toolTip.SetToolTip(_lblStatus, message);
    }
}
