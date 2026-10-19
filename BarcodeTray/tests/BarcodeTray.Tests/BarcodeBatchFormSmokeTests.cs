using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Fenêtre « Codes-barres en série » (BarcodeBatchForm) : création, chargement d'une liste, tableau en mode virtuel,
/// miniatures dessinées à la demande (cache LRU), lignes invalides, modification et suppression de lignes, validation avant
/// impression, masquage à la fermeture, ouverture depuis l'icône et depuis la fenêtre principale. Aucun de ces tests n'envoie
/// quoi que ce soit à une vraie imprimante : ils s'arrêtent avant tout envoi, ou visent une imprimante qui n'existe pas (échec
/// immédiat). Windows uniquement (WinForms, GDI+) ; la logique pure est testée dans BarcodeBatchLogicTests (aussi sous Linux).
/// </summary>
public class BarcodeBatchFormSmokeTests
{
    private const string NoSuchPrinter = "Imprimante-qui-n-existe-pas-0123456789";

    private static BarcodeBatchForm NewForm(AppSettings? settings = null) => new(settings ?? new AppSettings());

    private static void RequireDesktop()
        => Skip.IfNot(Environment.UserInteractive, "Session sans bureau interactif : pas de fenêtre possible.");

    private static ListReadResult Read(IReadOnlyList<string> values, IReadOnlyList<string>? warnings = null, string? error = null, string source = "test", string? skippedTitle = null)
        => new(values, warnings ?? Array.Empty<string>(), error, source, skippedTitle);

    private static string[] Values(params string[] values) => values;

    private static string Letters(int count) => new('A', count);

    // ---------------------------------------------------------------- création

    [SkippableFact]
    public void TheWindow_StartsEmpty_WithTheExpectedLayout()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            Assert.Equal("Codes-barres en série — POG's Industries Labels v3", form.Text);
            Assert.True(form.MinimumSize.Width >= 760 && form.MinimumSize.Height >= 560);
            Assert.True(form.AllowDrop);
            Assert.Equal(0, form.RowCount);
            Assert.Equal(BarcodeBatchLogic.NoCodesText, form.SummaryText);
            Assert.Equal(BarcodeBatchLogic.HintText, form.StatusText);
            Assert.False(form.FirstLineIsTitle);
            Assert.False(form.IsPrinting);

