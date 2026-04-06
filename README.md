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
- `Launcher/Directory.Build.targets` - Internal GitSemVer version computation.
- `.github/scripts/versioning/validate-semver-tag.ps1` - SemVer validation helper used by release automation.
- `.github/workflows/` - CI and release workflows (`pr.yml`, `build.yml`, `release.yml`).

---

## Contributing

- See `CONTRIBUTING.md` for contribution policy and required commit message format.
- Commit type definitions and examples are documented in `CONTRIBUTING.md#conventional-commit-type-guide`.

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

## SteamGridDB API Key Setup

Steam artwork sync is optional, but if you want banner/grid/hero/logo/icon downloads you need a SteamGridDB API key.

### 1. Get your SteamGridDB API key

1. Create or sign in to your SteamGridDB account.
2. Open your account settings/API section and generate a personal API key.
3. Copy the key and keep it private.

### 2. Create the local config file

Run sync once so the launcher creates the config file beside `EpicSteamLauncher.exe`:

```powershell
EpicSteamLauncher.exe --sync-nonsteam
```

This creates `steamgriddb.json` in the executable directory if it does not exist.

### 3. Set the key in `steamgriddb.json`

Edit `steamgriddb.json` and set `apiKey`:

```json
{
  "apiKey": "YOUR_STEAMGRIDDB_API_KEY",
  "dontAskAgain": false,
  "lastValidatedUtc": null
}
```

Then run sync again:

```powershell
EpicSteamLauncher.exe --sync-nonsteam
```

### 4. What to expect

- If the key is valid and Steam paths are detected, artwork will be downloaded during sync.
- If the key is missing/invalid (or SteamGridDB is unavailable), shortcut sync still completes and only artwork is skipped.
- You can disable prompts by setting `dontAskAgain` to `true`.

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

### Publish and run (recommended for Steam)

```powershell
Set-Location "D:\Development\Projects-VisualStudio\EpicSteamLauncher\Launcher"
dotnet publish "EpicSteamLauncher\EpicSteamLauncher.csproj" -c Release -r win-x64 --self-contained true -o "..\_release\publish\win-x64"
Set-Location "D:\Development\Projects-VisualStudio\EpicSteamLauncher\_release\publish\win-x64"
.\EpicSteamLauncher.exe --wizard
```

---

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Bad arguments / invalid command or launch URL |
| `2` | Epic URL launch failed |
| `3` | Timed out waiting for game process |
| `4` | Profile not found / no profiles discovered for command |
| `5` | Profile invalid |
| `6` | Wizard failed |
| `7` | Import failed |
| `8` | Unexpected error |

---

## CI and Release Workflows

Current GitHub Actions behavior:

- `pr.yml` (`PR Validation`): runs automatically on every pull request and can also be started manually.
- `build.yml` (`Main Build`): runs on pushes to `main` when changes touch `Launcher/**`, `.github/scripts/**`, or `.github/workflows/**`; also supports manual runs.
- `release.yml` (`Release`): manual-only (`workflow_dispatch`).

### Manual release behavior

- Start release from GitHub Actions (`Release` -> `Run workflow`).
- The release version/tag is computed automatically by internal GitSemVer logic in `Launcher/Directory.Build.targets`.
- You do not manually type a release version in workflow inputs.

### Versioning rules (GitSemVer)

- Base tag discovery accepts `vMAJOR.MINOR`, `MAJOR.MINOR`, `vMAJOR.MINOR.PATCH`, and `MAJOR.MINOR.PATCH`.
- Base version is normalized to `MAJOR.MINOR`.
- `PATCH` is computed as the number of commits since the latest matching tag (`<tag>..HEAD`).
- Final version format is `MAJOR.MINOR.PATCH`, and release tag format is `vMAJOR.MINOR.PATCH`.

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

## Where Profiles Live

- Profiles are created under `profiles` relative to the launcher executable location.
- Effective path pattern: `<folder containing EpicSteamLauncher.exe>\profiles`.
- If you run from `bin\Debug\net8.0-windows`, profiles are created there.
- If you run from a published folder, profiles are created beside the published exe.

---

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| `ERROR: Could not locate Steam install path.` | Steam registry path not found for current Windows user | Launch Steam once as the same user and retry `--sync-nonsteam` |
| `ERROR: Could not locate a Steam userdata config directory.` | No valid `userdata\<steamId>\config` detected | Sign in to Steam and start a game once, then retry sync |
| `ERROR: Could not find process '...' before timeout` | Process name mismatch or game startup delay | Re-check `GameProcessName`, retry launch, and use interactive diagnostics prompt when offered |
| `Profile 'X' is invalid or could not be loaded.` | Missing file or invalid JSON/fields | Run `--validate-profiles` and fix the reported profile |
| Artwork skipped during sync | Missing/invalid SteamGridDB key or service unavailable | Configure `steamgriddb.json` and rerun `--sync-nonsteam` |

---

## Known Limitations

- Steam shortcut/artwork sync currently depends on Windows Steam registry detection and Steam userdata heuristics.
- In multi-user or unusual Steam setups, the auto-selected userdata folder may not be the one you expect.
- Process-tree waiting uses WMI for child process traversal; hardened systems can limit WMI visibility.
- `--profile=` (empty equals value) is currently treated as bad arguments; use `--profile` for selector mode.

---

## Inspiration

This project was inspired by:

https://seanzwrites.com/posts/how-to-play-epic-games-on-steam-and-steamlink/

---

## License

MIT License. See [LICENSE](LICENSE).
