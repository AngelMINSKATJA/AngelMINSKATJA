using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using BarcodeTray.Tests.Support;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Identité de l'exécutable construit : version, « Description du fichier » (ce que Windows affiche dans le Gestionnaire des
/// tâches et dans Paramètres > Applications > Démarrage), nom du fichier, et noms techniques qui ne doivent PAS changer
/// (mutex, événement). Lecture par réflexion sur l'assemblage réel de l'application : ces tests tournent sous Windows
/// seulement (le projet de tests référence l'application, ce que la version Linux des tests purs ne fait pas).
/// </summary>
public class AppIdentityTests
{
    private static readonly Assembly App = typeof(AppSettings).Assembly;

    // ---------------------------------------------------------------- version et informations du fichier

    [Fact]
    public void TheAssemblyMajorVersion_IsTheOneInAppInfo()
    {
        Version version = App.GetName().Version!;

        Assert.Equal(AppInfo.MajorVersion, version.Major);
        Assert.Equal(new Version(3, 0, 0, 0), version);
    }

    [Fact]
    public void TheFileVersionAndTheProductVersion_StartWithTheMajorVersion()
    {
        var info = FileVersionInfo.GetVersionInfo(App.Location);

        Assert.Equal(AppInfo.MajorVersion, info.FileMajorPart);
        Assert.Equal("3.0.0.0", info.FileVersion);
        Assert.StartsWith(AppInfo.MajorVersion + ".0.0", info.ProductVersion ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAssemblyTitle_IsTheDisplayName_AndBecomesTheFileDescription()
    {
        string? title = App.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
        Assert.Equal(AppInfo.DisplayName, title);

        // Ce que lit Windows (Gestionnaire des tâches, Démarrage) : la ressource de version du fichier.
        var info = FileVersionInfo.GetVersionInfo(App.Location);
        Assert.Equal(AppInfo.DisplayName, info.FileDescription);
        Assert.Equal(AppInfo.ProductName, info.ProductName);
        Assert.Equal("POG's Industries", info.CompanyName);
    }

    [Fact]
    public void TheProductAndCompanyAttributes_AreTheNewOnes()
    {
        Assert.Equal(AppInfo.ProductName, App.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal("POG's Industries", App.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
        Assert.Contains("POG's Industries", App.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- nom du fichier, espace de noms

    [Fact]
    public void TheAssemblyName_IsTheExeFileName_WithoutTheApostrophe()
    {
        // AssemblyName du .csproj = nom du .exe publié : « POGs Industries Labels v3.exe ».
        Assert.Equal(AppInfo.FileBaseName, App.GetName().Name);
        Assert.Equal("POGs Industries Labels v3", App.GetName().Name);
    }

    [Fact]
    public void TheRootNamespaceIsStillBarcodeTray_AndTheIconIsStillEmbedded()
    {
        Assert.Equal("BarcodeTray", typeof(AppSettings).Namespace);
        Assert.NotNull(App.GetManifestResourceStream("app.ico"));
    }

    // ---------------------------------------------------------------- noms techniques inchangés

    [Fact]
    public void TheSingleInstanceNames_AreStillTheOldOnes()
    {
        // Une ancienne et une nouvelle version se reconnaissent comme « la même application » : jamais deux icônes.
        Assert.Equal(@"Local\BarcodeTray.SingleInstance", ReadProgramConstant("MutexName"));
        Assert.Equal(@"Local\BarcodeTray.Show", ReadProgramConstant("ShowEventName"));
    }

    [Fact]
    public void TheVersion3Marker_IsDistinctFromTheOldSingleInstanceNames()
    {
        // Le marqueur sert à reconnaître l'ANCIENNE version (qui ne le crée pas) : il ne doit jamais coïncider avec le mutex
        // ou l'événement partagés avec elle.
        string mutexName = ReadProgramConstant("MutexName");
        string eventName = ReadProgramConstant("ShowEventName");

        Assert.NotEqual(InstanceHandoff.RunningMarkerName, mutexName);
        Assert.NotEqual(InstanceHandoff.RunningMarkerName, eventName);
        Assert.NotEqual(mutexName, eventName);
    }

    private static string ReadProgramConstant(string name)
    {
        Type? program = App.GetType("BarcodeTray.Program");
        Assert.NotNull(program);
        FieldInfo? field = program!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(field);
        return (string)field!.GetRawConstantValue()!;
    }

    // ---------------------------------------------------------------- fenêtre principale et info-bulle

    [SkippableFact]
    public void TheMainWindowTitleAndTheTrayTooltip_AreTheDisplayName()
    {
        Skip.IfNot(Environment.UserInteractive, "Session sans bureau interactif : pas d'icône de notification possible.");
        using var sandbox = new SettingsSandbox();

        string? windowTitle = null;
        string? tooltip = null;
        Exception? error = null;
        Sta.Run(() =>
        {
            try
            {
                using var context = new TrayApplicationContext(new AppSettings(), startHidden: true);
                windowTitle = context.Window.Text;
                FieldInfo? icon = typeof(TrayApplicationContext).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic);
                tooltip = (icon?.GetValue(context) as NotifyIcon)?.Text;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        Skip.If(error != null, "Environnement sans bureau utilisable : " + error?.Message);
        Assert.Equal(AppInfo.DisplayName, windowTitle);
        Assert.Equal(AppInfo.DisplayName, tooltip);
    }
}
