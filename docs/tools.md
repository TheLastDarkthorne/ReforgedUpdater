# Extra tools

The WotLK downloads page lists a few tools besides the patches: a program that
patches `Wow.exe`, a faster Direct3D layer, a fog effect, and an HD font
renderer. The updater downloads these tools from GitHub, puts their files in
your game folder, and keeps them up to date. You run the `Wow.exe` patcher
yourself, because it changes your game's executable.

The updater manages tools for WotLK games only. The vanilla pages list
different tools, and the updater doesn't manage those. For a vanilla game, the
window's **Tools and extras…** item and the `tools` command say so.

## The tools

| ID | Tool | Where its files go |
| --- | --- | --- |
| `patcher` | St0ny's AIO WoW EXE Patcher. Patches `Wow.exe` in one pass, including the loaders that the other tools use. | A `Reforged Patcher` folder inside the game folder. |
| `dxvk` | DXVK, the Reforged build. A faster Direct3D 9 layer. | `d3d9.dll`, next to `Wow.exe`. |
| `fog` | Volumetric Fog & Light Shafts. | `version.dll`, `CoAVolFog.dll`, `fogdata.bin`, `waterdata.bin`, and `CoAVolFog.ini`, next to `Wow.exe`. |
| `lexara` | Lexara. HD fonts for the whole interface. | `Lexara.dll` and `skia.dll`, next to `Wow.exe`. |

Each of these is someone else's project. Some private servers block the DLLs,
and the patcher's own documentation rates some of its patches as a ban risk.
Check your server's rules before you install them.

## What the updater does and doesn't do

The updater does the following:

- Reads the latest full release of each tool from its GitHub page. It skips
  pre-releases, such as a `build-23-test` release of the fog.
- Checks each download against the SHA-256 checksum that GitHub lists for it. If
  the checksum doesn't match, the updater doesn't install the file.
- Writes each file next to its destination first, and then swaps it in. If a
  swap fails, the updater puts the previous files back.
- Records every file that it places, so that it can update and remove them later.

The updater doesn't do the following:

- It doesn't run any tool. You start the patcher yourself.
- It doesn't replace a file that it didn't place. If `d3d9.dll` is already in
  the game folder from another source, the install stops and names the file.
  A file that is byte-for-byte the same as the new one is taken over instead.
- It doesn't overwrite your settings. `CoAVolFog.ini` is yours after the first
  install.

## Install the tools

In the window:

1. Click **More** (**⋯**), and then click **Tools and extras…**.
1. On the tool's card, click **Install**. The first time, a dialog shows what the
   tool does and where it comes from.

The card shows the download and then updates in place. When the patcher is
installed, its card shows **Run patcher**.

On the command line, name the tools or use `--all`:

```bash
ReforgedUpdater tools install patcher dxvk fog lexara
ReforgedUpdater tools install --all
```

The updater lists the tools, the download size, and where the files go, and then
asks you to confirm. To see the list without installing, add `--dry-run`.

## Run the patcher

Close World of Warcraft first. In the window, click **Run patcher** on the
patcher's card. On the command line, run the following command:

```bash
ReforgedUpdater tools run patcher
```

Both ask you to confirm, and then open the patcher on this game's `Wow.exe`. In
the patcher's menu, press **R** to select the Reforged preset, which is the
Project Reforged selection of patches. The updater doesn't select or confirm
anything for you.

Keep the following in mind:

- The first time, the patcher needs an original, unmodified `Wow.exe` for
  build 12340. It keeps backups of your `Wow.exe` and your choices in the
  `Reforged Patcher` folder, so each game has its own patcher folder.
- Each patcher update can change which patches exist. Run the patcher again
  after an update to apply the changes.
- The updater can read `Wow.exe` without running it. The patcher's card says
  whether the patcher has patched the file. When you come back to the window from
  the patcher, the card checks again. `ReforgedUpdater tools` shows the same
  information.

## Lexara and the patcher

The Reforged preset in the patcher turns on patch No. 30, which makes `Wow.exe`
load a file named `Lexara.dll`. The updater therefore installs Lexara's DLL as
`Lexara.dll`, not as `dinput8.dll` like Lexara's own page describes.

If the card for Lexara says that your `Wow.exe` doesn't load Lexara yet, run the
patcher and keep patch No. 30 selected. If a `dinput8.dll` from an earlier manual
install of Lexara is also in the game folder, delete it. Otherwise Lexara loads
twice.

## Fog settings

The fog reads its settings from `CoAVolFog.ini`, and the game applies changes
while it runs. Press **Ctrl+F7** in the game to open the fog's settings window.

The Project Reforged site publishes recommended settings for the fog. They
differ from the fog's own defaults. The updater uses them as follows:

- The first time that you install the fog, `CoAVolFog.ini` starts from the
  recommended settings. If you already have a `CoAVolFog.ini`, the updater keeps it.
- Updates to the fog's program files never replace `CoAVolFog.ini`.
- When the recommended settings change later, the fog's card and `tools` say so,
  and your file stays as it is.

To compare your file with the recommended settings, and to use them, click
**Recommended settings** on the fog's card, or run the following command:

```bash
ReforgedUpdater tools preset fog
```

The updater lists the settings that differ, and asks whether to replace your
file. If you agree, it keeps your old file next to it as `CoAVolFog.ini.bak`. If
you keep yours, the updater doesn't remind you again until the recommended
settings change.

## Update the tools

`ReforgedUpdater status` lists the tools that are installed and shows which have
an update. `ReforgedUpdater update` brings them up to date after the patches.
To update only the tools, run `ReforgedUpdater tools update`. In the window,
click **Update** on a card.

A tool also shows as an update when its author re-uploads files under the same
version. If a file that the updater placed is missing or changed, the card shows
**Needs repair**, and **Repair** installs it again.

## Remove a tool

Click **Remove** on the tool's card, or run `ReforgedUpdater tools remove TOOL`.
The updater deletes the files that it placed. It leaves in place any file that
you changed since, `CoAVolFog.ini`, and in the `Reforged Patcher` folder anything
that the patcher made, such as its saved choices and its `Wow.exe` backups.
Removing the patcher doesn't undo the changes that it made to `Wow.exe`. To
restore your original file, use the patcher's own menu first.

## Limits

- GitHub allows about 60 requests an hour from one network without an account.
  Each tool takes one request to check. If you reach the limit, the updater says
  so. Try again later.
- The updater doesn't know a tool's files unless it placed them, so it can't
  update a copy that you installed by hand. Delete the old files or move them
  aside, and then install the tool with the updater.
- The updater keeps a fixed list of each tool's files. If an author renames a
  file in a release, the install stops with a message instead of guessing.

For the commands and options, see the [command-line reference](command-line.md#tools).
