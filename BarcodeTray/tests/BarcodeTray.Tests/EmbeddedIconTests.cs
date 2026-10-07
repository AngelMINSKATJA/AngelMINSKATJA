using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>L'icône de la zone de notification est lue dans une ressource intégrée à l'exécutable (nom logique « app.ico »).</summary>
public class EmbeddedIconTests
{
    private static Stream OpenIcon()
    {
        Assembly app = typeof(AppSettings).Assembly;
        Stream? stream = app.GetManifestResourceStream("app.ico");
        Assert.True(stream != null, "Ressource intégrée « app.ico » introuvable. Ressources présentes : "
                                    + string.Join(", ", app.GetManifestResourceNames()));
        return stream!;
    }

    [Fact]
    public void ResourceIsAValidIcoFile_WithTheExpectedSizes()
    {
        using Stream stream = OpenIcon();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] data = memory.ToArray();

        Assert.True(data.Length > 22);
        Assert.Equal(0, BitConverter.ToUInt16(data, 0));   // réservé
        Assert.Equal(1, BitConverter.ToUInt16(data, 2));   // type 1 = icône
        int count = BitConverter.ToUInt16(data, 4);
        Assert.InRange(count, 4, 12);

        var sizes = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + 16 * i;
            int width = data[entry] == 0 ? 256 : data[entry];
            sizes.Add(width);
        }

        foreach (int expected in new[] { 16, 32, 48, 256 })
        {
            Assert.Contains(expected, sizes);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void SystemDrawingCanLoadTheIconAtSmallSizes(int size)
    {
        using Stream stream = OpenIcon();

        using var icon = new Icon(stream, new Size(size, size));

        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        using Bitmap bitmap = icon.ToBitmap();
        Assert.Equal(size, bitmap.Width);
        // Le dessin n'est pas vide : au moins un pixel opaque.
        bool anyOpaque = false;
        for (int y = 0; y < bitmap.Height && !anyOpaque; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 200)
                {
                    anyOpaque = true;
                    break;
                }
            }
        }

        Assert.True(anyOpaque);
    }
}
