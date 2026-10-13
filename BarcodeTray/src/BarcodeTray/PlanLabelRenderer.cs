using System;
using System.Drawing;

namespace BarcodeTray;

/// <summary>Dessin de l'étiquette « plan de châssis » : type en haut à gauche, numéro de plan centré en dessous.</summary>
public static class PlanLabelRenderer
{
    /// <summary>Message d'erreur français si le champ ne peut pas être imprimé (vide, trop long, caractères de contrôle), sinon null.</summary>
    public static string? ValidateField(string? value, string fieldName)
        => throw new NotImplementedException();

    /// <summary>
    /// Image de la PAGE entière, en coordonnées de l'étiquette (origine = coin haut gauche du papier), à <c>settings.Dpi</c> :
    /// largeur = pageWidthUnits x Dpi / 100 points, hauteur = pageHeightUnits x Dpi / 100 points (centièmes de pouce).
    /// <paramref name="printableAreaUnits"/> est la zone imprimable annoncée par le pilote (X, Y, largeur, hauteur en centièmes
    /// de pouce, origine = coin du papier) : le texte est réduit s'il ne tient pas dans sa largeur. Fond blanc, 24 bits.
    /// L'appelant libère le Bitmap.
    /// </summary>
    public static Bitmap Render(string type, string plan, AppSettings settings, int pageWidthUnits, int pageHeightUnits, RectangleF printableAreaUnits)
        => throw new NotImplementedException();
}
