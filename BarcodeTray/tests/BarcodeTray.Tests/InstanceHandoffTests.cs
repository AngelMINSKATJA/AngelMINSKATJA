using System;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Premier lancement du nouvel exécutable alors que l'ANCIEN (BarcodeTray.exe, démarré avec Windows) tourne encore : le mutex
/// et l'événement « une seule instance » gardent leur ancien nom, donc l'ancien programme est pris pour « la même application ».
/// Le marqueur « version 3 » permet de le reconnaître. Tests purs : décision, nom du marqueur, message, vrai mutex nommé.
/// </summary>
public class InstanceHandoffTests
{
    // ---------------------------------------------------------------- décision

    [Fact]
    public void AutostartLaunch_ExitsSilently_AndNeverProbesTheMarker()
    {
        int probes = 0;

        foreach (bool markerThere in new[] { true, false })
        {
            SecondInstanceAction action = InstanceHandoff.Decide(true, () => { probes++; return markerThere; });
            Assert.Equal(SecondInstanceAction.ExitSilently, action);
        }

        Assert.Equal(0, probes);
    }

    [Fact]
    public void UserLaunch_WithTheMarkerPresent_ShowsTheRunningWindow_AsBefore()
    {
        Assert.Equal(SecondInstanceAction.ShowRunningWindow, InstanceHandoff.Decide(false, () => true));
    }

    [Fact]
    public void UserLaunch_WithoutTheMarker_ExplainsThatAnOlderVersionIsRunning()
    {
        Assert.Equal(SecondInstanceAction.ExplainOlderVersion, InstanceHandoff.Decide(false, () => false));
    }

    [Fact]
    public void UserLaunch_ProbesTheMarkerExactlyOnce()
    {
        int probes = 0;
        InstanceHandoff.Decide(false, () => { probes++; return true; });
        Assert.Equal(1, probes);
    }

    [Fact]
    public void Decide_RejectsAMissingProbe()
    {
        Assert.Throws<ArgumentNullException>(() => InstanceHandoff.Decide(false, null!));
    }

    // ---------------------------------------------------------------- noms

    [Fact]
    public void TheMarkerName_IsNewAndSessionLocal()
    {
        Assert.Equal(@"Local\BarcodeTray.v3.Running", InstanceHandoff.RunningMarkerName);
        Assert.StartsWith(@"Local\", InstanceHandoff.RunningMarkerName, StringComparison.Ordinal);

        // Les noms de l'ancienne version (inchangés dans Program) : le marqueur ne doit en être aucun, sinon une ancienne
        // version serait prise pour une version 3.
        Assert.NotEqual(@"Local\BarcodeTray.SingleInstance", InstanceHandoff.RunningMarkerName);
        Assert.NotEqual(@"Local\BarcodeTray.Show", InstanceHandoff.RunningMarkerName);
    }

    [Fact]
    public void TheProbeRetries_AreShortEnoughToBeInvisible()
    {
        Assert.InRange(InstanceHandoff.ProbeAttempts, 2, 10);
        Assert.InRange(InstanceHandoff.ProbePauseMs, 10, 200);
        Assert.True((InstanceHandoff.ProbeAttempts - 1) * InstanceHandoff.ProbePauseMs <= 600);
    }

    // ---------------------------------------------------------------- message

    [Fact]
    public void TheOlderVersionMessage_TellsTheUserWhatToDo_InFrench()
    {
        string message = InstanceHandoff.OlderVersionMessage();

        Assert.Contains("ancienne version", message, StringComparison.Ordinal);
        Assert.Contains("Code-barres Code 128", message, StringComparison.Ordinal);   // le titre que l'utilisateur connaissait
        Assert.Contains("BarcodeTray.exe", message, StringComparison.Ordinal);
        Assert.Contains("clic droit", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows 11", message, StringComparison.Ordinal);
        Assert.Contains("Quitter", message, StringComparison.Ordinal);
        Assert.Contains("relancez", message, StringComparison.Ordinal);
        Assert.Contains(AppInfo.FileBaseName + ".exe", message, StringComparison.Ordinal);
        Assert.Contains("conservés", message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- vrai mutex nommé

    [Fact]
    public void AMarkerThatExists_IsFound_AndOneThatDoesNot_IsNot()
    {
        string name = @"Local\BarcodeTray.Tests.Marker." + Guid.NewGuid().ToString("N");

        Assert.False(InstanceHandoff.IsMarkerPresent(name, 2, 1));

        using System.Threading.Mutex? marker = InstanceHandoff.CreateMarker(name);
        Assert.NotNull(marker);
        Assert.True(InstanceHandoff.IsMarkerPresent(name, 1, 1));
        Assert.True(InstanceHandoff.IsMarkerPresent(name, 3, 1));

        marker!.Dispose();
        Assert.False(InstanceHandoff.IsMarkerPresent(name, 2, 1));
    }

    [Fact]
    public void ProbingAMarker_DoesNotKeepItAlive()
    {
        string name = @"Local\BarcodeTray.Tests.Marker." + Guid.NewGuid().ToString("N");
        System.Threading.Mutex? marker = InstanceHandoff.CreateMarker(name);
        Assert.NotNull(marker);

        Assert.True(InstanceHandoff.IsMarkerPresent(name, 1, 1));   // ouvre puis referme sa propre poignée
        marker!.Dispose();

        Assert.False(InstanceHandoff.IsMarkerPresent(name, 1, 1));
    }
}
