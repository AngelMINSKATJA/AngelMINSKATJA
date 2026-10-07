using System;
using Xunit;

namespace BarcodeTray.Tests;

/// <summary>
/// ChooseModulePx(symbolCount, preferredPx, maxWidthPx) : plus grand nombre entier de pixels par module,
/// au plus preferredPx, tel que le code-barres complet (zones de silence comprises) tienne dans maxWidthPx ;
/// jamais moins de 1. Les cas ci-dessous restent valables que la zone de silence soit de 10 ou de 11 modules.
/// </summary>
public class ChooseModulePxTests
{
    [Theory]
    // symboles, souhaité, largeur max, attendu
    [InlineData(4, 3, 696, 3)]      // "A" : très court, la largeur souhaitée tient
    [InlineData(10, 3, 696, 3)]
    [InlineData(18, 3, 696, 3)]     // 222 modules x 3 = 666 px
    [InlineData(20, 3, 696, 2)]     // 3 px ne tient plus
    [InlineData(29, 3, 696, 2)]
    [InlineData(30, 3, 696, 1)]     // 2 px ne tient plus
    [InlineData(61, 3, 696, 1)]
    [InlineData(62, 3, 696, 1)]     // ne tient pas du tout : jamais en dessous de 1 px
    [InlineData(500, 3, 696, 1)]
    [InlineData(4, 1, 696, 1)]      // le souhait est un plafond
    [InlineData(4, 2, 696, 2)]
    [InlineData(4, 5, 696, 5)]
    [InlineData(4, 10, 696, 10)]
    [InlineData(4, 12, 696, 10)]    // souhait plus grand que ce qui tient : 696 / 68 = 10
    [InlineData(4, 0, 696, 1)]      // souhait absurde : au moins 1
    [InlineData(4, -3, 696, 1)]
    [InlineData(4, 3, 100, 1)]      // 100 px / 68 modules = 1
    [InlineData(4, 3, 140, 2)]
    [InlineData(4, 3, 10, 1)]
    [InlineData(20, 4, 1500, 4)]
    [InlineData(20, 8, 1500, 6)]    // 1500 / 244 = 6,1
    public void ReturnsLargestModuleThatFits(int symbols, int preferred, int maxWidth, int expected)
    {
        Assert.Equal(expected, BarcodeRenderer.ChooseModulePx(symbols, preferred, maxWidth));
    }

    [Fact]
    public void IsMonotonicAndAlwaysFits_ForEverySymbolCount()
    {
        int previous = int.MaxValue;
        for (int symbols = 3; symbols <= 400; symbols++)
        {
            int px = BarcodeRenderer.ChooseModulePx(symbols, 3, 696);

            Assert.InRange(px, 1, 3);
            Assert.True(px <= previous, $"{symbols} symboles : {px} px alors que {symbols - 1} symboles donnait {previous} px.");
            // Quand on dépasse 1 px, le code-barres (même avec seulement 10 modules de silence) tient dans la largeur maximale.
            Assert.True(px == 1 || px * (11 * symbols + 22) <= 696, $"{symbols} symboles, {px} px : trop large.");
            previous = px;
        }
    }
}
