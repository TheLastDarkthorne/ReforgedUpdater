# Development

This page explains how the code is organized, and how to change the window's
theme, Belora, and the downloads page reader.

## Build and run

To build both programs, run the following commands in the repository folder:

```bash
dotnet build -c Release
dotnet build gui -c Release
```

The first command builds `ReforgedUpdater.csproj`, the command line. The second
builds `gui/ReforgedUpdater.Gui.csproj`, the window. Both write to
`bin/Release/net48/`, so the two programs share one `reforged-updater.json`
during development, as they do for players.

Both projects target .NET Framework 4.8 and use only libraries that are part of
it, so each program stays a single file. The
`Microsoft.NETFramework.ReferenceAssemblies` package lets any .NET SDK from
version 5.0 build them without the .NET Framework developer pack.

## Project layout

The window compiles every file in `src/` except `Program.cs`, so both programs
run the same engine.

| Path | Role |
| --- | --- |
| `src/Program.cs` | The command line: argument parsing, commands, and output. |
| `src/Workspace.cs` | The settings file location and each game's `Data` folder, shared by both programs. |
| `src/Catalog.cs` | Reads the downloads page into a list of patches. |
| `src/Edition.cs` | The WotLK, Kronos, and Turtle WoW patch sets. |
| `src/Updater.cs` | Compares the site with the disk, and installs, adopts, removes, and verifies patches. |
| `src/Downloader.cs` | Resumable downloads, server checks, and hashing. |
| `src/ContentCheck.cs` | Recalculates the server's `ETag` from a file on disk. |
| `src/WowInstall.cs` | Finds the game folder, and owns its paths and file-lock checks. |
| `src/State.cs` | The settings file and each game's `state.json`. |
| `src/Ui.cs` | Console output and the progress bar, or the window when one is attached. |
| `gui/MainWindow.xaml` | The main window: game picker, patch cards, progress, and activity log. |
| `gui/AddGameWindow.xaml` | The **Add a game** dialog. |
| `gui/CuteDialog.xaml` | The confirmation and message dialog. |
| `gui/Mascot.xaml` | Belora, drawn in XAML, with her expressions and animations. |
| `gui/Theme.xaml` | Styles for buttons, lists, menus, and other controls. |
| `gui/Palette.Light.xaml`, `gui/Palette.Dark.xaml` | The colors for each theme, including Belora's outfit. |
| `gui/Themes.cs` | Reads the Windows theme setting and swaps the palettes. |
| `gui/WindowSink.cs` | Passes the engine's messages and progress to the window. |

## Engine output

The engine reports everything through the static `Ui` class: messages with
`Ui.Info`, `Ui.Good`, `Ui.Warn`, and `Ui.Error`, and download progress with
`Ui.Progress`. The command line leaves `Ui.Sink` empty, so the output goes to the
console. The window sets `Ui.Sink` to a `WindowSink`, which sends the output to
the activity log and the progress card instead.

When you add engine code, report through `Ui`. Code that writes to `Console`
directly shows nothing in the window.

The engine reports from background threads. `WindowSink` passes each call to
the window's dispatcher, so window code that handles the output always runs on
the UI thread.

## Change the colors

Every color that changes with the theme lives in `gui/Palette.Light.xaml` and
`gui/Palette.Dark.xaml`. Both files define the same resource keys. The
`Themes.Apply` method swaps one file for the other in the application's
resources, and every control repaints.

To add a color:

1. Add the key to both palette files, with a value for each theme.
1. Refer to it with `DynamicResource`, for example
   `Background="{DynamicResource Surface}"`. A `StaticResource` reference keeps
   the first palette's value and doesn't change with the theme.

Code that reads a brush directly, such as `Palette.Get` in `gui/ViewModels.cs`,
gets the value of the current theme only. The window rebuilds the patch cards
and the activity log after a theme change for that reason.

## Change Belora

Belora is drawn with XAML shapes on a 100 by 100 canvas in `gui/Mascot.xaml`,
and scales to any size.

- Her expressions are named groups of shapes, such as `EyesHappy` and
  `MouthOpen`. `gui/Mascot.xaml.cs` shows and hides them for each value of
  `MascotMood`, and runs the breathing, blinking, and hopping animations.
- Her tunic, collar, and belt colors come from the palettes, under keys such as
  `TunicTop` and `Belt`, so she changes outfits with the theme.

If you rename a named shape, update `gui/Mascot.xaml.cs` to match.

The app icon, `gui/Assets/app.ico`, is Belora in her light-theme outfit,
rendered from the `Body` canvas in `gui/Mascot.xaml` at sizes from 16 to 256
pixels, without the shadow and sparkles. After you change her drawing,
regenerate the icon so that it still matches.

## Update the downloads page reader

Project Reforged doesn't publish a machine-readable list of patches, so
`src/Catalog.cs` reads the downloads page's HTML. It finds each patch by the
page's own class names: `dl-card`, `dl-patch`, `dl-version`, and
`dl-variant-name`.

If the site is redesigned and those names change, `Catalog.Parse` stops with a
`CatalogException` instead of returning an empty list. An empty list would
report that nothing needs updating. To fix the reader, update the patterns at
the top of `src/Catalog.cs` to match the new page.

A `versions.json` file on the Project Reforged site would make this reader
unnecessary. It's worth asking the site's maintainers for one.

## Test without a real game

To try changes without touching a real game, make a test game folder:

1. Create a folder with an empty `Data` folder inside it.
1. Add the folder as a game: in the window, click **Add a game** (**+**), or run
   `ReforgedUpdater games add test "FOLDER" --edition wotlk`.
1. To test downloads, install a small patch. Patch-N and Patch-U are each under
   10 MB.
1. To test adopting, copy any file into `Data` as `patch-N.mpq`. The updater
   finds it, and after you adopt it, reports that it isn't the published build.

To keep your own settings separate while you test, run a copy of the programs
from another folder. Each copy uses the `reforged-updater.json` next to it.
