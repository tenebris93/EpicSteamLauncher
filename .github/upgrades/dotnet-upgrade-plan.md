# .NET 8.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 8.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 8.0 upgrade.
3. Upgrade EpicSteamLauncher\EpicSteamLauncher.csproj

## Settings

This section contains settings and data used by execution steps.

### Excluded projects

No projects are excluded from this upgrade.

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                        | Current Version | New Version | Description                               |
|:------------------------------------|:---------------:|:-----------:|:------------------------------------------|
| Newtonsoft.Json                     | 13.0.5-beta1    | 13.0.4      | Recommended for .NET 8.0                  |
| System.CodeDom                      | 10.0.1          | 8.0.0       | Recommended for .NET 8.0                  |
| System.Management                   | 10.0.1          | 8.0.0       | Recommended for .NET 8.0                  |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### EpicSteamLauncher\EpicSteamLauncher.csproj modifications

Project properties changes:
  - Target framework should be changed from `net48` to `net8.0`

NuGet packages changes:
  - Newtonsoft.Json should be updated from `13.0.5-beta1` to `13.0.4` (*recommended for .NET 8.0*)
  - System.CodeDom should be updated from `10.0.1` to `8.0.0` (*recommended for .NET 8.0*)
  - System.Management should be updated from `10.0.1` to `8.0.0` (*recommended for .NET 8.0*)
