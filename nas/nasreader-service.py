#!/usr/bin/env python3
"""
Nas Reader - small service for the Synology.

It lists the comics, manga and books directly on the NAS disks and writes the list
to the .nasreader folder, next to the sync file. The PC, the phone and the tablet
then load this one small file at start instead of walking every folder over the network.

Only the Python 3 that comes with DSM 7 is needed (no Docker, no package).
It reads the shared folders and writes only .nasreader/library.json.

Install (DSM > Control Panel > Task Scheduler > Create > Triggered task > User-defined script):
    event: Boot-up, user: root (or a user who can read every shared folder)
    script: python3 "/volume1/Comics Mangas Livre/.nasreader/nasreader-service.py"

Options:
    --nas NAME       network name of the NAS, as typed on the PC (default: the host name)
    --sync DIR       the .nasreader folder (default: <share Comics Mangas Livre>/.nasreader)
    --share DIR      a shared folder to scan, repeatable (default: every shared folder)
    --exclude NAME   a folder name to skip, repeatable (added to #recycle, @eaDir...)
    --every MINUTES  minutes between two scans (default 30)
    --once           scan once and stop
"""
import argparse
import glob
import hashlib
import json
import os
import socket
import sys
import time

EXTENSIONS = {".cbz", ".cbr", ".cb7", ".cbt", ".cba", ".pdf", ".epub", ".mobi", ".azw3"}
EXCLUDE = {"#recycle", "@eadir", "#snapshot", "_doublons", ".trash", "$recycle.bin"}
# Same table as Library.cs / Library.kt: folder names that mark a section of the library.
SECTIONS = {
    "comics": "comic", "comic": "comic", "bd": "comic", "bandes dessinées": "comic",
    "mangas": "manga", "manga": "manga",
    "webtoons": "webtoon", "webtoon": "webtoon", "manhwa": "webtoon",
    "ebooks": "ebook", "ebook": "ebook", "livres": "ebook", "romans": "ebook",
    "magazines": "magazine", "magazine": "magazine", "presse": "magazine",
}
SYNC_SHARE = "Comics Mangas Livre"
REFRESH_SECONDS = 6 * 3600  # rewrite the list at least this often: the PC trusts a list younger than 12 hours


def log(msg):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)


def book_id(unc):
    """Same id as the apps: SHA-1 of the lower-cased Windows path, first 8 bytes in hex."""
    return hashlib.sha1(unc.lower().encode("utf-8")).hexdigest()[:16]


def shared_folders():
    """Every shared folder of every volume, skipping DSM's own (@appstore, #recycle...)."""
    out = []
    for vol in sorted(glob.glob("/volume[0-9]*")):
        try:
            names = sorted(os.listdir(vol))
        except OSError:
            continue
        for n in names:
            p = os.path.join(vol, n)
            if n[:1] in "@#." or not os.path.isdir(p) or n.lower() in ("lost+found", "homes"):
                continue
            out.append(p)
    return out


def classify(nas, share_path, folder_parts, name, st):
    """Mirror of Library.Classify on the PC."""
    stem, ext = os.path.splitext(name)
    fmt = ext[1:].lower()
    share = share_path.replace("\\", "/").rstrip("/").split("/")[-1]
    folders = [share] + folder_parts
    kind, crumbs = None, []
    for i, f in enumerate(folders):
        k = SECTIONS.get(f.lower())
        if k is None:
            continue
        if kind == "ebook" and k == "ebook":
            continue
        kind, crumbs = k, folders[i + 1:]
        if k != "ebook":
            break
    for i, f in enumerate(folders):
        if SECTIONS.get(f.lower()) == "webtoon":
            kind, crumbs = "webtoon", folders[i + 1:]
            break
    sorted_ = kind is not None
    if kind is None:
        kind = "comic" if fmt in ("cbz", "cbr", "cb7", "cbt", "cba") else "ebook" if fmt in ("epub", "mobi", "azw3") else "pdf"
        crumbs = folders
    elif kind == "ebook" and fmt in ("cbz", "cbr", "cb7", "cbt", "cba"):
        kind = "comic"
    clean = []
    for c in crumbs:
        if not clean or clean[-1].lower() != c.lower():
            clean.append(c)
    unc = "\\\\" + nas + "\\" + "\\".join(folders + [name])
    return {
        "id": book_id(unc), "path": unc, "title": stem, "kind": kind, "fmt": fmt,
        "size": st.st_size, "mtime": st.st_mtime_ns // 1_000_000, "crumbs": clean, "sorted": sorted_,
    }


def scan(nas, shares, exclude):
    books = []
    for share in shares:
        for dirpath, dirnames, filenames in os.walk(share):
            dirnames[:] = [d for d in dirnames if not d.startswith(".") and d.lower() not in exclude]
            rel = os.path.relpath(dirpath, share)
            parts = [] if rel == "." else rel.split(os.sep)
            for f in filenames:
                if f.startswith(".") or os.path.splitext(f)[1].lower() not in EXTENSIONS:
                    continue
                try:
                    st = os.stat(os.path.join(dirpath, f))
                except OSError:
                    continue
                books.append(classify(nas, share, parts, f, st))
    return books


def write_list(sync, books):
    os.makedirs(sync, exist_ok=True)
    dst = os.path.join(sync, "library.json")
    tmp = dst + ".nas.tmp"
    data = {"books": books, "scannedAt": int(time.time() * 1000), "by": "nas"}
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, separators=(",", ":"))
    os.replace(tmp, dst)


def main():
    ap = argparse.ArgumentParser(description="Lists the books on the NAS for Nas Reader.")
    ap.add_argument("--nas", default=socket.gethostname())
    ap.add_argument("--sync")
    ap.add_argument("--share", action="append")
    ap.add_argument("--exclude", action="append", default=[])
    ap.add_argument("--every", type=float, default=30)
    ap.add_argument("--once", action="store_true")
    a = ap.parse_args()

    shares = [s.rstrip("/") for s in (a.share or shared_folders())]
    if not shares:
        sys.exit("No shared folder found under /volume*. Give them with --share.")
    sync = a.sync
    if not sync:
        main_share = next((s for s in shares if os.path.basename(s).lower() == SYNC_SHARE.lower()), None)
        if not main_share:
            sys.exit(f'Shared folder "{SYNC_SHARE}" not found. Give the .nasreader folder with --sync.')
        sync = os.path.join(main_share, ".nasreader")
    exclude = EXCLUDE | {e.lower() for e in a.exclude}
    log(f"Nas Reader service · NAS \\\\{a.nas} · {len(shares)} shared folders · list in {sync}")

    last_key, last_write = None, 0.0
    while True:
        try:
            t = time.time()
            books = scan(a.nas, shares, exclude)
            books.sort(key=lambda b: b["path"].lower())
            key = hashlib.sha1(json.dumps(books, ensure_ascii=False).encode("utf-8")).hexdigest()
            if key != last_key or time.time() - last_write > REFRESH_SECONDS:
                write_list(sync, books)
                last_key, last_write = key, time.time()
                log(f"{len(books)} books listed in {time.time() - t:.1f} s, list written")
        except Exception as e:  # keep running: the next round may work (a volume was busy...)
            log(f"scan failed: {e!r}")
        if a.once:
            break
        time.sleep(max(60, a.every * 60))


if __name__ == "__main__":
    main()
