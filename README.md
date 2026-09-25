# Project Reforged Updater

An updater for the [Project Reforged](https://projectreforged.github.io/)
HD patch libraries for World of Warcraft — WotLK 3.3.5a and vanilla 1.12 (Kronos
and Turtle WoW). It reads the public downloads page, compares it against your
`Data` folder, and re-downloads only what changed.

It comes as two programs that share one engine and one settings file:

- `ReforgedUpdaterGui.exe` — a window, for clicking.
- `ReforgedUpdater.exe` — the command line, for scripts, scheduled tasks, and
  launchers.

Each is a single `.exe`, with no installer and no third-party dependencies.
Both target .NET Framework 4.8, which is part of Windows 10 and 11, so players
don't install a runtime.

## Why not Lua

WoW's addon API has no HTTP client and cannot write outside `SavedVariables`,
so an in-game Lua updater can't download or replace an `.mpq`. This has to be
an external program.

## Build

```bash
dotnet build -c Release
dotnet build gui -c Release
```

The first command builds the command line and the second builds the window.
Both land in `bin/Release/net48/`: `ReforgedUpdater.exe` and
`ReforgedUpdaterGui.exe`. Copy either one, or both, anywhere. Keep them in the
same folder if you use both, so they share `reforged-updater.json` and see the
same games.

## The window

Run `ReforgedUpdaterGui.exe`. Belora, the little elf at the top, greets you in Thalassian and tells you
where things stand, and each patch gets a card with its state and one button:

| Card says | Button | What happens |
| --- | --- | --- |
| Not installed | **Install** | Downloads the patch into `Data` |
| Update ready | **Update** | Downloads the new build |
| Found in Data | **Adopt** | Registers a patch you downloaded by hand, checking its content against the server (the same as `adopt --verify`) |
| Missing, Changed | **Download again**, **Repair** | Replaces the file |
| Up to date | — | The bin icon removes the patch |

The big button at the top updates everything that changed, or installs every
patch on a game that has none yet. **Check again** reads the downloads page
afresh.

Use the game picker to switch between games, and **+** to add one. Adding asks
for the game folder, a nickname, and the patch set. The nickname works on the
command line too: `--game <name>`. The **⋯** menu has the rest: installing
every patch, a full content check (`verify --deep`), moving the Data folder,
forgetting a game, and a shortcut that opens the command line in a console.

Downloads show their speed and time left, and **Stop** keeps the partial file,
so the next attempt resumes. The clock icon opens the activity log, which shows
everything the command line would have printed.

The window follows the Windows light or dark mode setting, and switches when
Windows does. The sun and moon button in the title bar picks one yourself;
**Match the Windows theme** in the **⋯** menu goes back to following Windows.
In dark mode Belora changes her mint tunic for crimson and gold.

## The command line

Put the exe in your WoW folder (next to `Wow.exe`) and run it. It finds the
client on its own; otherwise point it at one:

```bash
ReforgedUpdater path "D:\Games\World of Warcraft"
```

### WotLK or vanilla: the edition

Each client folder has an **edition** that decides which downloads page the
updater reads:

| Edition | Client | Downloads page |
| --- | --- | --- |
| `wotlk` | 3.3.5a | [wotlk/downloads](https://projectreforged.github.io/wotlk/downloads/) |
| `kronos` | Vanilla 1.12, standard client | [vanilla/downloads/kronos](https://projectreforged.github.io/vanilla/downloads/kronos/) |
| `turtle` | Vanilla 1.12, Turtle WoW client | [vanilla/downloads/turtle](https://projectreforged.github.io/vanilla/downloads/turtle/) |

A WotLK client is recognized from `Wow.exe` and needs nothing. Vanilla ships a
separate patch set per server — Patch-A, I, and M are built for each server's
client — and `Wow.exe` can't tell Kronos from Turtle, so the first run on a
vanilla client asks. Tell it once:

```bash
ReforgedUpdater edition turtle
```

The choice is saved in that client's `.reforged\state.json`, so a WotLK install
and a vanilla install on the same machine each keep their own. `--edition <name>`
works on any command and is remembered the same way; `ReforgedUpdater edition`
on its own shows the current setting.

If you switch a client between Kronos and Turtle, modules the two servers share
stay installed, and the server-specific ones show as updates, because the file
you have was built for the other server.

Module ids follow the site's letters. A module's first variant keeps the plain
letter and the others get a suffix — on vanilla that gives `L` / `L-less-thicc`
and `T` / `T-ultra-base`. Run `ReforgedUpdater list` to see them.

The Kronos page also links two `.zip` downloads, VanillaFixes and a client
executable. They go outside `Data` and aren't patch archives, so the updater
leaves them alone; install those by hand.

### Setting the Data folder

Normally you don't: the patches go to `<client>\Data`, which the updater derives
from the client folder. Set it explicitly when the archives live somewhere else —
a second drive, or a launcher with its own layout:

```bash
ReforgedUpdater data "E:\WoW-Patches\Data"
```

`ReforgedUpdater data` on its own shows the current setting and what it resolves
to, and `ReforgedUpdater data --default` puts it back to `<client>\Data`. For a
single run without saving anything, use `--data <folder>`.

The setting belongs to one game: it's saved in that game's
`.reforged\state.json`, and other games keep their own Data folders. Add
`--game <name>` to set it for a registered game other than the last one used.
(Earlier versions saved it next to the exe and applied it to every game; a value
found there is moved into the game it was set for.)

Pointing `path` at a Data folder by mistake also works — the updater notices and
uses the parent as the client folder.

Partial downloads follow the Data folder rather than the client, keeping the
final move on one volume even when Data is on another drive.

### Several games, one exe

You don't need a copy of the exe per game. Each game folder keeps its own
edition, Data folder, and patch records in `<game>\.reforged`, so one exe can
look after all of them. Register each game once under a short name:

```bash
ReforgedUpdater games add warmane "D:\Games\WotLK-Warmane" --edition wotlk
ReforgedUpdater games add stormforge "D:\Games\WotLK-Stormforge" --edition wotlk
ReforgedUpdater games add octowow "D:\Games\OctoWow" --edition turtle
```

`--edition` is required when adding a game. `Wow.exe` reports the same version
for every vanilla server — a Turtle-based client like OctoWow looks exactly like
a stock 1.12.1 one — so registering is where you say which patch set a game
uses: `wotlk`, `kronos` for standard vanilla servers, or `turtle` for Turtle and
Turtle-based servers. If the choice contradicts `Wow.exe` (vanilla patches on a
3.3.5 client, say), the updater warns.

Then work on one game with `--game`, or on all of them with `--all-games`:

```bash
ReforgedUpdater status --game turtle
ReforgedUpdater update --all-games --yes
```

`--all-games` works with `status`, `update`, `adopt`, `verify`, and `list`.
Installing and removing modules stay one game at a time, since each game has its
own module set. If one game fails — a missing folder, or a vanilla game with no
edition — the rest still run, and a summary at the end lists which games are
current, which have updates, and which failed. The exit code is `1` if any game
failed, otherwise `2` if any has updates.

`ReforgedUpdater games` lists what's registered, and
`ReforgedUpdater games remove <name>` unregisters a game without touching its
files. With no `--game` or `--wow`, commands use the game you used last.

| Command | What it does |
| --- | --- |
| `ReforgedUpdater` | Status: site versions vs. what you have installed |
| `ReforgedUpdater list` | Every module the site offers |
| `ReforgedUpdater install A C G I` | Download and install those modules |
| `ReforgedUpdater install --all` | Install everything not yet installed |
| `ReforgedUpdater update` | Re-download whatever changed |
| `ReforgedUpdater update --yes` | Same, unattended |
| `ReforgedUpdater adopt --verify` | Register patches you downloaded by hand |
| `ReforgedUpdater remove N` | Delete `patch-N.mpq` and stop tracking it |
| `ReforgedUpdater verify --deep` | Read installed files and compare against the published build |

Module ids are the patch letters from the site — `A B C D E G I M N P S U`.
Patch-S ships in two forms; the standalone one is `S-standalone`.

Useful flags: `--dry-run`, `--yes`, `--fast` (skip server checks), `--no-hash`,
`--json`, `--quiet`, `--wow <folder>`, `--game <name>`, `--all-games`.

Exit codes: `0` success, `1` error, `2` updates are available. That makes
`status` usable from a scheduled task or a launcher script.

### Patches you already downloaded

The updater tracks what it installed itself, so `.mpq` files you downloaded by
hand start out unknown to it. They show up as `UNTRACKED` in the status table
rather than as missing, and one command registers them without downloading
anything:

```bash
ReforgedUpdater adopt
```

Use `--verify` and the file is identified by its **content**, which is what you
want: a stale build can be exactly the same length as the current one, and a
size comparison would wave it through as up to date.

```bash
ReforgedUpdater adopt --verify
```

- **Content matches** — recorded as the current build. Nothing is downloaded.
- **Content differs** — recorded as an unknown older build, so it shows as an
  available update.

Without `--verify` only sizes are compared, and the updater says so.

For Patch-S the content check also identifies which of the two variants you
have. If a `patch-S.mpq` matches neither, the updater says so instead of
guessing; name the variant yourself (`adopt S` or `adopt S-standalone`), or
delete the file and install it fresh.

`--verify` reads every byte, so budget roughly a minute per 3 GB.

## Checking content without downloading

The site publishes no checksums, but the storage behind it does, in a form that
can be recomputed locally. Every object carries an HTTP `ETag` derived from its
bytes:

- uploaded in one request (Patch-N, U, P) — the ETag is the MD5 of the file;
- uploaded in parts (the ten large patches) — it is
  `MD5(concatenated part MD5s)-<part count>`.

`ContentCheck.cs` rebuilds both forms from the file on disk. The part size isn't
advertised, so it is inferred: only a chunk size that yields the advertised part
count is worth trying, which in practice leaves exactly one candidate — 10 MiB
for this bucket. When nothing reproduces the ETag, the answer is "could not
verify" rather than "corrupt", so an unfamiliar upload layout is never reported
as a bad file.

This is what makes `adopt --verify` and `verify --deep` able to prove a local
file is byte-for-byte the published build, without downloading it again. MD5
appears here only because the ETag format is defined in terms of it; it is doing
checksum work, not security work.

`verify --deep` records what it finds, so a file it proves stale shows up as an
available update in the next `status` instead of being forgotten.

## How updates are detected

For each module the updater keeps a record of what it installed: the version
label from the site, the HTTP `ETag`, `Last-Modified`, the byte size, and a
SHA-256 of the finished file. A module counts as out of date when any of these
is true:

- the version on the page differs from the installed one;
- the server's `ETag` differs from the recorded one — this catches a silent
  re-upload that reuses the same version label;
- the size on the server differs;
- the `.mpq` is missing from `Data`, or its size no longer matches.

The `ETag` check is the reliable one; the version label is a convenience for
the status table.

## Downloads

The archives run to several gigabytes, so:

- transfers resume with HTTP range requests after a drop or a `Ctrl+C`;
- a resume sends `If-Range`, so if the file changed mid-download the server
  sends the whole new file instead of splicing two builds together;
- partial data lives in `<WoW>\.reforged\cache\*.part`, on the same drive as
  `Data` so the final move is instant;
- network failures retry five times with a growing delay, and a transfer that
  stalls for 60 seconds is treated as failed;
- free space is checked before anything starts;
- the existing `.mpq` is renamed aside and only deleted once the new file is in
  place, so a failed swap leaves the old one working.

Close the game before updating — the client holds the `.mpq` files open, and
the updater will refuse to replace a locked file rather than corrupt it.

## Files it writes

| Path | Contents |
| --- | --- |
| `reforged-updater.json` (next to the exes) | Registered games, last game used, catalog URL override, and the window's theme. Shared by the window and the command line |
| `<WoW>\.reforged\state.json` | The game's edition and Data folder, and what is installed with versions and hashes |
| `<Data>\..\.reforged\cache\` | Partial downloads; safe to delete |
| `<Data>\patch-*.mpq` | The patches themselves |

State lives inside the client folder, so a copied or moved WoW install keeps
its update history.

## Source layout

| File | Role |
| --- | --- |
| `src/Program.cs` | CLI parsing, commands, output |
| `src/Workspace.cs` | Settings-file location and per-game Data folders, shared by both programs |
| `src/Catalog.cs` | Reads the downloads page into module entries |
| `src/Edition.cs` | The WotLK, Kronos, and Turtle catalogs |
| `src/Updater.cs` | Compares site to disk, installs, verifies |
| `src/Downloader.cs` | Resumable HTTP transfer, probing, hashing |
| `src/WowInstall.cs` | Finds the client, owns paths and lock checks |
| `src/ContentCheck.cs` | Rebuilds the server's ETag locally to compare content |
| `src/State.cs` | Settings and install state as JSON |
| `src/Ui.cs` | Console formatting and the progress bar; sends output to the window when one is attached |
| `gui/` | The window: a WPF project that compiles everything in `src/` except `Program.cs` |
| `gui/MainWindow.xaml` | Game picker, patch cards, progress, and activity log |
| `gui/Mascot.xaml` | Belora the elf, drawn in XAML, with a mood for each state |
| `gui/Theme.xaml` | Buttons and other control styles |
| `gui/Palette.Light.xaml`, `gui/Palette.Dark.xaml` | The colours for each theme, including Belora's outfit |

The engine reports through `Ui`. The command line leaves it writing to the
console, and the window sets `Ui.Sink`, so messages and download progress
arrive in the window instead. New engine code should keep reporting through
`Ui` and never write to `Console` directly, so both programs keep working.

## Maintenance note

There is no machine-readable manifest on the site, so `Catalog.cs` reads the
HTML using the page's own class names (`dl-card`, `dl-patch`, `dl-version`,
`dl-variant-name`). If the site is redesigned, the updater stops with a clear
parse error instead of quietly reporting "no updates" — fix the patterns in
that one file.

A published `versions.json` on the site would remove that fragility entirely;
worth requesting from the Project Reforged maintainers.

## License

[MIT](LICENSE). Project Reforged's patches and World of Warcraft belong to their
respective owners; this updater only downloads the patches from their public site.
