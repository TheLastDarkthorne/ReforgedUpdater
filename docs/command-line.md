# Command-line reference

`ReforgedUpdater.exe` does everything that the window does, and suits scripts,
scheduled tasks, and game launchers. This page lists its commands, options,
exit codes, and JSON output.

## Syntax

```
ReforgedUpdater [COMMAND] [MODULE_ID ...] [OPTIONS]
```

Replace the following:

- `COMMAND`: one of the commands in the following table. Without a command, the
  updater runs `status`.
- `MODULE_ID`: the patch letter from the downloads page, such as `A` or `N`. For
  more information, see [Module IDs](#module-ids).
- `OPTIONS`: any of the options in [Options](#options).

Options can appear anywhere on the line. An option that takes a value accepts
either `--game warmane` or `--game=warmane`.

## Commands

| Command | What it does |
| --- | --- |
| `status` | Compares the downloads page with your `Data` folder and prints a table. This is the default command. |
| `list` | Lists every patch that the downloads page offers, with its ID, version, and variants. |
| `install MODULE_ID ...` | Downloads and installs the named patches. Patches that are already up to date are skipped. |
| `install --all` | Installs every patch that isn't installed. For a patch with several variants, installs the first variant. |
| `update [MODULE_ID ...]` | Downloads every installed patch that changed. With IDs, updates only those patches. |
| `adopt [MODULE_ID ...]` | Starts tracking patch files that you downloaded by hand, without downloading them again. Without IDs, adopts every untracked file. |
| `copy --from NAME [MODULE_ID ...]` | Copies the patches that another game of yours has, instead of downloading them again. Both games must use the same patch set. Copies only the patches that this game doesn't have. With IDs, copies only those patches. For more information, see [Copy patches from another game](games-and-editions.md#copy-patches-from-another-game). |
| `remove MODULE_ID ...` | Deletes the patch files and stops tracking them. With `--keep-file`, only stops tracking them. |
| `verify` | Asks the server about every installed file and prints the status table. With `--deep`, also reads each file and compares its content with the published build. |
| `games` | Lists the games that you added. |
| `games add NAME FOLDER --edition EDITION` | Adds a game under a nickname. `--edition` is required. `--data FOLDER` sets its `Data` folder at the same time. |
| `games remove NAME` | Removes a game from the list. The updater doesn't touch its files. `games forget` does the same. |
| `path [FOLDER]` | Shows the game folder that the updater uses by default, or sets it. |
| `data [FOLDER]` | Shows or sets the current game's `Data` folder. `--default` sets it back to `Data` in the game folder. |
| `edition [EDITION]` | Shows or sets the current game's patch set: `wotlk`, `kronos`, or `turtle`. |
| `help` | Prints the built-in help. `--help`, `-h`, and `-?` do the same. |

Before `install`, `update`, and `copy` change anything, the updater lists the
files and the total size, and asks you to confirm. To skip the question, add
`--yes`.

## Options

| Option | What it does |
| --- | --- |
| `--game NAME` | Uses the game that you added under `NAME`. |
| `--from NAME` | With `copy`, the game to copy patches from. |
| `--verify-copy` | With `copy`, reads each copied file back from your drive and checks it. This catches a bad write, but makes the copy slower. |
| `--all-games` | Runs `status`, `update`, `adopt`, `verify`, or `list` for every game that you added. For more information, see [Run a command for every game](games-and-editions.md#run-a-command-for-every-game). |
| `--wow FOLDER` | Uses this game folder for one run. `--path FOLDER` does the same. |
| `--data FOLDER` | Uses this `Data` folder for one run, without saving it. |
| `--edition EDITION` | Uses this patch set, and remembers it for the game. |
| `--all` | With `install`, installs every patch that isn't installed. |
| `--yes`, `-y` | Doesn't ask for confirmation. |
| `--dry-run` | Shows what would be downloaded or copied, and changes nothing. |
| `--fast` | Skips the server checks and compares version labels only. |
| `--no-hash` | Skips the SHA-256 calculation after each download. |
| `--keep-file` | With `remove`, leaves the patch file in `Data`. |
| `--verify` | With `adopt`, identifies each file by its content instead of its size. `--hash` does the same. |
| `--deep` | With `verify`, reads each installed file and compares its content with the published build. |
| `--default` | With `data`, sets the `Data` folder back to `Data` in the game folder. `--reset` does the same. |
| `--json` | With `status`, prints machine-readable JSON instead of a table. |
| `--no-color` | Prints plain text without colors. |
| `--quiet`, `-q` | Prints only errors and warnings. |

Without `--game` or `--wow`, commands use the game that you used last, or else
the game that the updater is in, next to `Wow.exe`. The updater doesn't search
for games. For more information, see [How the updater finds your game](games-and-editions.md#how-the-updater-finds-your-game).

`--all-games` can't be combined with `--game`, `--wow`, `--data`, `--edition`,
or `--json`, because those options describe a single game.

## Module IDs

A module ID is the patch letter from the downloads page, such as `A`, `C`, or
`N`. IDs aren't case-sensitive, and the updater also accepts the patch name
(`Patch-A`) and the file name (`patch-A.mpq`).

Some patches have several variants that share one file. The first variant on
the downloads page keeps the plain letter, and the other variants get a suffix.
For example:

- On WotLK, Patch-S has `S` and `S-standalone`.
- On vanilla, Patch-L has `L` and `L-less-thicc`, and Patch-T has `T` and
  `T-ultra-base`.

To see every ID for your game, run `ReforgedUpdater list`.

## Status table

`status` and `verify` print one row per patch. The **STATE** column shows one of
the following:

| State | Meaning |
| --- | --- |
| `up to date` | The file is the published build. |
| `not installed` | The patch isn't in `Data`. |
| `UPDATE` | A newer build is on the site, or the file isn't the published build. |
| `UNTRACKED` | The file is in `Data`, but the updater didn't install it. Run `adopt` to track it. |
| `MISSING` | The updater installed the patch, but the file is gone from `Data`. |
| `CHANGED` | The file's size no longer matches the file that the updater installed. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success. For `status` and `verify`, nothing needs downloading. |
| `1` | An error. With `--all-games`, at least one game failed. |
| `2` | Updates are available. `status` and `verify` return this code when at least one patch needs downloading. With `--all-games`, the updater returns it when no game failed and at least one game returned `2`. |

The exit codes let a launcher or a scheduled task check for updates without
reading the output. For example, the following batch script updates only when
something changed:

```bat
ReforgedUpdater status --quiet
if %ERRORLEVEL% EQU 2 ReforgedUpdater update --yes
```

## JSON output

`ReforgedUpdater status --json` prints one JSON object:

```json
{
  "checkedAt": "2026-09-25T04:20:27.9221438Z",
  "updatesAvailable": 1,
  "modules": [
    {
      "id": "N",
      "patch": "Patch-N",
      "name": "Darker Nights",
      "file": "patch-N.mpq",
      "siteVersion": "1.0.0",
      "installedVersion": null,
      "state": "UpdateAvailable",
      "detail": "not the published build",
      "remoteSize": 2975223,
      "url": "https://example.com/patch-N.mpq"
    }
  ]
}
```

The `modules` array has the following fields:

| Field | Description |
| --- | --- |
| `id` | The module ID. |
| `patch` | The patch name, with the variant in parentheses when the patch has several. |
| `name` | The patch's title on the downloads page. |
| `file` | The patch file name in `Data`. |
| `siteVersion` | The version on the downloads page, or `null` when the page shows none. |
| `installedVersion` | The installed version, or `null` when the patch isn't installed or its build is unknown. |
| `state` | One of `UpToDate`, `NotInstalled`, `UpdateAvailable`, `Untracked`, `FileMissing`, or `SizeMismatch`. |
| `detail` | A short explanation of the state, or an empty string. |
| `remoteSize` | The file size on the server in bytes, or `-1` when the server wasn't asked. |
| `url` | The download address. |

`--json` implies `--quiet`, so the output is valid JSON. Warnings go to standard
error.

## Examples

Check what changed:

```bash
ReforgedUpdater
```

Register patches that you downloaded by hand, identified by their content:

```bash
ReforgedUpdater adopt --verify
```

Install the core visual patches:

```bash
ReforgedUpdater install A C G I
```

Install the audio pack that doesn't need Patch-M:

```bash
ReforgedUpdater install S-standalone
```

Update every game that you added, without questions:

```bash
ReforgedUpdater update --all-games --yes
```

Copy the patches from one game to another, without downloading them again:

```bash
ReforgedUpdater copy --from warmane --game octowow
```

Find files that are damaged or not the published build:

```bash
ReforgedUpdater verify --deep
```
