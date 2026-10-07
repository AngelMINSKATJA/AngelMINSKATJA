# Aplatir PDF

Petit outil Windows qui reste dans la **zone de notification** (à côté de l'horloge).
Vous y **glissez-déposez des PDF** : les signatures électroniques, tampons, annotations et
champs de formulaire sont **gravés dans la page**, puis le fichier est enregistré sous
`[a]- <nom d'origine>.pdf` dans le dossier de sortie que vous avez choisi.

Résultat : vous pouvez ensuite fusionner les PDF aplatis dans Acrobat sans que les signatures disparaissent.

Vos fichiers d'origine ne sont **jamais modifiés**. Aucun droit administrateur n'est nécessaire.

## Obtenir l'exécutable (`AplatirPDF.exe`)

Un runner Windows de GitHub fabrique l'exe et le teste automatiquement :

1. Sur GitHub, ouvrez le dépôt → onglet **Actions** → workflow **Build AplatirPDF.exe**.
2. Cliquez sur la dernière exécution verte (✔) → section **Artifacts** en bas → **AplatirPDF-windows**.
3. Décompressez le `.zip` téléchargé : il contient `AplatirPDF.exe`. Copiez-le où vous voulez
   (par exemple `C:\Outils\AplatirPDF\`) puis lancez-le **depuis cet emplacement** (pas depuis le `.zip`).
   Le démarrage automatique mémorise cet emplacement : s'il change, relancez l'exe une fois à la main.

Les fichiers téléchargés depuis Actions sont conservés **90 jours**. Passé ce délai, cliquez sur
**Run workflow** (même onglet Actions) pour en fabriquer un nouveau.

Alternative : sur un PC Windows avec Python 3.10+ installé, double-cliquez sur
`build_windows.bat` ; l'exe est créé dans le dossier `dist`.

> Si Windows SmartScreen affiche « Windows a protégé votre PC » (exe non signé) : clic droit sur le
> `.zip` téléchargé → **Propriétés** → cocher **Débloquer** → OK, avant de le décompresser.

### Mettre à jour

Le numéro de version est affiché dans le titre de la fenêtre. Pour passer à une nouvelle version,
**lancez simplement le nouvel exe** : il demande à l'ancien de se fermer (sauf si celui-ci est en
train de traiter des fichiers : réessayez alors dans un instant), puis prend sa place.
Remplacez ensuite l'ancien fichier si vous les aviez rangés à deux endroits.

Cas particulier de la toute première version (sans numéro de version) : fermez-la d'abord
(clic droit sur son icône près de l'horloge → **Quitter**), puis lancez la nouvelle.

## Utilisation

1. Lancez `AplatirPDF.exe`. La fenêtre s'ouvre (la toute première fois).
2. Cliquez sur **Parcourir…** et choisissez le **dossier de sortie** (retenu pour la suite).
3. **Glissez-déposez** un ou plusieurs PDF — ou un dossier entier — sur la zone bleue.
   Chaque fichier est traité et le résultat s'affiche dans la liste. Vous pouvez aussi utiliser
   le bouton **Ajouter des PDF…**.
4. Fermez la fenêtre avec la croix : l'outil **reste actif dans la zone de notification**.
   Un clic sur son icône rouvre la fenêtre ; clic droit pour le menu
   (dossier de sortie, démarrage automatique, **Quitter**).

Par défaut l'outil **se lance au démarrage de Windows**, caché dans la zone de notification
(case « Lancer au démarrage de Windows » pour le désactiver). Sous Windows 11, l'icône peut se
trouver sous la flèche **^** ; faites-la glisser vers la barre pour la garder visible.
Si vous désactivez l'outil dans *Paramètres → Applications → Démarrage*, la case de la fenêtre le reflète
et la recocher le réactive.

La case **« Garder la fenêtre au premier plan »** (cochée par défaut) évite que la fenêtre passe sous
l'Explorateur pendant que vous cherchez vos fichiers ; décochez-la si elle vous gêne.

Lancer l'exe une 2ᵉ fois ne démarre pas une 2ᵉ copie : cela ramène simplement la fenêtre.
Glisser des PDF **directement sur l'icône du fichier `.exe`** les traite aussi.

## Les résultats dans la liste

| Résultat | Signification |
|---|---|
| ✔ **Aplati** | Signatures, tampons, annotations, champs gravés ; la ligne indique les **pages** concernées. |
| ✔ **Copié** | Rien à aplatir dans ce PDF : il est simplement copié sous le nouveau nom. |
| ⚠ **Aplati (image)** | Pour certaines pages, l'aplatissement normal changeait l'aspect : elles ont été **converties en image** (200 dpi, texte non sélectionnable) pour être fidèles. |
| ⚠ **À vérifier** | Ouvrez le PDF produit et contrôlez les pages citées (aspect différent, fichier source réparé automatiquement car abîmé, éléments restants…). |
| ✖ **Erreur** | Le fichier n'a pas été produit ; le message dit pourquoi (mot de passe, fichier abîmé, fichier de sortie ouvert dans Acrobat, formulaire XFA…). |
| — **Ignoré** | Ce n'est pas un PDF, ou son nom commence déjà par `[a]- `. |

Après l'enregistrement, l'outil **relit le fichier écrit** et le compare à l'original, page par page.

## Précisions

- **Noms en double** : si deux PDF de même nom viennent de dossiers différents (`OF-1\Rapport.pdf` et
  `OF-2\Rapport.pdf`), le second devient `[a]- Rapport (2).pdf` — aucun n'écrase l'autre. Reglisser *le même*
  fichier le remplace (la ligne le précise).
- **Signature numérique (certificat)** : elle perd sa valeur cryptographique en étant aplatie ; seule son apparence
  est conservée. C'est le but ici, mais gardez les originaux signés si vous devez prouver l'authenticité.
- **Formulaires XFA dynamiques** (Adobe LiveCycle) : leur contenu n'est pas dans les pages, l'outil le refuse. Ouvrez-les
  dans Adobe Reader, imprimez-les en PDF (*Microsoft Print to PDF*) puis déposez ce PDF.
- **PDF protégés** : un PDF qui demande un mot de passe pour s'ouvrir est refusé. Un PDF protégé en écriture seulement
  (mot de passe « propriétaire ») est traité et produit **sans** ces restrictions, pour pouvoir être fusionné.
- **Pièce jointe d'un e-mail** (Outlook) : enregistrez-la d'abord sur le Bureau, puis glissez-la — Windows ne permet pas
  de glisser directement une pièce jointe.
- Chemins : Windows limite le chemin complet à 259 caractères ; choisissez un dossier de sortie peu profond.

## Fichiers de l'outil et désinstallation

| Fichier | Contenu |
|---|---|
| `%APPDATA%\AplatirPDF\config.json` | vos réglages (dossier de sortie, options) |
| `%APPDATA%\AplatirPDF\aplatir.log` | journal détaillé (bouton **Journal**) |

Pour désinstaller : décochez « Lancer au démarrage de Windows », cliquez **Quitter**, puis supprimez `AplatirPDF.exe`
et le dossier `%APPDATA%\AplatirPDF`. Rien d'autre n'est installé.

## Dépannage

- **Le glisser-déposer ne marche pas** : n'exécutez pas l'exe « en tant qu'administrateur » —
  Windows interdit de glisser un fichier d'un Explorateur normal vers une application élevée.
  Utilisez le bouton **Ajouter des PDF…** en dernier recours.
- **Erreur « accès refusé… ouvert dans un autre programme »** : fermez le PDF dans Acrobat et recommencez.
- **L'icône n'apparaît pas au démarrage** : elle apparaît dès que la barre des tâches est prête ;
  sinon relancez l'exe.
- Une boîte « Aplatir PDF a rencontré une erreur » indique un souci interne : le détail est dans le journal.

## Pour les développeurs

```
pip install -r requirements-dev.txt
python -m pytest            # tests (les tests d'interface : APLATIR_GUI_TESTS=1 sous xvfb ou Windows)
python aplatir_tray.py      # lance l'application
AplatirPDF.exe --selftest rapport.txt   # auto-test sans interface (utilisé par la CI et par build.py)
```

Le dossier `tests/` contient aussi les cas limites mis au jour par l'audit ; les tests marqués `xfail` documentent
des limites connues et volontairement conservées (par exemple une page rendue en image quand un champ utilise
`/NeedAppearances`).

PyMuPDF est distribué sous licence AGPL-3.0 : un usage personnel ou interne est sans contrainte ; si vous
redistribuez l'exe à des tiers, fournissez aussi le code source.
