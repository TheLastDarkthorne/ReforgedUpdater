# Use the window

`ReforgedUpdaterGui.exe` shows your game's patches as cards and lets you
install, update, adopt, and remove them with one click. This page describes
each part of the window.

## Belora and the status bubble

Belora, the elf in the top-left corner, shows the state of your game at a
glance. The speech bubble next to her gives the details, and her expression
matches:

| Belora looks | Meaning |
| --- | --- |
| Smiling | Nothing to report, for example because no patches are installed yet. |
| Happy, with closed eyes and sparkles | Every installed patch is up to date. |
| Excited, with sparkles | Updates are ready, or the updater found patch files that you can adopt. |
| Bouncing | The updater is checking the site or downloading. |
| Flustered, with a sweat drop | Something went wrong. The bubble explains what. |

When the window opens, Belora greets you in Thalassian, the high elves'
language: "Bal'a dash, malanore!" means "Greetings, traveler!"

## The main button

The pink button in the top-right corner does the most useful thing for the
current game:

- **Install N selected**, **Update N selected**, or **Get N selected**:
  downloads the patches that you selected on their cards. For more
  information, see [Install several patches at once](#install-several-patches-at-once).
- **Update 1 patch** or **Update N patches**: downloads every installed patch
  that changed.
- **Install all patches**: appears on a game that has no patches yet, and
  installs every patch.
- **All caught up**: nothing needs downloading, so the button is unavailable.

**Check again** reads the downloads page again and asks the server about every
file. The window checks once when it opens, so use **Check again** when you
leave the window open for a long time.

## Patch cards

Each patch on the downloads page has a card. The cards are grouped the same way
as on the site, for example **Core Modules** and **Optional Enhancements**.

A card shows the patch letter, the patch name, a short description, and the
installed and latest versions. The label in the card's top-right corner shows
its state, and the button in the bottom-right corner fixes it:

| Label | Meaning | Button |
| --- | --- | --- |
| **Not installed** | The patch isn't in your `Data` folder. | **Install** |
| **Update ready** | A newer build is on the site, or your file isn't the published build. | **Update** |
| **Found in Data** | The file is in `Data`, but the updater didn't install it, so it doesn't know which build it is. | **Adopt** |
| **Missing** | The updater installed the patch, but the file is gone from `Data`. | **Download again** |
| **Changed** | The file's size no longer matches the file that the updater installed. | **Repair** |
| **Up to date** | The file is the published build. | None. The card shows **All good**. |

To see a patch's full description, its file name, and its ID for the command
line, hold the pointer over its card.

## Install several patches at once

Cards that have something to download, such as **Not installed** or **Update
ready**, have a round checkbox in the bottom-left corner. To download several
patches in one go, do the following:

1. Select the checkbox on each patch that you want, or click anywhere on the
   card outside its buttons. A selected card has a pink outline.
1. At the top of the window, click **Install N selected**. If your selection
   mixes new patches and updates, the button says **Get N selected**.
1. In the dialog, check the total download size, and then click the action
   again to start.

The updater downloads the patches one after another, in the order that the site
lists them. This is the same queue that `ReforgedUpdater install A C G` uses on
the command line.

Keep the following in mind:

- Some patches, such as Patch-S, have several variants that share one file.
  You can select only one variant of a patch. Selecting another variant clears
  the first one.
- Your selection stays after **Check again**. A patch that no longer needs
  downloading is cleared from the selection.
- If one patch in the selection can't be reached on the server, the dialog
  says so and the updater downloads the others.
- To clear the selection, click **Clear** next to the main button.

Before any download starts, a dialog shows the total size and the destination
folder. Click the action again, for example **Install**, to start, or click
**Not now** to cancel.

## Choose a game

The game picker in the strip under the bubble lists the following:

- Every game that you added, in the window or on the command line.
- The game that the window is in, if you copied it next to `Wow.exe` and didn't
  add that game. Its entry says **this app's folder**.

The updater doesn't search your computer for games.

When you pick a game, the window checks it and shows its patches. The window
opens the game that you used last, on the command line or in the window.

If you used a game on the command line with `--wow` but never added it, the
window doesn't list it. When you click **Add a game**, the window fills in that
game's folder for you.

## Add a game

1. Click **Add a game** (**+**) next to the game picker.
1. In **Game folder**, enter the folder that contains `Wow.exe`, or click
   **Browse** to pick it.

   The updater suggests a nickname from the folder name. If `Wow.exe` is a
   3.3.5 client, the updater also selects **WotLK 3.3.5a**.

1. In **Nickname**, keep the suggestion or type your own. A nickname has up to
   32 letters, digits, periods, hyphens, or underscores, for example `warmane`.
1. In **Patch set**, select the patch set that the game uses:

   - **WotLK 3.3.5a**: Wrath of the Lich King servers.
   - **Vanilla 1.12 · Kronos**: the standard 1.12 client, used by Kronos and
     other servers that aren't based on Turtle WoW.
   - **Vanilla 1.12 · Turtle WoW**: the Turtle WoW client, and servers based
     on it.

1. Click **Add game**.

The command line knows the game by the same nickname, for example
`ReforgedUpdater status --game warmane`.

## Choose the patch set

The patch set picker, next to the game picker, sets which downloads page the
updater reads for the current game. The updater remembers the choice in the
game's own folder.

Every vanilla 1.12 client has the same `Wow.exe`, so the updater can't tell a
Kronos client from a Turtle WoW client. The first time that you open a vanilla
game, the window asks **Which vanilla server is this for?** Click **Kronos** or
**Turtle WoW**.

For more information, see [Editions](games-and-editions.md#editions).

## Copy patches from another game

If you have another game that uses the same patch set, you can copy its patches
instead of downloading them again. For example, copy the patches from one WotLK
game to another WotLK game.

1. Click **More** (**⋯**), and then click **Copy patches from another game…**.
1. In **Copy from**, select the game to copy from. The list has only your other
   games that use the same patch set as this one.

   The window lists the patches that it would copy and their total size.
   Under **Left out**, it explains why it skips the others, for example because
   this game already has the patch.

1. Optional: select **Double-check each copy**. The window then reads every
   copied file back to make sure it was written correctly. This is slower, so
   it's off each time that you open the dialog.
1. Click **Copy N patches**.

A progress card shows the copy, and you can click **Stop**. Patches that are
already copied stay, and the patch in progress is dropped. When the copy ends,
the cards show the patches as installed. The updater copies only patches that
this game doesn't have, and never overwrites a file. For more information, see
[Copy patches from another game](games-and-editions.md#copy-patches-from-another-game).

If **Copy patches from another game…** is unavailable, you have only one game.
If the window says that there's no other game to copy from, none of your other
games use this game's patch set.

## Adopt patches that you already have

If you downloaded patches by hand before you used the updater, their cards show
**Found in Data**. To start tracking them without downloading anything again,
do either of the following:

- To adopt one file, click **Adopt** on its card.
- To adopt every file that the updater found, click **Adopt N files I found** in
  the strip under the bubble.

The updater reads each file and compares its content with the published build.
This takes about a minute for every 3 GB. Files that match are marked **Up to
date**. Files that don't match are marked **Update ready**, so that you can
replace them with the published build.

## Remove a patch

1. On the patch's card, click **Remove this patch** (the bin icon).
1. In the dialog, click **Remove**.

The updater deletes the patch file from your `Data` folder and stops tracking
it. You can install the patch again at any time.

## Follow a download

While the updater works, a progress card at the bottom of the window shows the
following:

- The patch that is downloading, and its position in the queue, for example
  **2 of 5**.
- The percentage, the amount downloaded, the speed, and the time left.

The Windows taskbar button shows the same progress.

To stop, click **Stop**. The updater keeps the partial file, and the next
download of that patch continues from where it stopped. If you close the window
during a download, the updater asks you to confirm first.

If the connection drops, the updater retries on its own. For more information,
see [Downloads](how-it-works.md#downloads).

## The More menu

The **More** button (**⋯**) at the right end of the strip opens the following
menu:

| Menu item | What it does |
| --- | --- |
| **Copy patches from another game…** | Copies patches that another game of yours already has, without downloading them. For more information, see [Copy patches from another game](#copy-patches-from-another-game). |
| **Install every patch** | Installs every patch that isn't installed. For a patch with several variants, such as Patch-S, the updater installs the first variant. |
| **Check file contents (slow)** | Reads every installed patch file and compares it with the published build. Files that differ are marked **Update ready**. |
| **Open the game folder** | Opens the game folder in File Explorer. |
| **Move the Data folder…** | Sets a different folder for this game's patch files. For more information, see [Data folders](games-and-editions.md#data-folders). |
| **Use the game's own Data folder** | Sets this game's patch folder back to `Data` in the game folder. |
| **Forget this game** | Removes the game from the list. The updater doesn't touch any files in the game folder. |
| **Match the Windows theme** | Makes the window follow the Windows light or dark mode setting again. |
| **Open the downloads page** | Opens the current patch set's downloads page in your browser. |
| **Open the command-line updater** | Opens a console that shows the command line's status for the current game. `ReforgedUpdater.exe` must be in the same folder as the window. |

To open the `Data` folder, click its path in the strip.

## Change the theme

The window follows the Windows **Choose your mode** setting for apps, and
switches when you change that setting. In dark mode, Belora changes her mint
tunic for a crimson and gold one.

To choose a theme yourself, click the sun or moon button in the title bar. The
window remembers your choice. To follow Windows again, click **More** (**⋯**),
and then click **Match the Windows theme**.

## Activity log

To see a record of everything the updater did, click **Activity log** (the clock
icon) in the strip. The log shows the same messages that the command line
prints, including warnings, such as a retried download.

When a task ends with warnings, the bubble says how many notes are in the
activity log.
