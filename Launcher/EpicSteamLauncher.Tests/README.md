# EpicSteamLauncher Tests

This project contains xUnit v3 tests for launcher command-line behavior.

## What It Validates

- Unknown-command and legacy argument validation
- `--profile` selector and direct profile launch error handling
- `--validate-profiles` empty, valid, invalid, and mixed-profile reporting
- Exit-code mapping for `ExitBadArgs`, `ExitProfileNotFound`, and `ExitProfileInvalid`
- `--profile=Name` parsing path and quoted equals syntax behavior
- Example profile exclusion rules (`example.profile.esl` is ignored)
- URL/process normalization behavior for loaded profiles
- Timing fallback behavior for non-positive timeout/poll/delay values

## Known Parser Behaviors (Current Contract)

- `--profile=` currently behaves as an unknown command token and returns bad arguments (`ExitBadArgs`).
- `--profile=""` falls back to profile selector mode and can return success when no valid profiles exist.
- When multiple recognized commands are present, the first recognized command token wins.

## Test Design Notes

- Tests are behavior-focused and assert concrete exit codes and user-visible messages.
- Profile fixtures are created per test in an isolated `profiles` folder under the test output directory.
- External dependencies (Steam install, Epic manifests, SGDB network) are intentionally excluded to keep tests deterministic.

## Run

```powershell
Set-Location "D:\Development\Projects-VisualStudio\EpicSteamLauncher\Launcher"
dotnet test "EpicSteamLauncher.Tests\EpicSteamLauncher.Tests.csproj"
```