            DataGridView grid = form.Grid;
            Assert.True(grid.VirtualMode);
            Assert.Equal(0, grid.RowCount);
            Assert.False(grid.AllowUserToAddRows);
            Assert.Equal(new[] { "N°", "Texte du code", "Code-barres", "État" }, grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).ToArray());
            Assert.Equal(new[] { true, false, true, true }, grid.Columns.Cast<DataGridViewColumn>().Select(c => c.ReadOnly).ToArray());

            // Rien à imprimer, rien à effacer ; on peut coller et ouvrir.
            Assert.True(Btn(form, "_btnPaste").Enabled);
            Assert.True(Btn(form, "_btnOpen").Enabled);
            Assert.False(Btn(form, "_btnClear").Enabled);
            Assert.False(Btn(form, "_btnPrintAll").Enabled);
            Assert.False(Btn(form, "_btnPrintSelection").Enabled);
            Assert.False(Btn(form, "_btnPrintFrom").Enabled);
            Assert.False(Btn(form, "_btnStop").Enabled);
            Assert.Equal("Imprimer tous", Btn(form, "_btnPrintAll").Text);
            Assert.Equal("Ouvrir un fichier…", Btn(form, "_btnOpen").Text);
            Assert.Equal("La première ligne est un titre (l'ignorer)", Field<CheckBox>(form, "_chkFirstLine").Text);
        });
    }

    [SkippableFact]
    public void TheRowContextMenu_OffersTheTwoCopyCommands()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            var menu = Field<ContextMenuStrip>(form, "_rowMenu");

            Assert.Same(menu, form.Grid.ContextMenuStrip);
            Assert.Equal(
                new[] { "Copier le code-barres (image)", "Copier le texte" },
                menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray());
        });
    }

    // ---------------------------------------------------------------- chargement de la liste

    [SkippableFact]
    public void LoadingAList_FillsTheGrid_TheSummary_AndTheStatus()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            form.LoadResult(Read(Values("A1", "B2", "C3")));

            Assert.Equal(3, form.RowCount);
            Assert.Equal(3, form.Grid.RowCount);
            Assert.Equal(new[] { "A1", "B2", "C3" }, form.GetTexts());
            Assert.Equal("3 codes-barres — tous valides", form.SummaryText);
            Assert.Equal("3 codes lus (test). Vérifiez la liste, puis cliquez sur « Imprimer tous ».", form.StatusText);

            // Numéro = rang dans la liste ; texte ; la colonne « Code-barres » est dessinée ; « État » vide au départ.
            Assert.Equal("2", form.Grid.Rows[1].Cells[0].FormattedValue);
            Assert.Equal("B2", form.Grid.Rows[1].Cells[1].Value);
            Assert.Equal(string.Empty, Convert.ToString(form.Grid.Rows[1].Cells[3].Value));
            Assert.All(Enumerable.Range(0, 3), row => Assert.Equal(RowState.Pending, form.GetRowState(row)));

            form.Grid.ClearSelection();
            Assert.True(Btn(form, "_btnPrintAll").Enabled);
            Assert.True(Btn(form, "_btnClear").Enabled);
            Assert.False(Btn(form, "_btnPrintSelection").Enabled); // rien n'est sélectionné
        });
    }

    [SkippableFact]
    public void ANewList_ReplacesTheCurrentOne_WithoutAsking_AndForgetsTheStates()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2", "C3")));
            form.SetRowState(0, RowState.Done);

            form.LoadResult(Read(Values("X9")));

            Assert.Equal(1, form.RowCount);
            Assert.Equal(1, form.Grid.RowCount);
            Assert.Equal(new[] { "X9" }, form.GetTexts());
            Assert.Equal(RowState.Pending, form.GetRowState(0));
            Assert.Equal("1 code-barres — valide", form.SummaryText);
        });
    }

    [SkippableFact]
    public void ABadLoad_KeepsTheCurrentList_AndExplainsWhy()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));

            form.LoadResult(Read(Array.Empty<string>(), error: "Le fichier est vide."));
            Assert.Equal(2, form.RowCount);
            Assert.Equal("Le fichier est vide.", form.StatusText);

            form.LoadResult(Read(Array.Empty<string>()));
            Assert.Equal(2, form.RowCount);
            Assert.Equal("Aucune valeur trouvée dans la liste.", form.StatusText);

            form.LoadResult(null);
            Assert.Equal(2, form.RowCount);
            Assert.Equal("La liste n'a pas pu être lue.", form.StatusText);
            Assert.Equal("2 codes-barres — tous valides", form.SummaryText);
        });
    }

    [SkippableFact]
    public void ReaderWarnings_AndInvalidRows_ShowInTheStatus()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            form.LoadResult(Read(Values("A1", "Réf 2"), new[] { "1 ligne vide ignorée." }, source: "fichier « a.txt »"));

            Assert.Contains("2 codes lus (fichier « a.txt »).", form.StatusText);
            Assert.Contains("1 ligne est à corriger (en rouge)", form.StatusText);
            Assert.Contains("1 ligne vide ignorée.", form.StatusText);
            Assert.Equal("2 codes-barres — 1 invalide", form.SummaryText);
        });
    }

    [SkippableFact]
    public void Clearing_EmptiesTheList_AndTheButtons()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));
            _ = form.ThumbnailFor(0);
            Assert.Equal(1, form.ThumbnailCount);

            Click(Btn(form, "_btnClear"));

            Assert.Equal(0, form.RowCount);
            Assert.Equal(0, form.Grid.RowCount);
            Assert.Equal(0, form.ThumbnailCount);
            Assert.Equal(BarcodeBatchLogic.NoCodesText, form.SummaryText);
            Assert.Equal(BarcodeBatchLogic.HintText, form.StatusText);
            Assert.False(Btn(form, "_btnPrintAll").Enabled);
        });
    }

    [SkippableFact]
    public void FiveThousandRows_LoadFast_WithoutDrawingAnyThumbnail()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            string[] values = Enumerable.Range(0, 5000).Select(i => "ART-" + (100000 + i).ToString("D7")).ToArray();

            var watch = Stopwatch.StartNew();
            form.LoadResult(Read(values));
            watch.Stop();

            Assert.Equal(5000, form.RowCount);
            Assert.Equal(5000, form.Grid.RowCount);
            Assert.Equal(0, form.ThumbnailCount); // les miniatures sont dessinées à l'affichage, pas au chargement
            Assert.StartsWith("5000 codes-barres — tous valides", form.SummaryText);
            Assert.True(watch.ElapsedMilliseconds < 3000, "Le chargement de 5000 lignes a pris " + watch.ElapsedMilliseconds + " ms.");

            // Sélectionner toutes les lignes reste rapide (pas d'objet par ligne).
            watch.Restart();
            form.Grid.SelectAll();
            int selected = form.Grid.Rows.GetRowCount(DataGridViewElementStates.Selected);
            watch.Stop();
            Assert.Equal(5000, selected);
            Assert.True(watch.ElapsedMilliseconds < 3000, "La sélection de 5000 lignes a pris " + watch.ElapsedMilliseconds + " ms.");
            Assert.True(Btn(form, "_btnPrintSelection").Enabled);
            Assert.True(Btn(form, "_btnPrintFrom").Enabled);
        });
    }

    [SkippableFact]
    public void TheRowNumbers_FollowTheListInVirtualMode()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Enumerable.Range(1, 120).Select(i => "C" + i).ToArray()));

            Assert.Equal("1", form.Grid.Rows[0].Cells[0].FormattedValue);
            Assert.Equal("120", form.Grid.Rows[119].Cells[0].FormattedValue);
            Assert.Equal("C77", form.Grid.Rows[76].Cells[1].Value);
        });
    }

    // ---------------------------------------------------------------- lignes invalides, barres fines

    [SkippableFact]
    public void InvalidRows_AreFlagged_WithTheMessage_AndNoThumbnail()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("OK1", "Réf 12", "", Letters(27), Letters(70))));

            Assert.True(form.GetAnalysis(0)!.IsValid);
            Assert.False(form.GetAnalysis(1)!.IsValid);
            Assert.Contains("« é »", form.GetAnalysis(1)!.Error);
            Assert.Equal("Saisissez un texte.", form.GetAnalysis(2)!.Error);
            Assert.True(form.GetAnalysis(3)!.ThinBars);
            Assert.True(form.GetAnalysis(4)!.Scaled);

            Assert.NotNull(form.ThumbnailFor(0));
            Assert.Null(form.ThumbnailFor(1));
            Assert.Null(form.ThumbnailFor(2));
            Assert.NotNull(form.ThumbnailFor(3));
            Assert.NotNull(form.ThumbnailFor(4));

            Assert.Equal("5 codes-barres — 2 invalides, 1 à barres fines, 1 réduit pour tenir", form.SummaryText);
            Assert.Equal(string.Empty, Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
            Assert.Equal("✖ invalide", form.Grid.Rows[1].Cells[3].Value);
            Assert.Equal("✖ invalide", form.Grid.Rows[2].Cells[3].Value);
            Assert.Equal("⚠ barres fines", form.Grid.Rows[3].Cells[3].Value);
            Assert.Equal("⚠ réduit", form.Grid.Rows[4].Cells[3].Value);
        });
    }

    [SkippableFact]
    public void TheAnalysis_AgreesWithTheRealRenderer()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            var settings = new AppSettings();
            string[] texts =
            {
                "A", "ABC-123", Letters(15), Letters(26), Letters(27), Letters(40), Letters(58), Letters(59), Letters(60), Letters(70),
                new string('7', 52), new string('7', 116), new string('7', 120), "a b c 1 2 3 !", "Mixte-ABC-def-0123456789",
            };

            foreach (string text in texts)
            {
                BarcodeRowAnalysis a = BarcodeBatchLogic.AnalyzeRow(text, settings.ModulePx, settings.MaxWidthPx);
                using Bitmap bitmap = BarcodeRenderer.Render(text, settings, out int modulePx);

                Assert.True(a.IsValid);
                Assert.Equal(modulePx, a.ModulePx);
                Assert.Equal(bitmap.Width > settings.MaxWidthPx * 1.02, a.Scaled);
                Assert.Equal(BarcodeRenderer.ThinBarsWarning(modulePx, settings.ModulePx) != null, a.ThinBars);
            }
        });
    }

    [SkippableFact]
    public void TheAnalysis_FollowsTheModulePxSetting()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm(new AppSettings { ModulePx = 1 });

            form.LoadResult(Read(Values(Letters(40))));

            Assert.False(form.GetAnalysis(0)!.ThinBars); // 1 point était déjà le choix de l'utilisateur
            Assert.Equal("1 code-barres — valide", form.SummaryText);
        });
    }

    // ---------------------------------------------------------------- miniatures

    [SkippableFact]
    public void Thumbnails_ShowTheRealBarcode_ScaledToTheCell()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("ABC-123456")));

            Bitmap thumbnail = form.ThumbnailFor(0) ?? throw new InvalidOperationException("Pas de miniature.");
            using Bitmap real = BarcodeRenderer.Render("ABC-123456", new AppSettings());

            // Mêmes proportions que l'image réellement imprimée, plus petite qu'elle, et du noir sur du blanc.
            double ratioThumb = (double)thumbnail.Width / thumbnail.Height;
            double ratioReal = (double)real.Width / real.Height;
            Assert.InRange(ratioThumb / ratioReal, 0.97, 1.03);
            Assert.True(thumbnail.Width < real.Width && thumbnail.Height < real.Height);
            Assert.True(thumbnail.Height >= 40, "La miniature doit rester lisible (hauteur " + thumbnail.Height + ").");

            int dark = 0;
            int light = 0;
            for (int y = 0; y < thumbnail.Height; y += 2)
            {
                for (int x = 0; x < thumbnail.Width; x += 2)
                {
                    Color c = thumbnail.GetPixel(x, y);
                    if (c.R < 128 && c.G < 128 && c.B < 128)
                    {
                        dark++;
                    }
                    else if (c.R > 200 && c.G > 200 && c.B > 200)
                    {
                        light++;
                    }
                }
            }

            Assert.True(dark > 50, "Pas de barres noires dans la miniature.");
            Assert.True(light > dark, "La miniature devrait être surtout blanche.");
            Assert.Equal(Color.White.ToArgb(), thumbnail.GetPixel(0, 0).ToArgb()); // zone de silence blanche, pas de liseré
        });
    }

    [SkippableFact]
    public void Thumbnails_AreCached_AndTheCacheIsCapped_AndEvictedOnesAreDisposed()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Enumerable.Range(0, 200).Select(i => "THUMB-" + i).ToArray()));
            int capacity = form.ThumbnailCapacity;
            Assert.Equal(BarcodeBatchLogic.ThumbnailCacheCapacity, capacity);

            Bitmap first = form.ThumbnailFor(0)!;
            Assert.Same(first, form.ThumbnailFor(0)); // pas de nouveau dessin pour une ligne déjà vue
            Assert.Equal(1, form.ThumbnailCount);

            for (int row = 1; row < 140; row++)
            {
                Assert.NotNull(form.ThumbnailFor(row));
                Assert.True(form.ThumbnailCount <= capacity);
            }

            Assert.Equal(capacity, form.ThumbnailCount);
            Assert.ThrowsAny<Exception>(() => { _ = first.Width; }); // la plus ancienne a été évincée, donc libérée
            Assert.NotNull(form.ThumbnailFor(139));
            Assert.Equal(capacity, form.ThumbnailCount);
        });
    }

    [SkippableFact]
    public void EditingARow_RedrawsItsThumbnail()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("OLD-1", "OTHER")));
            Bitmap before = form.ThumbnailFor(0)!;

            form.ApplyEdit(0, "NEW-VALUE-2");
            Bitmap after = form.ThumbnailFor(0)!;

            Assert.NotSame(before, after);
            Assert.NotEqual(before.Width, after.Width); // un autre texte, un autre code-barres
            Assert.Equal("NEW-VALUE-2", form.GetTexts()[0]);
        });
    }

    [SkippableFact]
    public void DrawRowBarcode_PaintsTheBarcode_OrTheRedPlaceholderWithTheMessage()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("PAINT-1", "Réf 2")));
            var bounds = new Rectangle(0, 0, 300, 88);

            using (var bitmap = new Bitmap(300, 88))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Gainsboro);
                form.DrawRowBarcode(g, bounds, 0);
                int dark = CountPixels(bitmap, c => c.R < 90 && c.G < 90 && c.B < 90);
                int white = CountPixels(bitmap, c => c.R > 250 && c.G > 250 && c.B > 250);
                Assert.True(dark > 300, "Le code-barres n'est pas dessiné (" + dark + " points noirs).");
                Assert.True(white > dark);
            }

            using (var bitmap = new Bitmap(300, 88))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Gainsboro);
                form.DrawRowBarcode(g, bounds, 1);
                Color inside = bitmap.GetPixel(3, 3);
                int red = CountPixels(bitmap, c => c.R > 150 && c.G < 90 && c.B < 90);
                Assert.True(Math.Abs(inside.R - 253) <= 2 && Math.Abs(inside.G - 236) <= 2 && Math.Abs(inside.B - 234) <= 2,
                    "Le fond d'une ligne invalide devrait être rose pâle (" + inside + ").");
                Assert.True(red > 20, "Le message de la ligne invalide devrait être écrit en rouge.");
            }

            // Une ligne qui n'existe pas ne dessine rien et ne lève rien.
            using (var bitmap = new Bitmap(10, 10))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                form.DrawRowBarcode(g, new Rectangle(0, 0, 10, 10), 99);
            }
        });
    }

    private static int CountPixels(Bitmap bitmap, Func<Color, bool> predicate)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ---------------------------------------------------------------- modification et suppression

    [SkippableFact]
    public void EditingACellInTheGrid_ReanalysesTheRow_ClearsItsState_AndRefreshesTheSummary()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));
            form.SetRowState(0, RowState.Done);
            Assert.Equal("✔ imprimée", Convert.ToString(form.Grid.Rows[0].Cells[3].Value));

            form.Grid.Rows[0].Cells[1].Value = "  Z9  "; // mode virtuel : la saisie arrive par CellValuePushed

            Assert.Equal(RowState.Pending, form.GetRowState(0));
            Assert.Equal(string.Empty, Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
            Assert.Equal("Z9", form.GetTexts()[0]);
            Assert.Equal("Z9", form.Grid.Rows[0].Cells[1].Value);
            Assert.Equal("2 codes-barres — tous valides", form.SummaryText);
        });
    }

    [SkippableFact]
    public void EditingARowToAnInvalidValue_TurnsTheSummaryRed_AndBlocksPrinting()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));

            form.ApplyEdit(1, "Café");

            Assert.False(form.GetAnalysis(1)!.IsValid);
            Assert.Equal("2 codes-barres — 1 invalide", form.SummaryText);
            Assert.Equal("✖ invalide", form.Grid.Rows[1].Cells[3].Value);

            form.ApplyEdit(1, "Cafe");
            Assert.True(form.GetAnalysis(1)!.IsValid);
            Assert.Equal("2 codes-barres — tous valides", form.SummaryText);
        });
    }

    [SkippableFact]
    public void AnEditThatChangesNothing_KeepsTheState()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1")));
            form.SetRowState(0, RowState.Done);

            form.ApplyEdit(0, " A1 ");
            form.ApplyEdit(7, "ignoré");

            Assert.Equal(RowState.Done, form.GetRowState(0));
        });
    }

    [SkippableFact]
    public void TheGridIsReadOnly_ExceptTheTextColumn_AndNothingChangesWhilePrinting()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));
            Assert.False(form.Grid.ReadOnly);
            Assert.False(form.Grid.Columns[1].ReadOnly);

            SetPrinting(form, true);
            Assert.True(form.Grid.Columns[1].ReadOnly);
            Assert.True(form.Grid.Columns[0].ReadOnly && form.Grid.Columns[2].ReadOnly && form.Grid.Columns[3].ReadOnly);
            Assert.False(Btn(form, "_btnPaste").Enabled);
            Assert.False(Btn(form, "_btnOpen").Enabled);
            Assert.False(Field<CheckBox>(form, "_chkFirstLine").Enabled);
            Assert.False(Btn(form, "_btnClear").Enabled);
            Assert.False(Btn(form, "_btnPrintAll").Enabled);
            Assert.True(Btn(form, "_btnStop").Enabled);
            Assert.Equal(0, form.RemoveRows(new[] { 0 })); // pas de suppression pendant une série

            SetPrinting(form, false);
            Assert.False(form.Grid.Columns[1].ReadOnly);
            Assert.True(form.Grid.Columns[0].ReadOnly && form.Grid.Columns[2].ReadOnly && form.Grid.Columns[3].ReadOnly);
            Assert.True(Btn(form, "_btnPaste").Enabled);
            Assert.True(Btn(form, "_btnPrintAll").Enabled);
            Assert.False(Btn(form, "_btnStop").Enabled);
        });
    }

    [SkippableFact]
    public void RemovingRows_KeepsTheRest_WithTheirStates_AndRefreshesTheSummary()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A", "B", "C", "D", "E", "A")));
            form.SetRowState(3, RowState.Done);
            Assert.Contains("1 doublon", form.SummaryText);

            int removed = form.RemoveRows(new[] { 0, 1, 1, 99, -4 });

            Assert.Equal(2, removed);
            Assert.Equal(4, form.RowCount);
            Assert.Equal(4, form.Grid.RowCount);
            Assert.Equal(new[] { "C", "D", "E", "A" }, form.GetTexts());
            Assert.Equal(RowState.Done, form.GetRowState(1)); // « D » garde son état
            Assert.Equal("1", form.Grid.Rows[0].Cells[0].FormattedValue);
            Assert.Equal("C", form.Grid.Rows[0].Cells[1].Value);
            Assert.Equal("4 codes-barres — tous valides", form.SummaryText);
            Assert.Equal(0, form.RemoveRows(Array.Empty<int>()));
            Assert.Equal(0, form.RemoveRows(new[] { 50 }));
        });
    }

    [SkippableFact]
    public void RemovingEveryRow_LeavesAnEmptyList()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A", "B", "C")));

            Assert.Equal(3, form.RemoveRows(Enumerable.Range(0, 3)));

            Assert.Equal(0, form.RowCount);
            Assert.Equal(0, form.Grid.RowCount);
            Assert.Equal(BarcodeBatchLogic.NoCodesText, form.SummaryText);
            Assert.False(Btn(form, "_btnPrintAll").Enabled);
        });
    }

    [SkippableFact]
    public void RemovingAThousandRows_OutOfFive_IsFast()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Enumerable.Range(0, 5000).Select(i => "R" + i).ToArray()));

            var watch = Stopwatch.StartNew();
            int removed = form.RemoveRows(Enumerable.Range(0, 5000).Where(i => i % 5 == 0));
            watch.Stop();

            Assert.Equal(1000, removed);
            Assert.Equal(4000, form.RowCount);
            Assert.Equal(4000, form.Grid.RowCount);
            Assert.Equal("R1", form.GetTexts()[0]);
            Assert.True(watch.ElapsedMilliseconds < 3000, "La suppression a pris " + watch.ElapsedMilliseconds + " ms.");
        });
    }

    [SkippableFact]
    public void TheCopyCommands_RefuseAnInvalidRow_AndPutTheTextOnTheClipboard()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("COPY-ME", "Réf 2")));

            Assert.False(form.CopyBarcodeImage(1));
            Assert.StartsWith("Ligne 2 : ", form.StatusText);
            Assert.False(form.CopyBarcodeImage(9));
            Assert.False(form.CopyText(9));

            bool copied = form.CopyBarcodeImage(0);
            Skip.IfNot(copied, "Presse-papier indisponible sur ce poste : " + form.StatusText);
            Assert.StartsWith("Code-barres de la ligne 1 copié dans le presse-papier", form.StatusText);

            IDataObject? data = GetDataObjectWithRetry();
            Skip.If(data == null, "Presse-papier illisible sur ce poste.");
            Assert.True(data!.GetDataPresent(DataFormats.Bitmap));
            Assert.True(data.GetDataPresent("PNG"));
            if (data.GetData(DataFormats.Bitmap) is Bitmap fromClipboard)
            {
                using (fromClipboard)
                {
                    Assert.Equal("COPY-ME", BarcodeDecoding.Decode(fromClipboard));
                }
            }

            bool textCopied = form.CopyText(0);
            Skip.IfNot(textCopied, "Presse-papier indisponible sur ce poste : " + form.StatusText);
            Assert.Equal("COPY-ME", Clipboard.GetText());
        });
    }

    private static IDataObject? GetDataObjectWithRetry()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return Clipboard.GetDataObject();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(100);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- avant l'impression (rien n'est envoyé)

    private static void SelectFakePrinter(BarcodeBatchForm form, string name = "Imprimante de test")
    {
        var combo = Field<ComboBox>(form, "_cmbPrinter");
        combo.Items.Add(name);
        combo.SelectedIndex = combo.Items.Count - 1;
    }

    [SkippableFact]
    public void PrintingWithABadRow_StopsBeforeAnyPrinter_AndSelectsTheRow()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            SelectFakePrinter(form);
            form.LoadResult(Read(Values("A1", "B2", "Réf 3", "D4")));
            bool asked = false;
            form.PromptHook = (_, _) =>
            {
                asked = true;
                return DialogResult.OK;
            };

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.False(form.IsPrinting);
            Assert.StartsWith("Ligne 3 : ", form.StatusText);
            Assert.Contains("« é »", form.StatusText);
            Assert.Equal(2, form.Grid.CurrentCell?.RowIndex);
            Assert.False(asked); // la validation passe avant toute question
            Assert.All(Enumerable.Range(0, 4), row => Assert.Equal(RowState.Pending, form.GetRowState(row)));
        });
    }

    [SkippableFact]
    public void PrintingTheSelection_ChecksOnlyTheSelectedRows()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            SelectFakePrinter(form);
            form.LoadResult(Read(Values("A1", "Réf 2", "C3")));
            string? asked = null;
            form.PromptHook = (text, _) =>
            {
                asked = text;
                return DialogResult.Cancel;
            };

            // La ligne invalide est dans la sélection : on s'arrête sur elle.
            form.Grid.ClearSelection();
            form.Grid.Rows[1].Selected = true;
            form.PrintAsync(PrintMode.Selection).GetAwaiter().GetResult();
            Assert.StartsWith("Ligne 2 : ", form.StatusText);
            Assert.Null(asked);

            // Elle n'y est pas : la validation passe, et la confirmation est posée pour les deux autres lignes seulement.
            form.Grid.ClearSelection();
            form.Grid.Rows[0].Selected = true;
            form.Grid.Rows[2].Selected = true;
            form.PrintAsync(PrintMode.Selection).GetAwaiter().GetResult();
            Assert.Equal(BarcodeBatchLogic.BuildConfirmText(2, 1, "Imprimante de test"), asked);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.False(form.IsPrinting);
        });
    }

    [SkippableFact]
    public void PrintingNothing_SaysSo()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();
            Assert.Equal(BarcodeBatchLogic.NothingToPrintMessage(PrintMode.All), form.StatusText);

            form.LoadResult(Read(Values("A1")));
            form.Grid.ClearSelection();
            form.PrintAsync(PrintMode.Selection).GetAwaiter().GetResult();
            Assert.Equal(BarcodeBatchLogic.NothingToPrintMessage(PrintMode.Selection), form.StatusText);
            form.PrintAsync(PrintMode.FromSelection).GetAwaiter().GetResult();
            Assert.Equal(BarcodeBatchLogic.NothingToPrintMessage(PrintMode.FromSelection), form.StatusText);
            Assert.False(form.IsPrinting);
        });
    }

    [SkippableFact]
    public void PrintingWithoutAPrinter_SendsNothing()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm(); // la liste des imprimantes n'est lue qu'à l'affichage : aucune sélection ici
            form.LoadResult(Read(Values("A1")));
            Assert.Null(form.SelectedPrinter);

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.Equal("Aucune imprimante sélectionnée.", form.StatusText);
            Assert.False(form.IsPrinting);
            Assert.Equal(RowState.Pending, form.GetRowState(0));
        });
    }

    [SkippableFact]
    public void TheConfirmationQuestion_NamesTheCountAndThePrinter_AndCancelSendsNothing()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm(); // BatchConfirm = true par défaut
            SelectFakePrinter(form);
            form.LoadResult(Read(Values("A1", "B2", "C3")));
            string? asked = null;
            MessageBoxButtons? buttons = null;
            form.PromptHook = (text, b) =>
            {
                asked = text;
                buttons = b;
                return DialogResult.Cancel;
            };

            form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

            Assert.Equal(BarcodeBatchLogic.BuildConfirmText(3, 1, "Imprimante de test"), asked);
            Assert.Equal("Imprimer 3 codes-barres sur « Imprimante de test » ?", asked);
            Assert.Equal(MessageBoxButtons.OKCancel, buttons);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.False(form.IsPrinting);
        });
    }

    [SkippableFact]
    public void PrintAllWithSomeRowsAlreadyDone_AsksFirst_AndCancelLeavesTheMarkersUntouched()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            // Même avec « Confirmer avant d'imprimer » décoché : c'est un vrai risque de doublon.
            using BarcodeBatchForm form = NewForm(new AppSettings { BatchConfirm = false });
            SelectFakePrinter(form);
            form.LoadResult(Read(Values("A1", "B2", "C3")));
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
            Assert.Contains("2 codes-barres sont déjà marqués « ✔ imprimée »", asked);
            Assert.Equal(MessageBoxButtons.YesNoCancel, buttons);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.Equal(RowState.Done, form.GetRowState(0));
            Assert.Equal(RowState.Done, form.GetRowState(1));
            Assert.Equal(RowState.Pending, form.GetRowState(2));
            Assert.Equal("✔ imprimée", Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
        });
    }

    [SkippableFact]
    public void PrintingOnlyRowsThatAreAllDone_AsksAnOkCancelQuestion_AndCancelSendsNothing()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            SelectFakePrinter(form);
            form.LoadResult(Read(Values("A1", "B2", "C3")));
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
            Assert.StartsWith("Ces 3 codes-barres sont déjà marqués", asked);
            Assert.Equal("Impression annulée.", form.StatusText);
            Assert.All(new[] { 0, 1, 2 }, row => Assert.Equal(RowState.Done, form.GetRowState(row)));
        });
    }

    [SkippableFact]
    public void WhileASendIsStillPending_ALoadExplainsWhyPrintingIsBlocked()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            var pending = new TaskCompletionSource<bool>();
            try
            {
                typeof(BarcodeBatchForm).GetMethod("TrackOrphan", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { pending.Task, "Imprimante de test" });

                form.LoadResult(Read(Values("A1", "B2")));

                Assert.Contains("2 codes lus (test).", form.StatusText);
                Assert.Contains(BatchLogic.LateSendBlockedMessage, form.StatusText);
                Assert.DoesNotContain("cliquez sur « Imprimer tous »", form.StatusText);
                Assert.False(Btn(form, "_btnPrintAll").Enabled);

                // Et l'impression reste refusée, avec la même explication.
                form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();
                Assert.Equal(BatchLogic.LateSendBlockedMessage, form.StatusText);
                Assert.False(form.IsPrinting);
            }
            finally
            {
                pending.SetResult(true);
            }
        });
    }

    // ---------------------------------------------------------------- une série complète (échec immédiat, aucune imprimante)

    [SkippableFact]
    public void ASeriesToAMissingPrinter_StopsOnTheFirstRow_WithAFrenchMessage_AndNeverLogsTheTexts()
    {
        RequireDesktop();
        string marker = "SECRET" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();

        Exception? error = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                SelectFakePrinter(form, NoSuchPrinter);
                form.LoadResult(Read(Values(marker + "-1", marker + "-2", marker + "-3")));
                form.PromptHook = (_, _) => DialogResult.OK;

                await form.PrintAsync(PrintMode.All);

                Assert.False(form.IsPrinting);
                Assert.Equal(RowState.Failed, form.GetRowState(0));
                Assert.Equal(RowState.Pending, form.GetRowState(1));
                Assert.Equal(RowState.Pending, form.GetRowState(2));
                Assert.StartsWith("Échec à la ligne 1 (" + marker + "-1) : ", form.StatusText);
                Assert.Contains("0 sur 3 codes-barres envoyés", form.StatusText);
                Assert.Contains("sélectionnez la ligne 1 et cliquez sur « Imprimer à partir de la sélection »", form.StatusText);
                Assert.Equal("✖ échec", Convert.ToString(form.Grid.Rows[0].Cells[3].Value));
                Assert.Equal(0, form.Grid.CurrentCell?.RowIndex); // la ligne en échec est sélectionnée pour la reprise
                Assert.True(Btn(form, "_btnPrintAll").Enabled);
                Assert.True(Btn(form, "_btnPaste").Enabled);
                Assert.False(Btn(form, "_btnStop").Enabled);
            });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        // Vie privée : le journal garde des comptes, jamais les textes des codes-barres.
        Assert.DoesNotContain(marker, ReadLogs(), StringComparison.Ordinal);
    }

    private static string ReadLogs()
    {
        string path = Log.FilePath;
        var sb = new StringBuilder();
        foreach (string file in new[] { path + ".1", path })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            sb.Append(reader.ReadToEnd());
        }

        return sb.ToString();
    }

    [SkippableFact]
    public void LoadingAList_NeverLogsTheTexts()
    {
        RequireDesktop();
        string marker = "PRIVE" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values(marker + "-A", marker + "-B", "Réf " + marker), new[] { "Remarque." }));
            _ = form.ThumbnailFor(0);
            form.ApplyEdit(1, marker + "-EDITE");
            form.RemoveRows(new[] { 0 });
            form.CopyText(0);
        });

        Assert.DoesNotContain(marker, ReadLogs(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fichiers et glisser-déposer

    [SkippableFact]
    public void DroppingSeveralItems_OrAFolder_ChangesNothing_AndSaysWhy()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1", "B2")));

            form.HandleDropAsync(new[] { "a.txt", "b.txt" }).GetAwaiter().GetResult();
            Assert.Equal("Déposez un seul fichier à la fois (2 éléments déposés).", form.StatusText);
            Assert.Equal(2, form.RowCount);

            string folder = Path.Combine(Path.GetTempPath(), "bbf-drop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                form.HandleDropAsync(new[] { folder }).GetAwaiter().GetResult();
                Assert.Contains("pas un dossier", form.StatusText);
                Assert.Equal(2, form.RowCount);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }

            form.HandleDropAsync(Array.Empty<string>()).GetAwaiter().GetResult();
            Assert.Equal(2, form.RowCount);
        });
    }

    [SkippableFact]
    public void AMissingFile_IsReportedInRed_AndTheListIsKept()
    {
        RequireDesktop();
        Exception? error = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                form.LoadResult(Read(Values("A1", "B2")));

                ListReadResult result = await form.LoadFileAsync(Path.Combine(Path.GetTempPath(), "bbf-absent-" + Guid.NewGuid().ToString("N") + ".txt"));

                Assert.False(string.IsNullOrWhiteSpace(result.Error));
                Assert.Equal(result.Error, form.StatusText);
                Assert.Equal(2, form.RowCount);
                Assert.False(form.UseWaitCursor);
                Assert.True(Btn(form, "_btnPaste").Enabled);
                Assert.True(Btn(form, "_btnOpen").Enabled);
                Assert.True(Btn(form, "_btnPrintAll").Enabled);
                Assert.True(Field<CheckBox>(form, "_chkFirstLine").Enabled);
            });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [SkippableFact]
    public void AFileRefusedOnAnEmptyList_LeavesTheToolbarUsable()
    {
        // Régression : la lecture grise la barre d'outils ; si elle échouait (.xls, fichier endommagé...), elle restait grisée
        // (Coller, Ouvrir...) tant qu'aucune ligne n'était cliquée, ce qui est impossible avec une liste vide.
        RequireDesktop();
        Exception? error = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                Assert.Equal(0, form.RowCount);

                ListReadResult legacy = await form.LoadFileAsync(Path.Combine(Path.GetTempPath(), "bbf-ancien-" + Guid.NewGuid().ToString("N") + ".xls"));

                Assert.False(string.IsNullOrWhiteSpace(legacy.Error));
                Assert.Equal(legacy.Error, form.StatusText);
                Assert.Equal(0, form.RowCount);
                Assert.False(form.UseWaitCursor);
                Assert.True(Btn(form, "_btnPaste").Enabled);
                Assert.True(Btn(form, "_btnOpen").Enabled);
                Assert.True(Field<CheckBox>(form, "_chkFirstLine").Enabled);
                Assert.True(Field<NumericUpDown>(form, "_numCopies").Enabled);
                Assert.True(Field<ComboBox>(form, "_cmbPrinter").Enabled);
                Assert.True(Field<CheckBox>(form, "_chkConfirm").Enabled);
                Assert.False(Btn(form, "_btnPrintAll").Enabled); // rien à imprimer
                Assert.False(Btn(form, "_btnStop").Enabled);

                // Et un fichier vide (erreur « Aucune valeur trouvée ») ne grise rien non plus.
                string empty = Path.Combine(Path.GetTempPath(), "bbf-vide-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(empty, "\r\n\r\n");
                try
                {
                    ListReadResult nothing = await form.LoadFileAsync(empty);
                    Assert.False(string.IsNullOrWhiteSpace(nothing.Error));
                    Assert.True(Btn(form, "_btnPaste").Enabled);
                    Assert.True(Btn(form, "_btnOpen").Enabled);
                }
                finally
                {
                    File.Delete(empty);
                }
            });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [SkippableFact]
    public void ATextFile_IsLoadedThroughTheRealReader()
    {
        RequireDesktop();
        string folder = Path.Combine(Path.GetTempPath(), "bbf-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "codes.txt");
        File.WriteAllText(file, "Référence\r\nAAA-1\r\nBBB-2\r\n\r\nCCC-3\r\n", new UTF8Encoding(false));
        try
        {
            Exception? error = RunWithMessageLoop(
                () => NewForm(),
                async form =>
                {
                    form.FirstLineIsTitle = true;

                    ListReadResult result = await form.LoadFileAsync(file);

                    Assert.Null(result.Error);
                    Assert.Equal(new[] { "AAA-1", "BBB-2", "CCC-3" }, form.GetTexts());
                    Assert.StartsWith("3 codes lus", form.StatusText);
                    Assert.Contains("codes.txt", form.StatusText);
                    Assert.Equal("3 codes-barres — tous valides", form.SummaryText);
                    Assert.False(form.UseWaitCursor);
                });

            Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
            if (error != null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [SkippableFact]
    public void PastedText_IsLoadedThroughTheRealReader_AndTheTitleCheckboxApplies()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            ListReadResult plain = form.LoadText("Titre\r\nAAA-1\r\nBBB-2\r\n");
            Assert.Null(plain.Error);
            Assert.Equal(new[] { "Titre", "AAA-1", "BBB-2" }, form.GetTexts());

            form.FirstLineIsTitle = true;
            ListReadResult withoutTitle = form.LoadText("Titre\r\nAAA-1\r\nBBB-2\r\n");
            Assert.Null(withoutTitle.Error);
            Assert.Equal(new[] { "AAA-1", "BBB-2" }, form.GetTexts());
            Assert.Contains("presse-papiers", form.StatusText);
            Assert.Contains("Titre ignoré : « Titre »", form.StatusText); // l'écran dit QUELLE ligne a disparu

            ListReadResult empty = form.LoadText("   ");
            Assert.NotNull(empty.Error);
            Assert.Equal(2, form.RowCount); // la liste précédente est conservée
            Assert.Equal(empty.Error, form.StatusText);
        });
    }

    [SkippableFact]
    public void TheTitleCheckbox_AppliesToOneLoadOnly_SoANextListWithoutTitleKeepsItsFirstCode()
    {
        // Régression : la case restait cochée entre deux chargements et retirait en silence le premier code d'une liste sans titre.
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();

            form.FirstLineIsTitle = true;
            form.LoadText("Référence\r\nAAA-1\r\nBBB-2\r\n");
            Assert.Equal(new[] { "AAA-1", "BBB-2" }, form.GetTexts());
            Assert.False(form.FirstLineIsTitle);

            form.LoadText("CCC-3\r\nDDD-4\r\nEEE-5\r\n");
            Assert.Equal(new[] { "CCC-3", "DDD-4", "EEE-5" }, form.GetTexts());
            Assert.DoesNotContain("Titre ignoré", form.StatusText);
            Assert.StartsWith("3 codes lus", form.StatusText);

            // Un chargement qui échoue ne consomme pas la case : l'utilisateur peut réessayer sans la cocher de nouveau.
            form.FirstLineIsTitle = true;
            ListReadResult empty = form.LoadText("   ");
            Assert.NotNull(empty.Error);
            Assert.True(form.FirstLineIsTitle);
            Assert.Equal(3, form.RowCount);

            // LoadResult (fichier ouvert ou déposé) suit la même règle.
            form.LoadResult(Read(Values("X1", "X2"), skippedTitle: "Titre long"));
            Assert.False(form.FirstLineIsTitle);
            Assert.Contains("Titre ignoré : « Titre long »", form.StatusText);
        });
    }

    // ---------------------------------------------------------------- menu du clic droit : la ligne visée est figée à l'ouverture

    private static void OpenRowMenu(BarcodeBatchForm form, int? mouseRow, out bool cancelled)
    {
        // Simule ce que fait Windows Forms : appui du bouton droit (relève la ligne visée), puis Opening ; mouseRow == null : ouverture au clavier.
        typeof(BarcodeBatchForm).GetField("_menuByMouse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, mouseRow.HasValue);
        typeof(BarcodeBatchForm).GetField("_menuRow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, mouseRow ?? -1);
        var args = new CancelEventArgs();
        typeof(BarcodeBatchForm).GetMethod("OnRowMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { null!, args });
        cancelled = args.Cancel;
    }

    [SkippableFact]
    public void TheRowMenu_ActsOnTheRowItWasOpenedOn_EvenWhenSeveralRowsAreSelected()
    {
        // Régression : le menu se refermait AVANT le Click de l'élément et la ligne visée était oubliée : la copie portait alors sur la
        // ligne COURANTE (la dernière cliquée), pas sur celle du clic droit.
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("ROW-1", "ROW-2", "ROW-3", "ROW-4", "ROW-5")));
            form.Grid.ClearSelection();
            form.Grid.CurrentCell = form.Grid.Rows[4].Cells[1];   // la ligne courante est la 5
            foreach (int row in new[] { 1, 2, 3, 4 })
            {
                form.Grid.Rows[row].Selected = true;
            }

            OpenRowMenu(form, mouseRow: 1, out bool cancelled);   // clic droit sur la ligne 2 (déjà sélectionnée : la sélection reste)
            Assert.False(cancelled);

            // Le menu est refermé puis l'élément est cliqué : la ligne 2 doit être copiée, pas la ligne courante.
            Field<ContextMenuStrip>(form, "_rowMenu").Close();
            Field<ToolStripMenuItem>(form, "_miCopyText").PerformClick();

            Skip.If(form.StatusText.StartsWith("Échec", StringComparison.Ordinal), "Presse-papier indisponible sur ce poste : " + form.StatusText);
            Assert.StartsWith("Texte de la ligne 2 copié", form.StatusText);
            Assert.Equal("ROW-2", Clipboard.GetText());
        });
    }

    [SkippableFact]
    public void TheRowMenu_DoesNotOpenOnTheHeaderOrAnEmptyArea_ButOpensOnTheCurrentRowFromTheKeyboard()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("ROW-1", "ROW-2", "ROW-3")));
            form.Grid.CurrentCell = form.Grid.Rows[2].Cells[1];

            OpenRowMenu(form, mouseRow: -1, out bool onHeader);   // clic droit sur l'en-tête ou dans une zone vide
            Assert.True(onHeader);

            OpenRowMenu(form, mouseRow: null, out bool fromKeyboard);   // touche Menu / Maj+F10 : ligne courante
            Assert.False(fromKeyboard);
            Field<ToolStripMenuItem>(form, "_miCopyText").PerformClick();
            Skip.If(form.StatusText.StartsWith("Échec", StringComparison.Ordinal), "Presse-papier indisponible sur ce poste : " + form.StatusText);
            Assert.StartsWith("Texte de la ligne 3 copié", form.StatusText);
        });
    }

    [SkippableFact]
    public void TheRowMenu_ChoosingAnItemWithoutOpeningTheMenuFirst_UsesTheCurrentRow()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("ROW-1", "ROW-2")));
            form.Grid.CurrentCell = form.Grid.Rows[1].Cells[1];

            Field<ToolStripMenuItem>(form, "_miCopyText").PerformClick();

            Skip.If(form.StatusText.StartsWith("Échec", StringComparison.Ordinal), "Presse-papier indisponible sur ce poste : " + form.StatusText);
            Assert.StartsWith("Texte de la ligne 2 copié", form.StatusText);
        });
    }

    // ---------------------------------------------------------------- largeur des colonnes et recréation des lignes selon le dpi

    private static void ScaleColumns(BarcodeBatchForm form, int dpi)
        => typeof(BarcodeBatchForm).GetMethod("ScaleColumns", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { dpi });

    [SkippableFact]
    public void TheColumnWidths_FollowTheScreenScale_AndComeBackWhenItGoesBack()
    {
        // Régression : seule la hauteur des lignes suivait le dpi ; les colonnes restaient en pixels physiques (miniature trop petite,
        // « N° » tronqué à 200 %).
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            DataGridViewColumnCollection columns = form.Grid.Columns;
            int no = columns[0].Width;
            int text = columns[1].Width;
            int bar = columns[2].Width;
            int stateMin = columns[3].MinimumWidth;

            ScaleColumns(form, 192);   // 200 %

            Assert.InRange(columns[0].Width, no * 2 - 1, no * 2 + 1);
            Assert.InRange(columns[1].Width, text * 2 - 1, text * 2 + 1);
            Assert.InRange(columns[2].Width, bar * 2 - 1, bar * 2 + 1);
            Assert.InRange(columns[3].MinimumWidth, stateMin * 2 - 1, stateMin * 2 + 1);

            ScaleColumns(form, 192);   // même échelle : aucun changement
            Assert.InRange(columns[1].Width, text * 2 - 1, text * 2 + 1);

            columns[1].Width += 40;    // colonne élargie à la souris : la proportion est gardée
            int widened = columns[1].Width;
            ScaleColumns(form, 96);
            Assert.InRange(columns[1].Width, widened / 2 - 1, widened / 2 + 1);
            Assert.InRange(columns[0].Width, no - 1, no + 1);
            Assert.InRange(columns[2].Width, bar - 1, bar + 1);
        });
    }

    [SkippableFact]
    public void ChangingTheRowHeight_KeepsTheSelectionAndTheCurrentRow()
    {
        // Régression : changer de dpi recréait les lignes et perdait la sélection (point de reprise de « Imprimer à partir de la sélection »).
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Enumerable.Range(1, 60).Select(i => "REF-" + i).ToArray()));
            form.Grid.ClearSelection();
            form.Grid.CurrentCell = form.Grid.Rows[21].Cells[1];
            foreach (int row in new[] { 20, 21, 22 })
            {
                form.Grid.Rows[row].Selected = true;
            }

            form.Grid.RowTemplate.Height = 40;   // comme si le dpi avait changé : ApplyRowHeight doit recréer les lignes
            typeof(BarcodeBatchForm).GetMethod("ApplyRowHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);

            Assert.Equal(60, form.Grid.RowCount);
            Assert.Equal(new[] { 20, 21, 22 }, form.Grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToArray());
            Assert.Equal(21, form.Grid.CurrentCell?.RowIndex);
            Assert.True(Btn(form, "_btnPrintSelection").Enabled);
        });
    }

    // ---------------------------------------------------------------- « Imprimer la sélection » interrompu : le reste de la sélection est gardé

    [SkippableFact]
    public void APrintSelectionRunThatFails_ReSelectsTheRemainingSelection_NotTheWholeTail()
    {
        // Régression : après un échec, seule la ligne en cause était sélectionnée et le message invitait à « Imprimer à partir de la
        // sélection », qui aurait imprimé toute la fin de la liste au lieu du reste de la sélection.
        RequireDesktop();
        Exception? error = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                SelectFakePrinter(form, NoSuchPrinter);
                form.LoadResult(Read(Enumerable.Range(1, 30).Select(i => "SEL-" + i).ToArray()));
                form.PromptHook = (_, _) => DialogResult.OK;
                form.Grid.ClearSelection();
                foreach (int row in new[] { 2, 9, 26 })
                {
                    form.Grid.Rows[row].Selected = true;
                }

                await form.PrintAsync(PrintMode.Selection);

                Assert.Equal(RowState.Failed, form.GetRowState(2));
                Assert.StartsWith("Échec à la ligne 3 (SEL-3) : ", form.StatusText);
                Assert.Contains("cliquez sur « Imprimer la sélection » (la ligne 3 et les suivantes de votre sélection sont déjà re-sélectionnées)", form.StatusText);
                Assert.DoesNotContain("Imprimer à partir de la sélection", form.StatusText);
                Assert.Equal(new[] { 2, 9, 26 }, form.Grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToArray());
                Assert.Equal(2, form.Grid.CurrentCell?.RowIndex);
                Assert.True(Btn(form, "_btnPrintSelection").Enabled);
            });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    // ---------------------------------------------------------------- une seule fenêtre à la fois envoie à une imprimante

    [SkippableFact]
    public void WhenAnotherWindowIsPrintingOnThePrinter_TheSeriesIsRefusedWithAMessage_AndNothingChanges()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            string printer = NoSuchPrinter + "-verrou";
            var otherWindow = new object();
            try
            {
                using BarcodeBatchForm form = NewForm();
                SelectFakePrinter(form, printer);
                form.LoadResult(Read(Values("A1", "B2")));
                form.PromptHook = (_, _) => DialogResult.OK;
                Assert.True(PrintGate.TryAcquire(printer, otherWindow, "Étiquettes en série", out _));

                form.PrintAsync(PrintMode.All).GetAwaiter().GetResult();

                Assert.False(form.IsPrinting);
                Assert.Equal(PrintGate.BusyMessage(printer, "Étiquettes en série"), form.StatusText);
                Assert.All(Enumerable.Range(0, 2), row => Assert.Equal(RowState.Pending, form.GetRowState(row)));
                Assert.True(Btn(form, "_btnPrintAll").Enabled);
                Assert.False(PrintGate.IsHeldBy(form));
            }
            finally
            {
                PrintGate.Release(otherWindow);
            }
        });
    }

    [SkippableFact]
    public void TheSeriesTakesThePrinterGate_AndGivesItBackWhenItEnds()
    {
        RequireDesktop();
        string printer = NoSuchPrinter + "-fin";
        Exception? error = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                SelectFakePrinter(form, printer);
                form.LoadResult(Read(Values("A1", "B2")));
                form.PromptHook = (_, _) => DialogResult.OK;

                await form.PrintAsync(PrintMode.All);   // échoue tout de suite (imprimante inexistante)

                Assert.Equal(RowState.Failed, form.GetRowState(0));
                Assert.False(PrintGate.IsHeldBy(form));
            });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        // Une seconde série, tout de suite après, n'est pas refusée : le verrou a bien été rendu.
        Exception? second = RunWithMessageLoop(
            () => NewForm(),
            async form =>
            {
                SelectFakePrinter(form, printer);
                form.LoadResult(Read(Values("C3")));
                form.PromptHook = (_, _) => DialogResult.OK;

                await form.PrintAsync(PrintMode.All);

                Assert.DoesNotContain("déjà utilisée", form.StatusText);
                Assert.Equal(RowState.Failed, form.GetRowState(0));
            });
        if (second != null)
        {
            ExceptionDispatchInfo.Capture(second).Throw();
        }
    }

    // ---------------------------------------------------------------- fermeture

    [SkippableFact]
    public void ClosingTheWindow_OnlyHidesIt_AndItCanBeShownAgain()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
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
            BarcodeBatchForm form = NewForm();
            form.LoadResult(Read(Values("A1")));
            _ = form.ThumbnailFor(0);
            form.AllowExit = true;

            form.Close();

            Assert.True(form.IsDisposed);
        });
    }

    [SkippableFact]
    public void ClosingWhilePrinting_IsRefused_WithAnExplanation()
    {
        RequireDesktop();
        Sta.Run(() =>
        {
            using BarcodeBatchForm form = NewForm();
            form.ShowAndFocus();
            SetPrinting(form, true);

            form.Close();

            Assert.False(form.IsDisposed);
            Assert.True(form.Visible);
            Assert.Contains("Arrêter", form.StatusText);

            SetPrinting(form, false);
            form.HideWindow();
        });
    }

    // ---------------------------------------------------------------- ouverture depuis l'icône et la fenêtre principale

    [SkippableFact]
    public void TheTrayMenuAndTheMainWindowLink_OpenTheSameBarcodeBatchWindow()
    {
        RequireDesktop();
        using var sandbox = new SettingsSandbox();

        Exception? error = RunTray(context =>
        {
            Assert.Null(context.BarcodeBatchWindow); // créée au premier besoin seulement

            // 1) menu de l'icône
            var menu = Field<ContextMenuStrip>(context, "_menu");
            ToolStripMenuItem item = menu.Items.OfType<ToolStripMenuItem>()
                .Single(i => i.Text == "Codes-barres en série (liste)…");
            item.PerformClick();
            BarcodeBatchForm first = context.BarcodeBatchWindow ?? throw new InvalidOperationException("La fenêtre n'a pas été créée par le menu.");
            Assert.True(first.Visible);
            Assert.False(first.IsDisposed);
            Assert.Null(context.BatchWindow); // l'autre fenêtre n'est pas créée pour autant

            first.HideWindow();
            Assert.False(first.Visible);

            // 2) lien de la fenêtre principale : même fenêtre, réaffichée
            MainForm main = context.Window;
            main.ShowAndFocus();
            var link = Field<LinkLabel>(main, "_lnkBarcodeBatch");
            var chassisLink = Field<LinkLabel>(main, "_lnkBatch");
            var status = Field<Label>(main, "_lblStatus");
            Assert.Equal("Codes-barres en série…", link.Text);
            Assert.Equal("Étiquettes en série…", chassisLink.Text);
            Assert.True(link.Top >= status.Bottom - 1, "le lien doit être sous le message d'état");
            Assert.True(Math.Abs(link.Top - chassisLink.Top) <= 6, "les deux liens doivent être sur la même ligne");
            Assert.True(link.Left > chassisLink.Right, "le second lien doit être à droite du premier");
            Assert.True(link.Bottom <= main.ClientSize.Height, "le lien doit rester visible dans la fenêtre");
            Assert.True(link.Right <= main.ClientSize.Width, "le lien doit rester visible dans la fenêtre");
            Assert.True(Field<Button>(main, "_btnGenerate").Right <= main.ClientSize.Width);
            Assert.True(Field<Button>(main, "_btnPrint").Right <= main.ClientSize.Width);

            bool raised = false;
            main.BarcodeBatchRequested += (_, _) => raised = true;
            typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(new LinkLabel.Link()) });
            Assert.True(raised);
            Assert.Same(first, context.BarcodeBatchWindow);
            Assert.True(first.Visible);

            // 3) la fenêtre est réutilisée par ShowBarcodeBatchWindow()
            first.HideWindow();
            context.ShowBarcodeBatchWindow();
            Assert.Same(first, context.BarcodeBatchWindow);
            Assert.True(first.Visible);
            first.HideWindow();
        });

        Skip.If(error is Win32Exception, "Environnement sans bureau utilisable : " + error?.Message);
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [SkippableFact]
    public void TheChassisLabelEntryPoints_AreUnchanged()
    {
        RequireDesktop();
        using var sandbox = new SettingsSandbox();

        Exception? error = RunTray(context =>
        {
            var menu = Field<ContextMenuStrip>(context, "_menu");
            string[] texts = menu.Items.OfType<ToolStripItem>().Select(i => i.Text ?? string.Empty).ToArray();
            Assert.Contains("Étiquettes en série (tableau Excel)…", texts);
            Assert.Contains("Codes-barres en série (liste)…", texts);
            Assert.Contains("Quitter", texts);

            menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Étiquettes en série (tableau Excel)…").PerformClick();
            Assert.NotNull(context.BatchWindow);
            Assert.Null(context.BarcodeBatchWindow);
            context.BatchWindow!.HideWindow();
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

    private static Button Btn(BarcodeBatchForm form, string field) => Field<Button>(form, field);

    /// <summary>
    /// Déclenche le clic d'un bouton. <c>PerformClick()</c> ne fait RIEN tant que la fenêtre n'a jamais été affichée (le bouton
    /// n'est alors pas « sélectionnable » : un parent est invisible) ; on appelle donc directement OnClick, qui lève l'événement Click.
    /// </summary>
    private static void Click(Button button)
    {
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    }

    private static void SetPrinting(BarcodeBatchForm form, bool printing)
    {
        typeof(BarcodeBatchForm).GetField("_printing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, printing);
        typeof(BarcodeBatchForm).GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    }

    /// <summary>
    /// Crée la fenêtre sur un thread STA, l'affiche dans une vraie boucle de messages (les continuations de PrintAsync et
    /// LoadFileAsync reviennent alors sur le thread d'interface, comme dans l'application), exécute <paramref name="script"/>
    /// puis ferme. Renvoie l'exception éventuelle (délai maximal 45 s).
    /// </summary>
    private static Exception? RunWithMessageLoop(Func<BarcodeBatchForm> create, Func<BarcodeBatchForm, Task> script)
    {
        Exception? error = null;
        bool finished = false;
        var thread = new Thread(() =>
        {
            try
            {
                using BarcodeBatchForm form = create();
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await script(form);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        finished = true;
                        form.AllowExit = true;
                        form.Close();
                    }
                };
                Application.Run(form);
            }
            catch (Exception ex)
            {
                error ??= ex;
            }
        })
        {
            IsBackground = true,
            Name = "Thread STA de test (codes-barres en série)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(45)))
        {
            return new TimeoutException("La boucle de messages n'a pas terminé en 45 s.");
        }

        return error ?? (finished ? null : new InvalidOperationException("La boucle de messages s'est arrêtée prématurément."));
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
            Name = "Thread STA de test (codes-barres en série, icône)",
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
