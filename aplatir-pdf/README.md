# Aplatir PDF

Petit outil Windows qui reste dans la **zone de notification** (à côté de l'horloge).
Vous y **glissez-déposez des PDF** : les signatures électroniques, tampons, annotations et
champs de formulaire sont **gravés dans la page**, puis le fichier est enregistré sous
`[a]- <nom d'origine>.pdf` dans le dossier de sortie que vous avez choisi.

Résultat : vous pouvez ensuite fusionner les PDF aplatis dans Acrobat sans que les signatures disparaissent.

Vos fichiers d'origine ne sont **jamais modifiés**.

## Obtenir l'exécutable (`AplatirPDF.exe`)

Un runner Windows de GitHub fabrique l'exe et le teste automatiquement :

1. Sur GitHub, ouvrez le dépôt → onglet **Actions** → workflow **Build AplatirPDF.exe**.
2. Cliquez sur la dernière exécution verte (✔) → section **Artifacts** en bas → **AplatirPDF-windows**.
3. Décompressez le `.zip` téléchargé : il contient `AplatirPDF.exe`. Copiez-le où vous voulez
   (par exemple `C:\Outils\AplatirPDF\`) — **ne le déplacez plus ensuite**, le démarrage
   automatique mémorise son emplacement (s'il bouge, relancez-le une fois à la main).

Alternative : sur un PC Windows avec Python 3.10+ installé, double-cliquez sur
`build_windows.bat` ; l'exe est créé dans le dossier `dist`.

> Windows SmartScreen peut afficher « Windows a protégé votre PC » car l'exe n'est pas signé :
> **Informations complémentaires → Exécuter quand même**.

## Utilisation

1. Lancez `AplatirPDF.exe`. La fenêtre s'ouvre (la toute première fois).
2. Cliquez sur **Parcourir…** et choisissez le **dossier de sortie** (retenu pour la suite).
3. **Glissez-déposez** un ou plusieurs PDF — ou un dossier entier — sur la zone bleue.
   Chaque fichier est traité et le résultat s'affiche dans la liste (✔ aplati, ⚠ à vérifier, ✖ erreur).
   Vous pouvez aussi utiliser le bouton **Ajouter des PDF…**.
4. Fermez la fenêtre avec la croix : l'outil **reste actif dans la zone de notification**.
   Un clic sur son icône rouvre la fenêtre ; clic droit pour le menu
   (dossier de sortie, démarrage automatique, **Quitter**).

Par défaut l'outil **se lance au démarrage de Windows**, caché dans la zone de notification
(case « Lancer au démarrage de Windows » pour le désactiver). Sous Windows 11, l'icône peut se
trouver sous la flèche **^** ; faites-la glisser vers la barre pour la garder visible.

Lancer l'exe une 2ᵉ fois ne démarre pas une 2ᵉ copie : cela ramène simplement la fenêtre.
Glisser des PDF **directement sur l'icône du fichier `.exe`** (ou « Envoyer vers ») les traite aussi.

## Ce que fait exactement l'aplatissement

- Les PDF sans signature ni annotation sont simplement **copiés** sous le nouveau nom.
- Les autres sont aplatis (le texte reste du texte, sélectionnable et recherchable), puis
  l'outil **compare le rendu avant/après, page par page**. Si une page a perdu quelque chose de visible,
  elle est **convertie en image** (200 dpi) par sécurité et signalée ⚠ dans la liste
  (option « Sécurité », activée par défaut).
- Si un fichier du même nom existe déjà dans le dossier de sortie, il est remplacé.
- Un fichier dont le nom commence déjà par `[a]- ` est ignoré.
- Les PDF protégés par mot de passe, vides ou corrompus sont refusés avec un message clair.

⚠ Une signature **numérique** (certificat) perd sa valeur cryptographique en étant aplatie : seule son
apparence est conservée. C'est le but ici, mais gardez les originaux signés si vous devez prouver l'authenticité.

## Fichiers de l'outil

| Fichier | Contenu |
|---|---|
| `%APPDATA%\AplatirPDF\config.json` | vos réglages (dossier de sortie, options) |
| `%APPDATA%\AplatirPDF\aplatir.log` | journal détaillé (bouton **Journal**) |

## Dépannage

- **Le glisser-déposer ne marche pas** : n'exécutez pas l'exe « en tant qu'administrateur » —
  Windows interdit de glisser un fichier d'un Explorateur normal vers une application élevée.
  Utilisez le bouton **Ajouter des PDF…** en dernier recours.
- **Erreur « fichier de sortie ouvert dans un autre programme »** : fermez le PDF dans Acrobat et recommencez.
- **L'icône n'apparaît pas au démarrage** : elle apparaît dès que la barre des tâches est prête ;
  sinon relancez l'exe.

## Pour les développeurs

```
pip install -r requirements-dev.txt
python -m pytest            # tests du coeur de traitement
python aplatir_tray.py      # lance l'application
AplatirPDF.exe --selftest rapport.txt   # auto-test sans interface (utilisé par la CI)
```

PyMuPDF est distribué sous licence AGPL-3.0 : un usage personnel ou interne est sans contrainte ; si vous
redistribuez l'exe à des tiers, fournissez aussi le code source.
