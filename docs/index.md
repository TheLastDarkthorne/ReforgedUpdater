# Project Reforged Updater documentation

Project Reforged Updater keeps the [Project Reforged](https://projectreforged.github.io/)
HD patches for World of Warcraft up to date. It reads the public downloads page,
compares it with the patch files in your game's `Data` folder, and downloads only
what changed.

The updater comes as two programs that share one engine and one settings file:

- `ReforgedUpdaterGui.exe`: a window that you click through.
- `ReforgedUpdater.exe`: a command line for scripts, scheduled tasks, and game
  launchers.

## Guides

| Page | Read it to |
| --- | --- |
| [Get started](get-started.md) | Build the programs, add your game, and install your first patches. |
| [Use the window](window.md) | Learn every part of the window: patch cards, games, the menu, and themes. |
| [Command-line reference](command-line.md) | Look up commands, options, exit codes, and the JSON output. |
| [Games, editions, and Data folders](games-and-editions.md) | Understand patch sets, look after several games, and move the `Data` folder. |
| [How the updater works](how-it-works.md) | Learn how updates are detected, how downloads resume, and which files the updater writes. |
| [Troubleshooting](troubleshooting.md) | Fix common problems, such as locked files or a missing game folder. |
| [Development](development.md) | Change the code, the theme, or Belora, the mascot. |
