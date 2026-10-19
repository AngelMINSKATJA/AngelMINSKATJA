using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BarcodeTray;

/// <summary>
/// Fenêtre « Codes-barres en série » : on charge une liste de textes (colonne Excel, lignes Word ou Bloc-notes collées, ou
/// fichier .txt, .csv, .xlsx, .docx ouvert ou glissé sur la fenêtre), le tableau montre pour chaque ligne le texte et son code-barres
/// Code 128 (miniature du vrai code-barres imprimé), et « Imprimer tous » les envoie un par un, en silence, sur l'imprimante
/// (un travail d'impression = une coupe par code-barres et par copie), exactement comme la fenêtre des étiquettes de plans de châssis.
/// Le tableau est en mode virtuel (les lignes ne sont que des données ; les miniatures sont dessinées à la demande puis gardées
/// dans un cache à capacité fixe) : 5 000 lignes ne ralentissent pas la fenêtre.
/// Toute la logique qui peut se tester sans imprimante (analyse des lignes, textes, choix des lignes, déroulement d'une série,
/// état des boutons) est dans <see cref="BarcodeBatchLogic"/> et <see cref="BatchLogic"/> ; cette classe ne fait que l'afficher et
/// brancher l'impression. La fenêtre ne se ferme jamais vraiment (sauf <see cref="AllowExit"/> ou fin de session) : fermer ou
/// Échap la masque.
/// </summary>
internal sealed class BarcodeBatchForm : Form
{
    private const int MaxStatusLength = 520;
    private const int PrintTimeoutMs = BatchLogic.DefaultTimeoutMs;
    private const int ClipboardAttempts = 5;

    private const int ColNo = 0;
    private const int ColText = 1;
    private const int ColBar = 2;
    private const int ColState = 3;

    // Dimensions pour 96 dpi (Windows les multiplie à 125 %, 150 %, 200 %...).
    private const int RowHeightLogical = 88;
    private const int CellPaddingLogical = 4;
    private const int StripeWidthLogical = 4;
    private const int BarColumnWidthLogical = 280;

    private static readonly Color InfoColor = SystemColors.GrayText;
    private static readonly Color SuccessColor = Color.FromArgb(0, 110, 40);
    private static readonly Color WarningColor = Color.FromArgb(160, 80, 0);
    private static readonly Color ErrorColor = Color.FromArgb(192, 0, 0);
    private static readonly Color InvalidBackColor = Color.FromArgb(253, 236, 234);
    private static readonly Color WarningStripeColor = Color.FromArgb(230, 140, 0);

    /// <summary>Une ligne du tableau : son constat (qui contient le texte rogné) et son état d'impression.</summary>
    private sealed class Entry
    {
        internal Entry(BarcodeRowAnalysis analysis)
        {
            Analysis = analysis;
        }

        internal BarcodeRowAnalysis Analysis { get; set; }

        internal RowState State { get; set; } = RowState.Pending;

        internal int CopiesDone { get; set; }

        internal int Copies { get; set; } = 1;
    }

    /// <summary>
    /// Tableau sans scintillement (le double tampon de DataGridView est protégé), dont la copie (Ctrl+C, Ctrl+Inser) ne donne que
    /// le texte des lignes sélectionnées, une par ligne, au lieu des quatre colonnes.
    /// </summary>
    private sealed class BufferedGrid : DataGridView
    {
        internal BufferedGrid()
        {
            DoubleBuffered = true;
        }

        /// <summary>Texte à copier ; null : copie standard du tableau.</summary>
        internal Func<string?>? CopyTextProvider { get; set; }

        public override DataObject? GetClipboardContent()
        {
            string? text = null;
            try
            {
                text = CopyTextProvider?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn("Texte à copier illisible : " + ex.Message);
            }

            if (string.IsNullOrEmpty(text))
            {
                return base.GetClipboardContent();
            }

            return new DataObject(DataFormats.UnicodeText, text);
        }
    }

    private readonly AppSettings _settings;
    private readonly Action<AppSettings>? _persist;
    private readonly List<Entry> _rows = new();
    private readonly BarcodeBatchLruCache<string, Bitmap> _thumbs = new(BarcodeBatchLogic.ThumbnailCacheCapacity, StringComparer.Ordinal);
    private readonly HashSet<string> _failedThumbs = new(StringComparer.Ordinal);

    private readonly Label _lblInstructions = new();
    private readonly Button _btnPaste = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnClear = new();
    private readonly CheckBox _chkFirstLine = new();
    private readonly NumericUpDown _numCopies = new();
    private readonly BufferedGrid _grid = new();
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
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly ToolStripMenuItem _miCopyImage = new(BarcodeBatchLogic.CopyImageMenuText);
    private readonly ToolStripMenuItem _miCopyText = new(BarcodeBatchLogic.CopyTextMenuText);

    private Icon? _formIcon;
    private Size _thumbBox;
    private int _menuRow = -1;          // ligne visée par le clic droit, relevée à l'appui du bouton (avant l'ouverture du menu)
    private int _menuTarget = -1;       // ligne figée à l'ouverture du menu : le menu est refermé AVANT le Click de son élément
    private bool _menuByMouse;          // le menu va s'ouvrir sur un clic droit (sinon : touche Menu / Maj+F10, ligne courante)
    private int _columnsDpi = 96;       // dpi pour lequel les largeurs de colonnes sont exprimées (le tableau ne les met pas à l'échelle)
    private bool _loadingPrinters;
    private bool _bulkLoading;
    private bool _refreshScheduled;
    private bool _printing;
    private bool _loading; // une liste est en cours de lecture (fichier)
    private bool _thumbFailureLogged;
    private bool _paintFailureLogged;
    private volatile bool _stopRequested;
    private bool _latePending; // un envoi qui a dépassé le délai tourne encore en arrière-plan

    /// <param name="settings">Réglages de l'application (partagés avec les autres fenêtres).</param>
    /// <param name="persist">Appelée pour enregistrer les réglages modifiés ici (imprimante, confirmation) ; null : rien n'est enregistré.</param>
    public BarcodeBatchForm(AppSettings settings, Action<AppSettings>? persist = null)
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

    /// <summary>Nombre de lignes dans la liste.</summary>
    internal int RowCount => _rows.Count;

    /// <summary>Texte du résumé sous la liste.</summary>
    internal string SummaryText => _lblSummary.Text;

    /// <summary>Texte du message d'état.</summary>
    internal string StatusText => _lblStatus.Text;

    /// <summary>Le tableau (pour les tests).</summary>
    internal DataGridView Grid => _grid;

    /// <summary>Case « La première ligne est un titre ».</summary>
    internal bool FirstLineIsTitle
    {
        get => _chkFirstLine.Checked;
        set => _chkFirstLine.Checked = value;
    }

    /// <summary>Nombre de miniatures actuellement gardées en mémoire.</summary>
    internal int ThumbnailCount => _thumbs.Count;

    /// <summary>Nombre maximal de miniatures gardées en mémoire.</summary>
    internal int ThumbnailCapacity => _thumbs.Capacity;

    /// <summary>
    /// Pour les tests : remplace les boîtes de question (confirmation, lignes déjà imprimées) par cette fonction
    /// (texte, boutons) -> réponse. Null : vraie boîte de dialogue.
    /// </summary>
    internal Func<string, MessageBoxButtons, DialogResult>? PromptHook { get; set; }

    /// <summary>Pourquoi l'impression est bloquée (envoi resté sans réponse).</summary>
    private static string LateBlockedMessage => BatchLogic.LateSendBlockedMessage;

    /// <summary>État de la ligne <paramref name="row"/>.</summary>
    internal RowState GetRowState(int row) => row >= 0 && row < _rows.Count ? _rows[row].State : RowState.Pending;

    /// <summary>États de toutes les lignes, dans l'ordre de la liste.</summary>
    private List<RowState> GetRowStates() => _rows.Select(r => r.State).ToList();

