using System.Drawing;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// Placement de l'image sur l'étiquette (centièmes de pouce). Ces tests s'appuient sur les fonctions pures
/// FitScale / ComputeDestination de LabelPrinter ; si elles sont renommées, adapter ou supprimer ce fichier.
/// </summary>
public class LabelGeometryTests
{
    private const float Area62mm = 244f;   // largeur imprimable maximale visée sur ruban 62 mm (centièmes de pouce)

    [Fact]
    public void ImageNarrowerThanTheTape_KeepsItsPhysicalSize_AndIsCentered()
    {
        // 600 px à 300 dpi = 2 pouces = 200 centièmes de pouce ; 150 px = 50 centièmes de pouce de haut.
        RectangleF dest = LabelPrinter.ComputeDestination(600, 150, 300f, 300f, Area62mm, 300f, 300f, out bool scaledDown);

        Assert.False(scaledDown);
        Assert.Equal(200f, dest.Width, 0.5);
        Assert.Equal(50f, dest.Height, 0.5);
        Assert.Equal((Area62mm - 200f) / 2f, dest.X, 0.5);
        Assert.Equal(0f, dest.Y);
    }

    [Fact]
    public void ImageExactlyTheTapeWidth_IsNotScaled()
    {
        // 732 px à 300 dpi = 244 centièmes de pouce.
        RectangleF dest = LabelPrinter.ComputeDestination(732, 100, 300f, 300f, Area62mm, 300f, 300f, out bool scaledDown);

        Assert.False(scaledDown);
        Assert.Equal(Area62mm, dest.Width, 0.5);
        Assert.Equal(0f, dest.X, 0.5);
    }

    [Fact]
    public void ImageWiderThanThePrintableArea_IsScaledDownUniformly()
    {
        // 1200 px à 300 dpi = 400 centièmes de pouce, bien plus que 244.
        RectangleF dest = LabelPrinter.ComputeDestination(1200, 300, 300f, 300f, Area62mm, 300f, 300f, out bool scaledDown);

        Assert.True(scaledDown);
        Assert.True(dest.Width <= Area62mm + 0.5f, $"largeur {dest.Width} > zone {Area62mm}");
        Assert.True(dest.Right <= Area62mm + 0.5f);
        Assert.True(dest.X >= -0.5f);
        // Même facteur en hauteur : le rapport largeur/hauteur de l'image (4:1) est conservé.
        Assert.Equal(4.0, dest.Width / dest.Height, 0.05);
    }

    [Fact]
    public void ImageIsNeverEnlarged_EvenOnAHigherResolutionPrinter()
    {
        // L'imprimante à 600 dpi doit dessiner la même taille physique, pas deux fois plus petite ni plus grande.
        RectangleF dest = LabelPrinter.ComputeDestination(600, 150, 300f, 300f, Area62mm, 600f, 600f, out bool scaledDown);

        Assert.False(scaledDown);
        Assert.Equal(200f, dest.Width, 0.5);
        Assert.Equal(50f, dest.Height, 0.5);
    }

    [Fact]
    public void ImageWithoutResolutionInformation_IsAssumedToBe300Dpi()
    {
        RectangleF dest = LabelPrinter.ComputeDestination(600, 150, 0f, 0f, Area62mm, 300f, 300f, out _);

        Assert.Equal(200f, dest.Width, 0.5);
    }

    [Theory]
    [InlineData(100.0, 244.0)]
    [InlineData(244.0, 244.0)]
    public void FitScale_IsOneWhenTheImageFits(double natural, double area)
    {
        Assert.Equal(1.0, LabelPrinter.FitScale(natural, area));
    }

    [Fact]
    public void FitScale_ShrinksWhenTheImageIsClearlyTooWide()
    {
        double scale = LabelPrinter.FitScale(488.0, 244.0);

        Assert.Equal(0.5, scale, 0.001);
    }

    [Fact]
    public void FitScale_WithUnknownArea_DoesNotScale()
    {
        Assert.Equal(1.0, LabelPrinter.FitScale(500.0, 0.0));
    }
}
