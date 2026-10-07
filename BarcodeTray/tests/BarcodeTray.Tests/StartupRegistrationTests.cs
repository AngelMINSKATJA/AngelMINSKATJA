using System;
using Microsoft.Win32;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Lancement au démarrage de Windows (HKCU\Software\Microsoft\Windows\CurrentVersion\Run, valeur « BarcodeTray »).
/// La valeur existante est sauvegardée puis restaurée : lancer ces tests sur son propre poste ne change rien.
/// </summary>
public class StartupRegistrationTests : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "BarcodeTray";

    private readonly object? _originalValue;
    private readonly RegistryValueKind _originalKind;
    private readonly object? _originalApproval;
    private readonly RegistryValueKind _originalApprovalKind;

    public StartupRegistrationTests()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        _originalValue = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        _originalKind = _originalValue != null ? key!.GetValueKind(ValueName) : RegistryValueKind.String;

        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, false);
        _originalApproval = approved?.GetValue(ValueName);
        _originalApprovalKind = _originalApproval != null ? approved!.GetValueKind(ValueName) : RegistryValueKind.Binary;
    }

    public void Dispose()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (_originalValue == null)
            {
                key.DeleteValue(ValueName, false);
            }
            else
            {
                key.SetValue(ValueName, _originalValue, _originalKind);
            }
        }
        catch
        {
            // Le nettoyage ne doit pas masquer le résultat du test.
        }

        try
        {
            using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath, true);
            if (_originalApproval == null)
            {
                approved.DeleteValue(ValueName, false);
            }
            else
            {
                approved.SetValue(ValueName, _originalApproval, _originalApprovalKind);
            }
        }
        catch
        {
            // idem
        }
    }

    private static byte[]? ReadApproval()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, false);
        return key?.GetValue(ValueName) as byte[];
    }

    /// <summary>Ce qu'écrit Windows quand l'utilisateur désactive (3) ou réactive (2) l'entrée dans le Gestionnaire des tâches.</summary>
    private static void WriteApproval(byte firstByte)
    {
        var data = new byte[12];
        data[0] = firstByte;
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath, true);
        key.SetValue(ValueName, data, RegistryValueKind.Binary);
    }

    private static string? ReadRawValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) as string;
    }

    private static void WriteRawValue(string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    private static string ExpectedCommand() => "\"" + Environment.ProcessPath + "\" --tray";

    [Fact]
    public void Enable_WritesTheQuotedExePathFollowedByTrayFlag()
    {
        Assert.True(StartupRegistration.Disable());
        Assert.False(StartupRegistration.IsEnabled());

        Assert.True(StartupRegistration.Enable());

        Assert.True(StartupRegistration.IsEnabled());
        Assert.Equal(ExpectedCommand(), ReadRawValue());
        Assert.StartsWith("\"", ReadRawValue(), StringComparison.Ordinal);
        Assert.EndsWith("\" --tray", ReadRawValue(), StringComparison.Ordinal);
    }

    [Fact]
    public void Disable_RemovesTheValue_AndIsHarmlessWhenAlreadyDisabled()
    {
        Assert.True(StartupRegistration.Enable());
        Assert.True(StartupRegistration.IsEnabled());

        Assert.True(StartupRegistration.Disable());
        Assert.False(StartupRegistration.IsEnabled());
        Assert.Null(ReadRawValue());

        Assert.True(StartupRegistration.Disable()); // déjà désactivé : toujours un succès
        Assert.False(StartupRegistration.IsEnabled());
    }

    [Fact]
    public void Enable_CanBeCalledTwice_WithoutDuplicates()
    {
        Assert.True(StartupRegistration.Enable());
        Assert.True(StartupRegistration.Enable());

        Assert.True(StartupRegistration.IsEnabled());
        Assert.Equal(ExpectedCommand(), ReadRawValue());
    }

    [Fact]
    public void EnsurePathCurrent_RewritesAStaleValue()
    {
        WriteRawValue("\"C:\\Ancien dossier\\BarcodeTray.exe\" --tray");
        Assert.True(StartupRegistration.IsEnabled());

        Assert.True(StartupRegistration.EnsurePathCurrent());

        Assert.Equal(ExpectedCommand(), ReadRawValue());
    }

    // ---------------------------------------------------------------- StartupApproved (robustness-4)

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]   // activé
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]   // activé
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]    // désactivé (Gestionnaire des tâches)
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[0], false)]
    public void IsDisabledApproval_ReadsTheFirstByte(byte[] raw, bool expected)
    {
        Assert.Equal(expected, StartupRegistration.IsDisabledApproval(raw));
    }

    [Fact]
    public void IsDisabledApproval_IgnoresMissingOrUnexpectedValues()
    {
        Assert.False(StartupRegistration.IsDisabledApproval(null));
        Assert.False(StartupRegistration.IsDisabledApproval("03"));
        Assert.False(StartupRegistration.IsDisabledApproval(3));
    }

    [Fact]
    public void IsEnabled_IsFalse_WhenWindowsDisabledTheEntryInTheTaskManager()
    {
        Assert.True(StartupRegistration.Enable());
        Assert.True(StartupRegistration.IsEnabled());

        WriteApproval(0x03); // l'utilisateur désactive BarcodeTray dans Gestionnaire des tâches > Démarrage

        Assert.NotNull(ReadRawValue()); // la valeur Run est toujours là...
        Assert.False(StartupRegistration.IsEnabled()); // ...mais Windows ne lancera pas le programme
    }

    [Fact]
    public void Enable_ReEnablesAnEntryDisabledByWindows()
    {
        Assert.True(StartupRegistration.Enable());
        WriteApproval(0x03);
        Assert.False(StartupRegistration.IsEnabled());

        Assert.True(StartupRegistration.Enable());

        Assert.True(StartupRegistration.IsEnabled());
        byte[]? approval = ReadApproval();
        Assert.True(approval == null || !StartupRegistration.IsDisabledApproval(approval));
    }

    [Fact]
    public void EnsurePathCurrent_DoesNotOverrideADisabledChoiceMadeInTheTaskManager()
    {
        WriteRawValue("\"C:\\Ancien dossier\\BarcodeTray.exe\" --tray");
        WriteApproval(0x03);

        Assert.True(StartupRegistration.EnsurePathCurrent());

        Assert.Equal(ExpectedCommand(), ReadRawValue()); // chemin mis à jour
        Assert.False(StartupRegistration.IsEnabled());   // mais le choix de l'utilisateur est respecté
        Assert.True(StartupRegistration.IsDisabledApproval(ReadApproval()));
    }

    [Fact]
    public void EnsurePathCurrent_DoesNothingWhenAlreadyCurrent()
    {
        Assert.True(StartupRegistration.Enable());

        Assert.True(StartupRegistration.EnsurePathCurrent());

        Assert.Equal(ExpectedCommand(), ReadRawValue());
    }

    [Fact]
    public void EnsurePathCurrent_DoesNotEnableAnythingWhenTheUserDisabledStartup()
    {
        Assert.True(StartupRegistration.Disable());

        Assert.True(StartupRegistration.EnsurePathCurrent());

        Assert.False(StartupRegistration.IsEnabled());
        Assert.Null(ReadRawValue());
    }
}
