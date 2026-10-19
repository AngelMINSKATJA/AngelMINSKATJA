using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Fenêtre « Étiquettes en série » (BatchForm) : création, collage d'un tableau, liste éditable, aperçu, validation avant
/// impression, masquage à la fermeture, ouverture depuis l'icône et depuis la fenêtre principale. Aucun de ces tests
/// n'imprime : ils s'arrêtent avant tout envoi à une imprimante. Windows uniquement (WinForms, GDI+) ; la logique pure est
/// testée dans BatchFormTests (aussi sous Linux).
/// </summary>
public class BatchFormSmokeTests
{
    private static BatchForm NewForm() => new(new AppSettings());

    private static void RequireDesktop()
        => Skip.IfNot(Environment.UserInteractive, "Session sans bureau interactif : pas de fenêtre possible.");

    // ---------------------------------------------------------------- liste et aperçu

    [SkippableFact]
    public void PastingTheReferenceTable_FillsTheList_TheSummaryAndThePreview()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            Assert.Equal("Étiquettes en série — plans de châssis — POG's Industries Labels v3", form.Text);
            Assert.True(form.MinimumSize.Width >= 760 && form.MinimumSize.Height >= 560);
            Assert.Equal(0, form.LabelCount);
            Assert.Equal(BatchLogic.NoLabelsText, form.SummaryText);
            Assert.Null(form.PreviewImage);

            PlanTableParseResult result = form.LoadTable(PlanTableFixture.BuildExcelText());

            Assert.Null(result.Error);
            Assert.Equal(PlanTableFixture.ExpectedItems.Count, form.LabelCount);
            Assert.Equal(PlanTableFixture.ExpectedItems, form.GetItems());
            Assert.StartsWith(PlanTableFixture.ExpectedItems.Count + " étiquettes — HB1 : ", form.SummaryText);
            Assert.Contains("VF2 : ", form.SummaryText);
            Assert.Contains("étiquettes lues", form.StatusText);

            // N° affiché = rang dans la liste (calculé, il suit les suppressions) ; colonne État vide au départ.
            Assert.Equal("3", form.Grid.Rows[2].Cells[0].FormattedValue);
            Assert.Equal(string.Empty, Convert.ToString(form.Grid.Rows[2].Cells[3].Value));

