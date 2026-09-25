# Get started

This page shows you how to build the updater, add your World of Warcraft game,
and install your first patches.

## Before you begin

To run the updater, you need Windows 10 or Windows 11. Both include
.NET Framework 4.8, which is the only runtime that the updater uses, so players
don't install anything else.

To build the updater from source, you also need the following:

- The [.NET SDK](https://dotnet.microsoft.com/download), version 5.0 or later.
- A copy of this repository.

## Build the programs

1. Open a terminal in the repository folder.
1. Build the command line:

   ```bash
   dotnet build -c Release
   ```

1. Build the window:

   ```bash
   dotnet build gui -c Release
   ```

Both programs are written to `bin/Release/net48/`:

- `ReforgedUpdater.exe`, the command line.
- `ReforgedUpdaterGui.exe`, the window.

Each program is a single file with no installer. You can copy either one, or
both, to any folder. If you use both, keep them in the same folder so that they
share the `reforged-updater.json` settings file and see the same games.

## Install patches with the window

1. Run `ReforgedUpdaterGui.exe`.

   Belora, the elf in the top-left corner, greets you and looks for your game.
   The updater checks the folder that the program is in, the folders that
   contain it, the Windows registry, and common install folders such as
   `C:\World of Warcraft`.

1. If the updater doesn't find your game, click **Choose game folder**. To add
   a game other than the one it found, click **Add a game** (**+**) next to the
   game picker. Then do the following:

   1. In **Game folder**, enter or browse to the folder that contains `Wow.exe`.
   1. In **Nickname**, keep the suggested name or type a short one, such as
      `warmane`.
   1. In **Patch set**, select the patch set that your game uses. For more
      information, see [Editions](games-and-editions.md#editions).
   1. Click **Add game**.

1. If the updater asks **Which vanilla server is this for?**, click **Kronos**
   or **Turtle WoW**. The updater remembers your answer for that game.
1. Install patches in either of the following ways:

   - To install one patch, click **Install** on its card.
   - To install every patch, click **Install all patches** at the top of the
     window.

1. In the confirmation dialog, check the download size, and then click
   **Install**.

The progress card at the bottom of the window shows the speed and the time
left. When the download finishes, each installed patch card shows
**Up to date**.

## Install patches from the command line

1. Copy `ReforgedUpdater.exe` into your game folder, next to `Wow.exe`.
1. Open a terminal in that folder.
1. Check what the site offers and what you have installed:

   ```bash
   ReforgedUpdater
   ```

1. If the updater reports a vanilla 1.12 client, tell it which server you play
   on:

   ```bash
   ReforgedUpdater edition turtle
   ```

   Use `kronos` for Kronos and other standard vanilla servers, and `turtle` for
   Turtle WoW and servers based on it.

1. Install the patches that you want, identified by their letters on the
   downloads page:

   ```bash
   ReforgedUpdater install A C G I
   ```

   To install every patch, run `ReforgedUpdater install --all` instead.

1. When the updater lists the files and the download size, type `y` and press
   Enter.

## Keep your patches up to date

When Project Reforged publishes a new build, update in either of the following
ways:

- In the window, click **Update**. The button shows how many patches changed,
  for example **Update 2 patches**.
- On the command line, run `ReforgedUpdater update`.

Close World of Warcraft before you update. The game locks its patch files while
it runs, and the updater doesn't replace a locked file.

## What's next

- To learn every part of the window, see [Use the window](window.md).
- If you already downloaded patches by hand, see
  [Adopt patches that you already have](window.md#adopt-patches-that-you-already-have).
- If you play on several servers, see
  [Look after several games](games-and-editions.md#look-after-several-games).
