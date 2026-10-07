using System;
using System.IO;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Garde-fou du démarrage automatique : on n'enregistre jamais un exécutable lancé depuis un emplacement
/// temporaire (typiquement un exécutable ouvert directement depuis un ZIP dans l'Explorateur).
/// Logique pure : aucun accès au registre.
/// </summary>
public class StartupTransientLocationTests
{
    [Theory]
    [InlineData(@"C:\Users\Angel\AppData\Local\Temp\Temp1_BarcodeTray.zip\BarcodeTray.exe")]
    [InlineData(@"C:\Users\Angel\Downloads\BarcodeTray.zip\BarcodeTray.exe")]
    [InlineData(@"d:\x\temp1_barcodetray-win-x64.zip\BarcodeTray.exe")]
    public void ZipPaths_AreTransient(string path)
    {
        Assert.True(StartupRegistration.IsTransientLocation(path));
    }

    [Fact]
    public void FileUnderTheTempFolder_IsTransient()
    {
        string path = Path.Combine(Path.GetTempPath(), "whatever", "BarcodeTray.exe");
        Assert.True(StartupRegistration.IsTransientLocation(path));
    }

    [Theory]
    [InlineData(@"C:\Outils\BarcodeTray\BarcodeTray.exe")]
    [InlineData(@"C:\Users\Angel\Documents\BarcodeTray.exe")]
    [InlineData(@"C:\Users\Angel\Downloads\BarcodeTray.exe")]
    [InlineData(@"D:\a\AngelMINSKATJA\AngelMINSKATJA\BarcodeTray\publish\BarcodeTray.exe")]
    public void PermanentFolders_AreNotTransient(string path)
    {
        Assert.False(StartupRegistration.IsTransientLocation(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_IsNotTransient(string? path)
    {
        Assert.False(StartupRegistration.IsTransientLocation(path));
    }
}
