# Project Reforged Updater

Project Reforged Updater keeps the [Project Reforged](https://projectreforged.github.io/)
HD patches for World of Warcraft up to date. It supports Wrath of the Lich King
(WotLK) 3.3.5a and vanilla 1.12, for both Kronos and Turtle WoW. The updater
reads the public downloads page, compares it with the patch files in your
game's `Data` folder, and downloads only what changed.

The updater comes as two programs that share one engine and one settings file:

- `ReforgedUpdaterGui.exe`: a window with Belora, a small elf who shows how
  things stand.
- `ReforgedUpdater.exe`: a command line for scripts, scheduled tasks, and game
  launchers.

Each program is a single file with no installer. Both run on Windows 10 and
Windows 11 without any extra runtime.

## Features

- Installs, updates, and removes patches with one click or one command.
- Detects silent re-uploads on the server, not only new version numbers.
- Adopts patches that you downloaded by hand, and checks their content against
  the published build without downloading them again.
- Resumes interrupted downloads, and never leaves a half-replaced patch file.
- Looks after several games, each with its own patch set and `Data` folder.
- Follows the Windows light or dark mode setting.

## Get started

To build both programs, install the [.NET SDK](https://dotnet.microsoft.com/download)
version 5.0 or later, and then run the following commands in the repository
folder:

```bash
dotnet build -c Release
dotnet build gui -c Release
```

The programs are written to `bin/Release/net48/`. Run
`ReforgedUpdaterGui.exe`, add your game, and install your first patches. For
step-by-step instructions, see [Get started](docs/get-started.md).

## Documentation

| Page | Read it to |
| --- | --- |
| [Get started](docs/get-started.md) | Build the programs, add your game, and install your first patches. |
| [Use the window](docs/window.md) | Learn every part of the window: patch cards, games, the menu, and themes. |
| [Command-line reference](docs/command-line.md) | Look up commands, options, exit codes, and the JSON output. |
| [Games, editions, and Data folders](docs/games-and-editions.md) | Understand patch sets, look after several games, and move the `Data` folder. |
| [How the updater works](docs/how-it-works.md) | Learn how updates are detected, how downloads resume, and which files the updater writes. |
| [Troubleshooting](docs/troubleshooting.md) | Fix common problems, such as locked files or a missing game folder. |
| [Development](docs/development.md) | Change the code, the theme, or Belora. |

## License

This project is licensed under the [MIT License](LICENSE).

Project Reforged's patches and World of Warcraft belong to their owners. The
updater downloads the patches from the public Project Reforged site.