            // Aperçu de la première ligne : page 62 x 40 mm.
            Assert.NotNull(form.PreviewImage);
            Assert.Equal(PlanLabelRenderer.PageSizePx(LabelPrinter.MmToUnits(62), 200), form.PreviewImage!.Width);
            Assert.Equal(PlanLabelRenderer.PageSizePx(LabelPrinter.MmToUnits(40), 200), form.PreviewImage.Height);
            Assert.Contains("Ligne 1", form.PreviewNoteText);
        });
    }

    [SkippableFact]
    public void ABadPaste_KeepsTheCurrentList_AndExplainsWhy()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\tVF1 (24)\r\n2020Q7E 22061\tX\t\r\n2020Q7E 22148\t\tX\r\n");
            Assert.Equal(2, form.LabelCount);

            PlanTableParseResult empty = form.LoadTable("   ");
            Assert.NotNull(empty.Error);
            Assert.Equal(2, form.LabelCount);
            Assert.Equal(empty.Error, form.StatusText);

            PlanTableParseResult noCross = form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\t\r\n");
            Assert.NotNull(noCross.Error);
            Assert.Equal(2, form.LabelCount);
            Assert.StartsWith("2 étiquettes — HB1 : 1, VF1 : 1", form.SummaryText);
        });
    }

    [SkippableFact]
    public void ANewPaste_ReplacesTheListWithoutAsking()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable(PlanTableFixture.BuildExcelText());
            form.SetRowState(0, RowState.Done);

            form.LoadTable("Plan SE\tVF2 (36)\r\n2020Q7E 22170\tX\r\n");

            Assert.Equal(1, form.LabelCount);
            Assert.Equal(new[] { new LabelItem("VF2", "22170") }, form.GetItems());
            Assert.Equal(RowState.Pending, form.GetRowState(0));
            Assert.Equal("1 étiquette — VF2 : 1", form.SummaryText);
        });
    }

    [SkippableFact]
    public void EditingACell_RestartsTheRow_AndRefreshesSummaryAndPreview()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n");
            form.SetRowState(0, RowState.Done);
            Assert.Equal("✔ imprimée", Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
            Assert.Equal(RowState.Done, form.GetRowState(0));

            form.Grid.Rows[0].Cells[1].Value = "HB9";

            Assert.Equal(RowState.Pending, form.GetRowState(0));
            Assert.Equal(string.Empty, Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
            Assert.Equal("2 étiquettes — HB9 : 1, HB1 : 1", form.SummaryText);
            Assert.Equal(new LabelItem("HB9", "22061"), form.GetItems()[0]);
        });
    }

    [SkippableFact]
    public void EditingARowToAnInvalidValue_ShowsTheProblemInsteadOfThePreview()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n");
            Assert.NotNull(form.PreviewImage);

            form.Grid.Rows[0].Cells[2].Value = "   ";

            Assert.Null(form.PreviewImage);
            Assert.Contains("vide", form.PreviewNoteText);
        });
    }

    [SkippableFact]
    public void DeletingRows_UpdatesTheSummaryAndTheNumbers()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\tHB2 (8)\r\n2020Q7E 22061\tX\t\r\n2020Q7E 22062\tX\t\r\n2020Q7E 22063\t\tX\r\n");
            Assert.Equal("3 étiquettes — HB1 : 2, HB2 : 1", form.SummaryText);

            form.Grid.Rows.RemoveAt(0);

            Assert.Equal(2, form.LabelCount);
            Assert.Equal("2 étiquettes — HB1 : 1, HB2 : 1", form.SummaryText);
            Assert.Equal("1", form.Grid.Rows[0].Cells[0].FormattedValue);
            Assert.Equal(new LabelItem("HB1", "22062"), form.GetItems()[0]);
        });
    }

    // ---------------------------------------------------------------- avant l'impression (rien n'est envoyé)

    [SkippableFact]
    public void PrintingWithABadRow_StopsBeforeAnyPrinter_AndSelectsTheRow()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n2020Q7E 22063\tX\r\n");
            form.Grid.Rows[2].Cells[2].Value = string.Empty;

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.False(form.IsPrinting);
            Assert.StartsWith("Ligne 3 : ", form.StatusText);
            Assert.Contains("N° de plan", form.StatusText);
            Assert.Equal(2, form.Grid.CurrentCell?.RowIndex);
            Assert.All(Enumerable.Range(0, 3), row => Assert.Equal(RowState.Pending, form.GetRowState(row)));
        });
    }

    [SkippableFact]
    public void PrintingNothing_SaysSo()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();
            Assert.Equal(BatchLogic.NothingToPrintMessage(PrintMode.All), form.StatusText);

            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n");
            form.Grid.ClearSelection();
            form.PrintAsync(PrintMode.Selection).GetAwaiter().GetResult();
            Assert.Equal(BatchLogic.NothingToPrintMessage(PrintMode.Selection), form.StatusText);
            form.PrintAsync(PrintMode.FromSelection).GetAwaiter().GetResult();
            Assert.Equal(BatchLogic.NothingToPrintMessage(PrintMode.FromSelection), form.StatusText);
            Assert.False(form.IsPrinting);
        });
    }

    [SkippableFact]
    public void PrintingWithoutAPrinter_SendsNothing()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm(); // la liste des imprimantes n'est lue qu'à l'affichage : aucune sélection ici
            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n");
            Assert.Null(form.SelectedPrinter);

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.Equal("Aucune imprimante sélectionnée.", form.StatusText);
            Assert.False(form.IsPrinting);
            Assert.Equal(RowState.Pending, form.GetRowState(0));
        });
    }

    // ---------------------------------------------------------------- colonnes N° et État jamais modifiables

    private static void SetPrinting(BatchForm form, bool printing)
    {
        typeof(BatchForm).GetField("_printing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, printing);
        typeof(BatchForm).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    }

    private static void AssertReadOnly(DataGridView grid, bool typeAndPlan)
    {
        Assert.False(grid.ReadOnly); // le réglage du tableau entier effacerait celui des colonnes N° et État
        Assert.True(grid.Columns[0].ReadOnly, "la colonne N° doit rester en lecture seule");
        Assert.True(grid.Columns[3].ReadOnly, "la colonne État doit rester en lecture seule");
        Assert.Equal(typeAndPlan, grid.Columns[1].ReadOnly);
        Assert.Equal(typeAndPlan, grid.Columns[2].ReadOnly);
        Assert.True(grid.Rows[0].Cells[0].ReadOnly);
        Assert.True(grid.Rows[0].Cells[3].ReadOnly);
        Assert.Equal(typeAndPlan, grid.Rows[0].Cells[1].ReadOnly);
        Assert.Equal(typeAndPlan, grid.Rows[0].Cells[2].ReadOnly);
    }

    [SkippableFact]
    public void NumberAndStateColumns_StayReadOnly_AfterEveryPrintRun()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.LoadTable("Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n");
            AssertReadOnly(form.Grid, typeAndPlan: false);

            for (int run = 0; run < 3; run++)
            {
                SetPrinting(form, true);   // début d'une série : plus rien ne se modifie
                AssertReadOnly(form.Grid, typeAndPlan: true);
                Assert.False(form.Grid.AllowUserToDeleteRows);

                SetPrinting(form, false);  // fin de la série : Type et N° de plan redeviennent modifiables, pas N° ni État
                AssertReadOnly(form.Grid, typeAndPlan: false);
                Assert.True(form.Grid.AllowUserToDeleteRows);
            }
        });
    }

    // ---------------------------------------------------------------- « Imprimer tout » après un arrêt : pas de doublon silencieux

    private static void SelectFakePrinter(BatchForm form)
    {
        var combo = Field<ComboBox>(form, "_cmbPrinter");
        combo.Items.Add("Imprimante de test");
        combo.SelectedIndex = 0;
    }

    private const string ThreeLabels = "Plan SE\tHB1 (20)\r\n2020Q7E 22061\tX\r\n2020Q7E 22062\tX\r\n2020Q7E 22063\tX\r\n";

    [SkippableFact]
    public void PrintAllWithSomeRowsAlreadyDone_AsksFirst_AndCancelLeavesTheMarkersUntouched()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            // Même avec « Confirmer avant d'imprimer » décoché : c'est un vrai risque de doublon.
            using BatchForm form = new(new AppSettings { BatchConfirm = false });
            SelectFakePrinter(form);
            form.LoadTable(ThreeLabels);
            form.SetRowState(0, RowState.Done);
            form.SetRowState(1, RowState.Done);

            string? asked = null;
            MessageBoxButtons? buttons = null;
            form.PromptHook = (text, b) =>
            {
                asked = text;
                buttons = b;
                return DialogResult.Cancel;
            };

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.NotNull(asked);
            Assert.Contains("2 étiquettes sont déjà marquées « ✔ imprimée »", asked);
            Assert.Equal(MessageBoxButtons.YesNoCancel, buttons);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.False(form.IsPrinting);
            Assert.Equal(RowState.Done, form.GetRowState(0));
            Assert.Equal(RowState.Done, form.GetRowState(1));
            Assert.Equal(RowState.Pending, form.GetRowState(2));
            Assert.Equal("✔ imprimée", Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
        });
    }

    [SkippableFact]
    public void PrintingOnlyRowsThatAreAllDone_AsksAYesNoQuestion_AndCancelSendsNothing()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            SelectFakePrinter(form);
            form.LoadTable(ThreeLabels);
            for (int i = 0; i < 3; i++)
            {
                form.SetRowState(i, RowState.Done);
            }

            MessageBoxButtons? buttons = null;
            string? asked = null;
            form.PromptHook = (text, b) =>
            {
                asked = text;
                buttons = b;
                return DialogResult.Cancel;
            };

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.Equal(MessageBoxButtons.OKCancel, buttons);
            Assert.StartsWith("Ces 3 étiquettes sont déjà marquées", asked);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.All(new[] { 0, 1, 2 }, row => Assert.Equal(RowState.Done, form.GetRowState(row)));
        });
    }

    [SkippableFact]
    public void PrintAllWithNothingDoneYet_StillAsksTheOrdinaryConfirmation()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm(); // BatchConfirm = true par défaut
            SelectFakePrinter(form);
            form.LoadTable(ThreeLabels);

            string? asked = null;
            MessageBoxButtons? buttons = null;
            form.PromptHook = (text, b) =>
            {
                asked = text;
                buttons = b;
                return DialogResult.Cancel;
            };

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.Equal(BatchLogic.BuildConfirmText(3, 1, "Imprimante de test"), asked);
            Assert.Equal(MessageBoxButtons.OKCancel, buttons);
            Assert.Equal("Impression annulée.", form.StatusText);
        });
    }

    // ---------------------------------------------------------------- envoi ou préparation resté sans réponse

    [SkippableFact]
    public void WhileAPaperPreparationIsStillPending_APasteExplainsWhyPrintingIsBlocked()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            var pending = new TaskCompletionSource<bool>();
            try
            {
                typeof(BatchForm).GetMethod("TrackOrphan", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { pending.Task, "Imprimante de test", false });

                form.LoadTable(ThreeLabels);

                Assert.Contains("3 étiquettes lues dans le tableau.", form.StatusText);
                Assert.Contains(BatchLogic.LatePrepareBlockedMessage, form.StatusText);
                Assert.DoesNotContain("cliquez sur « Imprimer tout »", form.StatusText);

                // Et l'impression reste refusée, avec la même explication (rien n'a été envoyé : pas de « file d'impression »).
                form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();
                Assert.Equal(BatchLogic.LatePrepareBlockedMessage, form.StatusText);
                Assert.False(form.IsPrinting);
            }
            finally
            {
                pending.SetResult(true);
            }
        });
    }

    // ---------------------------------------------------------------- fermeture

    [SkippableFact]
    public void ClosingTheWindow_OnlyHidesIt_AndItCanBeShownAgain()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BatchForm form = NewForm();
            form.ShowAndFocus();
            Assert.True(form.Visible);

            form.Close(); // comme la croix ou Alt+F4

            Assert.False(form.IsDisposed);
            Assert.False(form.Visible);

            form.ShowAndFocus();
            Assert.True(form.Visible);
            form.HideWindow();
            Assert.False(form.Visible);
        });
    }

    [SkippableFact]
    public void WhenTheApplicationExits_TheWindowReallyCloses()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            BatchForm form = NewForm();
            form.AllowExit = true;

            form.Close();

            Assert.True(form.IsDisposed);
        });
    }

    // ---------------------------------------------------------------- ouverture depuis l'icône et la fenêtre principale

    [SkippableFact]
    public void TheTrayMenuAndTheMainWindowLink_OpenTheSameBatchWindow()
    {
        RequireDesktop();
        using var sandbox = new SettingsSandbox();

        Exception? error = RunTray(context =>
        {
            Assert.Null(context.BatchWindow); // créée au premier besoin seulement

            // 1) menu de l'icône
            var menu = Field<ContextMenuStrip>(context, "_menu");
            ToolStripMenuItem item = menu.Items.OfType<ToolStripMenuItem>()
                .Single(i => i.Text == "Étiquettes en série (tableau Excel)…");
            item.PerformClick();
            BatchForm first = context.BatchWindow ?? throw new InvalidOperationException("La fenêtre n'a pas été créée par le menu.");
            Assert.True(first.Visible);
            Assert.False(first.IsDisposed);

            first.HideWindow();
            Assert.False(first.Visible);

            // 2) lien de la fenêtre principale : même fenêtre, réaffichée
            MainForm main = context.Window;
            main.ShowAndFocus();
            var link = Field<LinkLabel>(main, "_lnkBatch");
            var status = Field<Label>(main, "_lblStatus");
            Assert.Equal("Étiquettes en série…", link.Text);
            Assert.True(link.Top >= status.Bottom - 1, "le lien doit être sous le message d'état");
            Assert.True(link.Bottom <= main.ClientSize.Height, "le lien doit rester visible dans la fenêtre");

            typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(new LinkLabel.Link()) });
            Assert.Same(first, context.BatchWindow);
            Assert.True(first.Visible);

            // 3) la fenêtre est réutilisée par ShowBatchWindow()
            first.HideWindow();
            context.ShowBatchWindow();
            Assert.Same(first, context.BatchWindow);
            Assert.True(first.Visible);
        });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    // ---------------------------------------------------------------- outils

    private static T Field<T>(object instance, string name) where T : class
    {
        FieldInfo? field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) as T
               ?? throw new InvalidOperationException("Champ introuvable : " + instance.GetType().Name + "." + name);
    }

    /// <summary>
    /// Lance un TrayApplicationContext (démarré masqué) dans une vraie boucle de messages sur un thread STA, exécute
    /// <paramref name="script"/> une fois la boucle démarrée puis arrête la boucle. Renvoie l'exception éventuelle.
    /// </summary>
    private static Exception? RunTray(Action<TrayApplicationContext> script)
    {
        Exception? error = null;
        bool ended = false;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new TrayApplicationContext(new AppSettings(), startHidden: true);
                var starter = new System.Windows.Forms.Timer { Interval = 150 };
                starter.Tick += (_, _) =>
                {
                    starter.Stop();
                    starter.Dispose();
                    try
                    {
                        script(context);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        context.ExitThread();
                    }
                };
                starter.Start();
                Application.Run(context);
                ended = true;
            }
            catch (Exception ex)
            {
                error ??= ex;
            }
        })
        {
            IsBackground = true,
            Name = "Thread STA de test (étiquettes en série)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            return new TimeoutException("La boucle de messages n'a pas terminé en 30 s.");
        }

        return error ?? (ended ? null : new InvalidOperationException("La boucle de messages s'est arrêtée prématurément."));
    }
}
