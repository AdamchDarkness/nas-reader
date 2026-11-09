# Nas Reader

🇫🇷 Français · [🇬🇧 English](README.en.md)

Liseuse de comics, mangas, ebooks et PDF pour une NAS à la maison, sur **Windows** et **Android** (téléphone et tablette).
Nas Reader parcourt les partages de la NAS, classe tout seul la bibliothèque (Marvel, DC, Indépendants…) et ouvre
les CBR, CBZ, CB7, PDF et EPUB directement, sans rien copier à la main.

## Télécharger

Dernière version : **[Releases](https://github.com/AdamchDarkness/nas-reader/releases/latest)**

| Fichier | Pour |
|---|---|
| `NasReader-Setup-x.y.z.exe` | Windows 10 / 11 : installateur, avec mises à jour automatiques |
| `NasReader-x.y.z.apk` | Android 8 et plus : téléphone et tablette |
| `NasReader-x.y.z-portable.exe` | Windows sans installation (pas de mise à jour automatique) |

## Ce que fait l'appli

- **Scan de la NAS** : tous les partages de `\\lord_of_adam` (réglable), sans la corbeille `#recycle` ni `_Doublons`.
- **Classement automatique** d'après les dossiers : un dossier `Comics`, `Mangas`, `Ebooks` ou `Magazines` donne la
  catégorie, puis les sous-dossiers deviennent éditeur → personnage → série
  (`Comics\Marvel\Spider-Man\Amazing Spider-Man`).
- **Webtoons** : un dossier `Webtoons` (ou `Manhwa`) s'ouvre en bande verticale continue.
- **Recherche tolérante** dans les titres, séries, auteurs, dessinateurs, résumés et numéros (`#666`) : `batmn`
  trouve Batman. Filtres combinés (type, lu / non lu, téléchargé, format, éditeur) et recherches récentes.
- **Vue chronologique** par décennie et par année, et **numéros manquants** signalés dans chaque série.
- **Fiches** lues dans `ComicInfo.xml` (CBZ/CBR) et dans les métadonnées EPUB : résumé, scénario, dessin, année.
- **Doublons regroupés** : un même livre en CBR et en PDF n'apparaît qu'une fois, avec les deux versions.
- **Lecteur** : page simple, double page, défilement vertical, mode webtoon, sens manga, zoom, loupe, miniatures,
  plein écran. Le sens manga est retenu par série et pris dans ComicInfo. À la dernière page, le numéro suivant
  s'ouvre après 8 secondes. N'importe quelle page peut devenir la couverture du livre.
  Sur Android : glisser pour tourner les pages, pincer pour zoomer, orientation bloquée par série.
- **Progression, favoris, liste « À lire », « À suivre »**, séries marquées comme lues.
- **Synchronisation** entre le PC, le téléphone et la tablette, via un fichier sur la NAS.
- **Profils** : chacun sa progression, ses listes et ses statistiques (« Qui lit ? » au démarrage).
- **Statistiques** : pages, temps de lecture, jours d'affilée, séries et auteurs préférés, et **bilan de l'année**.
- **Accueil personnalisable** (rangées et ordre), **interface en anglais**, **rapport de bug** à copier ou partager.
- **Lecture hors ligne** : télécharge un livre ou une série entière sur l'appareil pour la lire loin de la NAS.
- **Mises à jour automatiques** depuis les releases GitHub.

## Installer

### Windows

1. Télécharge et lance `NasReader-Setup-x.y.z.exe`. Pas besoin de droits administrateur.
2. L'appli scanne la NAS au premier lancement (environ 1 minute pour 10 000 fichiers), puis prépare les couvertures
   en arrière-plan.

Windows utilise les identifiants déjà enregistrés pour `\\lord_of_adam` : rien à saisir.

### Android

1. Télécharge `NasReader-x.y.z.apk` sur le téléphone ou la tablette et ouvre-le.
   Android demande d'autoriser l'installation d'applis depuis cette source : accepte.
2. Au premier lancement, ouvre **Réglages** et renseigne :
   - **Adresse de la NAS** : `192.168.11.108` par défaut (l'appli essaie aussi `lord_of_adam.local`) ;
   - **Utilisateur** et **Mot de passe** : le même compte que pour ouvrir `\\lord_of_adam` depuis le PC.
3. Touche **Enregistrer et rescanner**.

L'appli se connecte directement à la NAS par le Wi-Fi de la maison (SMB), sans passer par le PC.

## Synchronisation

La progression, les favoris et la liste « À lire » sont enregistrés dans
`\\lord_of_adam\Comics Mangas Livre\.nasreader\userdata.json`. Chaque appareil fusionne ce fichier toutes les
2 minutes et après chaque lecture : pour chaque livre, la modification la plus récente l'emporte, y compris
« marquer comme non lu » et « retirer des favoris ». Le dossier se change dans Réglages → Synchronisation.

Un livre a le même identifiant sur tous les appareils (il est calculé à partir de son chemin sur la NAS). Si un
fichier est déplacé ou renommé, sa progression le suit au scan suivant grâce à sa taille, sa date et son nom.

## Couvertures et liste partagées

Le PC fait le gros du travail une seule fois et le dépose sur la NAS, dans
`\\lord_of_adam\Comics Mangas Livre\.nasreader` :

| Fichier | Contenu |
|---|---|
| `library.json` | La liste des livres, après chaque scan du PC (ou du service Synology) |
| `covers\` | Une couverture par livre (environ 60 Ko chacune) |
| `meta.json` | Les fiches lues dans ComicInfo et les EPUB |
| `userdata.json` | Progression, favoris, « À lire » (synchronisation) |

Le téléphone et la tablette reprennent ces fichiers au lieu de parcourir la NAS et d'ouvrir chaque comic par le
Wi-Fi. Quand un nouveau fichier arrive sur la NAS, le PC fait sa couverture au scan suivant et l'y dépose aussi.
Pendant la lecture, les appareils Android ne font plus rien en arrière-plan.

## Petit service sur la Synology (facultatif)

[`nas/nasreader-service.py`](nas/nasreader-service.py) fait le scan **directement sur la NAS**, en lisant ses
disques au lieu de passer par le réseau, et dépose `library.json` dans `.nasreader`. Les appareils reprennent
cette liste au démarrage au lieu de parcourir les dossiers. Le service ne demande que le Python 3 livré avec
DSM 7 (pas de Docker, pas de paquet), relit les dossiers toutes les 30 minutes et ne réécrit la liste que si un
fichier a changé. Il ne modifie rien d'autre.

1. Copie `nasreader-service.py` dans `\\lord_of_adam\Comics Mangas Livre\.nasreader\`.
2. DSM → Panneau de configuration → Planificateur de tâches → Créer → Tâche déclenchée → Script défini par
   l'utilisateur. Événement : **Démarrage**. Utilisateur : `root` (ou un compte qui lit tous les dossiers partagés).
3. Script :
   ```
   python3 "/volume1/Comics Mangas Livre/.nasreader/nasreader-service.py" --nas lord_of_adam
   ```
4. Clic droit sur la tâche → **Exécuter** pour la lancer sans redémarrer.

`--nas` doit être le nom tapé sur le PC (`\\lord_of_adam`) : les identifiants des livres en dépendent.
`--share /volume1/Comics` limite le scan à certains dossiers, `--every 60` change l'intervalle.

## Lecture hors ligne

Dans la fiche d'un livre (bouton ⓘ) ou par un clic droit (appui long sur Android), choisis **Télécharger**.
Le bouton « Télécharger la série » d'un dossier prend tous ses numéros d'un coup. Les livres téléchargés
portent un petit badge ✓ et se retrouvent dans **Téléchargés**, avec la place utilisée et la place libre.

Loin de la NAS, un bandeau le signale : les livres téléchargés s'ouvrent normalement, les autres sont grisés.
La progression est envoyée à la NAS au retour. Les fichiers sont gardés dans l'espace de l'appli
(`%LOCALAPPDATA%\NasReader\offline` sur Windows) et sont supprimés avec elle.

## Mises à jour

- **Windows** (version installée) : l'appli vérifie les releases au démarrage puis toutes les 6 heures,
  télécharge la nouvelle version en arrière-plan et l'installe à la fermeture, ou tout de suite avec le bouton
  « Installer maintenant ».
- **Android** : la nouvelle version est téléchargée en arrière-plan. Le bouton « Installer maintenant » ouvre
  l'installateur d'Android, qui demande toujours une confirmation.

Les mises à jour automatiques se désactivent dans Réglages → Mises à jour.

## Raccourcis du lecteur (Windows)

| Touche | Action |
|---|---|
| ← → / Espace | Page précédente / suivante |
| S / D / W | Simple / double page / défilement vertical |
| B | Mode webtoon (bande sans espace) |
| Z | Ajuster à la hauteur ↔ à la largeur |
| R | Sens manga (droite → gauche) |
| T | Miniatures |
| + / − / 0, Ctrl + molette, double-clic | Zoom (glisser pour se déplacer) |
| L | Loupe |
| F | Plein écran |
| Échap | Fermer |
| Ctrl K ou / | Rechercher |

## Ce qui est gardé sur l'appareil

Les comics restent sur la NAS : ils ne sont **jamais copiés en entier** sur le PC ou la tablette, sauf ceux
que tu télécharges pour la lecture hors ligne. L'appareil garde seulement de quoi aller vite :

| Quoi | Taille | Réglage |
|---|---|---|
| Cache de lecture (pages des livres ouverts récemment) | limité, 8 Go par défaut | Réglages → Cache de lecture, **0** = rien gardé |
| Couvertures (petites images) | environ 60 Ko par livre | bouton « Vider », elles reviennent de la NAS |
| Téléchargements hors ligne | ce que tu choisis | Téléchargés → Tout supprimer |
| Liste, fiches, progression, réglages, journal | quelques Mo | — |

Réglages → **Stockage sur cet appareil** affiche la place prise par chacun.
Windows : `%LOCALAPPDATA%\NasReader`. Android : stockage privé de l'appli, effacé à la désinstallation.

## Compiler depuis les sources

Prérequis : .NET 10 SDK, Inno Setup 6, JDK 17 ou plus récent, Android SDK (plateforme 35), GitHub CLI.

```powershell
.\release.ps1 -Version 1.2.0            # construit tout dans dist\
.\release.ps1 -Version 1.2.0 -Publish   # construit puis publie la release v1.2.0
```

L'APK est signé avec la clé du dossier `keys\`, qui n'est pas dans git. **Garde une copie de ce dossier** :
Android n'accepte une mise à jour que si elle est signée avec la même clé.

### Organisation du code

| Dossier | Contenu |
|---|---|
| `src/NasReader/` | Appli Windows : C# .NET 10, WinForms + WebView2, serveur local ASP.NET Core |
| `src/NasReader/wwwroot/index.html` | L'interface, **partagée** par Windows et Android |
| `android/` | Appli Android : Kotlin, WebView + serveur local NanoHTTPD |
| `nas/nasreader-service.py` | Service facultatif de la Synology (Python 3, sans dépendance) |
| `installer/NasReader.iss` | Script de l'installateur Windows (Inno Setup) |
| `release.ps1` | Construction et publication des releases |

Les deux applis exposent la même API locale (`/api/library`, `/api/book/{id}/page/{n}`, …) à la même interface.

### Bibliothèques utilisées

- Windows : [SharpCompress](https://github.com/adamhathcock/sharpcompress) (ZIP, RAR, 7z), Windows.Data.Pdf,
  [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).
- Android : [smbj](https://github.com/hierynomus/smbj) (SMB 2/3), [libarchive](https://github.com/zhanghai/libarchive-android)
  (ZIP, RAR 4/5, 7z), PdfRenderer, [NanoHTTPD](https://github.com/NanoHttpd/nanohttpd).
- Interface : [epub.js](https://github.com/futurepress/epub.js) et [JSZip](https://github.com/Stuk/jszip).