    /// <summary>Constat sur la ligne <paramref name="row"/> (null si elle n'existe pas).</summary>
    internal BarcodeRowAnalysis? GetAnalysis(int row) => row >= 0 && row < _rows.Count ? _rows[row].Analysis : null;

    /// <summary>Les textes de la liste (rognés), dans l'ordre.</summary>
    internal IReadOnlyList<string> GetTexts() => _rows.Select(r => r.Analysis.Text).ToList();

    private List<BarcodeRowAnalysis> GetAnalyses() => _rows.Select(r => r.Analysis).ToList();

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
                SyncConfirmFromSettings();
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
            if (_rows.Count == 0)
            {
                _btnPaste.Focus();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Affichage de la fenêtre des codes-barres en série impossible.", ex);
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
            Log.Error("Masquage de la fenêtre des codes-barres en série impossible.", ex);
        }
    }

    // ------------------------------------------------------------------ construction

    private void BuildUi()
    {
        SuspendLayout();

        // Mise à l'échelle : tailles exprimées pour 96 dpi, Windows les multiplie (125 %, 150 %, 200 %...).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = BarcodeBatchLogic.AppTitle;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = true;
        AllowDrop = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 660); // 3 à 4 lignes de 88 points visibles ; la fenêtre peut être agrandie
        MinimumSize = new Size(760, 560);
        _formIcon = TrayApplicationContext.LoadAppIcon();
        if (_formIcon != null)
        {
            Icon = _formIcon;
        }

        // 1) consigne
        _lblInstructions.Text =
            "Préparez la liste dans Excel, Word ou le Bloc-notes (une ligne par code), copiez-la (Ctrl+C), puis cliquez sur « Coller »." + Environment.NewLine
            + "Ou ouvrez un fichier .txt, .csv, .xlsx ou .docx (ou glissez-le ici). Un code-barres est créé par ligne : vérifiez-les, puis cliquez sur « Imprimer tous ».";
        _lblInstructions.AutoSize = false;
        _lblInstructions.Dock = DockStyle.Fill;
        _lblInstructions.Height = 64;
        _lblInstructions.MinimumSize = new Size(0, 64); // hauteur réservée à la consigne (2 à 4 lignes)
        _lblInstructions.Margin = new Padding(3, 0, 3, 6);
        _lblInstructions.UseMnemonic = false;

        // 2) coller / ouvrir / vider / copies
        StyleButton(_btnPaste, "Coller", 0);
        StyleButton(_btnOpen, "Ouvrir un fichier…", 1);
        StyleButton(_btnClear, "Vider la liste", 2);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(_btnPaste);
        buttons.Controls.Add(_btnOpen);
        buttons.Controls.Add(_btnClear);

        var lblCopies = new Label
        {
            Text = "Copies par code-barres :",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 0),
        };
        _numCopies.Minimum = BatchLogic.MinCopies;
        _numCopies.Maximum = BatchLogic.MaxCopies;
        _numCopies.Value = BatchLogic.MinCopies;
        _numCopies.Width = 60;
        _numCopies.Anchor = AnchorStyles.Left;
        _numCopies.TextAlign = HorizontalAlignment.Right;
        _numCopies.TabIndex = 4;
        _numCopies.AccessibleName = "Copies par code-barres";

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

        var topRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topRow.Controls.Add(buttons, 0, 0);
        topRow.Controls.Add(copiesPanel, 2, 0);

        _chkFirstLine.Text = "La première ligne est un titre (l'ignorer)";
        _chkFirstLine.AutoSize = true;
        _chkFirstLine.Anchor = AnchorStyles.Left;
        _chkFirstLine.Margin = new Padding(6, 4, 3, 0);
        _chkFirstLine.TabIndex = 3;
        _chkFirstLine.UseMnemonic = false;

        // 3) tableau
        BuildGrid();

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
        _cmbPrinter.TabIndex = 6;
        _cmbPrinter.AccessibleName = "Imprimante à étiquettes";
        _chkConfirm.Text = "Confirmer avant d'imprimer";
        _chkConfirm.AutoSize = true;
        _chkConfirm.Anchor = AnchorStyles.Left;
        _chkConfirm.Checked = _settings.BatchConfirm;
        _chkConfirm.TabIndex = 7;

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
        StyleButton(_btnPrintAll, "Imprimer tous", 8);
        StyleButton(_btnPrintSelection, "Imprimer la sélection", 9);
        StyleButton(_btnPrintFrom, "Imprimer à partir de la sélection", 10);
        StyleButton(_btnStop, "Arrêter", 11);
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
        _lblStatus.Text = BarcodeBatchLogic.HintText;
        _lblStatus.UseMnemonic = false;

        const int rowCount = 9;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rowCount,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < rowCount; i++)
        {
            root.RowStyles.Add(i == 3 ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));
        }

        root.Controls.Add(_lblInstructions, 0, 0);
        root.Controls.Add(topRow, 0, 1);
        root.Controls.Add(_chkFirstLine, 0, 2);
        root.Controls.Add(_grid, 0, 3);
        root.Controls.Add(_lblSummary, 0, 4);
        root.Controls.Add(printerRow, 0, 5);
        root.Controls.Add(actions, 0, 6);
        root.Controls.Add(_progress, 0, 7);
        root.Controls.Add(_lblStatus, 0, 8);
        Controls.Add(root);

        // Menu du clic droit sur une ligne.
        _rowMenu.Items.Add(_miCopyImage);
        _rowMenu.Items.Add(_miCopyText);
        _grid.ContextMenuStrip = _rowMenu;

        _toolTip.SetToolTip(_btnPaste, "Colle la liste copiée (colonne Excel, lignes Word ou Bloc-notes) — Ctrl+V. Remplace la liste actuelle.");
        _toolTip.SetToolTip(_btnOpen, "Charge un fichier .txt, .csv, .xlsx ou .docx (vous pouvez aussi le glisser sur la fenêtre). Remplace la liste actuelle.");
        _toolTip.SetToolTip(_btnClear, "Efface toute la liste.");
        _toolTip.SetToolTip(
            _chkFirstLine,
            "À cocher AVANT de coller ou d'ouvrir une liste dont la première ligne est un titre de colonne (« Référence », « Code »…) : "
            + "elle ne sera pas convertie en code-barres. Vaut pour UN seul chargement (la case se décoche ensuite) et reste sans effet sur la liste déjà chargée.");
        _toolTip.SetToolTip(_numCopies, "Nombre de copies imprimées pour chaque code-barres (1 à 99).");
        _toolTip.SetToolTip(_btnPrintAll, "Imprime tous les codes-barres de la liste, l'un après l'autre.");
        _toolTip.SetToolTip(_btnPrintSelection, "Imprime seulement les lignes sélectionnées.");
        _toolTip.SetToolTip(_btnPrintFrom, "Imprime de la première ligne sélectionnée jusqu'à la fin de la liste (pour reprendre après un arrêt ou une erreur).");
        _toolTip.SetToolTip(_btnStop, "Arrête la série avant l'envoi du code-barres suivant.");
        _toolTip.SetToolTip(_grid, "Double-clic ou F2 pour corriger un texte ; Suppr pour supprimer les lignes sélectionnées ; clic droit pour copier le code-barres.");

        _btnPaste.Click += (_, _) => PasteFromClipboard();
        _btnOpen.Click += OnOpenClick;
        _btnClear.Click += OnClearClick;
        _numCopies.ValueChanged += (_, _) => UpdateSummary();
        _cmbPrinter.DropDown += OnPrinterDropDown;
        _cmbPrinter.SelectedIndexChanged += OnPrinterSelectionChanged;
        _chkConfirm.CheckedChanged += OnConfirmCheckedChanged;
        _btnPrintAll.Click += (_, _) => StartPrint(PrintMode.All);
        _btnPrintSelection.Click += (_, _) => StartPrint(PrintMode.Selection);
        _btnPrintFrom.Click += (_, _) => StartPrint(PrintMode.FromSelection);
        _btnStop.Click += OnStopClick;
        _rowMenu.Opening += OnRowMenuOpening;
        _miCopyImage.Click += (_, _) => CopyBarcodeImage(TakeMenuTarget());
        _miCopyText.Click += (_, _) => CopyText(TakeMenuTarget());

        // Glisser-déposer d'un fichier n'importe où sur la fenêtre (les événements ne remontent pas aux parents : chaque zone
        // survolée doit les accepter).
        DragEnter += OnDragEnterOrOver;
        DragOver += OnDragEnterOrOver;
        DragDrop += OnDragDropFiles;
        WireDropTargets(this);

        _thumbBox = ComputeThumbBox(BarColumnWidthLogical, RowHeightLogical);

        ResumeLayout(false);
        PerformLayout();

        UpdateSummary();
        UpdateButtons();
    }

    /// <summary>Accepte le dépôt de fichiers sur <paramref name="parent"/> et tous ses contrôles (sauf les zones de saisie de texte).</summary>
    private void WireDropTargets(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is not UpDownBase && child is not ComboBox)
            {
                child.AllowDrop = true;
                child.DragEnter += OnDragEnterOrOver;
                child.DragOver += OnDragEnterOrOver;
                child.DragDrop += OnDragDropFiles;
            }

            WireDropTargets(child);
        }
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
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false; // la suppression est faite ici (RemoveRows), en une seule fois
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.StandardTab = true;
        _grid.ShowCellToolTips = true;
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        _grid.CopyTextProvider = SelectedTextsForCopy;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.RowTemplate.Height = RowHeightLogical;
        _grid.RowTemplate.MinimumHeight = 24;
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.TabIndex = 5;
        _grid.AccessibleName = "Liste des codes-barres";

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

        var colText = new DataGridViewTextBoxColumn
        {
            Name = "colText",
            HeaderText = "Texte du code",
            Width = 220,
            MaxInputLength = BarcodeBatchLogic.MaxTextLength + 50,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        var colBar = new DataGridViewTextBoxColumn
        {
            Name = "colBar",
            HeaderText = "Code-barres",
            ReadOnly = true,
            Width = BarColumnWidthLogical,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        var colState = new DataGridViewTextBoxColumn
        {
            Name = "colState",
            HeaderText = "État",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 100,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        _grid.Columns.AddRange(colNo, colText, colBar, colState);

        _grid.CellValueNeeded += OnGridCellValueNeeded;
        _grid.CellValuePushed += OnGridCellValuePushed;
        _grid.CellFormatting += OnGridCellFormatting;
        _grid.CellPainting += OnGridCellPainting;
        _grid.CellToolTipTextNeeded += OnGridCellToolTipTextNeeded;
        _grid.CellBeginEdit += OnGridCellBeginEdit;
        _grid.MouseDown += OnGridMouseDown;
        _grid.KeyDown += OnGridKeyDown;
        _grid.SelectionChanged += (_, _) => ScheduleRefresh();
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            e.Cancel = false;
        };
    }

    /// <summary>Zone de la miniature dans une cellule de <paramref name="cellWidth"/> x <paramref name="cellHeight"/> (marges comprises).</summary>
    private Size ComputeThumbBox(int cellWidth, int cellHeight)
    {
        int pad = Math.Max(2, LogicalToDeviceUnits(CellPaddingLogical));
        return new Size(Math.Max(1, LogicalToDeviceUnits(cellWidth) - 2 * pad), Math.Max(1, LogicalToDeviceUnits(cellHeight) - 2 * pad));
    }

    // ------------------------------------------------------------------ fermeture / masquage

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ScaleColumns(DeviceDpi);
        ApplyRowHeight();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ScaleColumns(e.DeviceDpiNew);
        ApplyRowHeight();
    }

    /// <summary>
    /// Met les largeurs de colonnes à l'échelle de l'écran (<paramref name="dpi"/>) : DataGridView ne le fait pas lui-même, seule la
    /// hauteur des lignes l'était. Proportionnel à l'échelle déjà appliquée, donc une colonne élargie à la souris reste proportionnée.
    /// </summary>
    private void ScaleColumns(int dpi)
    {
        if (dpi <= 0 || dpi == _columnsDpi)
        {
            return;
        }

        try
        {
            double factor = (double)dpi / _columnsDpi;
            foreach (DataGridViewColumn column in _grid.Columns)
            {
                column.MinimumWidth = Math.Max(2, (int)Math.Round(column.MinimumWidth * factor));
                if (column.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill)
                {
                    column.Width = Math.Max(column.MinimumWidth, (int)Math.Round(column.Width * factor));
                }
            }

            _columnsDpi = dpi;
        }
        catch (Exception ex)
        {
            Log.Warn("Largeur des colonnes impossible à mettre à l'échelle : " + ex.Message);
        }
    }

    /// <summary>
    /// Hauteur des lignes (88 points à 96 dpi, mise à l'échelle). Sans effet si Windows Forms l'a déjà mise à l'échelle ;
    /// sinon la corrige : en mode virtuel les lignes déjà créées gardent leur hauteur, on les recrée.
    /// </summary>
    private void ApplyRowHeight()
    {
        try
        {
            int desired = Math.Max(24, LogicalToDeviceUnits(RowHeightLogical));
            if (_grid.RowTemplate.Height != desired)
            {
                _grid.RowTemplate.Height = desired;
                if (_rows.Count > 0)
                {
                    // Les lignes sont recréées : la sélection (point de reprise de « Imprimer à partir de la sélection ») et la position
                    // dans la liste sont relevées avant, remises après.
                    List<int> selected = SelectedRowIndexes();
                    int current = _grid.CurrentCell?.RowIndex ?? -1;
                    int firstShown = _grid.FirstDisplayedScrollingRowIndex;
                    ResizeGridRows(resetScroll: false, forceRebuild: true);
                    SelectRowSet(selected, current);
                    RestoreScroll(firstShown);
                    UpdateButtons();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Hauteur des lignes impossible à régler : " + ex.Message);
        }
    }

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
        bool busy = _printing || _loading;

        if (keyData == Keys.Escape && !busy && !editing && !_cmbPrinter.DroppedDown)
        {
            HideWindow();
            return true;
        }

        if (keyData == (Keys.Control | Keys.V) && !editing && ActiveControl is not UpDownBase)
        {
            if (!busy)
            {
                PasteFromClipboard();
            }

            return true;
        }

        if (keyData == Keys.Delete && !editing && ReferenceEquals(ActiveControl, _grid))
        {
            DeleteSelectedRows();
            return true;
        }

        if (keyData == (Keys.Control | Keys.C) && !editing && ReferenceEquals(ActiveControl, _grid))
        {
            CopySelectedTexts();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Même chose depuis le tableau lui-même, au cas où il traiterait la touche avant la fenêtre (une seule des deux voies agit).
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_grid.IsCurrentCellInEditMode)
        {
            return;
        }

        if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            DeleteSelectedRows();
        }
        else if (e.KeyCode == Keys.C && e.Modifiers == Keys.Control)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CopySelectedTexts();
        }
    }

    /// <summary>Touche Suppr : supprime les lignes sélectionnées (sans effet pendant une série ou une lecture).</summary>
    private void DeleteSelectedRows()
    {
        if (_printing || _loading)
        {
            return;
        }

        int removed = RemoveRows(SelectedRowIndexes());
        if (removed > 0)
        {
            SetStatus(Count(removed, "ligne supprimée", "lignes supprimées") + ".", BatchTone.Info);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopRequested = true;
            PrintGate.Release(this);
            _toolTip.Dispose();
            _rowMenu.Dispose();
            _thumbs.Dispose();
            _lblSummary.Font?.Dispose();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _formIcon?.Dispose();
            _formIcon = null;
        }
    }

    private static string Count(int n, string singular, string plural)
        => n.ToString(CultureInfo.InvariantCulture) + " " + (n <= 1 ? singular : plural);

    // ------------------------------------------------------------------ chargement de la liste

    /// <summary>Colle le texte du presse-papiers (remplace la liste, sans rien demander).</summary>
    private void PasteFromClipboard()
    {
        if (_printing || _loading || IsDisposed)
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
                SetStatus(BarcodeBatchLogic.NoClipboardTextMessage, BatchTone.Error);
                return;
            }

            LoadText(text);
        }
        catch (Exception ex)
        {
            Log.Error("Collage de la liste impossible (" + ex.GetType().Name + ")."); // jamais le contenu collé dans le journal
            SetStatus("Échec du collage : " + ex.Message, BatchTone.Error);
        }
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

        problem = BarcodeBatchLogic.ClipboardBusyMessage;
        return null;
    }

    /// <summary>
    /// Lit <paramref name="text"/> (texte collé) comme une liste et, s'il contient des valeurs, remplace la liste. En cas d'erreur la
    /// liste actuelle est conservée et le message est affiché en rouge.
    /// </summary>
    internal ListReadResult LoadText(string? text)
    {
        ListReadResult result = ListFileReader.ReadText(text, _chkFirstLine.Checked);
        LoadResult(result);
        return result;
    }

    /// <summary>
    /// Lit le fichier <paramref name="path"/> (sur un thread de travail : la fenêtre reste réactive) et, s'il contient des valeurs,
    /// remplace la liste. Ne lève jamais d'exception.
    /// </summary>
    internal async Task<ListReadResult> LoadFileAsync(string path)
    {
        if (_printing || _loading || IsDisposed)
        {
            return new ListReadResult(Array.Empty<string>(), Array.Empty<string>(), "Une opération est déjà en cours.", "fichier");
        }

        bool skipFirst = _chkFirstLine.Checked;
        string name = SafeFileName(path);
        _loading = true;
        UseWaitCursor = true;
        UpdateButtons();
        SetStatus("Lecture de « " + name + " »…", BatchTone.Info);

        ListReadResult result;
        try
        {
            result = await Task.Run(() => ListFileReader.ReadFile(path, skipFirst));
        }
        catch (Exception ex)
        {
            // Ni le nom du fichier ni son contenu dans le journal : seulement le type de l'erreur.
            Log.Error("Lecture du fichier impossible (" + ex.GetType().Name + ").");
            result = new ListReadResult(Array.Empty<string>(), Array.Empty<string>(), "Lecture du fichier impossible : " + ex.Message, "fichier « " + name + " »");
        }
        finally
        {
            _loading = false;
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                UpdateButtons(); // lecture finie (réussie ou non) : sans cela, un fichier refusé laisserait la barre d'outils grisée
            }
        }

        if (IsDisposed)
        {
            return result;
        }

        LoadResult(result);
        return result;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// Applique ce que le lecteur de listes a rendu : s'il y a des valeurs, la liste est remplacée ; sinon (erreur, liste vide) la
    /// liste actuelle est conservée. Le message d'état dit le résultat (rouge, orange ou vert).
    /// </summary>
    internal void LoadResult(ListReadResult? result)
    {
        if (_printing)
        {
            SetStatus("Impression en cours : patientez la fin de la série (ou cliquez sur « Arrêter ») avant de charger une liste.", BatchTone.Warning);
            return;
        }

        if (result == null || !string.IsNullOrWhiteSpace(result.Error) || result.Values.Count == 0)
        {
            (string message, BatchTone tone) = BarcodeBatchLogic.BuildLoadStatus(result, null);
            SetStatus(message, tone);
            return;
        }

        ReplaceRows(result.Values);

        // « La première ligne est un titre » vaut pour UN chargement : restée cochée, elle ferait perdre en silence le premier code
        // de la liste suivante (collée ou ouverte plus tard, sans titre).
        if (_chkFirstLine.Checked)
        {
            _chkFirstLine.Checked = false;
        }

        BarcodeListStats stats = BarcodeBatchLogic.ComputeStats(GetAnalyses());
        (string text, BatchTone status) = BarcodeBatchLogic.BuildLoadStatus(result, stats, _latePending ? LateBlockedMessage : null);
        SetStatus(text, status);
        Log.Info("Liste chargée : " + _rows.Count + " valeur(s), " + result.Warnings.Count + " remarque(s), " + stats.Invalid
                 + " invalide(s), " + stats.Thin + " à barres fines, " + stats.Scaled + " réduite(s), " + stats.Duplicates + " doublon(s).");
    }

    private void ReplaceRows(IReadOnlyList<string> values)
    {
        try
        {
            _grid.EndEdit();
        }
        catch (Exception ex)
        {
            Log.Warn("Fin de modification impossible : " + ex.Message);
        }

        (int preferred, int max) = BarcodeBatchLogic.ModuleLimits(_settings);
        var rows = new List<Entry>(values.Count);
        foreach (string value in values)
        {
            rows.Add(new Entry(BarcodeBatchLogic.AnalyzeRow(value, preferred, max)));
        }

        _rows.Clear();
        _rows.AddRange(rows);
        ClearThumbnails();
        _progress.Maximum = 1;
        _progress.Value = 0;
        ResizeGridRows(resetScroll: true, forceRebuild: true);
        OnListChanged();
    }

    private void OnOpenClick(object? sender, EventArgs e)
    {
        if (_printing || _loading)
        {
            return;
        }

        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Ouvrir une liste de codes",
                Filter = BarcodeBatchLogic.SafeFileFilter(),
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                StartLoadFile(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture du fichier impossible.", ex);
            SetStatus("Impossible d'ouvrir un fichier : " + ex.Message, BatchTone.Error);
        }
    }

    private async void StartLoadFile(string path)
    {
        try
        {
            await LoadFileAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error("Chargement du fichier : erreur inattendue (" + ex.GetType().Name + ").");
            _loading = false;
            UseWaitCursor = false;
            UpdateButtons();
            SetStatus("Échec de la lecture : " + ex.Message, BatchTone.Error);
        }
    }

    // ------------------------------------------------------------------ glisser-déposer

    private void OnDragEnterOrOver(object? sender, DragEventArgs e)
    {
        bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = files && !_printing && !_loading ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDropFiles(object? sender, DragEventArgs e)
    {
        string[]? files = null;
        try
        {
            files = e.Data?.GetData(DataFormats.FileDrop) as string[];
        }
        catch (Exception ex)
        {
            Log.Warn("Lecture des éléments déposés impossible : " + ex.Message);
        }

        if (files == null || files.Length == 0)
        {
            return; // pas des fichiers (texte, image...) : ignoré
        }

        // La lecture se fait après le retour du glisser-déposer : l'Explorateur n'est pas bloqué pendant ce temps.
        try
        {
            BeginInvoke(new Action(() => StartDrop(files)));
        }
        catch (InvalidOperationException)
        {
            // poignée détruite : fermeture en cours
        }
    }

    private async void StartDrop(string[] files)
    {
        try
        {
            await HandleDropAsync(files);
        }
        catch (Exception ex)
        {
            Log.Error("Fichier déposé : erreur inattendue (" + ex.GetType().Name + ").");
            SetStatus("Échec de la lecture : " + ex.Message, BatchTone.Error);
        }
    }

    /// <summary>Traite les éléments déposés sur la fenêtre : un seul fichier est lu, le reste est expliqué.</summary>
    internal async Task HandleDropAsync(IReadOnlyList<string>? paths)
    {
        if (_printing || _loading || IsDisposed)
        {
            SetStatus("Impression ou lecture en cours : patientez avant de déposer un fichier.", BatchTone.Warning);
            return;
        }

        (string? path, string? problem) = BarcodeBatchLogic.ClassifyDrop(paths);
        if (problem != null)
        {
            SetStatus(problem, BatchTone.Warning);
            return;
        }

        if (path != null)
        {
            await LoadFileAsync(path);
        }
    }

    // ------------------------------------------------------------------ tableau (mode virtuel)

    private void OnGridCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        Entry entry = _rows[e.RowIndex];
        switch (e.ColumnIndex)
        {
            case ColNo:
                e.Value = (e.RowIndex + 1).ToString(CultureInfo.InvariantCulture);
                break;
            case ColText:
                e.Value = entry.Analysis.Text;
                break;
            case ColState:
                e.Value = BarcodeBatchLogic.StateColumnText(entry.State, entry.CopiesDone, entry.Copies, entry.Analysis);
                break;
            default:
                e.Value = string.Empty; // la colonne « Code-barres » est dessinée (OnGridCellPainting)
                break;
        }
    }

    private void OnGridCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.ColumnIndex != ColText || _bulkLoading || _printing || _loading)
        {
            return;
        }

        ApplyEdit(e.RowIndex, Convert.ToString(e.Value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// Remplace le texte de la ligne <paramref name="row"/> (saisie dans le tableau) : le constat est refait, la miniature sera
    /// redessinée et l'état de la ligne est effacé (elle n'est plus « imprimée »).
    /// </summary>
    internal void ApplyEdit(int row, string text)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        Entry entry = _rows[row];
        string trimmed = (text ?? string.Empty).Trim();
        if (string.Equals(entry.Analysis.Text, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        (int preferred, int max) = BarcodeBatchLogic.ModuleLimits(_settings);
        entry.Analysis = BarcodeBatchLogic.AnalyzeRow(trimmed, preferred, max);
        entry.State = RowState.Pending;
        entry.CopiesDone = 0;
        entry.Copies = 1;
        _failedThumbs.Remove(entry.Analysis.Text);
        InvalidateRowSafe(row);
        UpdateSummary();
    }

    private void OnGridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.CellStyle == null)
        {
            return;
        }

        Entry entry = _rows[e.RowIndex];
        Color color = Color.Empty;
        if (e.ColumnIndex == ColState)
        {
            color = ToneColor(BarcodeBatchLogic.StateTone(entry.State, entry.Analysis));
        }
        else if (e.ColumnIndex == ColText && !entry.Analysis.IsValid)
        {
            color = ErrorColor;
        }

        // Sur une ligne sélectionnée, la couleur de sélection du système garde un contraste lisible.
        if (color != Color.Empty && !IsRowSelected(e.RowIndex))
        {
            e.CellStyle.ForeColor = color;
        }
    }

    private static Color ToneColor(BatchTone tone) => tone switch
    {
        BatchTone.Success => SuccessColor,
        BatchTone.Warning => WarningColor,
        BatchTone.Error => ErrorColor,
        _ => Color.Empty,
    };

    private void OnGridCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
        {
            return;
        }

        string? message = _rows[e.RowIndex].Analysis.Message;
        if (!string.IsNullOrEmpty(message))
        {
            e.ToolTipText = message;
        }
    }

    // Ceinture et bretelles : « N° », « Code-barres » et « État » ne se modifient jamais, et rien ne se modifie pendant une série.
    private void OnGridCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_printing || _loading || e.ColumnIndex != ColText)
        {
            e.Cancel = true;
        }
    }

    /// <summary>
    /// Clic droit : relève la ligne visée (et la sélectionne si elle ne l'était pas) AVANT l'ouverture du menu. Un clic sur l'en-tête
    /// ou dans une zone vide ne vise aucune ligne : le menu ne s'ouvre pas (voir <see cref="OnRowMenuOpening"/>).
    /// </summary>
    private void OnGridMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        _menuByMouse = true;
        DataGridView.HitTestInfo hit = _grid.HitTest(e.X, e.Y);
        _menuRow = hit.Type == DataGridViewHitTestType.Cell && hit.RowIndex >= 0 && hit.RowIndex < _rows.Count ? hit.RowIndex : -1;
        if (_menuRow >= 0 && !IsRowSelected(_menuRow))
        {
            SelectRow(_menuRow, ColText);
        }
    }

    // ------------------------------------------------------------------ dessin des cellules

    private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || (e.ColumnIndex != ColBar && e.ColumnIndex != ColNo))
        {
            return;
        }

        try
        {
            if (e.ColumnIndex == ColBar)
            {
                PaintBarcodeCell(e);
            }
            else
            {
                PaintNumberCell(e);
            }
        }
        catch (Exception ex)
        {
            if (!_paintFailureLogged)
            {
                _paintFailureLogged = true;
                Log.Error("Dessin d'une cellule impossible (le dessin standard est utilisé).", ex);
            }

            e.Handled = false;
        }
    }

    /// <summary>« N° » : dessin standard, plus un liseré rouge (ligne invalide) ou orange (barres fines, code-barres réduit).</summary>
    private void PaintNumberCell(DataGridViewCellPaintingEventArgs e)
    {
        BarcodeRowSeverity severity = _rows[e.RowIndex].Analysis.Severity;
        if (severity == BarcodeRowSeverity.Ok || e.Graphics == null)
        {
            return;
        }

        e.Paint(e.CellBounds, DataGridViewPaintParts.All);
        int width = Math.Max(3, LogicalToDeviceUnits(StripeWidthLogical));
        using var brush = new SolidBrush(severity == BarcodeRowSeverity.Invalid ? ErrorColor : WarningStripeColor);
        e.Graphics.FillRectangle(brush, e.CellBounds.X + 1, e.CellBounds.Y + 1, width, Math.Max(1, e.CellBounds.Height - 2));
        e.Handled = true;
    }

    /// <summary>« Code-barres » : miniature du vrai code-barres, ou cadre rouge avec le message pour une ligne invalide.</summary>
    private void PaintBarcodeCell(DataGridViewCellPaintingEventArgs e)
    {
        Graphics? g = e.Graphics;
        if (g == null)
        {
            return;
        }

        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);

        int pad = Math.Max(2, LogicalToDeviceUnits(CellPaddingLogical));
        DrawBarcodeContent(g, Rectangle.Inflate(e.CellBounds, -pad, -pad), _rows[e.RowIndex]);
        e.Handled = true;
    }

    /// <summary>Dessine dans <paramref name="inner"/> la miniature de la ligne, ou le cadre rouge avec le message si elle est invalide.</summary>
    private void DrawBarcodeContent(Graphics g, Rectangle inner, Entry entry)
    {
        if (inner.Width < 8 || inner.Height < 8)
        {
            return;
        }

        if (!entry.Analysis.IsValid)
        {
            DrawPlaceholder(g, inner, entry.Analysis.Error ?? string.Empty, ErrorColor);
            return;
        }

        Bitmap? thumbnail = GetThumbnail(entry.Analysis.Text, inner.Size);
        if (thumbnail != null)
        {
            int x = inner.X + (inner.Width - thumbnail.Width) / 2;
            int y = inner.Y + (inner.Height - thumbnail.Height) / 2;
            g.DrawImage(thumbnail, new Rectangle(x, y, thumbnail.Width, thumbnail.Height));
        }
        else
        {
            DrawPlaceholder(g, inner, "Aperçu indisponible", WarningColor);
        }
    }

    /// <summary>Dessine le contenu de la cellule « Code-barres » de la ligne dans <paramref name="bounds"/> (pour les tests).</summary>
    internal void DrawRowBarcode(Graphics g, Rectangle bounds, int row)
    {
        if (row >= 0 && row < _rows.Count)
        {
            DrawBarcodeContent(g, bounds, _rows[row]);
        }
    }

    private void DrawPlaceholder(Graphics g, Rectangle bounds, string message, Color color)
    {
        using (var back = new SolidBrush(InvalidBackColor))
        {
            g.FillRectangle(back, bounds);
        }

        using (var pen = new Pen(color))
        {
            g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        Rectangle textBounds = Rectangle.Inflate(bounds, -4, -2);
        TextRenderer.DrawText(
            g,
            message,
            _grid.DefaultCellStyle.Font ?? _grid.Font,
            textBounds,
            color,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Miniature du code-barres de <paramref name="text"/> (dessinée à la demande puis gardée dans le cache, qui libère les plus
    /// anciennes). La taille suit la cellule : si elle change (colonne redimensionnée, écran à un autre dpi), le cache est vidé.
    /// Null si le dessin a échoué. Ne libérez PAS le Bitmap rendu : il appartient au cache.
    /// </summary>
    private Bitmap? GetThumbnail(string text, Size box)
    {
        if (box.Width < 1 || box.Height < 1)
        {
            return null;
        }

        if (box != _thumbBox)
        {
            ClearThumbnails();
            _thumbBox = box;
        }

        if (_failedThumbs.Contains(text))
        {
            return null;
        }

        try
        {
            Size size = _thumbBox;
            return _thumbs.GetOrAdd(text, t => BarcodeBatchThumbnail.Create(t, _settings, size.Width, size.Height));
        }
        catch (Exception ex)
        {
            _failedThumbs.Add(text);
            if (!_thumbFailureLogged)
            {
                _thumbFailureLogged = true;
                Log.Error("Miniature d'un code-barres impossible.", ex); // jamais le texte lui-même
            }

            return null;
        }
    }

    /// <summary>Miniature de la ligne <paramref name="row"/> (null si la ligne est invalide ou inexistante) ; pour les tests.</summary>
    internal Bitmap? ThumbnailFor(int row)
    {
        if (row < 0 || row >= _rows.Count || !_rows[row].Analysis.IsValid)
        {
            return null;
        }

        return GetThumbnail(_rows[row].Analysis.Text, _thumbBox);
    }

    private void ClearThumbnails()
    {
        _thumbs.Clear();
        _failedThumbs.Clear();
    }

    // ------------------------------------------------------------------ lignes

    private bool IsRowSelected(int row)
        => row >= 0 && row < _grid.Rows.Count && (_grid.Rows.GetRowState(row) & DataGridViewElementStates.Selected) != 0;

    private List<int> SelectedRowIndexes()
    {
        var indexes = new List<int>();
        int count = Math.Min(_rows.Count, _grid.Rows.Count);
        for (int i = 0; i < count; i++)
        {
            if ((_grid.Rows.GetRowState(i) & DataGridViewElementStates.Selected) != 0)
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    /// <summary>
    /// Met le nombre de lignes du tableau en accord avec la liste. <paramref name="resetScroll"/> : repart du haut (nouvelle liste) ;
    /// <paramref name="forceRebuild"/> : recrée toutes les lignes (nouvelle liste, nouvelle hauteur) au lieu d'ajuster leur nombre.
    /// </summary>
    private void ResizeGridRows(bool resetScroll, bool forceRebuild)
    {
        _bulkLoading = true;
        try
        {
            if (resetScroll || forceRebuild)
            {
                _grid.CurrentCell = null;
                _grid.RowCount = 0;
            }

            _grid.RowCount = _rows.Count;
            _grid.ClearSelection();
            _grid.Invalidate();
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour du tableau impossible.", ex);
        }
        finally
        {
            _bulkLoading = false;
        }
    }

    /// <summary>Supprime les lignes données de la liste (touche Suppr). Renvoie leur nombre.</summary>
    internal int RemoveRows(IEnumerable<int> indexes)
    {
        if (_printing || _loading || IsDisposed)
        {
            return 0;
        }

        var remove = new HashSet<int>(indexes.Where(i => i >= 0 && i < _rows.Count));
        if (remove.Count == 0)
        {
            return 0;
        }

        try
        {
            _grid.EndEdit();
        }
        catch (Exception ex)
        {
            Log.Warn("Fin de modification impossible : " + ex.Message);
        }

        int first = remove.Min();
        var kept = new List<Entry>(_rows.Count - remove.Count);
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!remove.Contains(i))
            {
                kept.Add(_rows[i]);
            }
        }

        _rows.Clear();
        _rows.AddRange(kept);
        ResizeGridRows(resetScroll: false, forceRebuild: false);
        if (_rows.Count > 0)
        {
            SelectRow(Math.Min(first, _rows.Count - 1), ColText);
        }

        OnListChanged();
        return remove.Count;
    }

    private void OnClearClick(object? sender, EventArgs e)
    {
        if (_printing || _loading)
        {
            return;
        }

        try
        {
            _grid.EndEdit();
            _rows.Clear();
            ClearThumbnails();
            ResizeGridRows(resetScroll: true, forceRebuild: true);
            _progress.Maximum = 1;
            _progress.Value = 0;
            OnListChanged();
            SetStatus(BarcodeBatchLogic.HintText, BatchTone.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Vidage de la liste impossible.", ex);
            SetStatus("Impossible de vider la liste : " + ex.Message, BatchTone.Error);
        }
    }

    internal void SetRowState(int row, RowState state, int copiesDone = 0, int copies = 1)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        Entry entry = _rows[row];
        entry.State = state;
        entry.CopiesDone = copiesDone;
        entry.Copies = copies;
        InvalidateRowSafe(row);
    }

    private void InvalidateRowSafe(int row)
    {
        try
        {
            if (row >= 0 && row < _grid.RowCount)
            {
                _grid.InvalidateRow(row);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Actualisation d'une ligne impossible : " + ex.Message);
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

    /// <summary>
    /// Sélectionne exactement <paramref name="rows"/> (lignes déjà existantes), la ligne courante étant <paramref name="current"/>
    /// (à défaut la première). Utilisé pour reprendre une sélection après un arrêt ou un changement d'écran.
    /// </summary>
    private void SelectRowSet(IReadOnlyList<int> rows, int current)
    {
        _bulkLoading = true; // une sélection de milliers de lignes ne doit pas recalculer l'interface à chaque ligne
        try
        {
            int count = _grid.RowCount;
            int cur = current >= 0 && current < count ? current : (rows.Count > 0 ? rows[0] : -1);
            if (cur >= 0 && cur < count)
            {
                _grid.CurrentCell = _grid.Rows[cur].Cells[ColText];
            }

            _grid.ClearSelection();
            foreach (int row in rows)
            {
                if (row >= 0 && row < count)
                {
                    _grid.Rows[row].Selected = true;
                }
            }

            if (cur >= 0 && cur < count)
            {
                EnsureRowVisible(cur);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Sélection des lignes impossible : " + ex.Message);
        }
        finally
        {
            _bulkLoading = false;
        }

        UpdateButtons();
    }

    /// <summary>Remet la première ligne affichée du tableau (après une recréation des lignes).</summary>
    private void RestoreScroll(int firstShown)
    {
        try
        {
            if (firstShown >= 0 && firstShown < _grid.RowCount)
            {
                _grid.FirstDisplayedScrollingRowIndex = firstShown;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Défilement de la liste impossible : " + ex.Message);
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

    // ------------------------------------------------------------------ résumé, boutons

    /// <summary>Remet à jour résumé et boutons.</summary>
    private void OnListChanged()
    {
        if (_bulkLoading || IsDisposed)
        {
            return;
        }

        UpdateSummary();
        UpdateButtons();
    }

    /// <summary>
    /// Met les boutons à jour une seule fois par tour de boucle de messages : sélectionner 5 000 lignes d'un coup déclenche
    /// beaucoup d'événements, il serait absurde de tout recalculer à chaque fois.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_bulkLoading || IsDisposed)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            UpdateButtons();
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
                UpdateButtons();
            }));
        }
        catch (InvalidOperationException)
        {
            _refreshScheduled = false; // poignée détruite : fermeture en cours
        }
    }

    private void UpdateSummary()
    {
        if (IsDisposed)
        {
            return;
        }

        List<BarcodeRowAnalysis> analyses = GetAnalyses();
        string text = BarcodeBatchLogic.BuildSummary(analyses, (int)_numCopies.Value);
        _lblSummary.Text = text;
        _lblSummary.ForeColor = BarcodeBatchLogic.SummaryTone(analyses) switch
        {
            BatchTone.Error => ErrorColor,
            BatchTone.Warning => WarningColor,
            _ => SystemColors.ControlText,
        };
        _toolTip.SetToolTip(_lblSummary, text);
    }

    /// <summary>Active ou désactive les commandes selon l'état (voir <see cref="BatchLogic.ComputeButtons"/>).</summary>
    private void UpdateButtons()
    {
        if (IsDisposed)
        {
            return;
        }

        int selected = _grid.Rows.GetRowCount(DataGridViewElementStates.Selected);
        BatchButtonStates s = BatchLogic.ComputeButtons(_rows.Count, selected, _printing || _loading, _stopRequested, _latePending);
        s = s with { Stop = s.Stop && _printing }; // « Arrêter » n'a de sens que pendant une série, pas pendant la lecture d'un fichier
        _btnPaste.Enabled = s.Paste;
        _btnOpen.Enabled = s.Paste;
        _chkFirstLine.Enabled = s.Paste;
        _btnClear.Enabled = s.Clear;
        _numCopies.Enabled = s.Copies;
        _cmbPrinter.Enabled = s.Printer;
        _chkConfirm.Enabled = s.Confirm;
        _btnPrintAll.Enabled = s.PrintAll;
        _btnPrintSelection.Enabled = s.PrintSelection;
        _btnPrintFrom.Enabled = s.PrintFromSelection;
        _btnStop.Enabled = s.Stop;

        // Seule la colonne « Texte du code » est modifiable, et seulement hors impression.
        bool editable = s.GridEditable;
        if (_grid.Columns[ColText].ReadOnly == editable)
        {
            _grid.Columns[ColText].ReadOnly = !editable;
        }

        _toolTip.SetToolTip(_btnPrintAll, _latePending ? LateBlockedMessage : "Imprime tous les codes-barres de la liste, l'un après l'autre.");
    }

    // ------------------------------------------------------------------ copie (menu du clic droit, Ctrl+C)

    /// <summary>Ligne courante du tableau, ou -1.</summary>
    private int CurrentRow()
    {
        int current = _grid.CurrentCell?.RowIndex ?? -1;
        return current >= 0 && current < _rows.Count ? current : -1;
    }

    /// <summary>
    /// Ligne visée par le menu, figée à son ouverture. Windows Forms referme le menu AVANT de lancer le Click de l'élément choisi : la
    /// ligne ne peut pas être relue à ce moment-là (une sélection de plusieurs lignes laisserait croire que c'est la ligne courante).
    /// Sans ouverture préalable (appel direct), c'est la ligne courante.
    /// </summary>
    private int TakeMenuTarget()
    {
        int target = _menuTarget;
        _menuTarget = -1;
        return target >= 0 && target < _rows.Count ? target : CurrentRow();
    }

    private void OnRowMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Clic droit : la ligne relevée à l'appui du bouton (aucune si c'est l'en-tête ou une zone vide) ; clavier : la ligne courante.
        int row = _menuByMouse ? (_menuRow >= 0 && _menuRow < _rows.Count ? _menuRow : -1) : CurrentRow();
        _menuByMouse = false;
        _menuRow = -1;
        if (row < 0)
        {
            e.Cancel = true; // clic dans une zone vide du tableau
            return;
        }

        _menuTarget = row;
        _miCopyImage.Enabled = !_printing && _rows[row].Analysis.IsValid;
        _miCopyText.Enabled = true;
    }

    /// <summary>
    /// Copie le code-barres de la ligne dans le presse-papiers, comme le bouton « Générer » de la fenêtre principale : image
    /// bitmap + PNG, 300 dpi. Renvoie faux (avec un message rouge) si la ligne est invalide ou si le presse-papiers est occupé.
    /// </summary>
    internal bool CopyBarcodeImage(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return false;
        }

        BarcodeRowAnalysis analysis = _rows[row].Analysis;
        if (!analysis.IsValid)
        {
            SetStatus("Ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " : " + analysis.Error, BatchTone.Error);
            return false;
        }

        try
        {
            using Bitmap bitmap = BarcodeRenderer.Render(analysis.Text, _settings, out int modulePx);
            byte[] png = BarcodeRenderer.ToPng(bitmap);
            ClipboardService.SetBarcodeImage(bitmap, png);
            Log.Info("Code-barres de la liste copié dans le presse-papier (" + analysis.Text.Length + " caractères, " + bitmap.Width + "x"
                     + bitmap.Height + " px, barre fine " + modulePx + " px).");
            string? warning = BarcodeRenderer.ThinBarsWarning(modulePx, _settings.ModulePx);
            SetStatus(
                "Code-barres de la ligne " + (row + 1).ToString(CultureInfo.InvariantCulture)
                + " copié dans le presse-papier — collez-le avec Ctrl+V." + (warning != null ? " " + warning : string.Empty),
                warning != null ? BatchTone.Warning : BatchTone.Success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Copie du code-barres impossible.", ex);
            SetStatus("Échec de la copie : " + ex.Message, BatchTone.Error);
            return false;
        }
    }

    /// <summary>Copie le texte de la ligne dans le presse-papiers.</summary>
    internal bool CopyText(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return false;
        }

        try
        {
            ClipboardService.SetText(_rows[row].Analysis.Text);
            SetStatus("Texte de la ligne " + (row + 1).ToString(CultureInfo.InvariantCulture) + " copié dans le presse-papier.", BatchTone.Success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Copie du texte impossible.", ex);
            SetStatus("Échec de la copie : " + ex.Message, BatchTone.Error);
            return false;
        }
    }

    /// <summary>Les textes des lignes sélectionnées, une par ligne ; null si aucune ligne n'est sélectionnée.</summary>
    private string? SelectedTextsForCopy()
    {
        List<int> selected = SelectedRowIndexes();
        return selected.Count == 0 ? null : string.Join(Environment.NewLine, selected.Select(i => _rows[i].Analysis.Text));
    }

    /// <summary>Ctrl+C dans le tableau : copie les textes des lignes sélectionnées, une par ligne.</summary>
    private void CopySelectedTexts()
    {
        string? text = SelectedTextsForCopy();
        if (text == null)
        {
            return;
        }

        try
        {
            ClipboardService.SetText(text);
            SetStatus(Count(SelectedRowIndexes().Count, "texte copié", "textes copiés") + " dans le presse-papier.", BatchTone.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Copie des textes impossible.", ex);
            SetStatus("Échec de la copie : " + ex.Message, BatchTone.Error);
        }
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
        SetStatus("Arrêt demandé : la série s'arrêtera avant le code-barres suivant (l'envoi en cours se termine).", BatchTone.Warning);
    }

    private async void StartPrint(PrintMode mode)
    {
        try
        {
            await PrintAsync(mode);
        }
        catch (Exception ex)
        {
            Log.Error("Série de codes-barres : erreur inattendue.", ex);
            SetStatus("Échec de l'impression : " + ex.Message, BatchTone.Error);
            EndBusy();
        }
    }

    /// <summary>Valide puis imprime les lignes choisies (appelée par les boutons ; interne pour les tests).</summary>
    internal async Task PrintAsync(PrintMode mode)
    {
        if (_printing || _loading || IsDisposed)
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

        List<BarcodeRowAnalysis> analyses = GetAnalyses();
        IReadOnlyList<int> rows = BatchLogic.ChooseRows(mode, analyses.Count, SelectedRowIndexes());
        if (rows.Count == 0)
        {
            SetStatus(BarcodeBatchLogic.NothingToPrintMessage(mode), BatchTone.Warning);
            return;
        }

        // Toutes les lignes à imprimer sont vérifiées AVANT d'en envoyer une seule : on s'arrête à la première invalide.
        BarcodeRowProblem? problem = BarcodeBatchLogic.FindInvalidRow(analyses, rows);
        if (problem != null)
        {
            SelectRow(problem.Row, ColText);
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

        // Des lignes déjà marquées « ✔ imprimée » dans la série à lancer (typiquement « Imprimer tous » après un arrêt ou un échec) :
        // on ne les réimprime jamais sans le dire, même si la confirmation est décochée.
        IReadOnlyList<RowState> states = GetRowStates();
        int alreadyDone = BatchLogic.CountDone(states, rows);
        if (alreadyDone > 0)
        {
            bool allDone = alreadyDone >= rows.Count;
            DialogResult answer = Ask(
                BarcodeBatchLogic.BuildReprintText(rows.Count, alreadyDone, copies, printer),
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
                BarcodeBatchLogic.BuildConfirmText(rows.Count, copies, printer),
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
        if (!PrintGate.TryAcquire(printer, this, BarcodeBatchLogic.AppTitle, out string? busyWindow))
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
        IReadOnlyList<string> texts = GetTexts(); // la liste ne change pas pendant la série (tableau verrouillé)
        BarcodeListStats stats = BarcodeBatchLogic.ComputeStats(analyses);
        Log.Info("Série de codes-barres : " + rows.Count + " ligne(s), " + copies + " copie(s) chacune, imprimante « " + printer
                 + " » (" + stats.Thin + " à barres fines, " + stats.Scaled + " réduite(s), " + stats.Duplicates + " doublon(s) dans la liste).");

        // Les lignes de CETTE série repartent à zéro (les autres, déjà imprimées, gardent leur « ✔ »).
        foreach (int r in rows)
        {
            SetRowState(r, RowState.Pending);
        }

        // Chaque copie est dessinée puis envoyée sur un thread de travail, avec un délai par envoi. Le chemin d'impression est celui
        // du bouton « Imprimer » de la fenêtre principale (LabelPrinter.Print) : un travail d'impression, une coupe.
        Task PrintSheet(int row, int copy)
        {
            string text = texts[row];
            return Task.Run(() => PrintOne(text, printer, snapshot));
        }

        BatchOutcome outcome = await BatchLogic.Run(
            rows,
            copies,
            PrintSheet,
            () => _stopRequested || IsDisposed,
            e => ApplyBatchEvent(e, texts),
            PrintTimeoutMs,
            0);

        if (outcome.Orphan != null)
        {
            TrackOrphan(outcome.Orphan, printer);
        }

        LogOutcome(outcome, printer);
        EndBusy();

        string? qualityNote = BarcodeBatchLogic.BuildQualityNote(analyses, rows, outcome);
        string message = BarcodeBatchLogic.BuildOutcomeMessage(outcome, texts, copies, printer, PrintTimeoutMs, qualityNote, mode, out BatchTone tone);
        if (outcome.ResumeRow.HasValue)
        {
            if (mode == PrintMode.Selection)
            {
                // Série interrompue sur « Imprimer la sélection » : on garde le RESTE de la sélection (les lignes pas encore envoyées),
                // pour que « Imprimer la sélection » la reprenne telle quelle ; une seule ligne choisie ferait imprimer toute la fin de la liste.
                SelectRowSet(BarcodeBatchLogic.RemainingRows(rows, outcome.ResumeRow.Value), outcome.ResumeRow.Value);
            }
            else
            {
                SelectRow(outcome.ResumeRow.Value, ColText);
            }
        }

        SetStatus(message, tone);
    }

    /// <summary>Pose une question à l'utilisateur (ou appelle <see cref="PromptHook"/> dans les tests).</summary>
    private DialogResult Ask(string text, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        => PromptHook != null
            ? PromptHook(text, buttons)
            : MessageBox.Show(this, text, BarcodeBatchLogic.AppTitle, buttons, icon, defaultButton);

    /// <summary>Dessine et envoie UNE copie (thread de travail : aucun accès à l'interface). L'image est libérée après le travail.</summary>
    private static void PrintOne(string text, string printer, AppSettings settings)
    {
        using Bitmap bitmap = BarcodeRenderer.Render(text, settings);
        LabelPrinter.Print(bitmap, printer, settings);
    }

    private void ApplyBatchEvent(BatchEvent e, IReadOnlyList<string> texts)
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
            string text = e.Row >= 0 && e.Row < texts.Count ? texts[e.Row] : string.Empty;
            SetStatus(BarcodeBatchLogic.BuildProgressText(e.Position, e.TotalRows, e.Copy, e.Copies, text), BatchTone.Info);
            EnsureRowVisible(e.Row);
        }
    }

    // Le journal ne contient que des comptes : jamais les textes des codes-barres.
    private static void LogOutcome(BatchOutcome outcome, string printer)
    {
        string counts = outcome.RowsDone + " / " + outcome.TotalRows + " code(s)-barres, " + outcome.SheetsDone + " / "
                        + outcome.TotalSheets + " impression(s)";
        switch (outcome.End)
        {
            case BatchEnd.Completed:
                Log.Info("Série de codes-barres terminée sur « " + printer + " » : " + counts + ".");
                break;
            case BatchEnd.Failed:
                Log.Error("Série de codes-barres interrompue sur « " + printer + " » à la ligne " + ((outcome.ResumeRow ?? -1) + 1) + " : " + counts
                          + ". " + outcome.Error);
                break;
            case BatchEnd.TimedOut:
                Log.Error("Série de codes-barres interrompue sur « " + printer + " » : pas de confirmation de Windows à la ligne "
                          + ((outcome.ResumeRow ?? -1) + 1) + " après " + PrintTimeoutMs / 1000 + " s (" + counts + ").");
                break;
            default:
                Log.Info("Série de codes-barres " + (outcome.End == BatchEnd.Paused ? "en pause" : "arrêtée") + " sur « " + printer + " » : " + counts + ".");
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
    /// Un envoi n'a pas répondu à temps et tourne encore en arrière-plan : toute nouvelle impression est bloquée jusqu'à sa fin
    /// (sinon chaque clic ajouterait un envoi qui sortirait en double plus tard).
    /// </summary>
    private void TrackOrphan(Task orphan, string printer)
    {
        _latePending = true;
        UpdateButtons();
        _ = orphan.ContinueWith(t => OnLateFinished(printer, t), TaskScheduler.Default);
    }

    private void OnLateFinished(string printer, Task finished)
    {
        Exception? error = finished.Exception?.InnerException ?? finished.Exception;
        if (error != null)
        {
            Log.Error("Série de codes-barres : envoi tardif en échec (imprimante « " + printer + " »).", error);
        }
        else
        {
            Log.Info("Série de codes-barres : envoi tardif terminé (imprimante « " + printer + " »).");
        }

        try
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(new Action(() => OnLateUiUpdate(printer, error)));
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

    private void OnLateUiUpdate(string printer, Exception? error)
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

        SetStatus(BatchLogic.BuildLateFinishedMessage(true, printer, error?.Message, out BatchTone tone), tone);
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
        }
        catch (Exception ex)
        {
            Log.Error("Mémorisation de l'imprimante impossible.", ex);
        }
    }

    /// <summary>
    /// La case « Confirmer avant d'imprimer » suit le réglage <c>BatchConfirm</c>, partagé avec la fenêtre des étiquettes en série :
    /// il a pu être changé là-bas depuis la dernière ouverture de cette fenêtre-ci.
    /// </summary>
    private void SyncConfirmFromSettings()
    {
        if (_chkConfirm.Checked != _settings.BatchConfirm)
        {
            _chkConfirm.Checked = _settings.BatchConfirm;
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
    /// (<paramref name="preferSaved"/>) on reprend l'imprimante mémorisée (elle a pu être changée dans une autre fenêtre) ;
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
