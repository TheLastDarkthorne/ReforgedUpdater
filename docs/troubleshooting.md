# Troubleshooting

This page lists common problems and how to fix them. In the window, the full
text of every message is in the activity log. To open the log, click
**Activity log** (the clock icon).

## The updater can't find your game

**Message:** `Could not find your WoW folder.`

The updater looks in a few usual places, and your game is somewhere else. Add
the game yourself in either of the following ways:

- In the window, click **Choose game folder**, or click **Add a game** (**+**).
- On the command line, run the following command:

  ```bash
  ReforgedUpdater games add NAME "FOLDER" --edition EDITION
  ```

  For the values to use, see
  [Look after several games](games-and-editions.md#look-after-several-games).

## The updater asks which vanilla server you play on

Every vanilla 1.12 client has the same `Wow.exe`, so the updater can't tell a
Kronos client from a Turtle WoW client. This question appears once for each
vanilla game:

- In the window, click **Kronos** or **Turtle WoW**.
- On the command line, run `ReforgedUpdater edition kronos` or
  `ReforgedUpdater edition turtle`.

For more information, see [Editions](games-and-editions.md#editions).

## A patch file is in use

**Message:** `Cannot replace patch-A.mpq because it is in use.`

World of Warcraft, or another program, has the patch file open. Close the game,
and then run the update again. The download isn't lost: the updater keeps the
finished file and installs it on the next attempt.

If the game is closed and the message stays, check for other programs that
might have the file open, such as an archive viewer or an antivirus scan.

## The drive is full

**Message:** `Not enough free space: … needed, … available on the client's drive.`

The updater needs room for every file that it's about to download, plus 256 MB.
Free up space on the drive that holds the `Data` folder, or move the `Data`
folder to another drive. For more information, see
[Data folders](games-and-editions.md#data-folders).

## Downloads fail or keep retrying

**Messages:** `Could not reach …` or `… failed after 5 attempts`

The downloads page or the file server didn't answer. Check your internet
connection, and then try again. Partial downloads are kept, so the next attempt
continues from where the last one stopped.

## The downloads page layout changed

**Messages:** `No download cards found - the page layout has changed.` or
`The page listed no .mpq downloads - the layout has probably changed.`

The updater reads the downloads page itself, because Project Reforged doesn't
publish a machine-readable list of patches. When the page is redesigned, the
updater stops with this message instead of reporting that nothing changed. The
updater needs a code change to read the new layout. For more information, see
[Update the downloads page reader](development.md#update-the-downloads-page-reader).

## The saved Data folder no longer exists

**Message:** `This game's saved Data folder no longer exists: …`

You moved the game's `Data` folder to another drive or folder, and that folder
isn't available any more. Do one of the following:

- If the folder is on a removable or network drive, reconnect the drive.
- To use the game's own `Data` folder again, run the following command:

  ```bash
  ReforgedUpdater data --default --game NAME --data "GAME_FOLDER\Data"
  ```

  Replace the following:

  - `NAME`: the game's nickname.
  - `GAME_FOLDER`: the folder that contains `Wow.exe`.

  The `--data` option lets the command run without the missing folder. The
  command then clears the saved setting. The `Data` folder in the game folder
  must exist.

## A patch shows Update ready right after you adopt it

The file that you adopted isn't the build that the downloads page offers now,
for example because you downloaded it before the last update. The updater
recorded it as an older build. To replace it with the published build, click
**Update**, or run `ReforgedUpdater update`.

## Patch-S matches neither variant

**Message:** `patch-S.mpq: content matches no current variant, so the updater cannot tell which one it is.`

Patch-S has two variants that use the same file name, and your file matches
neither published build. Do one of the following:

- If you know which variant you have, adopt it by name:
  `ReforgedUpdater adopt S` or `ReforgedUpdater adopt S-standalone`.
- To replace the file with a published build, install the variant that you want:
  `ReforgedUpdater install S` or `ReforgedUpdater install S-standalone`.

## Patches show updates after you switch servers

When you switch a vanilla game between Kronos and Turtle WoW, the patches that
are built for each server show as updates. This is expected: your files were
built for the other server. Update them to get the builds for your new server.

## The window can't open the command line

**Message:** `The command-line updater isn't here`

**Open the command-line updater** in the **More** menu needs
`ReforgedUpdater.exe` in the same folder as `ReforgedUpdaterGui.exe`. Copy the
command line into that folder, or build it with `dotnet build -c Release`.
