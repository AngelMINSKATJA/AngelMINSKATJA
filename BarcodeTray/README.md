# BarcodeTray - Code-barres Code 128 pour étiquettes Brother QL-800

Petit utilitaire Windows qui vit dans la zone de notification (à côté de l'horloge) :

1. vous tapez un texte,
2. **Générer** copie un code-barres **Code 128** (avec le texte lisible dessous) dans le presse-papier : il suffit de coller (Ctrl+V) dans Word ou Excel,
3. **Imprimer** envoie la même étiquette, sans aucune boîte de dialogue, sur une imprimante **Brother QL-800** (ruban 62 mm, coupe automatique),
4. **Étiquettes en série** : on copie un tableau Excel (plans de châssis), le programme imprime **une étiquette par plan** (type de châssis en haut à gauche, n° de plan en grand dessous, 62 x 40 mm) en une seule fois, sans retaper ni valider chaque étiquette. Voir [Étiquettes en série](#étiquettes-en-série-plans-de-châssis).

Un seul fichier, **`BarcodeTray.exe`** : pas d'installation, pas de droits administrateur, pas besoin d'installer .NET (le programme embarque tout ce qu'il lui faut, y compris son propre .NET 8 ; le .NET Framework 4.8 du poste n'est pas utilisé). Le fichier fait environ 65 à 70 Mo, c'est normal. Il fonctionne sous **Windows 10 ou 11, en 64 bits** (Windows 7 n'est pas pris en charge).

> **État des tests, en toute franchise** : à chaque modification, GitHub compile le programme et lance des tests automatiques (génération et relecture des codes-barres, presse-papier, réglages, démarrage automatique, choix du format de papier) ; **ne prenez que l'exécutable d'un lancement marqué d'une coche verte** (onglet *Actions*). En revanche, **la fenêtre « Étiquettes en série » n'a jamais été essayée sur un vrai poste Windows, ni avec une vraie Brother QL-800** (aspect de la fenêtre, longueur exacte et position du texte sur l'étiquette, comportement du pilote quand 90 étiquettes partent d'affilée) : les tests automatiques vérifient la lecture du tableau, le dessin de l'étiquette et le déroulement d'une série, pas le papier qui sort. La partie impression est la plus délicate (chaque pilote nomme ses formats de papier à sa façon). **Imprimez d'abord 2 ou 3 étiquettes** et comparez-les à celles de P-touch Editor avant de lancer toute la série. Si quelque chose ne va pas, voir [Si l'impression ne va pas](#si-limpression-ne-va-pas) : un réglage dans un petit fichier texte suffit en général.

## Sommaire

- [Récupérer BarcodeTray.exe](#récupérer-barcodetrayexe)
- [Premier lancement](#premier-lancement)
- [Utilisation au quotidien](#utilisation-au-quotidien)
- [Étiquettes en série (plans de châssis)](#étiquettes-en-série-plans-de-châssis)
- [Régler l'imprimante QL-800](#régler-limprimante-ql-800)
- [Si l'impression ne va pas](#si-limpression-ne-va-pas)
- [Réglages (settings.json)](#réglages-settingsjson)
- [Fichiers créés par le programme](#fichiers-créés-par-le-programme)
- [Désinstaller](#désinstaller)
- [Pour les développeurs](#pour-les-développeurs)

## Récupérer BarcodeTray.exe

Le programme est compilé automatiquement par GitHub (workflow *BarcodeTray*, dans ce dépôt).

**Option 1 - artefact d'un lancement du workflow** (nécessite d'être connecté à GitHub) :

1. Ouvrir <https://github.com/AngelMINSKATJA/AngelMINSKATJA/actions/workflows/barcode-tray.yml>.
2. Cliquer sur le lancement le plus récent portant une coche verte.
3. En bas de la page, section **Artifacts**, télécharger **`BarcodeTray-win-x64`** (c'est un fichier .zip qui contient `BarcodeTray.exe`).
4. Extraire `BarcodeTray.exe` dans un dossier de votre choix (par exemple `C:\Users\<vous>\BarcodeTray\`) : c'est tout.

Les artefacts sont conservés 90 jours. Pour en obtenir un nouveau : onglet **Actions**, workflow **BarcodeTray**, bouton **Run workflow** (ce bouton n'apparaît que lorsque le fichier du workflow est présent sur la branche principale du dépôt).

**Option 2 - Release GitHub (conservée sans limite de durée, téléchargeable sans compte)** :

1. Vérifier que le dossier `BarcodeTray` et le workflow sont bien **sur la branche principale (`main`) du dépôt** (fusionner d'abord la demande de modification, *pull request*, qui les contient). Une étiquette créée avant n'a rien à compiler : la Release resterait vide, sans message d'erreur.
2. Sur la page du dépôt, cliquer sur **Releases**, puis **Draft a new release**.
3. Dans **Choose a tag**, taper `barcodetray-v1.0.0` (le nom doit commencer par `barcodetray-v`), puis choisir **Create new tag on publish** ; laisser la cible sur `main`.
4. Cliquer sur **Publish release**.
5. **Patienter environ 10 minutes** (le temps des tests et de la compilation, visible dans l'onglet *Actions*), puis recharger la page : `BarcodeTray.exe` et son empreinte `BarcodeTray.exe.sha256` apparaissent sous **Assets**. Avant cela, la Release est publiée mais vide.

(Si l'étiquette est créée en ligne de commande avec `git tag` puis `git push`, le workflow crée lui-même la Release, avec l'empreinte SHA-256 dans la description.)

**Si le navigateur affiche « n'est pas couramment téléchargé »** (Edge, Chrome : le fichier n'est pas signé), choisir **Conserver**, puis **Conserver quand même**.

**Si Windows bloque le fichier** (voir aussi plus bas) : clic droit sur `BarcodeTray.exe`, **Propriétés**, cocher **Débloquer** en bas de l'onglet *Général*, puis OK.

## Premier lancement

1. Double-cliquer sur `BarcodeTray.exe`. Le premier démarrage prend quelques secondes (le programme dépose quelques composants dans le dossier temporaire de Windows) ; les suivants sont immédiats.
2. **Windows SmartScreen** peut afficher « Windows a protégé votre ordinateur » : le fichier n'est pas signé numériquement (une signature coûte plusieurs centaines d'euros par an). Cliquer sur **Plus d'infos**, puis sur **Exécuter quand même**.
3. Sur un **ordinateur d'entreprise**, l'antivirus ou une stratégie de sécurité (AppLocker, liste blanche de programmes) peut bloquer un programme inconnu. Dans ce cas il faut demander au service informatique d'autoriser `BarcodeTray.exe` ; le programme n'a besoin d'aucun droit administrateur et n'écrit que dans votre profil utilisateur.
4. La fenêtre s'ouvre. **Au tout premier lancement, le programme s'ajoute au démarrage de Windows** (case « Lancer au démarrage de Windows »). Décochez-la si vous ne le souhaitez pas. Si vous désactivez le programme dans le Gestionnaire des tâches (onglet *Démarrage*), la case apparaît décochée ; la cocher à nouveau réactive le démarrage automatique.
5. Choisissez votre imprimante dans la liste : la **Brother QL-800** est présélectionnée si elle est installée, et votre choix est mémorisé.

## Utilisation au quotidien

- **Texte** : taper (ou coller) le texte, 200 caractères au maximum. Code 128 ne gère que l'**ASCII** : lettres **sans accent**, chiffres, espace et ponctuation courante. Un caractère non géré (é, œ, €, retour à la ligne, espace insécable venue d'un copier-coller depuis Word ou Excel...) est refusé avec un message qui le nomme ; dans le cas d'une espace insécable, il suffit de la remplacer par une espace normale.
- **Générer** (ou touche Entrée) : l'image est placée dans le presse-papier. Aller dans Word ou Excel et faire **Ctrl+V**. L'image est enregistrée à 300 points par pouce : elle arrive à sa taille réelle (quelques centimètres de large) et peut être redimensionnée à la souris en gardant les proportions. Aucun fichier n'est créé.
- **Imprimer** : imprime tout de suite l'étiquette sur l'imprimante choisie, sans boîte de dialogue ni fenêtre « Impression en cours ». Il n'est pas nécessaire d'avoir cliqué sur Générer avant, et le presse-papier n'est pas modifié. La longueur de l'étiquette s'adapte à l'image et la QL-800 coupe toute seule.
- **Fermer la fenêtre** (croix, Alt+F4, Échap) ne quitte pas le programme : il reste dans la zone de notification. Clic gauche sur l'icône : masquer la fenêtre si elle est devant, sinon l'afficher et la ramener devant (même si elle est cachée derrière Word ou Excel). **Clic droit** sur l'icône :
  - **Ouvrir** ;
  - **Étiquettes en série (tableau Excel)…** (voir la section suivante) ;
  - **Lancer au démarrage de Windows** (case à cocher) ;
  - **Copier le diagnostic d'impression** (voir plus bas) ;
  - **Quitter** (la façon normale d'arrêter le programme ; « Fin de tâche » dans le Gestionnaire des tâches l'arrête aussi).
- **Windows 11** range souvent les nouvelles icônes dans le menu « ^ » (icônes masquées) de la barre des tâches. Pour la garder visible : la faire glisser vers la barre des tâches, ou **Paramètres, Personnalisation, Barre des tâches, Autres icônes de la barre d'état système**, puis activer BarcodeTray.
- Relancer `BarcodeTray.exe` alors qu'il tourne déjà ne crée pas de seconde copie : cela affiche simplement la fenêtre existante.

**Lisibilité selon la longueur du texte.** L'étiquette fait 62 mm de ruban, soit environ 58,9 mm imprimables (696 points à 300 dpi). Le programme prend les barres les plus larges qui tiennent : avec un texte court (jusqu'à environ 15 caractères, ou 30 chiffres) la barre la plus fine fait 3 points (0,25 mm) ; jusqu'à environ 26 caractères (52 chiffres) 2 points ; jusqu'à environ 58 caractères (116 chiffres) 1 point (0,085 mm), ce qui est très fin : le programme l'indique alors en orange (« barres très fines, la lecture peut échouer ») et il faut vérifier avec votre lecteur, ou raccourcir le texte. Au-delà, l'image dépasse la largeur du ruban et est réduite à l'impression : à éviter. Le texte lisible sous les barres reste complet : quand il est plus large que les barres, l'image est élargie (jusqu'à 696 points, la largeur du ruban) et les barres restent centrées.

## Étiquettes en série (plans de châssis)

Pour imprimer d'un coup toutes les étiquettes d'un tableau Excel, par exemple les plans de châssis : une étiquette de **62 x 40 mm** par plan, avec le **type de châssis** en haut à gauche (Arial 23) et le **n° de plan** centré en dessous en grand (Arial 61), comme dans P-touch Editor, mais **sans retaper ni valider chaque étiquette**.

### Ouvrir la fenêtre

- clic droit sur l'icône de la zone de notification, puis **Étiquettes en série (tableau Excel)…** ;
- ou le lien **Étiquettes en série…** tout en bas de la fenêtre principale.

La fenêtre s'appelle **Étiquettes en série — plans de châssis**. Comme la fenêtre principale, la fermer (croix, Échap) la masque seulement : elle garde sa liste tant que le programme tourne et se rouvre au même endroit. Elle peut être agrandie à la souris.

### 1. Copier le tableau dans Excel

1. Dans Excel, **sélectionnez tout le tableau, ligne d'en-tête comprise** (la ligne « Plan SE | HB1 | HBA | HB2 | VF1 | VF2 », avec la colonne « Plan SE » et toutes les colonnes de types). Le plus simple : cliquer dans une cellule du tableau puis **Ctrl+A**.
2. **Copiez** avec **Ctrl+C**. Si le tableau est **filtré**, seules les lignes visibles sont copiées : c'est voulu, les lignes masquées ne seront pas imprimées.

Il n'est pas nécessaire d'enregistrer ou d'envoyer le fichier : le programme lit seulement ce qui est dans le presse-papier. Les en-têtes de colonnes écrits à la verticale ne posent pas de problème.

### 2. Coller dans la fenêtre

Cliquez sur **Coller depuis Excel**, ou tapez **Ctrl+V** dans la fenêtre. La liste est **remplacée** par le nouveau tableau, sans question. Le message en bas indique le nombre d'étiquettes lues ; s'il est écrit **en orange**, le programme a ignoré quelque chose (par exemple une ligne sans croix) et le dit : lisez-le avant d'imprimer.

**Ce qui est lu dans le tableau :**

- la **première ligne** est l'en-tête : la première colonne (« Plan SE ») contient les plans, les autres titres sont les **types de châssis** ; le nombre entre parenthèses (« HB1 (20) ») n'est pas gardé : le type est **HB1** ;
- le **n° de plan** est la fin de la première cellule **sans le préfixe** : « 2020Q7E 22061 » donne **22061** ;
- le programme crée **une étiquette par croix** (**X**, x, ×) dans une colonne de type, **dans l'ordre du tableau**. Un même plan marqué dans deux colonnes (par exemple 22120 sous HB1 et sous HB2) donne donc **deux étiquettes**, une par type ;
- une colonne de type sans aucune croix (par exemple HBA) est simplement ignorée ;
- une ligne qui a un plan mais aucune croix est ignorée (avec une remarque orange) ; une croix sans n° de plan aussi ;
- à défaut de croix, une **simple liste à deux colonnes** « Type » puis « N° de plan » (une ligne par étiquette) est acceptée.

Au maximum 5 000 étiquettes par collage. Si le texte collé n'est pas un tableau reconnu, un message rouge explique quoi recopier, et la liste précédente est conservée.

### 3. Vérifier et corriger la liste

| Colonne | Contenu |
|---|---|
| **N°** | Numéro de la ligne (suit les suppressions). |
| **Type** | Type de châssis (en haut à gauche de l'étiquette). **Modifiable.** |
| **N° de plan** | Numéro imprimé en grand. **Modifiable.** |
| **État** | Vide, `en cours…`, `✔ imprimée`, `✖ échec`, `? à vérifier` ou `arrêtée` (voir plus bas). |

- **Double-clic** (ou **F2**) sur une cellule pour corriger une faute de frappe ; **Entrée** valide. Une étiquette corrigée repart à zéro (son état est effacé).
- **Suppr** supprime les lignes sélectionnées (Ctrl+clic ou Maj+clic pour en choisir plusieurs, **Ctrl+A** pour toutes). **Vider la liste** efface tout.
- Sous la liste, une ligne de résumé compte les étiquettes par type : « 88 étiquettes — HB1 : 44, HB2 : 19, VF1 : 12, VF2 : 13 ».
- À droite, **l'aperçu** montre la ligne sélectionnée (la première à défaut) telle qu'elle sera imprimée, avec en pointillés la zone que la QL-800 peut réellement imprimer (après une première impression, l'aperçu reprend la longueur de papier que le pilote a réellement acceptée). Un type ou un n° de plan vide, de plus de 30 caractères ou avec un caractère invisible est signalé ici et bloque l'impression.
- Si un n° de plan (ou un type) est trop large pour la zone imprimable (58,9 mm sur le ruban 62 mm), **sa taille est réduite automatiquement** (par pas de 0,5 point, jusqu'à 4 points au minimum) pour qu'il ne soit jamais coupé. Un numéro de 5 chiffres tient en général à 61 points, parfois à 60,5 : la différence ne se voit pas.

### 4. Imprimer

1. Choisissez l'imprimante (la **Brother QL-800** est présélectionnée) et, si besoin, le nombre de **Copies par étiquette** (1 à 99).
2. Cliquez sur :
   - **Imprimer tout** : toute la liste ;
   - **Imprimer la sélection** : seulement les lignes sélectionnées ;
   - **Imprimer à partir de la sélection** : de la première ligne sélectionnée jusqu'à la fin (pour **reprendre** après un arrêt ou une erreur).
3. Si la case **Confirmer avant d'imprimer** est cochée (réglage mémorisé), une seule question est posée : « Imprimer 88 étiquettes sur « Brother QL-800 » ? ». Décochez la case pour ne plus la voir.
   **Exception : des étiquettes déjà imprimées.** Si la série à lancer contient des lignes déjà marquées `✔ imprimée` (typiquement **Imprimer tout** après un arrêt ou une erreur), le programme le dit **toujours**, même avec la case décochée, car ces étiquettes sortiraient en double. Trois réponses : **Oui** (tout imprimer, les `✔ imprimée` sortent une deuxième fois), **Non** (imprimer seulement celles qui ne sont pas encore imprimées ; c'est la réponse proposée par défaut) ou **Annuler**. Si toutes les lignes choisies sont déjà imprimées, il n'y a que deux réponses (OK / Annuler) et le message rappelle qu'elles sortiraient en double.
4. L'impression part **en silence**, sans aucune boîte de dialogue de Windows ni de P-touch. La barre de progression et le message indiquent où en est la série (« Étiquette 12 / 88 (HB1 22072)… »), et la colonne **État** se remplit au fur et à mesure.

Chaque étiquette (et chaque copie) est envoyée comme un **travail d'impression séparé** : la QL-800 **coupe donc après chaque étiquette**. Dans les options du pilote, ne mettez pas de coupe « toutes les N étiquettes » : laissez **Couper à la fin / Cut at end** activé (voir [Régler l'imprimante QL-800](#régler-limprimante-ql-800)).

Pendant l'impression, seul le bouton **Arrêter** est actif et la fenêtre ne peut pas être fermée (le message le rappelle). Vous pouvez la réduire et continuer à travailler dans Excel.

### Arrêter, reprendre

- **Arrêter** : la série s'arrête **avant l'étiquette suivante** (celle qui est en cours d'envoi se termine). Le message dit combien d'étiquettes sont parties et sur quelle ligne reprendre. Pour reprendre : sélectionnez la ligne indiquée, puis **Imprimer à partir de la sélection**. Si vous cliquez sur **Imprimer tout** à la place, le programme prévient que des lignes sont déjà marquées `✔ imprimée` et demande s'il faut les refaire : répondez **Non** pour n'imprimer que le reste.
- **En cas d'échec** (imprimante éteinte, plus de ruban, capot ouvert, câble débranché...), la série **s'arrête tout de suite sur cette ligne** et le message rouge dit laquelle et pourquoi. Le programme **ne réessaie jamais tout seul** : il pourrait sortir deux fois la même étiquette. Corrigez le problème (rallumer, remettre un rouleau...), puis sélectionnez la ligne en échec (déjà sélectionnée pour vous) et cliquez sur **Imprimer à partir de la sélection** : les étiquettes déjà marquées `✔ imprimée` ne sont pas refaites.
- **`? à vérifier`** : Windows n'a pas confirmé l'envoi en 60 secondes (spouleur bloqué, imprimante qui ne répond plus). Regardez la **file d'impression de Windows** (Paramètres, Imprimantes et scanners, la QL-800, *Ouvrir la file d'attente*) : l'étiquette est peut-être sortie. Tant que Windows n'a pas répondu, les boutons d'impression restent inactifs (sinon l'étiquette sortirait en double plus tard). Quand c'est réglé, reprenez à cette ligne, ou à la suivante si l'étiquette est bien sortie.
- Si c'est la **préparation du papier**, avant la première étiquette, qui n'a pas reçu de réponse en 60 secondes (le message dit « Rien n'a été envoyé »), aucune étiquette n'est partie et aucune ligne n'est marquée `?`. Les boutons d'impression restent grisés (le message de collage le rappelle) jusqu'à ce que Windows réponde : un message « La préparation du papier ... a fini par aboutir » vous prévient, vous pouvez alors relancer l'impression.
- Si vous avez demandé plusieurs copies et que la série s'arrête au milieu d'une étiquette (`arrêtée` ou `✖ échec` avec « 2/3 copies »), le message le précise : la reprise réimprime **toutes** les copies de cette ligne.
- Si vous quittez le programme (menu de l'icône) pendant une série, il demande confirmation.

### Si les étiquettes en série ne sortent pas comme prévu

- **Rien ne sort, message rouge** : lisez-le, il dit quoi faire (imprimante hors connexion, rouleau absent, format de papier non pris en compte...). Pour un problème de papier ou de pilote, utilisez **Copier le diagnostic d'impression** (voir [Si l'impression ne va pas](#si-limpression-ne-va-pas)) : il liste les formats de papier du pilote et les réglages, et contient une ligne « Étiquettes en série (40 mm demandés) : ... » qui montre le papier qui serait utilisé pour la série. Une remarque commençant par **ATTENTION** dans cette ligne signifie que le pilote ne respecte pas la longueur demandée.
- **Après la première étiquette, le programme s'arrête avec un message orange** (« Première étiquette envoyée, puis pause... », suivi de « ... le pilote semble imposer sa propre longueur ») : le pilote ignore la longueur de 40 mm demandée et sort ses propres étiquettes (trop longues ou trop courtes). Regardez cette première étiquette : si elle vous convient, reprenez avec **Imprimer à partir de la sélection** (ce message n'apparaît qu'une fois par collage) ; sinon, réglez le format du pilote (*Options d'impression* de la QL-800 : format `62mm`, rouleau continu, pas de longueur fixe) et vérifiez que `PaperName` est vide dans `settings.json`.
- **« La zone imprimable de l'étiquette s'arrête à ... mm ... le bas du texte serait coupé »** (message rouge, **rien n'est envoyé**) : avant la première étiquette, le programme dessine la première ligne sur la vraie page du pilote et refuse la série si le texte ne tient pas dans sa longueur. Cas typique : `PaperName` renseigné avec une étiquette prédécoupée plus courte (par exemple `"62mm x 29mm"`, 29 mm) alors que le numéro est placé à 32 mm du haut. Videz `PaperName` dans `settings.json` (la longueur est alors `BatchLabelLengthMm`), ou augmentez `BatchLabelLengthMm`, ou remontez le texte (`BatchPlanBaselineMm`).
- **Étiquette trop longue ou trop courte** (le rouleau est coupé à une autre longueur que 40 mm) : réglez `BatchLabelLengthMm` dans `settings.json` (voir plus bas). Rappel : la QL-800 ne peut pas imprimer environ 3 mm en haut et en bas de l'étiquette ; le texte est placé comme dans P-touch, donc il n'est pas touché.
- **Texte coupé ou décalé** (le haut d'un grand numéro est rogné, ou le texte est collé au bord) : la page est calée sur le coin du papier ; si le pilote a d'autres marges que prévu, ajustez `BatchTypeBaselineMm` / `BatchPlanBaselineMm` (position verticale, en mm depuis le haut de l'étiquette), `BatchTypeLeftMm` (marge gauche du type) ou les tailles `BatchTypeFontPt` / `BatchPlanFontPt`. Un numéro trop large est réduit tout seul : un numéro plus petit que les autres n'est pas un défaut.
- **Une étiquette en trop ou en double** : voir `? à vérifier` plus haut ; contrôlez la file d'impression avant de relancer.
- **« Aucune croix (X) trouvée »** : le tableau collé n'a pas sa ligne d'en-tête, ou aucune croix n'est dans la sélection. Recopiez le tableau complet depuis Excel (Ctrl+A, Ctrl+C).
- **Le bouton « Coller depuis Excel » ne colle rien** : le presse-papier doit contenir du texte copié depuis des cellules Excel (pas une image, pas un fichier). Recommencez Ctrl+C dans Excel.
- Le journal (`%LOCALAPPDATA%\BarcodeTray\barcodetray.log`) note les nombres d'étiquettes, l'imprimante, le papier retenu et les erreurs, **jamais** les types ni les n° de plan.

## Régler l'imprimante QL-800

À faire une seule fois, dans Windows : **Paramètres, Bluetooth et appareils, Imprimantes et scanners, Brother QL-800, Options d'impression** (ou *Panneau de configuration, Périphériques et imprimantes*, clic droit sur la QL-800, *Options d'impression*). Les intitulés varient légèrement selon la version du pilote.

- Installer le **pilote d'imprimante Brother** officiel pour la QL-800 (site support.brother.com), pas seulement l'application P-touch Editor.
- Rouleau : **DK-22205** (62 mm, continu, blanc) bien installé dans l'imprimante.
- **Format du papier / Paper size : `62mm`** (le rouleau continu). Éviter les entrées du type « 62mm x 29mm » (étiquettes prédécoupées) et « 62mm(Red/Black) » (ruban bicolore).
- **Coupe automatique / Auto cut : activée**, et **Couper à la fin / Cut at end : activé**.
- **Qualité : priorité à la qualité d'impression** ; si le pilote propose un mode de demi-teinte, choisir **Binaire** (noir et blanc pur, pas de tramage) : c'est ce qui donne les barres les plus nettes.
- Ne pas modifier l'orientation (portrait) ni ajouter de marges : le programme les gère.

Le programme demande au pilote **son propre format `62mm`** (avec son identifiant interne) en précisant la longueur de l'image. Les pilotes Brother ignorent un papier « personnalisé » envoyé sans cet identifiant et reprennent alors leur format par défaut (par exemple 29 mm x 90 mm), ce qui ne correspond pas au rouleau de 62 mm : l'imprimante refuse alors d'imprimer. **Avant d'imprimer, le programme vérifie donc, sans rien imprimer, que le pilote a bien pris le format en compte** (zone imprimable de la largeur du rouleau) ; il essaie plusieurs façons de le demander et retient celle que le pilote accepte. Si aucune n'est acceptée, il affiche un message rouge et n'envoie rien. La longueur de l'étiquette inclut les marges non imprimables du pilote (environ 3 mm en haut et en bas ; mesurées auprès du pilote, ou estimées à environ 6 mm au total s'il ne les annonce pas, y compris avec `CheckPaper` à `false`) : pour les réduire, ouvrez les préférences d'impression de la QL-800 dans Windows et changez la **marge** (Margin).

**Avant chaque impression, le programme vérifie l'état de l'imprimante dans Windows** : imprimante éteinte ou débranchée, option « Utiliser l'imprimante hors connexion », file en pause, capot ouvert, plus de ruban, bourrage. Dans ces cas il affiche un message rouge et **n'envoie rien** (sinon Windows garderait l'étiquette en attente et, à l'allumage, toutes les étiquettes demandées entre-temps sortiraient d'un coup). Si Windows affiche un état erroné qui bloque l'impression à tort, mettez `CheckPrinterStatus` à `false` dans `settings.json` (voir plus bas). Si une impression met plus de 60 secondes à être confirmée, le bouton **Imprimer** reste inactif jusqu'à la réponse de Windows (l'étiquette peut encore sortir : regarder la file d'impression avant de réessayer).

## Si l'impression ne va pas

Symptômes possibles : étiquette trop longue ou trop courte, plusieurs étiquettes, étiquette blanche, image coupée ou décalée, message d'erreur rouge.

1. Faire **clic droit sur l'icône** puis **Copier le diagnostic d'impression**.
2. Coller le contenu (Ctrl+V) dans un message ou un bloc-notes et **l'envoyer** à la personne qui assure le support : il contient les imprimantes et les formats de papier vus par Windows, l'état de la file d'impression (hors connexion, capot ouvert...), la zone imprimable, les réglages et la fin du journal. Le texte de vos codes-barres n'est jamais enregistré.
3. En attendant, vous pouvez agir dans le fichier de réglages (voir ci-dessous), programme fermé (menu **Quitter**) :
   - étiquettes prédécoupées ou pilote qui nomme ses formats autrement : mettre dans `PaperName` le nom **exact** d'un format de la section « Formats de papier du pilote » du diagnostic (par exemple `"62mm x 29mm"`) ; la largeur de ce format sert alors de référence pour la vérification (un rouleau de 29 mm avec `"29mm x 90mm"` n'est pas refusé parce que `LabelWidthMm` vaut 62) ;
   - image trop grande ou trop petite : ajuster `ModulePx`, `BarHeightPx` ou `MaxWidthPx` ;
   - étiquette trop courte : augmenter `MinLabelLengthMm` ;
   - refus d'imprimer alors que l'imprimante est prête (« hors connexion », « capot ouvert »...) : mettre `CheckPrinterStatus` à `false`.
   - message « Le pilote ... n'a pas pris en compte le format de 62 mm » alors que le bon rouleau est chargé : lire dans le diagnostic la section « Test des formats de papier (sans imprimer) » (une ligne par essai, avec la zone imprimable obtenue) et l'envoyer ; pour essayer quand même sans la vérification, mettre `CheckPaper` à `false` (le diagnostic indique alors le format qui serait réellement utilisé) ;
   - message « ... a pris en compte le format de 62 mm, mais sa hauteur imprimable (... mm) est plus courte que l'étiquette » : le format choisi (typiquement une étiquette prédécoupée imposée par `PaperName`) est plus court que l'image ; choisir un format plus long, ou réduire `BarHeightPx`. `CheckPaper` n'y change rien.

## Réglages (settings.json)

Fichier texte JSON modifiable avec le Bloc-notes : `%APPDATA%\BarcodeTray\settings.json` (collez ce chemin dans la barre d'adresse de l'Explorateur). Le programme le crée au premier lancement. Fermer le programme (**Quitter**) avant de le modifier. Une clé absente prend sa valeur par défaut ; les valeurs absurdes sont ramenées dans des bornes raisonnables. **Si le fichier est illisible** (faute de frappe : guillemet ou virgule oublié, valeur du mauvais type), les valeurs par défaut sont utilisées pour cette session, une bulle d'information le signale, et une copie du fichier abîmé est gardée sous le nom `settings.json.invalide` (une copie déjà conservée n'est jamais écrasée : un contenu différent est gardé sous `settings.json.invalide-AAAAMMJJ-HHMMSS`). Ce n'est pas considéré comme un premier lancement : le démarrage automatique n'est pas réactivé, et le fichier abîmé n'est remplacé que lorsque vous modifiez un réglage dans le programme (par exemple en changeant d'imprimante). Corrigez la faute, ou supprimez le fichier, puis relancez le programme.

| Clé | Défaut | Rôle |
|---|---|---|
| `PrinterName` | *(vide)* | Imprimante mémorisée (nom exact). Vide : choix automatique. |
| `FirstRunDone` | `false` | `true` une fois le premier lancement traité (démarrage automatique activé une fois). |
| `Dpi` | `300` | Résolution de l'image (72 à 1200). 300 correspond à la QL-800. |
| `ModulePx` | `3` | Largeur souhaitée de la barre la plus fine, en points (1 à 10). Réduite automatiquement si le texte est long. |
| `MaxWidthPx` | `696` | Largeur maximale de l'image en points (zone imprimable du ruban 62 mm à 300 dpi). |
| `BarHeightPx` | `150` | Hauteur des barres en points (150 points = 12,7 mm). |
| `FontName` | `"Arial"` | Police du texte sous les barres. |
| `FontSizePt` | `11` | Taille de cette police en points typographiques (réduite si le texte est long). |
| `LabelWidthMm` | `62` | Largeur du ruban en mm, pour retrouver le bon format de papier du pilote. |
| `MinLabelLengthMm` | `15` | Longueur minimale de l'étiquette en mm. |
| `PaperName` | *(vide)* | Nom exact d'un format de papier du pilote à utiliser tel quel (sa propre largeur sert de référence à la vérification). Vide : choix automatique (ruban continu). **Laissez-le vide pour les étiquettes en série.** |
| `CheckPrinterStatus` | `true` | Vérifie l'état de l'imprimante dans Windows avant d'imprimer et refuse d'envoyer l'étiquette si elle ne pourrait pas sortir. `false` : aucun contrôle. |
| `CheckPaper` | `true` | Avant d'imprimer, essaie chaque façon de demander le papier au pilote (sans imprimer) et n'utilise que celle dont la zone imprimable correspond au rouleau ; la page est rallongée des marges non imprimables du pilote. `false` : le premier format est demandé sans vérification et sans refus possible (la page est quand même rallongée des marges du pilote, mesurées ou estimées). |
| `BatchLabelLengthMm` | `40` | **Étiquettes en série** : longueur de l'étiquette en mm (10 à 300). Sa largeur est celle du ruban (`LabelWidthMm`, 62 mm). |
| `BatchFontName` | `"Arial"` | Police du type de châssis et du n° de plan. |
| `BatchTypeFontPt` | `23` | Taille du type de châssis (en haut à gauche), en points (4 à 200). Réduite si le texte est trop large. |
| `BatchPlanFontPt` | `61` | Taille du n° de plan (centré), en points (4 à 300). Réduite automatiquement si le numéro ne tient pas dans la zone imprimable. |
| `BatchTypeLeftMm` | `1.9` | Distance entre le bord gauche de l'étiquette et le bord gauche du type, en mm (0 à 60). |
| `BatchTypeBaselineMm` | `10.6` | Distance entre le haut de l'étiquette et le bas des lettres (ligne de base) du type, en mm (1 à 300). |
| `BatchPlanBaselineMm` | `32.0` | Distance entre le haut de l'étiquette et le bas des chiffres du n° de plan, en mm (1 à 300). |
| `BatchConfirm` | `true` | Demande « Imprimer N étiquettes sur ... ? » avant d'imprimer plus d'une étiquette (case « Confirmer avant d'imprimer » de la fenêtre). |

Les étiquettes en série utilisent aussi `PrinterName`, `Dpi`, `LabelWidthMm`, `CheckPrinterStatus` et `CheckPaper` (mêmes rôles que pour les codes-barres). Laissez **`PaperName` vide** pour elles : un format prédécoupé imposerait sa propre longueur à toutes les étiquettes (et si cette longueur est plus courte que le texte, le programme refuse la série avec un message rouge plutôt que d'imprimer des numéros coupés).

Exemple :

```json
{
  "PrinterName": "Brother QL-800",
  "FirstRunDone": true,
  "ModulePx": 3,
  "BarHeightPx": 120,
  "PaperName": null
}
```

## Fichiers créés par le programme

| Emplacement | Contenu |
|---|---|
| `%APPDATA%\BarcodeTray\settings.json` | Vos réglages (et, seulement si le fichier a été abîmé, des copies `settings.json.invalide*`). |
| `%LOCALAPPDATA%\BarcodeTray\barcodetray.log` | Journal technique (environ 256 Ko au maximum, l'ancien est gardé dans `barcodetray.log.1`). Ne contient jamais le texte des codes-barres. |
| `%TEMP%\.net\BarcodeTray\...` | Quelques composants .NET extraits au premier démarrage. Peut être supprimé sans risque (il sera recréé). |
| Registre : `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, valeur `BarcodeTray` | Lancement au démarrage de Windows (uniquement pour votre compte, aucun droit administrateur). |

Si le programme refuse de démarrer sans rien afficher : vérifier que le dossier temporaire est accessible en écriture ; à défaut, définir la variable d'environnement utilisateur `DOTNET_BUNDLE_EXTRACT_BASE_DIR` vers un dossier où vous pouvez écrire.

## Désinstaller

1. Décocher **Lancer au démarrage de Windows** (fenêtre du programme ou menu de l'icône).
2. Clic droit sur l'icône, **Quitter**.
3. Supprimer `BarcodeTray.exe`.
4. Facultatif : supprimer les dossiers `%APPDATA%\BarcodeTray`, `%LOCALAPPDATA%\BarcodeTray` et `%TEMP%\.net\BarcodeTray`.

Il n'y a rien d'autre : pas d'installateur, pas de service, pas de pilote.

## Pour les développeurs

Structure (dossier `BarcodeTray/` de ce dépôt) :

| Chemin | Contenu |
|---|---|
| `src/Code128/` | Encodeur Code 128 (.NET 8, aucune dépendance), encodage optimal sur les jeux A, B et C. |
| `src/BarcodeTray/` | Application Windows Forms (C# 12, .NET 8), aucune dépendance NuGet. |
| `tests/Code128.Tests/` | Tests de l'encodeur (xUnit, tournent sous Linux et Windows ; ZXing.Net sert de décodeur indépendant). |
| `tests/BarcodeTray.Tests/` | Tests de l'application (xUnit, Windows uniquement : dessin GDI+, presse-papier, registre...). |
| `tools/make_icon.py` | Génère `src/BarcodeTray/Assets/app.ico` (Python, bibliothèque standard seulement). |
| `../.github/workflows/barcode-tray.yml` | Intégration continue. |

Commandes (SDK .NET 8 ; les projets Windows nécessitent Windows) :

```powershell
dotnet test tests/Code128.Tests                      # n'importe quel système
dotnet test tests/BarcodeTray.Tests -c Release       # Windows
dotnet publish src/BarcodeTray -c Release -o publish # Windows : publish\BarcodeTray.exe (fichier unique autonome)
```

La publication est paramétrée dans `src/BarcodeTray/BarcodeTray.csproj` : win-x64, autonome, fichier unique compressé, ReadyToRun, `InvariantGlobalization`, manifeste `asInvoker` (jamais d'élévation). Les symboles de débogage sont intégrés à l'exécutable, ce qui donne des numéros de ligne dans le journal en cas d'erreur.

Options de ligne de commande : `--tray` (démarre masqué dans la zone de notification ; utilisé par le démarrage automatique), `--selftest sortie.png` (sans interface : génère un code-barres d'exemple et l'enregistre ; utilisé par l'intégration continue pour prouver que l'exécutable publié démarre) et `--selftest-label sortie.png` (sans interface : dessine l'étiquette d'exemple « HB1 » / « 22062 » de 62 x 40 mm, vérifie que le texte est bien dans la zone imprimable de la QL-800 et l'enregistre en PNG de 732 x 471 points).

Le workflow GitHub Actions exécute les tests Code128 (Linux), les tests de l'application (Windows), publie l'exécutable, vérifie que le dossier de publication ne contient **que** `BarcodeTray.exe`, lance `--selftest` puis `--selftest-label` (dimensions du PNG de l'étiquette en série), démarre brièvement `--tray` (avertissement si le processus s'arrête, si une fenêtre ou une boîte d'erreur s'ouvre, ou si le journal contient une erreur), puis archive l'exécutable (`BarcodeTray-win-x64`, 90 jours). Un tag `barcodetray-v*` crée en plus une Release GitHub (avec `BarcodeTray.exe` et `BarcodeTray.exe.sha256`).
