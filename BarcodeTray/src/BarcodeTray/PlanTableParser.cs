using System;
using System.Collections.Generic;

namespace BarcodeTray;

/// <summary>Une étiquette à imprimer : type de châssis (en haut à gauche) et numéro de plan (grand, centré).</summary>
public sealed record LabelItem(string Type, string Plan);

/// <summary>Résultat de l'analyse d'un tableau collé depuis Excel.</summary>
public sealed class PlanTableParseResult
{
    public PlanTableParseResult(IReadOnlyList<LabelItem> items, IReadOnlyList<string> types, IReadOnlyList<string> warnings, string? error)
    {
        Items = items;
        Types = types;
        Warnings = warnings;
        Error = error;
    }

    /// <summary>Étiquettes, dans l'ordre des lignes du tableau (une par croix).</summary>
    public IReadOnlyList<LabelItem> Items { get; }

    /// <summary>Types de châssis trouvés dans l'en-tête, dans l'ordre des colonnes.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>Remarques (français) : cellules ignorées, lignes sans croix, etc.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Message d'erreur (français) si rien d'exploitable n'a été trouvé, sinon null.</summary>
    public string? Error { get; }
}

/// <summary>Analyse d'un tableau copié depuis Excel (texte séparé par des tabulations).</summary>
public static class PlanTableParser
{
    public static PlanTableParseResult Parse(string? text)
        => throw new NotImplementedException();

    /// <summary>« 2020Q7E 22061 » devient « 22061 » (chiffres de fin ; à défaut, dernier mot).</summary>
    public static string ExtractPlanNumber(string? cell)
        => throw new NotImplementedException();

    /// <summary>« HB1 (20) » devient « HB1 » (espaces normalisés, parenthèse finale retirée).</summary>
    public static string NormalizeTypeName(string? header)
        => throw new NotImplementedException();
}
