# EpicSteamLauncher

EpicSteamLauncher is a Windows .NET command-line launcher that makes Epic Games Store titles behave like native running games in Steam (including Big Picture
and Steam Link workflows).

It launches an Epic URI, finds the game process, and stays alive until the game exits so Steam keeps the session active.

---

## What It Does

- Launches Epic games from `com.epicgames.launcher://apps/...` URIs.
- Supports classic two-argument mode and profile-based commands.
- Creates and validates profile files (`*.esl`) in a local `profiles` folder.
- Imports installed Epic games and auto-generates profiles.
- Syncs profiles into Steam `shortcuts.vdf` as Non-Steam shortcuts.
- Optionally downloads Steam artwork/icon assets through SteamGridDB.
- Uses process-tree waiting and startup diagnostics to improve process detection reliability.

---

## Requirements

- Windows
- .NET 8 SDK/runtime (`net8.0-windows`)

---

## Getting Started

### 1. Build

```powershell
Set-Location "D:\Development\Projects-VisualStudio\EpicSteamLauncher\Launcher"
dotnet build "EpicSteamLauncher.sln"
```

### 2. Create a profile

```powershell
EpicSteamLauncher.exe --wizard
```

### 3. Add to Steam

1. Add `EpicSteamLauncher.exe` as a Non-Steam Game.
2. Set Steam launch options to:

```text
--profile "YourProfileName"
```

---

## Repository Layout

- `Launcher/EpicSteamLauncher/` - Main console application.
- `Launcher/EpicSteamLauncher/Application/Internal/` - Internal command/menu parsing and defaults types.
- `Launcher/EpicSteamLauncher/Application/Models/` - Profile/import/runtime data models.
- `Launcher/EpicSteamLauncher.Tests/` - xUnit v3 test project.
- `Launcher/EpicSteamLauncher.sln` - Solution file.
- `Launcher/Directory.Packages.props` - Central package versions.

---

## CLI Usage

Run from the published output folder or `bin/...` output where `EpicSteamLauncher.exe` exists.

### Interactive menu

```powershell
EpicSteamLauncher.exe
```

### Profile and sync commands

```powershell
EpicSteamLauncher.exe --wizard
EpicSteamLauncher.exe --import-installed
EpicSteamLauncher.exe --validate-profiles
EpicSteamLauncher.exe --profile "GameProfileName"
EpicSteamLauncher.exe --profile
EpicSteamLauncher.exe --sync-nonsteam
```

### Command reference

| Command                                     | Purpose                                         |
|---------------------------------------------|-------------------------------------------------|
| `EpicSteamLauncher.exe`                     | Opens interactive menu mode                     |
| `EpicSteamLauncher.exe --wizard`            | Creates/edits a profile interactively           |
| `EpicSteamLauncher.exe --import-installed`  | Imports installed Epic titles into profiles     |
| `EpicSteamLauncher.exe --validate-profiles` | Validates all profile files and prints a report |
| `EpicSteamLauncher.exe --profile "Name"`    | Launches a specific profile                     |
| `EpicSteamLauncher.exe --profile=Name`      | Launches a specific profile (equals form)       |
| `EpicSteamLauncher.exe --profile`           | Opens profile selector mode                     |
| `EpicSteamLauncher.exe --sync-nonsteam`     | Upserts Steam Non-Steam shortcuts from profiles |

### Global flags

These flags can be passed with other commands:

| Flag         | Purpose                             |
|--------------|-------------------------------------|
| `--pause`    | Pause on exit for command-line runs |
| `--no-pause` | Explicitly disable pause-on-exit    |

### Legacy mode (still supported)

```powershell
EpicSteamLauncher.exe "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true" FortniteClient-Win64-Shipping
```

---

## Typical Steam Setup

1. Build or publish the launcher.
2. Add `EpicSteamLauncher.exe` to Steam as a Non-Steam Game.
3. Create a profile with `--wizard` (or `--import-installed`).
4. Set Steam launch options to:

```text
--profile "YourProfileName"
```

---

## Profiles

- Profiles are JSON files with extension `.esl`.
- The app auto-creates a `profiles` folder beside the executable.
- The file `example.profile.esl` is an example template and is intentionally ignored by profile selection/validation.
- Key fields:
    - `EpicLaunchUrl`
    - `GameProcessName`
    - Optional timing and install metadata fields

Example profile:

```json
{
  "Name": "Fortnite",
  "EpicLaunchUrl": "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true",
  "GameProcessName": "FortniteClient-Win64-Shipping",
  "StartTimeoutSeconds": 60,
  "PollIntervalMs": 500,
  "LaunchDelayMs": 0,
  "InstallLocation": "",
  "LaunchExecutable": ""
}
```

---

## Build and Test

```powershell
Set-Location "D:\Development\Projects-VisualStudio\EpicSteamLauncher\Launcher"
dotnet build "EpicSteamLauncher.sln"
dotnet test "EpicSteamLauncher.Tests\EpicSteamLauncher.Tests.csproj"
```

---

## Notes

- Steam sync writes/upserts shortcuts in the active Steam user `shortcuts.vdf`.
- SteamGridDB usage is optional; API key settings are stored in `steamgriddb.json` beside the executable.
- Documentation is centralized in this root README to avoid project-internal README drift during IDE cleanup/code-style workflows.

CLI parser notes:

- `--profile=Name` and `--profile "Name"` are both supported.
- `--profile=""` falls back to profile selector mode.
- `--profile=` (empty value without quotes) is currently treated as bad arguments.

SteamGridDB artwork behavior:

- If no API key is configured, sync still runs and creates shortcuts.
- Artwork sync covers banner, grid, hero, logo, and icon assets when available.
- Icons are written and then linked into `shortcuts.vdf` in a follow-up pass.

---

## Inspiration

This project was inspired by:

https://seanzwrites.com/posts/how-to-play-epic-games-on-steam-and-steamlink/

---

## License

MIT License. See `LICENSE`.
