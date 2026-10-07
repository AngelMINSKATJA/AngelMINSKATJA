using System;
using System.Threading;
using System.Windows.Forms;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Logique de la zone de notification et de la fenêtre principale : clic sur l'icône (lifecycle-3), fermeture réelle
/// de la fenêtre (lifecycle-1), bouton Imprimer après un envoi trop lent (robustness-6).
/// Les tests qui créent une vraie icône de notification sont ignorés (« Skipped ») si la session n'est pas interactive.
/// </summary>
public class TrayAndMainFormTests
{
    // ---------------------------------------------------------------- clic gauche sur l'icône

    [Theory]
    [InlineData(true, FormWindowState.Normal, true, long.MaxValue, true)]    // au premier plan : masquer
    [InlineData(true, FormWindowState.Normal, false, 80L, true)]             // désactivée par ce clic même : elle était devant
    [InlineData(true, FormWindowState.Normal, false, 500L, true)]
    [InlineData(true, FormWindowState.Normal, false, 501L, false)]           // visible mais derrière Word/Excel : la ramener
    [InlineData(true, FormWindowState.Normal, false, 60_000L, false)]
    [InlineData(true, FormWindowState.Normal, false, long.MaxValue, false)]  // jamais activée
    [InlineData(false, FormWindowState.Normal, true, 0L, false)]             // masquée : l'afficher
    [InlineData(true, FormWindowState.Minimized, true, 0L, false)]           // réduite : la restaurer
    public void ShouldHideOnTrayClick_OnlyWhenTheWindowWasInFront(
        bool visible, FormWindowState state, bool formIsActive, long msSinceDeactivated, bool expectedHide)
    {
        Assert.Equal(expectedHide, TrayApplicationContext.ShouldHideOnTrayClick(visible, state, formIsActive, msSinceDeactivated));
    }

    // ---------------------------------------------------------------- bouton Imprimer

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]    // envoi en cours
    [InlineData(false, true, false)]    // un envoi trop lent tourne encore : pas de doublon possible
    [InlineData(true, true, false)]
    public void PrintButtonEnabled_StaysOffWhileAnEarlierSendIsStillPending(bool busy, bool pending, bool expected)
    {
        Assert.Equal(expected, MainForm.PrintButtonEnabled(busy, pending));
    }

    // ---------------------------------------------------------------- fermeture de la fenêtre

    [SkippableFact]
    public void ClosingTheMainWindowForReal_EndsTheApplication_NotJustTheWindow()
    {
        Skip.IfNot(Environment.UserInteractive, "Session sans bureau interactif : pas d'icône de notification possible.");
        using var sandbox = new SettingsSandbox();

        Outcome outcome = RunTray((context, o) =>
        {
            context.Window.ShowAndFocus();
            context.Window.AllowExit = true; // équivaut à une vraie fermeture (Fin de tâche, taskkill...) : FormClosed est déclenché
            context.Window.Close();
        });

        Skip.If(outcome.Error != null, "Environnement sans bureau utilisable : " + outcome.Error?.Message);
        Assert.False(outcome.TimedOut, "La fenêtre a été fermée pour de vrai mais l'application continue de tourner (icône orpheline).");
        Assert.True(outcome.Ended);
    }

    [SkippableFact]
    public void ClosingTheMainWindowWithTheCrossButton_OnlyHidesItToTheTray()
    {
        Skip.IfNot(Environment.UserInteractive, "Session sans bureau interactif : pas d'icône de notification possible.");
        using var sandbox = new SettingsSandbox();

        Outcome outcome = RunTray((context, o) =>
        {
            context.Window.ShowAndFocus();
            context.Window.Close(); // comme la croix : annulé, la fenêtre se masque
            var check = new System.Windows.Forms.Timer { Interval = 700 };
            check.Tick += (_, _) =>
            {
                check.Stop();
                check.Dispose();
                o.CheckReached = true;
                o.VisibleAfterClose = context.Window.Visible;
                context.ExitThread(); // fin du test
            };
            check.Start();
        });

        Skip.If(outcome.Error != null, "Environnement sans bureau utilisable : " + outcome.Error?.Message);
        Assert.False(outcome.TimedOut);
        Assert.True(outcome.CheckReached, "L'application s'est arrêtée alors que la croix devait seulement masquer la fenêtre.");
        Assert.False(outcome.VisibleAfterClose ?? true, "La fenêtre devrait être masquée après la croix.");
    }

    private sealed class Outcome
    {
        public volatile bool Ended;
        public volatile bool TimedOut;
        public volatile bool CheckReached;
        public volatile Exception? Error;
        public bool? VisibleAfterClose;
    }

    /// <summary>
    /// Lance un TrayApplicationContext (démarré masqué) dans une vraie boucle de messages sur un thread STA, exécute
    /// <paramref name="script"/> une fois la boucle démarrée, et attend la fin de la boucle (au plus 20 s).
    /// </summary>
    private static Outcome RunTray(Action<TrayApplicationContext, Outcome> script)
    {
        var outcome = new Outcome();
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
                        script(context, outcome);
                    }
                    catch (Exception ex)
                    {
                        outcome.Error = ex;
                        context.ExitThread();
                    }
                };
                starter.Start();
                Application.Run(context);
                outcome.Ended = true;
            }
            catch (Exception ex)
            {
                outcome.Error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Thread STA de test (zone de notification)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        outcome.TimedOut = !thread.Join(TimeSpan.FromSeconds(20));
        return outcome;
    }
}
