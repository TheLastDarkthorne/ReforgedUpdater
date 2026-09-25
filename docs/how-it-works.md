# How the updater works

This page explains how the updater decides that a patch needs downloading, how
it checks file contents without downloading them, how downloads resume, and
which files it writes.

## Why the updater is a separate program

The World of Warcraft addon API has no way to make web requests, and addons
can't write files outside `SavedVariables`. An addon written in Lua therefore
can't download or replace a patch archive, so the updater runs outside the game.

## Update detection

For each patch that it installs, the updater records the following:

- The version label from the downloads page.
- The `ETag` and `Last-Modified` headers that the server sent. An `ETag` is an
  identifier that the server derives from the file's content.
- The file size in bytes.
- A SHA-256 hash of the finished file.

A patch needs downloading when any of the following is true:

- The version on the downloads page differs from the installed version.
- The server's `ETag` differs from the recorded one. This check catches a
  re-upload that keeps the same version label.
- The file size on the server differs from the recorded size.
- The patch file is missing from `Data`, or its size changed.
- The updater proved that the file isn't the published build, for example
  during `adopt --verify` or `verify --deep`.

The `ETag` check is the reliable one. The version label makes the status table
easier to read.

## Content checks without downloading

The downloads page publishes no checksums, but the storage service behind it
does, in a form that the updater can recalculate from a file on disk. Every
file's `ETag` comes from its content:

- For a file uploaded in one piece, such as Patch-N, Patch-P, and Patch-U, the
  `ETag` is the MD5 hash of the file.
- For a file uploaded in parts, such as the large patches, the `ETag` is the
  MD5 hash of all the parts' MD5 hashes, followed by a hyphen and the number of
  parts.

The server doesn't say how big each part was, so the updater tries the part
sizes that common upload tools use and keeps the size that gives the right
number of parts. For this storage service, that size is 10 MiB.

The updater uses these checks in two places:

- `adopt --verify`, and **Adopt** in the window, which identify a file that you
  downloaded by hand.
- `verify --deep`, and **Check file contents (slow)** in the window, which find
  damaged files and files that aren't the published build.

Both read every byte of each file, which takes about a minute for every 3 GB.

When the updater can't reproduce an `ETag`, for example because of an unfamiliar
upload layout, it reports that it couldn't verify the file. It doesn't report
the file as damaged.

MD5 appears here only because the `ETag` format uses it. The check detects
damaged and outdated files, and isn't a security measure.

## Downloads

Patch archives can be several gigabytes, so downloads are built to survive
interruptions:

- A download that stops, because the connection drops or you click **Stop** or
  press Ctrl+C, resumes from where it stopped on the next attempt.
- If the file changed on the server since the download started, the server
  sends the whole new file. The updater never joins two different builds into
  one file.
- When a network error occurs, the updater retries up to four times. It waits 5,
  10, 15, and then 20 seconds before the retries. A download that receives no
  data for 60 seconds counts as failed.
- Before a download starts, the updater checks that the drive has enough free
  space for all the files, plus 256 MB.
- After a download finishes, the updater calculates the file's SHA-256 hash,
  unless you use `--no-hash`.

To install a finished download, the updater renames the old patch file, moves
the new file into place, and then deletes the old file. If the move fails, the
updater restores the old file, so the game always has a working patch.

The game locks its patch files while it runs. If a patch file is locked, the
updater stops with an error instead of replacing it. Close World of Warcraft,
and then run the update again.

## Files that the updater writes

| Path | Contents |
| --- | --- |
| `reforged-updater.json`, next to the programs | The games that you added, the game that you used last, and the window's theme. Both programs share this file. |
| `GAME_FOLDER\.reforged\state.json` | The game's edition and `Data` folder, and a record of each installed patch. |
| `.reforged\cache\` next to the `Data` folder | Partial downloads. You can delete this folder at any time. |
| `DATA_FOLDER\patch-*.mpq` | The patch files. |

If the folder that contains the programs is read-only, for example
`C:\Program Files`, the updater saves `reforged-updater.json` in
`%LOCALAPPDATA%\ProjectReforgedUpdater` instead.

Each game's records live inside the game folder, so a copied or moved game keeps
its update history.

`reforged-updater.json` also has a `CatalogUrl` setting that no command sets.
To read the patches from a mirror of the downloads page, set it to the mirror's
address by editing the file.
