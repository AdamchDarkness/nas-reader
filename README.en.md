# Nas Reader

[🇫🇷 Français](README.md) · 🇬🇧 English

A comics, manga, ebook and PDF reader for a home NAS, on **Windows** and **Android** (phone and tablet).
Nas Reader walks the NAS shares, sorts the library by itself (Marvel, DC, Independents…) and opens CBR, CBZ, CB7,
PDF and EPUB files directly, without copying anything by hand.

The interface is in French.

## Download

Latest version: **[Releases](https://github.com/AdamchDarkness/nas-reader/releases/latest)**

| File | For |
|---|---|
| `NasReader-Setup-x.y.z.exe` | Windows 10 / 11: installer, with automatic updates |
| `NasReader-x.y.z.apk` | Android 8 and later: phone and tablet |
| `NasReader-x.y.z-portable.exe` | Windows without installing (no automatic updates) |

## Features

- **NAS scan**: every share of `\\lord_of_adam` (configurable), skipping the `#recycle` bin and `_Doublons`.
- **Automatic sorting** from the folders: a `Comics`, `Mangas`, `Ebooks` or `Magazines` folder sets the category,
  and the sub-folders become publisher → character → series
  (`Comics\Marvel\Spider-Man\Amazing Spider-Man`).
- **Search** across titles, series, writers, artists and issue numbers (`#666`).
- **Book details** read from `ComicInfo.xml` (CBZ/CBR) and EPUB metadata: summary, writer, artist, year.
- **Duplicates grouped**: the same book in CBR and PDF shows up once, with both versions.
- **Reader**: single page, double page, vertical scrolling, manga direction, zoom, magnifier, thumbnails, full screen.
  On Android: swipe to turn pages, pinch to zoom.
- **Progress, favorites, "to read" list, "up next"**, series marked as read.
- **Sync** between the PC, the phone and the tablet, through a file on the NAS.
- **Automatic updates** from the GitHub releases.

## Install

### Windows

1. Download and run `NasReader-Setup-x.y.z.exe`. No administrator rights needed.
2. The app scans the NAS on first launch (about 1 minute for 10,000 files), then prepares covers in the background.

Windows uses the credentials already saved for `\\lord_of_adam`: nothing to type.

### Android

1. Download `NasReader-x.y.z.apk` on the phone or tablet and open it.
   Android asks to allow installing apps from this source: accept.
2. On first launch, open **Réglages** (Settings) and fill in:
   - **Adresse de la NAS** (NAS address): `192.168.11.108` by default (the app also tries `lord_of_adam.local`);
   - **Utilisateur** and **Mot de passe** (user and password): the same account used to open `\\lord_of_adam` from the PC.
3. Tap **Enregistrer et rescanner** (Save and rescan).

The app connects to the NAS directly over the home Wi-Fi (SMB), without going through the PC.

## Sync

Progress, favorites and the "to read" list are stored in
`\\lord_of_adam\Comics Mangas Livre\.nasreader\userdata.json`. Each device merges this file every 2 minutes and
after each reading session: for each book, the most recent change wins, including "mark as unread" and
"remove from favorites". The folder can be changed in Réglages → Synchronisation.

A book has the same id on every device (it is computed from its path on the NAS). When a file is moved or renamed,
its progress follows it on the next scan, matched by size, date and name.

## Updates

- **Windows** (installed version): the app checks the releases at startup and every 6 hours, downloads the new
  version in the background and installs it when the app closes, or right away with "Installer maintenant".
- **Android**: the new version is downloaded in the background. "Installer maintenant" opens Android's installer,
  which always asks for confirmation.

Automatic updates can be turned off in Réglages → Mises à jour.

## Reader shortcuts (Windows)

| Key | Action |
|---|---|
| ← → / Space | Previous / next page |
| S / D / W | Single / double page / vertical scrolling |
| Z | Fit height ↔ fit width |
| R | Manga direction (right to left) |
| T | Thumbnails |
| + / − / 0, Ctrl + wheel, double-click | Zoom (drag to move around) |
| L | Magnifier |
| F | Full screen |
| Esc | Close |
| Ctrl K or / | Search |

## Local files

- Windows: `%LOCALAPPDATA%\NasReader` (library, progress, covers, reading cache).
- Android: the app's private storage.

Uninstalling keeps this data. The shared copy of the progress stays on the NAS.

## Build from source

Requirements: .NET 10 SDK, Inno Setup 6, JDK 17 or later, Android SDK (platform 35), GitHub CLI.

```powershell
.\release.ps1 -Version 1.2.0            # builds everything into dist\
.\release.ps1 -Version 1.2.0 -Publish   # builds, then publishes release v1.2.0
```

The APK is signed with the key in `keys\`, which is not in git. **Keep a copy of that folder**: Android only
accepts an update signed with the same key.

### Code layout

| Folder | Content |
|---|---|
| `src/NasReader/` | Windows app: C# .NET 10, WinForms + WebView2, local ASP.NET Core server |
| `src/NasReader/wwwroot/index.html` | The interface, **shared** by Windows and Android |
| `android/` | Android app: Kotlin, WebView + local NanoHTTPD server |
| `installer/NasReader.iss` | Windows installer script (Inno Setup) |
| `release.ps1` | Builds and publishes releases |

Both apps expose the same local API (`/api/library`, `/api/book/{id}/page/{n}`, …) to the same interface.

### Libraries

- Windows: [SharpCompress](https://github.com/adamhathcock/sharpcompress) (ZIP, RAR, 7z), Windows.Data.Pdf,
  [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).
- Android: [smbj](https://github.com/hierynomus/smbj) (SMB 2/3), [libarchive](https://github.com/zhanghai/libarchive-android)
  (ZIP, RAR 4/5, 7z), PdfRenderer, [NanoHTTPD](https://github.com/NanoHttpd/nanohttpd).
- Interface: [epub.js](https://github.com/futurepress/epub.js) and [JSZip](https://github.com/Stuk/jszip).
