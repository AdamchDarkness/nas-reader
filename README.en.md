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
- **Webtoons**: a `Webtoons` (or `Manhwa`) folder opens as one continuous vertical strip.
- **Tolerant search** across titles, series, writers, artists, summaries and issue numbers (`#666`): `batmn`
  finds Batman. Combined filters (type, read / unread, downloaded, format, publisher) and recent searches.
- **Timeline** by decade and year, and **missing issues** shown in each series.
- **Book details** read from `ComicInfo.xml` (CBZ/CBR) and EPUB metadata: summary, writer, artist, year.
- **Duplicates grouped**: the same book in CBR and PDF shows up once, with both versions.
- **Reader**: single page, double page, vertical scrolling, webtoon mode, manga direction, zoom, magnifier,
  thumbnails, full screen. The manga direction is kept per series and read from ComicInfo. On the last page,
  the next issue opens after 8 seconds. Any page can become the book's cover.
  On Android: swipe to turn pages, pinch to zoom, orientation locked per series.
- **Progress, favorites, "to read" list, "up next"**, series marked as read.
- **Sync** between the PC, the phone and the tablet, through a file on the NAS.
- **Profiles**: each one has its own progress, lists and statistics ("Who's reading?" at launch).
- **Statistics**: pages, reading time, streaks, favorite series and authors, and a **year in review**.
- **Custom home page** (rows and order), **English interface**, **bug report** to copy or share.
- **Offline reading**: download a book or a whole series to the device to read it away from the NAS.
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

## Shared covers and list

The PC does the heavy work once and leaves it on the NAS, in
`\\lord_of_adam\Comics Mangas Livre\.nasreader`:

| File | Content |
|---|---|
| `library.json` | The list of books, after each scan made by the PC (or the Synology service) |
| `covers\` | One cover per book (about 60 KB each) |
| `meta.json` | Details read from ComicInfo and EPUB files |
| `userdata.json` | Progress, favorites, "to read" (sync) |

Phones and tablets take these files instead of walking the NAS and opening every comic over Wi-Fi. When a new
file lands on the NAS, the PC makes its cover on its next scan and puts it there too. While you read, the
Android app does no background work.

## Small Synology service (optional)

[`nas/nasreader-service.py`](nas/nasreader-service.py) runs the scan **on the NAS itself**, reading its disks
instead of going over the network, and puts `library.json` in `.nasreader`. The devices load this list at
launch instead of walking the folders. It only needs the Python 3 that ships with DSM 7 (no Docker, no
package), reads the folders every 30 minutes and rewrites the list only when a file changed. It changes
nothing else.

1. Copy `nasreader-service.py` to `\\lord_of_adam\Comics Mangas Livre\.nasreader\`.
2. DSM → Control Panel → Task Scheduler → Create → Triggered task → User-defined script.
   Event: **Boot-up**. User: `root` (or an account that can read every shared folder).
3. Script:
   ```
   python3 "/volume1/Comics Mangas Livre/.nasreader/nasreader-service.py" --nas lord_of_adam
   ```
4. Right-click the task → **Run** to start it without rebooting.

`--nas` must be the name typed on the PC (`\\lord_of_adam`): book ids depend on it.
`--share /volume1/Comics` limits the scan to some folders, `--every 60` changes the interval.

## Offline reading

In a book's details (ⓘ button) or with a right-click (long press on Android), choose **Télécharger** (Download).
A folder's "Télécharger la série" button takes every issue at once. Downloaded books carry a small ✓ badge and
are listed under **Téléchargés** (Downloads), with the space used and the space left.

Away from the NAS, a banner says so: downloaded books open as usual, the others are greyed out. Progress is
sent to the NAS once back home. The files live in the app's own storage
(`%LOCALAPPDATA%\NasReader\offline` on Windows) and are removed with it.

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
| B | Webtoon mode (strip without gaps) |
| Z | Fit height ↔ fit width |
| R | Manga direction (right to left) |
| T | Thumbnails |
| + / − / 0, Ctrl + wheel, double-click | Zoom (drag to move around) |
| L | Magnifier |
| F | Full screen |
| Esc | Close |
| Ctrl K or / | Search |

## What stays on the device

Comics stay on the NAS: they are **never copied in full** to the PC or the tablet, except the ones you
download for offline reading. The device only keeps what makes it fast:

| What | Size | Setting |
|---|---|---|
| Reading cache (pages of recently opened books) | limited, 8 GB by default | Settings → Reading cache, **0** = keep nothing |
| Covers (small pictures) | about 60 KB per book | "Clear" button, they come back from the NAS |
| Offline downloads | what you choose | Downloaded → Remove all |
| List, details, progress, settings, log | a few MB | — |

Settings → **Storage on this device** shows the space each one takes.
Windows: `%LOCALAPPDATA%\NasReader`. Android: the app's private storage, wiped when it is uninstalled.

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
