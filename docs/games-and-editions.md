# Games, editions, and Data folders

Every game that the updater looks after has three settings: its folder, its
edition, and its `Data` folder. This page explains each one, and shows you how
to look after several games with one copy of the updater.

## How the updater finds your game

When you don't name a game, the updater uses the first of the following that
contains `Wow.exe` or a `Data` folder:

1. The game that you used last.
1. The folder that the updater is in, and each folder that contains it.
1. The install folder in the Windows registry.
1. `World of Warcraft`, `Games\World of Warcraft`, and
   `Program Files (x86)\World of Warcraft` on each fixed drive.

If you point the updater at a `Data` folder by mistake, it uses the folder that
contains `Data` as the game folder.

## Editions

An edition is the patch set that a game uses. It decides which downloads page
the updater reads:

| Edition | Client | Downloads page |
| --- | --- | --- |
| `wotlk` | Wrath of the Lich King (WotLK) 3.3.5a | [WotLK downloads](https://projectreforged.github.io/wotlk/downloads/) |
| `kronos` | Vanilla 1.12, standard client | [Vanilla downloads for Kronos](https://projectreforged.github.io/vanilla/downloads/kronos/) |
| `turtle` | Vanilla 1.12, Turtle WoW client | [Vanilla downloads for Turtle WoW](https://projectreforged.github.io/vanilla/downloads/turtle/) |

Vanilla has a separate patch set for each server, because Patch-A, Patch-I, and
Patch-M are built for each server's client. Use `kronos` for Kronos and other
standard vanilla servers, and `turtle` for Turtle WoW and servers based on it,
such as OctoWow.

The updater recognizes a WotLK client from `Wow.exe`. Every vanilla client
reports the same version, so the updater can't tell which server a vanilla
client is for, and asks you the first time. The answer is saved in the game's
own folder, so each game keeps its own edition.

To set the edition from the command line, run the following command:

```bash
ReforgedUpdater edition EDITION
```

Replace `EDITION` with `wotlk`, `kronos`, or `turtle`. To see the current
edition, run `ReforgedUpdater edition` without a value. In the window, use the
patch set picker next to the game picker.

If you set an edition that doesn't match `Wow.exe`, for example vanilla patches
for a 3.3.5 client, the updater warns you.

### Switch a game between Kronos and Turtle WoW

When you switch a vanilla game between `kronos` and `turtle`, the patches that
both servers share stay installed. The patches that are built for each server
show as updates, because your files were built for the other server.

### Downloads that the updater leaves alone

The Kronos downloads page also links two `.zip` files: VanillaFixes and a client
executable. They go outside `Data` and aren't patch archives, so the updater
doesn't install them. Install them by hand.

## Look after several games

You need only one copy of the updater for all your games. Each game keeps its
own edition, `Data` folder, and patch records in a `.reforged` folder inside the
game folder.

To add a game in the window, click **Add a game** (**+**). For more
information, see [Add a game](window.md#add-a-game).

To add a game from the command line, run the following command:

```bash
ReforgedUpdater games add NAME "FOLDER" --edition EDITION
```

Replace the following:

- `NAME`: a nickname of up to 32 letters, digits, periods, hyphens, or
  underscores, for example `warmane`. The name `all` isn't allowed.
- `FOLDER`: the folder that contains `Wow.exe`.
- `EDITION`: `wotlk`, `kronos`, or `turtle`.

For example, the following commands add three games:

```bash
ReforgedUpdater games add warmane "D:\Games\WotLK-Warmane" --edition wotlk
ReforgedUpdater games add stormforge "D:\Games\WotLK-Stormforge" --edition wotlk
ReforgedUpdater games add octowow "D:\Games\OctoWow" --edition turtle
```

Games that you add in the window and on the command line share one list, so
both programs know every game by the same nickname.

To work on one game, add `--game NAME` to any command:

```bash
ReforgedUpdater status --game octowow
```

To list your games, run `ReforgedUpdater games`. To remove a game from the list
without touching its files, run `ReforgedUpdater games remove NAME`.

### Run a command for every game

To run `status`, `update`, `adopt`, `verify`, or `list` for every game that you
added, use `--all-games`:

```bash
ReforgedUpdater update --all-games --yes
```

If one game fails, for example because its folder is missing, the updater still
runs the others. A summary at the end lists which games are up to date, which
have updates, and which failed.

`install` and `remove` work on one game at a time, because each game has its own
set of patches.

## Data folders

The updater writes patch files to the `Data` folder inside the game folder. If
your patches live somewhere else, for example on another drive or in a launcher's
own folder layout, set a different `Data` folder for that game.

To set the `Data` folder in the window, click **More** (**⋯**), and then click
**Move the Data folder…**. To go back to the game's own folder, click
**Use the game's own Data folder**.

To set the `Data` folder from the command line, run the following command:

```bash
ReforgedUpdater data "FOLDER"
```

Replace `FOLDER` with the folder that holds the patch files. Add
`--game NAME` to set it for a game other than the one that you used last.

The command line also has the following related options:

- To see the current setting, run `ReforgedUpdater data` without a folder.
- To set it back to `Data` in the game folder, run
  `ReforgedUpdater data --default`.
- To use a different folder for one run without saving it, add `--data FOLDER`
  to any command.

Each game saves its own `Data` folder, so a setting for one game doesn't affect
the others. Partial downloads are kept next to the `Data` folder, so the final
move of a finished download stays on one drive.
