namespace BarcodeTray;

/// <summary>
/// Identité de l'application telle que l'utilisateur la voit : titres de fenêtres, info-bulle de la zone de notification,
/// bulles et boîtes de dialogue, en-tête du diagnostic, nom du document dans la file d'impression, nom du fichier .exe.
/// Un seul endroit à changer pour renommer le logiciel.
/// <para>
/// Ce qui ne doit JAMAIS suivre le nom affiché, pour qu'une mise à jour ne fasse rien perdre à l'utilisateur : le dossier des
/// réglages (%APPDATA%\BarcodeTray), le dossier du journal (%LOCALAPPDATA%\BarcodeTray), le nom de la valeur de démarrage
/// automatique du registre (« BarcodeTray ») et les noms du mutex / de l'événement « une seule instance ». Ces noms
/// techniques restent donc « BarcodeTray », comme l'espace de noms, le dossier des sources et le nom du projet.
/// </para>
/// </summary>
internal static class AppInfo
{
    /// <summary>Nom du produit, sans numéro de version (propriété « Nom du produit » du fichier .exe).</summary>
    internal const string ProductName = "POG's Industries Labels";

    /// <summary>Numéro de version majeur : doit rester égal à la version de l'assemblage (Version dans BarcodeTray.csproj).</summary>
    internal const int MajorVersion = 3;

    /// <summary>
    /// Nom complet affiché partout : « POG's Industries Labels v3 ». Reste une constante (utilisable dans d'autres constantes) ;
    /// C# ne sait pas concaténer un nombre dans une expression constante, le « 3 » est donc répété ici : le test
    /// AppInfoTests vérifie que DisplayName == ProductName + " v" + MajorVersion.
    /// </summary>
    internal const string DisplayName = ProductName + " v3";

    /// <summary>
    /// Nom du fichier exécutable, sans « .exe » : le nom d'affichage SANS l'apostrophe (une apostrophe dans un nom de
    /// fichier gêne les scripts et les lignes de commande). Doit rester égal à AssemblyName dans BarcodeTray.csproj.
    /// </summary>
    internal const string FileBaseName = "POGs Industries Labels v3";

    /// <summary>Titre d'une fenêtre : « fonction — nom complet », par exemple « Codes-barres en série — POG's Industries Labels v3 ».</summary>
    internal static string WindowTitle(string function)
    {
        return string.IsNullOrWhiteSpace(function) ? DisplayName : function + " — " + DisplayName;
    }
}
