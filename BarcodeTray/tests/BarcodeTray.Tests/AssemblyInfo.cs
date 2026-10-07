using Xunit;

// Ces tests partagent des ressources globales (presse-papier, fichier de réglages dans %APPDATA%,
// clé de registre Run, journal) : on les exécute l'un après l'autre.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
