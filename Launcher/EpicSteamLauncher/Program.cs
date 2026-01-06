/*
    EpicSteamLauncher

    HOW THIS TOOL IS USED (Steam / Steam Link workflow)
    ---------------------------------------------------
    This executable is intended to be added to Steam as a "Non-Steam Game".

    Steam launches this EXE, and EpicSteamLauncher will:
      1) Open an Epic Games launch URL (via shell / protocol activation).
      2) Detect the game process by name.
      3) Wait until the game exits so Steam treats the session as "running".

    PHASE 2 ADDITIONS
    -----------------
    - Import installed Epic games by reading local launcher data:
        - ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item
        - ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat
    - Auto-generate JSON profiles for installed games.
    - Wizard can pick from installed games and autofill URL + process guess.

    PHASE 3 ADDITIONS
    -----------------
    - More reliable process detection:
        - Snapshot PIDs before launch.
        - Prefer a newly started matching process after launch.
        - If StartTime is accessible, prefer the newest process.
        - Optional fallback to any matching process if no "new" one appears.

    JSON Dependency:
      - This file expects Newtonsoft.Json to be referenced by the project.
        Add it via NuGet:
            dotnet add package Newtonsoft.Json
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpicSteamLauncher
{
    internal static class Program
    {
        // -----------------------------
        // Exit codes (Steam-friendly)
        // -----------------------------
        private const int ExitSuccess = 0;
        private const int ExitBadArgs = 1;
        private const int ExitLaunchFailed = 2;
        private const int ExitProcessNotFound = 3;
        private const int ExitProfileNotFound = 4;
        private const int ExitProfileInvalid = 5;
        private const int ExitWizardFailed = 6;
        private const int ExitImportFailed = 7;

        /// <summary>
        ///     Folder name where per-game profile JSON files are stored.
        ///     Stored next to the executable so the tool stays portable.
        /// </summary>
        private const string ProfilesFolderName = "profiles";

        /// <summary>
        ///     Default settings applied when profiles omit values or specify invalid ones.
        /// </summary>
        private static class Defaults
        {
            public const int StartTimeoutSeconds = 60;
            public const int PollIntervalMs = 500;
            public const int LaunchDelayMs = 0;

            // Legacy mode in your original script used Thread.Sleep(5000).
            // We keep that behavior by default in legacy mode.
            public const int LegacyLaunchDelayMs = 5000;

            // Phase 3: process matching tolerance.
            public const int StartTimeToleranceSeconds = 3;

            // If no "new" matching process appears, should we accept an existing instance?
            public const bool FallbackToAnyMatchingProcess = true;
        }

        /// <summary>
        ///     Simple model for dynamically numbered menu options.
        /// </summary>
        private sealed class MenuOption(string label, Func<MenuOutcome> action)
        {
            public string Label { get; } = label ?? throw new ArgumentNullException(nameof(label));
            public Func<MenuOutcome> Action { get; } = action ?? throw new ArgumentNullException(nameof(action));
        }

        /// <summary>
        ///     Result of running a menu action.
        /// </summary>
        private readonly struct MenuOutcome
        {
            private MenuOutcome(bool shouldExit, int? lastResultCode)
            {
                ShouldExit = shouldExit;
                LastResultCode = lastResultCode;
            }

            public bool ShouldExit { get; }
            public int? LastResultCode { get; }

            public static MenuOutcome Continue(int? lastResultCode = null)
            {
                return new MenuOutcome(false, lastResultCode);
            }

            public static MenuOutcome Exit()
            {
                return new MenuOutcome(true, null);
            }
        }

        private static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                BootstrapProfilesFolder();
                return ShowMainMenu();
            }

            // Legacy mode (backward compatible): exactly 2 args (not flags) = URL + process name.
            if (args.Length == 2 && !IsFlag(args[0]))
            {
                string epicUrl = args[0]?.Trim();
                string exeName = NormalizeProcessName(args[1]);

                int rc = Launch(
                    epicUrl,
                    exeName,
                    Defaults.StartTimeoutSeconds,
                    Defaults.PollIntervalMs,
                    Defaults.LegacyLaunchDelayMs
                );

                PauseIfInteractive();
                return rc;
            }

            string flag = (args[0] ?? string.Empty).Trim();

            if (flag.Equals("--wizard", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();

                try
                {
                    RunWizard();
                    PauseIfInteractive();
                    return ExitSuccess;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Wizard failed. {ex.GetType().Name}: {ex.Message}");
                    PauseIfInteractive();
                    return ExitWizardFailed;
                }
            }

            if (flag.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();

                if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
                {
                    string profileName = args[1].Trim().Trim('"');
                    int rc = LaunchFromProfileName(profileName);
                    PauseIfInteractive();
                    return rc;
                }

                int selectorRc = ProfilesSelectorScreen();
                PauseIfInteractive();
                return selectorRc;
            }

            if (flag.Equals("--validate-profiles", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();
                int rc = ValidateProfilesReport();
                PauseIfInteractive();
                return rc;
            }

            if (flag.Equals("--import-installed", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();
                int rc = ImportInstalledEpicGames(true);
                PauseIfInteractive();
                return rc;
            }

            PrintUsage();
            PauseIfInteractive();
            return ExitBadArgs;
        }

        private static bool IsFlag(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim();
            return token.StartsWith("-", StringComparison.Ordinal) || token.StartsWith("/", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------
        // Bootstrap / Main Menu (dynamic numbering)
        // ---------------------------------------------------------------------

        private static void BootstrapProfilesFolder()
        {
            string profilesDir = GetProfilesDirectory();
            Directory.CreateDirectory(profilesDir);

            string readmePath = Path.Combine(profilesDir, "README.txt");

            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath, BuildProfilesReadmeText(profilesDir));
            }

            string examplePath = Path.Combine(profilesDir, "example.profile.json");

            if (!File.Exists(examplePath))
            {
                var example = new GameProfile
                {
                    Name = "ExampleGame",
                    EpicLaunchUrl = "com.epicgames.launcher://apps/YourGameId?action=launch&silent=true",
                    GameProcessName = "GameProcessNameWithoutExe",
                    StartTimeoutSeconds = Defaults.StartTimeoutSeconds,
                    PollIntervalMs = Defaults.PollIntervalMs,
                    LaunchDelayMs = Defaults.LaunchDelayMs
                };

                string json = JsonConvert.SerializeObject(example, Formatting.Indented);
                File.WriteAllText(examplePath, json);
            }

            Console.WriteLine("EpicSteamLauncher: Profiles folder is ready.");
            Console.WriteLine($"Location: {profilesDir}");
            Console.WriteLine("Files: README.txt, example.profile.json (created if missing)");
            Console.WriteLine();
        }

        private static int ShowMainMenu()
        {
            var options = new List<MenuOption>
            {
                new(
                    "Create a profile (wizard)",
                    () =>
                    {
                        try
                        {
                            RunWizard();
                            return MenuOutcome.Continue();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ERROR: Wizard failed. {ex.GetType().Name}: {ex.Message}");
                            return MenuOutcome.Continue(ExitWizardFailed);
                        }
                    }
                ),

                new(
                    "Profiles (select to launch)",
                    () =>
                    {
                        int rc = ProfilesSelectorScreen();
                        return MenuOutcome.Continue(rc);
                    }
                ),

                new(
                    "Import installed Epic games (auto-create profiles)",
                    () =>
                    {
                        int rc = ImportInstalledEpicGames(true);
                        return MenuOutcome.Continue(rc);
                    }
                ),

                new(
                    "Open profiles folder",
                    () =>
                    {
                        TryOpenFolderInExplorer(GetProfilesDirectory());
                        return MenuOutcome.Continue();
                    }
                ),

                new(
                    "Validate profiles (report)",
                    () =>
                    {
                        int rc = ValidateProfilesReport();
                        return MenuOutcome.Continue(rc);
                    }
                ),

                new("Exit", MenuOutcome.Exit)
            };

            while (true)
            {
                Console.WriteLine("Main Menu");
                Console.WriteLine("---------");

                for (int i = 0; i < options.Count; i++)
                {
                    Console.WriteLine($"  [{i + 1}] {options[i].Label}");
                }

                Console.WriteLine();
                Console.Write("Select an option: ");
                string input = (Console.ReadLine() ?? string.Empty).Trim();
                Console.WriteLine();

                if (!int.TryParse(input, out int selected) || selected < 1 || selected > options.Count)
                {
                    Console.WriteLine("Invalid option. Returning to main menu.");
                    Console.WriteLine();
                    continue;
                }

                var outcome = options[selected - 1].Action();

                if (outcome.LastResultCode.HasValue)
                {
                    int rc = outcome.LastResultCode.Value;

                    if (rc != ExitSuccess)
                    {
                        Console.WriteLine($"Result: Failed (code {rc})");
                        Console.WriteLine();
                    }
                }

                if (outcome.ShouldExit)
                {
                    return ExitSuccess;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Profiles: enumeration + validation
        // ---------------------------------------------------------------------

        private static IEnumerable<string> EnumerateCandidateProfileJsonFiles()
        {
            string dir = GetProfilesDirectory();

            if (!Directory.Exists(dir))
            {
                yield break;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileName(file);

                if (filename.Equals("example.profile.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }

        private static bool TryLoadAndValidateProfile(string path, out GameProfile profile, out string error)
        {
            profile = null;
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Profile path is empty.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = "Profile file not found.";
                return false;
            }

            try
            {
                string json = File.ReadAllText(path);
                profile = JsonConvert.DeserializeObject<GameProfile>(json);
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON ({ex.GetType().Name}: {ex.Message})";
                return false;
            }

            return TryValidateAndNormalizeProfile(profile, out error);
        }

        private static bool TryValidateAndNormalizeProfile(GameProfile profile, out string error)
        {
            error = null;

            if (profile == null)
            {
                error = "Profile is null after deserialization.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.EpicLaunchUrl))
            {
                error = "Missing required field: EpicLaunchUrl";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.GameProcessName))
            {
                error = "Missing required field: GameProcessName";
                return false;
            }

            profile.EpicLaunchUrl = profile.EpicLaunchUrl.Trim();
            profile.GameProcessName = NormalizeProcessName(profile.GameProcessName);

            if (string.IsNullOrWhiteSpace(profile.GameProcessName))
            {
                error = "GameProcessName is invalid after normalization.";
                return false;
            }

            if (!profile.EpicLaunchUrl.Contains("://"))
            {
                error = "EpicLaunchUrl does not look like a valid URI (missing '://').";
                return false;
            }

            if (profile.StartTimeoutSeconds <= 0)
            {
                profile.StartTimeoutSeconds = Defaults.StartTimeoutSeconds;
            }

            if (profile.PollIntervalMs <= 0)
            {
                profile.PollIntervalMs = Defaults.PollIntervalMs;
            }

            if (profile.LaunchDelayMs < 0)
            {
                profile.LaunchDelayMs = Defaults.LaunchDelayMs;
            }

            return true;
        }

        private static List<DiscoveredProfile> GetValidProfiles()
        {
            var results = new List<DiscoveredProfile>();

            foreach (string file in EnumerateCandidateProfileJsonFiles())
            {
                string displayName = Path.GetFileNameWithoutExtension(file);

                if (TryLoadAndValidateProfile(file, out var profile, out _))
                {
                    results.Add(new DiscoveredProfile(displayName, file, profile));
                }
            }

            return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int ProfilesSelectorScreen()
        {
            var profiles = GetValidProfiles();

            Console.WriteLine("Profiles");
            Console.WriteLine("--------");

            if (profiles.Count == 0)
            {
                Console.WriteLine("No valid profiles found.");
                Console.WriteLine("Tip: Use 'Create a profile (wizard)' to make one.");
                return ExitSuccess;
            }

            Console.WriteLine("  [0] Back");

            for (int i = 0; i < profiles.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}] {profiles[i].Name}");
            }

            Console.WriteLine();
            Console.Write("Select a profile number: ");
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (!int.TryParse(input, out int selected))
            {
                return ExitSuccess;
            }

            if (selected == 0)
            {
                return ExitSuccess;
            }

            if (selected < 1 || selected > profiles.Count)
            {
                return ExitSuccess;
            }

            string chosenName = profiles[selected - 1].Name;
            Console.WriteLine();

            return LaunchFromProfileName(chosenName);
        }

        private static int ValidateProfilesReport()
        {
            string dir = GetProfilesDirectory();

            if (!Directory.Exists(dir))
            {
                Console.WriteLine("Profiles folder does not exist.");
                return ExitProfileNotFound;
            }

            var candidates = EnumerateCandidateProfileJsonFiles().ToList();

            if (candidates.Count == 0)
            {
                Console.WriteLine("No candidate profile JSON files found.");
                Console.WriteLine("Tip: Use the wizard to create one.");
                return ExitProfileNotFound;
            }

            int validCount = 0;
            int invalidCount = 0;

            Console.WriteLine("Profile validation report:");
            Console.WriteLine();

            foreach (string file in candidates.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);

                if (TryLoadAndValidateProfile(file, out _, out string error))
                {
                    Console.WriteLine($"  [OK]   {name}");
                    validCount++;
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {name}");
                    Console.WriteLine($"         Reason: {error}");
                    invalidCount++;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Summary: {validCount} valid, {invalidCount} invalid");

            return invalidCount == 0 ? ExitSuccess : ExitProfileInvalid;
        }

        // ---------------------------------------------------------------------
        // Phase 2: Installed Epic games import
        // ---------------------------------------------------------------------

        private sealed class EpicInstalledGame
        {
            public string DisplayName { get; set; }
            public string AppName { get; set; }
            public string InstallLocation { get; set; }
            public string LaunchExecutable { get; set; }
            public string Source { get; set; }
        }

        private static int ImportInstalledEpicGames(bool writeConsoleReport)
        {
            try
            {
                var games = DiscoverInstalledEpicGames();

                if (games.Count == 0)
                {
                    if (writeConsoleReport)
                    {
                        Console.WriteLine("No installed Epic games were discovered.");
                        Console.WriteLine("Tip: Install a game in Epic first, then try again.");
                    }

                    return ExitProfileNotFound;
                }

                string profilesDir = GetProfilesDirectory();
                int created = 0;
                int skipped = 0;
                int failed = 0;

                foreach (var game in games.OrderBy(g => g.DisplayName ?? g.AppName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    string proposedName = !string.IsNullOrWhiteSpace(game.DisplayName)
                        ? game.DisplayName
                        : !string.IsNullOrWhiteSpace(game.AppName)
                            ? game.AppName
                            : "UnknownGame";

                    string safeName = MakeSafeFileName(proposedName).Trim();

                    if (string.IsNullOrWhiteSpace(safeName))
                    {
                        safeName = "UnknownGame";
                    }

                    string profilePath = Path.Combine(profilesDir, $"{safeName}.json");

                    if (File.Exists(profilePath))
                    {
                        skipped++;
                        continue;
                    }

                    string epicUrl = BuildEpicLaunchUrl(game);
                    string processGuess = GuessProcessName(game);

                    var profile = new GameProfile
                    {
                        Name = safeName,
                        EpicLaunchUrl = epicUrl,
                        GameProcessName = processGuess,
                        StartTimeoutSeconds = Defaults.StartTimeoutSeconds,
                        PollIntervalMs = Defaults.PollIntervalMs,
                        LaunchDelayMs = Defaults.LaunchDelayMs
                    };

                    if (!TryValidateAndNormalizeProfile(profile, out string validationError))
                    {
                        failed++;

                        if (writeConsoleReport)
                        {
                            Console.WriteLine($"[FAIL] {safeName} ({game.Source})");
                            Console.WriteLine($"       Reason: {validationError}");
                        }

                        continue;
                    }

                    File.WriteAllText(profilePath, JsonConvert.SerializeObject(profile, Formatting.Indented));
                    created++;

                    if (writeConsoleReport)
                    {
                        Console.WriteLine($"[OK]   Created: {safeName}.json");
                        Console.WriteLine($"       URL:     {profile.EpicLaunchUrl}");
                        Console.WriteLine($"       Process: {profile.GameProcessName}");
                    }
                }

                if (writeConsoleReport)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Import summary: {created} created, {skipped} skipped (already existed), {failed} failed");
                }

                return failed == 0 ? ExitSuccess : ExitImportFailed;
            }
            catch (Exception ex)
            {
                if (writeConsoleReport)
                {
                    Console.WriteLine($"ERROR: Import failed. {ex.GetType().Name}: {ex.Message}");
                }

                return ExitImportFailed;
            }
        }

        private static List<EpicInstalledGame> DiscoverInstalledEpicGames()
        {
            var results = new List<EpicInstalledGame>();
            results.AddRange(ReadEpicItemManifests());
            results.AddRange(ReadLauncherInstalledDat());

            var deduped = results
                .GroupBy(
                    g =>
                        !string.IsNullOrWhiteSpace(g.AppName)
                            ? $"APP:{g.AppName.Trim()}"
                            : $"NAME:{(g.DisplayName ?? string.Empty).Trim()}|LOC:{(g.InstallLocation ?? string.Empty).Trim()}",
                    StringComparer.OrdinalIgnoreCase
                )
                .Select(grp =>
                    grp.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.LaunchExecutable))
                        .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.DisplayName))
                        .First()
                )
                .ToList();

            return deduped;
        }

        private static List<EpicInstalledGame> ReadEpicItemManifests()
        {
            var games = new List<EpicInstalledGame>();
            string manifestsDir = GetEpicManifestsDirectory();

            if (!Directory.Exists(manifestsDir))
            {
                return games;
            }

            foreach (string file in Directory.EnumerateFiles(manifestsDir, "*.item", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var obj = JObject.Parse(File.ReadAllText(file));

                    string displayName = ReadString(obj, "DisplayName");
                    string appName = ReadString(obj, "AppName");
                    string installLocation = ReadString(obj, "InstallLocation");
                    string launchExe = ReadString(obj, "LaunchExecutable");

                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = ReadString(obj, "Metadata", "DisplayName");
                    }

                    if (string.IsNullOrWhiteSpace(installLocation))
                    {
                        installLocation = ReadString(obj, "Install", "InstallLocation");
                    }

                    games.Add(
                        new EpicInstalledGame
                        {
                            DisplayName = displayName,
                            AppName = appName,
                            InstallLocation = installLocation,
                            LaunchExecutable = launchExe,
                            Source = $".item ({Path.GetFileName(file)})"
                        }
                    );
                }
                catch
                {
                    // ignore
                }
            }

            return games;
        }

        private static List<EpicInstalledGame> ReadLauncherInstalledDat()
        {
            var games = new List<EpicInstalledGame>();
            string path = GetLauncherInstalledDatPath();

            if (!File.Exists(path))
            {
                return games;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));

                if (root["InstallationList"] is not JArray arr)
                {
                    return games;
                }

                foreach (var token in arr)
                {
                    if (token is not JObject obj)
                    {
                        continue;
                    }

                    string appName = ReadString(obj, "AppName");
                    string installLocation = ReadString(obj, "InstallLocation");
                    string displayName = ReadString(obj, "DisplayName");

                    games.Add(
                        new EpicInstalledGame
                        {
                            DisplayName = displayName,
                            AppName = appName,
                            InstallLocation = installLocation,
                            LaunchExecutable = null,
                            Source = "LauncherInstalled.dat"
                        }
                    );
                }
            }
            catch
            {
                // ignore
            }

            return games;
        }

        private static string BuildEpicLaunchUrl(EpicInstalledGame game)
        {
            if (!string.IsNullOrWhiteSpace(game.AppName))
            {
                string app = game.AppName.Trim();
                return $"com.epicgames.launcher://apps/{app}?action=launch&silent=true";
            }

            return "com.epicgames.launcher://apps/YourGameId?action=launch&silent=true";
        }

        private static string GuessProcessName(EpicInstalledGame game)
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchExecutable))
            {
                try
                {
                    string exeFile = Path.GetFileName(game.LaunchExecutable.Trim());
                    return NormalizeProcessName(exeFile);
                }
                catch
                {
                    // ignore
                }
            }

            if (!string.IsNullOrWhiteSpace(game.DisplayName))
            {
                return NormalizeProcessName(game.DisplayName);
            }

            if (!string.IsNullOrWhiteSpace(game.AppName))
            {
                return NormalizeProcessName(game.AppName);
            }

            return "GameProcessNameWithoutExe";
        }

        private static string ReadString(JObject obj, params string[] path)
        {
            if (obj == null || path == null || path.Length == 0)
            {
                return null;
            }

            JToken current = obj;

            foreach (string part in path)
            {
                if (current is not JObject o)
                {
                    return null;
                }

                current = o[part];

                if (current == null)
                {
                    return null;
                }
            }

            return current.Type == JTokenType.String ? current.Value<string>() : current.ToString();
        }

        private static string GetEpicManifestsDirectory()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        }

        private static string GetLauncherInstalledDatPath()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();

            string cleaned = new string(chars).Trim();
            cleaned = cleaned.Replace(':', '_').Replace('/', '_').Replace('\\', '_');

            if (cleaned.Length > 80)
            {
                cleaned = cleaned.Substring(0, 80).Trim();
            }

            return cleaned;
        }

        // ---------------------------------------------------------------------
        // Wizard (Phase 2)
        // ---------------------------------------------------------------------

        private static void RunWizard()
        {
            string profilesDir = GetProfilesDirectory();

            Console.WriteLine("EpicSteamLauncher - Profile Wizard");
            Console.WriteLine("----------------------------------");
            Console.WriteLine("This will create a JSON profile in:");
            Console.WriteLine(profilesDir);
            Console.WriteLine();

            // Definite assignment safety.
            string name = string.Empty;
            string epicUrl = string.Empty;
            string processName = string.Empty;

            Console.Write("Pick from installed Epic games? (y/N): ");
            bool pickInstalled = ReadYesNo(true);

            if (pickInstalled)
            {
                var installed = DiscoverInstalledEpicGames()
                    .OrderBy(g => g.DisplayName ?? g.AppName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (installed.Count == 0)
                {
                    Console.WriteLine("No installed Epic games were discovered. Switching to manual entry.");
                    Console.WriteLine();
                    pickInstalled = false;
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Installed Epic games:");
                    Console.WriteLine("  [0] Cancel / Back");

                    for (int i = 0; i < installed.Count; i++)
                    {
                        string label = installed[i].DisplayName ?? installed[i].AppName ?? "Unknown";
                        Console.WriteLine($"  [{i + 1}] {label}");
                    }

                    Console.WriteLine();
                    Console.Write("Select a game number: ");
                    string input = (Console.ReadLine() ?? string.Empty).Trim();

                    if (!int.TryParse(input, out int choice) || choice < 0 || choice > installed.Count || choice == 0)
                    {
                        Console.WriteLine("Wizard cancelled.");
                        return;
                    }

                    var chosen = installed[choice - 1];

                    string proposedName = chosen.DisplayName ?? chosen.AppName ?? "NewProfile";
                    name = MakeSafeFileName(proposedName);
                    epicUrl = BuildEpicLaunchUrl(chosen);
                    processName = GuessProcessName(chosen);

                    Console.WriteLine();
                    Console.WriteLine("Auto-filled values (you can edit them):");
                    Console.WriteLine($"  Profile name: {name}");
                    Console.WriteLine($"  Epic URL:     {epicUrl}");
                    Console.WriteLine($"  Process:      {processName}");
                    Console.WriteLine();

                    Console.Write("Profile name (press Enter to keep): ");
                    string nameEdit = (Console.ReadLine() ?? string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(nameEdit))
                    {
                        name = nameEdit.Trim();
                    }

                    Console.Write("Epic launch URL (press Enter to keep): ");
                    string urlEdit = (Console.ReadLine() ?? string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(urlEdit))
                    {
                        epicUrl = urlEdit.Trim();
                    }

                    Console.Write("Game process name (press Enter to keep): ");
                    string procEdit = (Console.ReadLine() ?? string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(procEdit))
                    {
                        processName = procEdit.Trim();
                    }
                }
            }

            if (!pickInstalled)
            {
                Console.Write("Profile name (file name): ");
                name = (Console.ReadLine() ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Profile name cannot be empty.");
                }

                Console.Write("Epic launch URL: ");
                epicUrl = (Console.ReadLine() ?? string.Empty).Trim();

                Console.Write("Game process name (with or without .exe): ");
                processName = (Console.ReadLine() ?? string.Empty).Trim();
            }

            Console.Write($"Start timeout seconds (default {Defaults.StartTimeoutSeconds}): ");
            int timeoutSeconds = ReadIntOrDefault(Defaults.StartTimeoutSeconds);

            Console.Write($"Poll interval ms (default {Defaults.PollIntervalMs}): ");
            int pollIntervalMs = ReadIntOrDefault(Defaults.PollIntervalMs);

            Console.Write($"Launch delay ms before scanning (default {Defaults.LaunchDelayMs}): ");
            int launchDelayMs = ReadIntOrDefault(Defaults.LaunchDelayMs);

            var profile = new GameProfile
            {
                Name = name,
                EpicLaunchUrl = epicUrl,
                GameProcessName = processName,
                StartTimeoutSeconds = timeoutSeconds,
                PollIntervalMs = pollIntervalMs,
                LaunchDelayMs = launchDelayMs
            };

            if (!TryValidateAndNormalizeProfile(profile, out string validationError))
            {
                throw new InvalidOperationException($"Profile is invalid: {validationError}");
            }

            string safeFileName = MakeSafeFileName(profile.Name);

            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                safeFileName = "NewProfile";
            }

            string profilePath = Path.Combine(profilesDir, $"{safeFileName}.json");

            if (File.Exists(profilePath))
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: A profile named '{safeFileName}' already exists.");
                Console.Write("Overwrite it? (y/N): ");
                bool overwrite = ReadYesNo(true);

                if (!overwrite)
                {
                    Console.WriteLine("Wizard cancelled (no changes made).");
                    return;
                }
            }

            File.WriteAllText(profilePath, JsonConvert.SerializeObject(profile, Formatting.Indented));

            Console.WriteLine();
            Console.WriteLine("Profile created:");
            Console.WriteLine(profilePath);
            Console.WriteLine();
            Console.WriteLine("Steam Launch Options to use:");
            Console.WriteLine($"  --profile \"{safeFileName}\"");
            Console.WriteLine();

            Console.Write("Open profiles folder now? (y/N): ");

            if (ReadYesNo(true))
            {
                TryOpenFolderInExplorer(profilesDir);
            }
        }

        private static bool ReadYesNo(bool defaultNo)
        {
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return !defaultNo;
            }

            return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------
        // Profile launching
        // ---------------------------------------------------------------------

        private static int LaunchFromProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                Console.WriteLine("ERROR: Profile name is required.");
                return ExitBadArgs;
            }

            string profilePath = Path.Combine(GetProfilesDirectory(), $"{profileName}.json");

            if (!TryLoadAndValidateProfile(profilePath, out var profile, out string error))
            {
                Console.WriteLine($"ERROR: Profile '{profileName}' is invalid or could not be loaded.");
                Console.WriteLine($"Path:   {profilePath}");
                Console.WriteLine($"Reason: {error}");
                return File.Exists(profilePath) ? ExitProfileInvalid : ExitProfileNotFound;
            }

            Console.WriteLine($"Launching profile: {profileName}");
            Console.WriteLine($"Epic URL: {profile.EpicLaunchUrl}");
            Console.WriteLine($"Process:  {profile.GameProcessName}");
            Console.WriteLine();

            return Launch(
                profile.EpicLaunchUrl,
                profile.GameProcessName,
                profile.StartTimeoutSeconds,
                profile.PollIntervalMs,
                profile.LaunchDelayMs
            );
        }

        // ---------------------------------------------------------------------
        // Phase 3: Core launch logic with improved process selection
        // ---------------------------------------------------------------------

        private static int Launch(string epicUrl, string exeName, int timeoutSeconds, int pollIntervalMs, int launchDelayMs)
        {
            if (string.IsNullOrWhiteSpace(epicUrl) || string.IsNullOrWhiteSpace(exeName))
            {
                PrintUsage();
                return ExitBadArgs;
            }

            // Snapshot currently running PIDs for the target process name BEFORE launching.
            var baselinePids = CaptureExistingProcessIds(exeName);

            // Record launch time (local time is fine here because Process.StartTime is local time).
            var launchStartLocal = DateTime.Now;

            // Open the Epic URL via a protocol handler.
            try
            {
                var psi = new ProcessStartInfo(epicUrl)
                {
                    UseShellExecute = true,
                    Verb = "open"
                };

                Console.WriteLine($"Starting Epic URL: {epicUrl}");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to launch Epic URL. {ex.GetType().Name}: {ex.Message}");
                return ExitLaunchFailed;
            }

            // Optional delay before scanning for the process.
            if (launchDelayMs > 0)
            {
                Console.WriteLine($"Waiting {launchDelayMs}ms before scanning for process...");
                Thread.Sleep(launchDelayMs);
            }

            var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? Defaults.StartTimeoutSeconds : timeoutSeconds);
            var poll = TimeSpan.FromMilliseconds(pollIntervalMs <= 0 ? Defaults.PollIntervalMs : pollIntervalMs);
            var deadline = DateTime.UtcNow + timeout;

            // We allow a small tolerance because process StartTime can be very close to the launch moment.
            var minStartTimeLocal = launchStartLocal.AddSeconds(-Defaults.StartTimeToleranceSeconds);

            Process fallbackAnyMatch = null;

            while (DateTime.UtcNow < deadline)
            {
                Process[] matches;

                try
                {
                    matches = Process.GetProcessesByName(exeName);
                }
                catch
                {
                    matches = [];
                }

                if (matches.Length > 0)
                {
                    // Keep a fallback of "any match" in case no "new" process appears.
                    // Prefer the newest one if we can read StartTime.
                    fallbackAnyMatch = SelectNewestProcessIfPossible(matches) ?? matches[0];

                    // Prefer processes that were NOT running before we launched (new PID).
                    var bestNew = SelectBestNewProcess(matches, baselinePids, minStartTimeLocal);

                    if (bestNew != null)
                    {
                        Console.WriteLine($"Game detected (new instance) (PID: {bestNew.Id}). Waiting for exit...");
                        WaitForExitSafe(bestNew);
                        return ExitSuccess;
                    }
                }

                Thread.Sleep(poll);
            }

            // Timeout reached. Optionally, fall back to any matching process.
            if (Defaults.FallbackToAnyMatchingProcess && fallbackAnyMatch != null)
            {
                Console.WriteLine("WARNING: No new game process was detected before timeout.");
                Console.WriteLine($"Falling back to existing matching process (PID: {fallbackAnyMatch.Id}). Waiting for exit...");
                WaitForExitSafe(fallbackAnyMatch);
                return ExitSuccess;
            }

            Console.WriteLine($"ERROR: Could not find process '{exeName}' before timeout ({timeout.TotalSeconds:0}s).");
            return ExitProcessNotFound;
        }

        /// <summary>
        ///     Captures existing process IDs for a given process name.
        ///     Used to distinguish "newly started" instances from existing ones.
        /// </summary>
        private static HashSet<int> CaptureExistingProcessIds(string exeName)
        {
            var set = new HashSet<int>();

            try
            {
                foreach (var p in Process.GetProcessesByName(exeName))
                {
                    try
                    {
                        set.Add(p.Id);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
            catch
            {
                // ignore
            }

            return set;
        }

        /// <summary>
        ///     Selects the best "new" process instance:
        ///     - PID was not present in baseline.
        ///     - If StartTime is available, it must be >= minStartTimeLocal (with tolerance).
        ///     - Prefer the newest StartTime; otherwise, prefer the highest PID (reasonable proxy).
        /// </summary>
        private static Process SelectBestNewProcess(Process[] matches, HashSet<int> baselinePids, DateTime minStartTimeLocal)
        {
            if (matches == null || matches.Length == 0)
            {
                return null;
            }

            var candidates = new List<(Process Proc, DateTime? StartTimeLocal)>();

            foreach (var p in matches)
            {
                int pid;

                try
                {
                    pid = p.Id;
                }
                catch
                {
                    continue;
                }

                if (baselinePids != null && baselinePids.Contains(pid))
                {
                    continue;
                }

                var start = TryGetStartTimeLocal(p);

                // If we can read StartTime, enforce a minimum start time threshold.
                if (start.HasValue && start.Value < minStartTimeLocal)
                {
                    continue;
                }

                candidates.Add((p, start));
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer candidates with StartTime and pick the newest.
            var withTime = candidates.Where(c => c.StartTimeLocal.HasValue).ToList();

            if (withTime.Count > 0)
            {
                return withTime.OrderByDescending(c => c.StartTimeLocal ?? default).First().Proc;
            }

            // Otherwise, choose the highest PID (often correlates with the newest).
            return candidates.OrderByDescending(c =>
                {
                    try
                    {
                        return c.Proc.Id;
                    }
                    catch
                    {
                        return -1;
                    }
                }
            ).First().Proc;
        }

        /// <summary>
        ///     If possible, selects the newest process by StartTime; otherwise returns null.
        /// </summary>
        private static Process SelectNewestProcessIfPossible(Process[] matches)
        {
            var bestTime = DateTime.MinValue;
            Process best = null;

            foreach (var p in matches)
            {
                var st = TryGetStartTimeLocal(p);

                if (!st.HasValue)
                {
                    continue;
                }

                if (st.Value > bestTime)
                {
                    bestTime = st.Value;
                    best = p;
                }
            }

            return best;
        }

        /// <summary>
        ///     Attempts to read process StartTime (local). This can throw for protected processes.
        /// </summary>
        private static DateTime? TryGetStartTimeLocal(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Waits for a process to exit, safely.
        /// </summary>
        private static void WaitForExitSafe(Process process)
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Unable to wait for process. {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // Models / Helpers
        // ---------------------------------------------------------------------

        private sealed class GameProfile
        {
            public string Name { get; set; }
            public string EpicLaunchUrl { get; set; }
            public string GameProcessName { get; set; }

            public int StartTimeoutSeconds { get; set; } = Defaults.StartTimeoutSeconds;
            public int PollIntervalMs { get; set; } = Defaults.PollIntervalMs;
            public int LaunchDelayMs { get; set; } = Defaults.LaunchDelayMs;
        }

        private readonly struct DiscoveredProfile(string name, string path, GameProfile profile)
        {
            public string Name { get; } = name;
            public string Path { get; } = path;
            public GameProfile Profile { get; } = profile;
        }

        private static string GetProfilesDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, ProfilesFolderName);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("EpicSteamLauncher Usage:");
            Console.WriteLine();
            Console.WriteLine("Profiles:");
            Console.WriteLine("  EpicSteamLauncher.exe                      (bootstrap + menu)");
            Console.WriteLine("  EpicSteamLauncher.exe --wizard             (create profile interactively)");
            Console.WriteLine("  EpicSteamLauncher.exe --import-installed   (auto-create profiles from installed Epic games)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile \"Name\"      (launch profiles/Name.json)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile            (select from valid profiles)");
            Console.WriteLine("  EpicSteamLauncher.exe --validate-profiles  (print validation report)");
            Console.WriteLine();
            Console.WriteLine("Legacy:");
            Console.WriteLine("  EpicSteamLauncher.exe \"<EpicLaunchUrl>\" <GameExeName>");
        }

        private static string NormalizeProcessName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string name = raw.Trim();

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return name;
        }

        private static int ReadIntOrDefault(int defaultValue)
        {
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            return int.TryParse(input, out int value) ? value : defaultValue;
        }

        private static void TryOpenFolderInExplorer(string folderPath)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = folderPath,
                        UseShellExecute = true
                    }
                );
            }
            catch
            {
                // ignored
            }
        }

        private static string BuildProfilesReadmeText(string profilesDir)
        {
            return
                $@"EpicSteamLauncher - Profiles

This folder contains per-game JSON profiles used by EpicSteamLauncher.

Quick Start:
  1) Create a profile:
       EpicSteamLauncher.exe --wizard

  2) (Optional) Auto-create profiles for installed Epic games:
       EpicSteamLauncher.exe --import-installed

  3) In Steam:
       - Add EpicSteamLauncher.exe as a Non-Steam Game
       - Set Launch Options to:
           --profile ""YourProfileName""

Profiles live here:
  {profilesDir}

Notes:
  - GameProcessName should be the process name (with or without .exe).
  - You can validate all profiles from the app menu or via:
       EpicSteamLauncher.exe --validate-profiles
";
        }

        private static void PauseIfInteractive()
        {
            try
            {
                if (!Console.IsInputRedirected)
                {
                    Thread.Sleep(250);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
