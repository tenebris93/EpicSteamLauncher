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

    PHASE 4 ADDITIONS
    -----------------
    - Process Diagnostics Fallback (interactive + profile launch only):
        - If process name is wrong and we time out, scan for newly started processes after launch.
        - Prefer candidates whose EXE path is under the profile's InstallLocation (if known).
        - Allow user to pick the correct process name.
        - Optionally write the corrected GameProcessName back into the profile JSON.

    JSON Dependency:
      - This file expects Newtonsoft.Json to be referenced by the project.
        Add it via NuGet:
            dotnet add package Newtonsoft.Json
*/

using System.Diagnostics;
using System.Management;
using System.Text;
using EpicSteamLauncher.Application.Internal;
using EpicSteamLauncher.Application.Models;
using EpicSteamLauncher.Configuration;
using EpicSteamLauncher.Infrastructure.Steam;
using EpicSteamLauncher.Services.SteamGridDb;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EpicSteamLauncher.Application
{
    /// <summary>
    ///     Executes the launcher command flow and interactive menu behavior.
    /// </summary>
    internal static class LauncherApplication
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
        private const int ExitUnexpectedError = 8;

        private const string ProfilesFolderName = "profiles";

        private static bool _pauseOnExit;

        /// <summary>
        ///     Runs the main application workflow for CLI and interactive modes.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Process exit code.</returns>
        internal static int Run(string[] args)
        {
            return args.Length == 0 ? RunInteractive() : RunCommandLine(args);
        }

        /// <summary>
        ///     Runs interactive menu mode when no command-line arguments are provided.
        /// </summary>
        /// <returns>Process exit code.</returns>
        internal static int RunInteractive()
        {
            _pauseOnExit = true; // menu mode (double-click): keep pause behavior if you want it
            BootstrapProfilesFolder();
            return ShowMainMenu();
        }

        /// <summary>
        ///     Runs command-line mode for explicit launcher commands.
        /// </summary>
        /// <param name="args">Non-empty command-line arguments.</param>
        /// <returns>Process exit code.</returns>
        internal static int RunCommandLine(string[] args)
        {
            var parsed = ParseArgs(args);

            // For Steam / args-mode: default is no pause, unless --pause was explicitly provided
            _pauseOnExit = parsed.PauseOnExit;

            if (parsed.Command == "legacy")
            {
                string epicUrl = parsed.Positionals[0].Trim();
                string exeName = NormalizeProcessName(parsed.Positionals[1]);
                int rc = Launch(
                    epicUrl,
                    exeName,
                    Defaults.StartTimeoutSeconds,
                    Defaults.PollIntervalMs,
                    Defaults.LegacyLaunchDelayMs,
                    null,
                    null
                );

                PauseIfInteractive();
                return rc;
            }

            if (parsed.Command is null)
            {
                PrintUsage();
                PauseIfInteractive();
                return ExitBadArgs;
            }

            if (parsed.Command.Equals("--wizard", StringComparison.OrdinalIgnoreCase))
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

            if (parsed.Command.Equals("--profile", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();

                if (!string.IsNullOrWhiteSpace(parsed.CommandValue))
                {
                    int rc = LaunchFromProfileName(parsed.CommandValue);
                    PauseIfInteractive();
                    return rc;
                }

                int selectorRc = ProfilesSelectorScreen();
                PauseIfInteractive();
                return selectorRc;
            }

            if (parsed.Command.Equals("--validate-profiles", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();
                int rc = ValidateProfilesReport();
                PauseIfInteractive();
                return rc;
            }

            if (parsed.Command.Equals("--sync-nonsteam", StringComparison.OrdinalIgnoreCase))
            {
                BootstrapProfilesFolder();
                int rc = SyncNonSteamShortcutsFromProfiles(false);
                PauseIfInteractive();
                return rc;
            }

            if (parsed.Command.Equals("--import-installed", StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        ///     Determines whether the provided token is a command-style flag.
        /// </summary>
        /// <param name="token">Token value to classify.</param>
        /// <returns><see langword="true" /> when the token starts with '-' or '/'; otherwise <see langword="false" />.</returns>
        private static bool IsFlag(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim();
            return token.StartsWith('-') || token.StartsWith('/');
        }

        // ---------------------------------------------------------------------
        // Bootstrap / Main Menu
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Ensures the profiles folder exists and initializes optional onboarding files.
        /// </summary>
        private static void BootstrapProfilesFolder()
        {
            string profilesDir = GetProfilesDirectory();

            if (!TryEnsureDirectory(profilesDir, out string? dirError))
            {
                Console.WriteLine("WARNING: Could not create profiles folder (launcher can still run).");
                Console.WriteLine($"Location: {profilesDir}");
                Console.WriteLine($"Reason: {dirError}");
                Console.WriteLine();
                return;
            }

            // README + example are optional niceties—don’t fail if these writes don’t work.
            try
            {
                string readmePath = Path.Combine(profilesDir, "README.txt");

                if (!File.Exists(readmePath))
                {
                    WriteAllTextAtomic(readmePath, BuildProfilesReadmeText(profilesDir));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Failed to write README.txt. {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                string examplePath = Path.Combine(profilesDir, "example.profile.esl");

                if (!File.Exists(examplePath))
                {
                    var example = new GameProfile
                    {
                        Name = "ExampleGame",
                        EpicLaunchUrl = "com.epicgames.launcher://apps/YourGameId?action=launch&silent=true",
                        GameProcessName = "GameProcessNameWithoutExe",
                        StartTimeoutSeconds = Defaults.StartTimeoutSeconds,
                        PollIntervalMs = Defaults.PollIntervalMs,
                        LaunchDelayMs = Defaults.LaunchDelayMs,
                        InstallLocation = "",
                        LaunchExecutable = ""
                    };

                    if (!TryWriteJsonAtomic(examplePath, example, out string? writeErr))
                    {
                        Console.WriteLine($"WARNING: Failed to write example.profile.esl. {writeErr}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Failed to write example.profile.esl. {ex.GetType().Name}: {ex.Message}");
            }

            Console.WriteLine("EpicSteamLauncher: Profiles folder is ready.");
            Console.WriteLine($"Location: {profilesDir}");
            Console.WriteLine("Files: README.txt, example.profile.esl (created if missing)");
            Console.WriteLine();
        }

        /// <summary>
        ///     Displays the interactive main menu loop and executes selected actions.
        /// </summary>
        /// <returns>Process exit code.</returns>
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
                    "Sync games to Steam (Non-Steam shortcuts)",
                    () =>
                    {
                        int rc = SyncNonSteamShortcutsFromProfiles(true);
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
                // Prevent "text shifting" by redrawing menu from a clean screen
                Console.ResetColor();
                Console.Clear();

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
                    Console.WriteLine("Invalid selection. Please enter a number from the list.");
                    Console.WriteLine();
                    Console.Write("Press Enter to continue...");
                    Console.ReadLine();
                    continue;
                }

                // Run the selected option
                MenuOutcome outcome;

                try
                {
                    outcome = options[selected - 1].Action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                    outcome = MenuOutcome.Continue(ExitUnexpectedError);
                }

                // If the action wants to exit, exit immediately (don’t clear / redraw menu)
                if (outcome.ShouldExit)
                {
                    return ExitSuccess;
                }

                // Show failure summary (you already do this)
                if (outcome.LastResultCode.HasValue)
                {
                    int rc = outcome.LastResultCode.Value;

                    if (rc != ExitSuccess)
                    {
                        Console.WriteLine($"Result: Failed (code {rc})");
                        Console.WriteLine();
                    }
                }

                // IMPORTANT: Pause so the user can read the output before the next loop clears the screen
                Console.WriteLine();
                Console.Write("Press Enter to return to the main menu...");
                Console.ReadLine();
            }
        }

        // ---------------------------------------------------------------------
        // Profiles: enumeration + validation
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Enumerates candidate profile files from the profiles directory.
        /// </summary>
        /// <returns>Sequence of profile file paths excluding the generated example profile.</returns>
        private static IEnumerable<string> EnumerateCandidateProfileJsonFiles()
        {
            string dir = GetProfilesDirectory();

            if (!Directory.Exists(dir))
            {
                yield break;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.esl", SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileName(file);

                if (filename.Equals("example.profile.esl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }

        /// <summary>
        ///     Loads a profile from disk and validates its required fields.
        /// </summary>
        /// <param name="path">Profile file path.</param>
        /// <param name="profile">Loaded and normalized profile on success.</param>
        /// <param name="error">Validation or load error details on failure.</param>
        /// <returns><see langword="true" /> when loading and validation succeed; otherwise <see langword="false" />.</returns>
        private static bool TryLoadAndValidateProfile(string path, out GameProfile? profile, out string? error)
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

        /// <summary>
        ///     Validates profile data and normalizes values used by launcher flows.
        /// </summary>
        /// <param name="profile">Profile instance to validate.</param>
        /// <param name="error">Validation error details when validation fails.</param>
        /// <returns><see langword="true" /> when profile data is valid after normalization; otherwise <see langword="false" />.</returns>
        private static bool TryValidateAndNormalizeProfile(GameProfile? profile, out string? error)
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

            // Optional fields: normalize empty strings.
            profile.InstallLocation = profile.InstallLocation?.Trim() ?? string.Empty;
            profile.LaunchExecutable = profile.LaunchExecutable?.Trim() ?? string.Empty;

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

        /// <summary>
        ///     Collects and sorts valid profiles discovered on disk.
        /// </summary>
        /// <returns>Sorted list of valid profiles.</returns>
        private static List<DiscoveredProfile> GetValidProfiles()
        {
            var results = new List<DiscoveredProfile>();

            foreach (string file in EnumerateCandidateProfileJsonFiles())
            {
                string displayName = Path.GetFileNameWithoutExtension(file);

                if (TryLoadAndValidateProfile(file, out var profile, out _) && profile != null)
                {
                    results.Add(new DiscoveredProfile(displayName, file, profile));
                }
            }

            return results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        ///     Displays profile selection and launches the chosen profile when selected.
        /// </summary>
        /// <returns>Process exit code.</returns>
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

            if (!int.TryParse(input, out int selected) || selected == 0 || selected < 1 || selected > profiles.Count)
            {
                return ExitSuccess;
            }

            string chosenName = profiles[selected - 1].Name;
            Console.WriteLine();

            return LaunchFromProfileName(chosenName);
        }

        /// <summary>
        ///     Validates all candidate profiles and prints a report to the console.
        /// </summary>
        /// <returns>Validation command exit code.</returns>
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

                if (TryLoadAndValidateProfile(file, out _, out string? error))
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

        /// <summary>
        ///     Discovers installed Epic games and writes missing profile files.
        /// </summary>
        /// <param name="writeConsoleReport">Whether to print per-game import details.</param>
        /// <returns>Import command exit code.</returns>
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

                if (!TryEnsureDirectory(profilesDir, out string? dirError))
                {
                    Console.WriteLine($"ERROR: Could not create profiles folder: {profilesDir}");
                    Console.WriteLine($"Reason: {dirError}");
                    return ExitImportFailed;
                }

                if (!IsDirectoryWritable(profilesDir, out string? whyNotWritable))
                {
                    Console.WriteLine($"ERROR: Profiles folder is not writable: {profilesDir}");
                    Console.WriteLine($"Reason: {whyNotWritable}");
                    return ExitImportFailed;
                }

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

                    string profilePath = Path.Combine(profilesDir, $"{safeName}.esl");

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
                        LaunchDelayMs = Defaults.LaunchDelayMs,
                        InstallLocation = game.InstallLocation ?? string.Empty,
                        LaunchExecutable = game.LaunchExecutable ?? string.Empty
                    };

                    if (!TryValidateAndNormalizeProfile(profile, out string? validationError))
                    {
                        failed++;

                        if (writeConsoleReport)
                        {
                            Console.WriteLine($"[FAIL] {safeName} ({game.Source})");
                            Console.WriteLine($"       Reason: {validationError ?? "Unknown validation error"}");
                        }

                        continue;
                    }

                    if (!TryWriteJsonAtomic(profilePath, profile, out string? err))
                    {
                        failed++;

                        if (writeConsoleReport)
                        {
                            Console.WriteLine($"[FAIL] {safeName} ({game.Source})");
                            Console.WriteLine($"       Reason: Failed to write profile. {err}");
                        }

                        continue;
                    }

                    created++;

                    if (writeConsoleReport)
                    {
                        Console.WriteLine($"[OK]   Created: {safeName}.esl");
                        Console.WriteLine($"       URL:     {profile.EpicLaunchUrl}");
                        Console.WriteLine($"       Process: {profile.GameProcessName}");

                        if (!string.IsNullOrWhiteSpace(profile.InstallLocation))
                        {
                            Console.WriteLine($"       Install: {profile.InstallLocation}");
                        }
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

        /// <summary>
        ///     Aggregates installed Epic game records from supported launcher data sources.
        /// </summary>
        /// <returns>Deduplicated installed game list.</returns>
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

        /// <summary>
        ///     Reads installed game metadata from Epic <c>.item</c> manifest files.
        /// </summary>
        /// <returns>Games discovered from manifest files.</returns>
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

                    string? displayName = ReadString(obj, "DisplayName");
                    string? appName = ReadString(obj, "AppName");
                    string? installLocation = ReadString(obj, "InstallLocation");
                    string? launchExe = ReadString(obj, "LaunchExecutable");

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

        /// <summary>
        ///     Reads installed game metadata from <c>LauncherInstalled.dat</c>.
        /// </summary>
        /// <returns>Games discovered from launcher installation metadata.</returns>
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

                    string? appName = ReadString(obj, "AppName");
                    string? installLocation = ReadString(obj, "InstallLocation");
                    string? displayName = ReadString(obj, "DisplayName");

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

        /// <summary>
        ///     Builds a best-effort Epic launch URI for a discovered game.
        /// </summary>
        /// <param name="game">Installed game metadata.</param>
        /// <returns>Epic launch URI.</returns>
        private static string BuildEpicLaunchUrl(EpicInstalledGame game)
        {
            if (!string.IsNullOrWhiteSpace(game.AppName))
            {
                string app = game.AppName.Trim();
                return $"com.epicgames.launcher://apps/{app}?action=launch&silent=true";
            }

            return "com.epicgames.launcher://apps/YourGameId?action=launch&silent=true";
        }

        /// <summary>
        ///     Produces an initial process-name guess from discovered game metadata.
        /// </summary>
        /// <param name="game">Installed game metadata.</param>
        /// <returns>Normalized process name guess.</returns>
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

        /// <summary>
        ///     Syncs valid launcher profiles into Steam non-Steam shortcuts and optional artwork assets.
        /// </summary>
        /// <param name="interactive">Whether to prompt for interactive decisions during sync.</param>
        /// <returns>Sync command exit code.</returns>
        private static int SyncNonSteamShortcutsFromProfiles(bool interactive)
        {
            // -----------------------------
            // 1) Locate Steam + shortcuts.vdf
            // -----------------------------
            string? steamPath = SteamLocator.TryGetSteamPath();

            if (steamPath == null)
            {
                Console.WriteLine("ERROR: Could not locate Steam install path.");

                if (interactive)
                {
                    PauseIfInteractive();
                }

                return ExitBadArgs;
            }

            string? shortcutsVdfPath = SteamLocator.TryGetShortcutsVdfPathEvenIfMissing(steamPath);

            if (shortcutsVdfPath == null)
            {
                Console.WriteLine("ERROR: Could not locate a Steam userdata config directory.");

                if (interactive)
                {
                    PauseIfInteractive();
                }

                return ExitBadArgs;
            }

            // -----------------------------
            // 2) Resolve profiles
            // -----------------------------
            var profiles = GetValidProfiles();

            if (profiles.Count == 0)
            {
                Console.WriteLine("No valid profiles to sync.");

                if (interactive)
                {
                    PauseIfInteractive();
                }

                return ExitSuccess;
            }

            // -----------------------------
            // 3) Determine launcher exe & start dir (for shortcut Exe/StartDir)
            // -----------------------------
            string selfExe =
                Environment.ProcessPath ?? throw new InvalidOperationException("Unable to determine current executable path.");

            string startDir = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );

            string exeQuoted = Quote(selfExe);
            string startDirQuoted = Quote(startDir);

            // -----------------------------
            // 4) SteamGridDB config + key flow (same UX you already built)
            // -----------------------------
            string sgdbConfigPath = Path.Combine(AppContext.BaseDirectory, "steamgriddb.json");
            var cfg = SteamGridDbConfig.LoadOrCreate(sgdbConfigPath, out bool createdConfig);

            bool hasKey = !string.IsNullOrWhiteSpace(cfg.ApiKey);
            bool keyValid = false;

            // This flag is the key: if true, we will NOT attempt artwork/icon downloads this run.
            bool skipArtworkThisRun = cfg.DontAskAgain;

            if (interactive && !skipArtworkThisRun && (!hasKey || createdConfig))
            {
                Console.WriteLine();
                Console.WriteLine("SteamGridDB Artwork");
                Console.WriteLine("-------------------");
                Console.WriteLine("To automatically download library artwork (grid/hero/logo/wide/icon), you need a SteamGridDB API key.");
                Console.WriteLine($"Config file: {sgdbConfigPath}");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  1) Skip for now (sync games without artwork)");
                Console.WriteLine("  2) Open config file (add the key yourself)");
                Console.WriteLine("  3) Don't ask again (sync without artwork; you can edit the file later)");
                Console.WriteLine();

                int choice = ReadMenuChoice(1, 3, 1);

                if (choice == 2)
                {
                    TryOpenFile(sgdbConfigPath);
                    Console.WriteLine("Edit the file and add your API key, then run sync again.");
                    return ExitSuccess; // back to the main menu
                }

                if (choice == 3)
                {
                    cfg.DontAskAgain = true;
                    SteamGridDbConfig.SaveAtomic(sgdbConfigPath, cfg);
                }

                // Choice 1 or 3: proceed syncing shortcuts WITHOUT artwork this run
                skipArtworkThisRun = true;
            }

            if (!skipArtworkThisRun && hasKey)
            {
                try
                {
                    keyValid = SteamGridDbClient
                        .ValidateApiKeyAsync(cfg.ApiKey, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    keyValid = false;
                }

                if (interactive && !keyValid)
                {
                    Console.WriteLine();
                    Console.WriteLine("SteamGridDB Artwork");
                    Console.WriteLine("-------------------");
                    Console.WriteLine("Your SteamGridDB API key appears invalid (or SteamGridDB is unreachable).");
                    Console.WriteLine($"Config file: {sgdbConfigPath}");
                    Console.WriteLine();
                    Console.WriteLine("Options:");
                    Console.WriteLine("  1) Skip for now (sync games without artwork)");
                    Console.WriteLine("  2) Open config file (fix the key yourself)");
                    Console.WriteLine("  3) Don't ask again (sync without artwork; you can edit the file later)");
                    Console.WriteLine();

                    int choice = ReadMenuChoice(1, 3, 1);

                    if (choice == 2)
                    {
                        TryOpenFile(sgdbConfigPath);
                        Console.WriteLine("Edit the file and add your API key, then run sync again.");
                        return ExitSuccess;
                    }

                    if (choice == 3)
                    {
                        cfg.DontAskAgain = true;
                        SteamGridDbConfig.SaveAtomic(sgdbConfigPath, cfg);
                    }

                    skipArtworkThisRun = true;
                }
                else if (!interactive && !keyValid)
                {
                    skipArtworkThisRun = true;
                }
            }

            // -----------------------------
            // 5) PHASE A: Upsert shortcuts (WITHOUT icon path)
            // -----------------------------
            var root = SteamShortcutsEditor.LoadOrCreateShortcuts(shortcutsVdfPath);

            int created = 0;
            int updated = 0;

            // We'll use this list for artwork downloads and for icon updates later
            var touched = new List<(uint AppId, string GameNameForSearch)>();

            foreach (var p in profiles)
            {
                string displayName = $"{p.Name} (Epic)";

                // Stable identity: exe path + profile name (not display)
                uint appId = SteamShortcutId.GenerateAppId(selfExe, p.Name);

                string launchOptions = $"--profile \"{p.Name}\"";

                // IMPORTANT: iconPath is null here (we set it AFTER downloading icons)
                var result = SteamShortcutsEditor.UpsertShortcutForProfile(
                    root,
                    displayName,
                    null,
                    exeQuoted,
                    startDirQuoted,
                    launchOptions,
                    appId,
                    ["Epic", "Imported"]
                );

                if (result == ShortcutUpsertResult.Created)
                {
                    created++;
                }
                else
                {
                    updated++;
                }

                touched.Add((appId, p.Name));
            }

            SteamShortcutsEditor.SaveAtomic(shortcutsVdfPath, root);

            Console.WriteLine();
            Console.WriteLine("Steam sync complete.");
            Console.WriteLine($"  Shortcuts created: {created}");
            Console.WriteLine($"  Shortcuts updated: {updated}");
            Console.WriteLine($"  shortcuts.vdf: {shortcutsVdfPath}");

            // -----------------------------
            // 6) PHASE B: Download artwork (grid/hero/logo/wide/icon)
            // -----------------------------
            string? gridFolder = null;

            if (!skipArtworkThisRun && hasKey && keyValid)
            {
                gridFolder = SteamLocator.TryGetGridFolderEvenIfMissing(steamPath);

                if (gridFolder == null)
                {
                    Console.WriteLine("WARNING: Could not locate Steam grid folder; skipping artwork.");
                }
                else
                {
                    try
                    {
                        SteamArtworkWriter
                            .DownloadMissingArtworkForShortcutsAsync(
                                cfg.ApiKey,
                                gridFolder,
                                touched,
                                CancellationToken.None
                            )
                            .GetAwaiter()
                            .GetResult();

                        Console.WriteLine($"Artwork sync complete. grid folder: {gridFolder}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WARNING: Artwork sync failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                if (interactive)
                {
                    Console.WriteLine();
                    Console.WriteLine("Artwork sync skipped.");
                    Console.WriteLine($"You can enable it later by adding an API key here: {sgdbConfigPath}");
                }
            }

            // -----------------------------
            // 7) PHASE C: Set shortcut icon paths (AFTER icons exist)
            // -----------------------------
            if (!skipArtworkThisRun && hasKey && keyValid && !string.IsNullOrWhiteSpace(gridFolder))
            {
                var root2 = SteamShortcutsEditor.LoadOrCreateShortcuts(shortcutsVdfPath);

                int iconsSet = 0;

                foreach ((uint appId, string _) in touched)
                {
                    string? iconPath = FindExistingIconPath(gridFolder, appId);

                    if (iconPath == null)
                    {
                        continue;
                    }

                    if (SteamShortcutsEditor.TrySetIconPath(root2, appId, iconPath))
                    {
                        iconsSet++;
                    }
                }

                if (iconsSet > 0)
                {
                    SteamShortcutsEditor.SaveAtomic(shortcutsVdfPath, root2);
                    Console.WriteLine($"Updated shortcut icons: {iconsSet}");
                }
            }

            return ExitSuccess;
        }

        // Helper for quoting Steam shortcut fields
        /// <summary>
        ///     Wraps a value in quotes unless already quoted.
        /// </summary>
        /// <param name="value">Value to quote.</param>
        /// <returns>Quoted value.</returns>
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            {
                return value;
            }

            return $"\"{value}\"";
        }

        /// <summary>
        ///     Attempts to open a file path with the shell default handler.
        /// </summary>
        /// <param name="filePath">File path to open.</param>
        private static void TryOpenFile(string filePath)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    }
                );
            }
            catch
            {
                Console.WriteLine($"Could not open file: {filePath}");
            }
        }

        /// <summary>
        ///     Reads a numeric menu choice within the provided range.
        /// </summary>
        /// <param name="min">Minimum accepted value.</param>
        /// <param name="max">Maximum accepted value.</param>
        /// <param name="defaultChoice">Fallback value when input is empty.</param>
        /// <returns>Validated numeric choice.</returns>
        private static int ReadMenuChoice(int min, int max, int defaultChoice)
        {
            while (true)
            {
                Console.Write($"Select [{min}-{max}] (default {defaultChoice}): ");
                string? input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    return defaultChoice;
                }

                if (int.TryParse(input, out int choice) && choice >= min && choice <= max)
                {
                    return choice;
                }

                Console.WriteLine("Invalid choice.");
            }
        }

        /// <summary>
        ///     Reads a string-like value from a nested JSON object path.
        /// </summary>
        /// <param name="obj">Root JSON object.</param>
        /// <param name="path">Property path segments.</param>
        /// <returns>Resolved string value, or <see langword="null" /> when unavailable.</returns>
        private static string? ReadString(JObject? obj, params string[]? path)
        {
            if (obj == null || path == null || path.Length == 0)
            {
                return null;
            }

            JToken? current = obj;

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

        /// <summary>
        ///     Gets the Epic manifest directory under ProgramData.
        /// </summary>
        /// <returns>Manifest directory path.</returns>
        private static string GetEpicManifestsDirectory()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        }

        /// <summary>
        ///     Gets the expected <c>LauncherInstalled.dat</c> path under ProgramData.
        /// </summary>
        /// <returns>Metadata file path.</returns>
        private static string GetLauncherInstalledDatPath()
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        }

        /// <summary>
        ///     Converts a profile name into a filesystem-safe file name.
        /// </summary>
        /// <param name="name">Source name.</param>
        /// <returns>Sanitized file name.</returns>
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
        // Wizard
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Runs the interactive profile creation wizard.
        /// </summary>
        private static void RunWizard()
        {
            string profilesDir = GetProfilesDirectory();

            Console.WriteLine("EpicSteamLauncher - Profile Wizard");
            Console.WriteLine("----------------------------------");
            Console.WriteLine("This will create a JSON profile in:");
            Console.WriteLine(profilesDir);
            Console.WriteLine();

            string name = string.Empty;
            string epicUrl = string.Empty;
            string processName = string.Empty;

            string installLocation = string.Empty;
            string launchExecutable = string.Empty;

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

                    installLocation = chosen.InstallLocation ?? string.Empty;
                    launchExecutable = chosen.LaunchExecutable ?? string.Empty;

                    Console.WriteLine();
                    Console.WriteLine("Auto-filled values (you can edit them):");
                    Console.WriteLine($"  Profile name: {name}");
                    Console.WriteLine($"  Epic URL:     {epicUrl}");
                    Console.WriteLine($"  Process:      {processName}");

                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        Console.WriteLine($"  Install:      {installLocation}");
                    }

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

                // Optional metadata (manual entry)
                Console.Write("Install location (optional, press Enter to skip): ");
                installLocation = (Console.ReadLine() ?? string.Empty).Trim();

                Console.Write("Launch executable (optional, press Enter to skip): ");
                launchExecutable = (Console.ReadLine() ?? string.Empty).Trim();
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
                LaunchDelayMs = launchDelayMs,
                InstallLocation = installLocation,
                LaunchExecutable = launchExecutable
            };

            if (!TryValidateAndNormalizeProfile(profile, out string? validationError))
            {
                throw new InvalidOperationException($"Profile is invalid: {validationError ?? "Unknown validation error"}");
            }

            string safeFileName = MakeSafeFileName(profile.Name);

            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                safeFileName = "NewProfile";
            }

            string profilePath = Path.Combine(profilesDir, $"{safeFileName}.esl");

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

            if (!IsDirectoryWritable(profilesDir, out string? whyNotWritable))
            {
                Console.WriteLine();
                Console.WriteLine("ERROR: Profiles folder is not writable. Cannot create profile.");
                Console.WriteLine($"Location: {profilesDir}");
                Console.WriteLine($"Reason: {whyNotWritable}");
                return;
            }

            if (!TryWriteJsonAtomic(profilePath, profile, out string? err))
            {
                Console.WriteLine();
                Console.WriteLine($"ERROR: Failed to save profile. {err}");
                return;
            }

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

        /// <summary>
        ///     Reads a yes/no response from console input.
        /// </summary>
        /// <param name="defaultIsNo">Whether an empty response maps to no.</param>
        /// <returns><see langword="true" /> for yes responses; otherwise <see langword="false" />.</returns>
        private static bool ReadYesNo(bool defaultIsNo)
        {
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return !defaultIsNo;
            }

            return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------------
        // Profile launching
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Loads and launches a profile by profile name.
        /// </summary>
        /// <param name="profileName">Profile file name without extension.</param>
        /// <returns>Launch command exit code.</returns>
        private static int LaunchFromProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                Console.WriteLine("ERROR: Profile name is required.");
                return ExitBadArgs;
            }

            string profilePath = Path.Combine(GetProfilesDirectory(), $"{profileName}.esl");

            if (!TryLoadAndValidateProfile(profilePath, out var profile, out string? error))
            {
                Console.WriteLine($"ERROR: Profile '{profileName}' is invalid or could not be loaded.");
                Console.WriteLine($"Path:   {profilePath}");
                Console.WriteLine($"Reason: {error}");
                return File.Exists(profilePath) ? ExitProfileInvalid : ExitProfileNotFound;
            }


            var p = profile!;
            Console.WriteLine($"Launching profile: {profileName}");
            Console.WriteLine($"Epic URL: {p.EpicLaunchUrl}");
            Console.WriteLine($"Process:  {p.GameProcessName}");

            if (!string.IsNullOrWhiteSpace(p.InstallLocation))
            {
                Console.WriteLine($"Install:  {p.InstallLocation}");
            }

            Console.WriteLine();

            return Launch(
                p.EpicLaunchUrl,
                p.GameProcessName,
                p.StartTimeoutSeconds,
                p.PollIntervalMs,
                p.LaunchDelayMs,
                profilePath,
                profile
            );
        }

        /// <summary>
        ///     Launches an Epic URL, detects the game process, and blocks until process tree exit.
        /// </summary>
        /// <param name="epicUrl">Epic launch URI.</param>
        /// <param name="exeName">Expected game process name.</param>
        /// <param name="timeoutSeconds">Detection timeout in seconds.</param>
        /// <param name="pollIntervalMs">Polling interval in milliseconds.</param>
        /// <param name="launchDelayMs">Delay before process scanning in milliseconds.</param>
        /// <param name="profilePathForDiagnostics">Profile path used for diagnostics updates.</param>
        /// <param name="profileForDiagnostics">Profile model used for diagnostics updates.</param>
        /// <returns>Launch workflow exit code.</returns>
        private static int Launch(
            string epicUrl,
            string exeName,
            int timeoutSeconds,
            int pollIntervalMs,
            int launchDelayMs,
            string? profilePathForDiagnostics,
            GameProfile? profileForDiagnostics)
        {
            if (string.IsNullOrWhiteSpace(epicUrl) || string.IsNullOrWhiteSpace(exeName))
            {
                PrintUsage();
                return ExitBadArgs;
            }

            var baselinePidsByName = CaptureExistingProcessIds(exeName);

            var baselineAll = CaptureAllProcessStartTimes();

            var launchStartLocal = DateTime.Now;

            try
            {
                // Validate Epic URL format.
                if (!IsAllowedEpicUri(epicUrl))
                {
                    Console.WriteLine($"ERROR: Invalid Epic launch URL: {epicUrl}");
                    return ExitBadArgs;
                }

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

            if (launchDelayMs > 0)
            {
                Console.WriteLine($"Waiting {launchDelayMs}ms before scanning for process...");
                Thread.Sleep(launchDelayMs);
            }

            var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? Defaults.StartTimeoutSeconds : timeoutSeconds);
            var poll = TimeSpan.FromMilliseconds(pollIntervalMs <= 0 ? Defaults.PollIntervalMs : pollIntervalMs);
            var deadline = DateTime.UtcNow + timeout;

            var minStartTimeLocal = launchStartLocal.AddSeconds(-Defaults.StartTimeToleranceSeconds);

            Process? fallbackAnyMatch = null;

            while (DateTime.UtcNow < deadline)
            {
                var matches = SafeGetProcessesByName(exeName);

                if (matches.Length > 0)
                {
                    fallbackAnyMatch = SelectNewestProcessIfPossible(matches) ?? matches[0];

                    var bestNew = SelectBestNewProcess(matches, baselinePidsByName, minStartTimeLocal);

                    if (bestNew != null)
                    {
                        Console.WriteLine($"Game detected (new instance) (PID: {bestNew.Id}). Waiting for exit...");
                        WaitForProcessTreeExit(bestNew.Id);
                        return ExitSuccess;
                    }
                }

                Thread.Sleep(poll);
            }

            if (Defaults.FallbackToAnyMatchingProcess && fallbackAnyMatch != null)
            {
                Console.WriteLine("WARNING: No new game process was detected before timeout.");
                Console.WriteLine($"Falling back to existing matching process (PID: {fallbackAnyMatch.Id}). Waiting for exit...");
                WaitForProcessTreeExit(fallbackAnyMatch.Id);
                return ExitSuccess;
            }

            if (IsInteractiveConsole() && !string.IsNullOrWhiteSpace(profilePathForDiagnostics) && profileForDiagnostics != null)
            {
                Console.WriteLine();
                Console.WriteLine("Could not detect the game process by name before timeout.");
                Console.WriteLine("Diagnostics: scanning for newly started processes after launch...");
                Console.WriteLine();

                string? chosenProcessName = RunDiagnosticsPickProcessName(
                    baselineAll,
                    launchStartLocal,
                    minStartTimeLocal,
                    profileForDiagnostics.InstallLocation ?? string.Empty
                );

                if (!string.IsNullOrWhiteSpace(chosenProcessName))
                {
                    Console.WriteLine();
                    Console.WriteLine($"Selected process name: {chosenProcessName}");
                    Console.Write("Save this process name to the profile? (y/N): ");
                    bool save = ReadYesNo(true);

                    if (save)
                    {
                        TryUpdateProfileProcessName(profilePathForDiagnostics, profileForDiagnostics, chosenProcessName);
                    }

                    Console.WriteLine();
                    Console.WriteLine("Re-trying launch using the selected process name...");
                    Console.WriteLine();

                    // Retry once using the selected name (no diagnostics recursion).
                    return Launch(
                        epicUrl,
                        chosenProcessName,
                        timeoutSeconds,
                        pollIntervalMs,
                        0,
                        null,
                        null
                    );
                }
            }

            Console.WriteLine($"ERROR: Could not find process '{exeName}' before timeout ({timeout.TotalSeconds:0}s).");
            return ExitProcessNotFound;
        }

        /// <summary>
        ///     Parses command-line arguments into normalized launcher command metadata.
        /// </summary>
        /// <param name="args">Raw command-line arguments.</param>
        /// <returns>Normalized parsed arguments.</returns>
        private static ParsedArgs ParseArgs(string[] args)
        {
            bool pause = false;

            // Strip global flags first (in any position)
            var remaining = new List<string>(args.Length);

            foreach (string raw in args)
            {
                string a = raw.Trim();

                if (a.Equals("--pause", StringComparison.OrdinalIgnoreCase))
                {
                    pause = true;
                    continue;
                }

                // Optional: if you want to force no pause even in menu/double-click
                if (a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase))
                {
                    pause = false;
                    continue;
                }

                remaining.Add(a);
            }

            // Legacy: EpicSteamLauncher.exe "<EpicUrl>" <GameExeName>
            // Only if it still looks like legacy after removing globals.
            if (remaining.Count == 2 && !IsFlag(remaining[0]))
            {
                return new ParsedArgs(
                    pause, // pause only if explicitly requested
                    "legacy",
                    null,
                    remaining
                );
            }

            // Find first command flag anywhere
            string? command = null;
            int commandIndex = -1;

            for (int i = 0; i < remaining.Count; i++)
            {
                string a = remaining[i];

                if (a.Equals("--wizard", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--profile", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--validate-profiles", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--sync-nonsteam", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--import-installed", StringComparison.OrdinalIgnoreCase))
                {
                    command = a;
                    commandIndex = i;
                    break;
                }
            }

            if (command is null)
            {
                // No recognized command; treat as "unknown" and let Main print usage
                return new ParsedArgs(pause, null, null, remaining);
            }

            // Remove the command token
            remaining.RemoveAt(commandIndex);

            // Support --profile=Name or --profile Name
            string? commandValue = null;

            if (command.StartsWith("--profile", StringComparison.OrdinalIgnoreCase))
            {
                // If original was --profile=Something, capture it
                int eq = command.IndexOf('=');

                if (eq >= 0 && eq + 1 < command.Length)
                {
                    commandValue = command[(eq + 1)..].Trim().Trim('"');
                    command = "--profile";
                }
                else
                {
                    // Next non-flag token becomes value (more forgiving than "args[1]")
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        if (!IsFlag(remaining[i]) && !string.IsNullOrWhiteSpace(remaining[i]))
                        {
                            commandValue = remaining[i].Trim().Trim('"');
                            remaining.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            return new ParsedArgs(
                pause,
                command,
                commandValue,
                remaining
            );
        }

        /// <summary>
        ///     Ensures a directory exists.
        /// </summary>
        /// <param name="path">Directory path.</param>
        /// <param name="error">Error details when creation fails.</param>
        /// <returns><see langword="true" /> when the directory exists or was created; otherwise <see langword="false" />.</returns>
        private static bool TryEnsureDirectory(string path, out string? error)
        {
            try
            {
                Directory.CreateDirectory(path);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        ///     Validates that a directory is writable by creating and deleting a probe file.
        /// </summary>
        /// <param name="path">Directory path.</param>
        /// <param name="error">Error details when write validation fails.</param>
        /// <returns><see langword="true" /> when the directory is writable; otherwise <see langword="false" />.</returns>
        private static bool IsDirectoryWritable(string path, out string? error)
        {
            try
            {
                Directory.CreateDirectory(path);

                string probeFile = Path.Combine(path, $".write_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, "probe", new UTF8Encoding(false));
                File.Delete(probeFile);

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        ///     Writes text atomically: write temp -> replace/move.
        ///     Prevents corruption if the process is killed mid-write.
        /// </summary>
        private static void WriteAllTextAtomic(string path, string contents)
        {
            var encoding = new UTF8Encoding(false);

            string? dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Unique temp file name to avoid collisions when multiple instances run.
            string tempPath =
                string.IsNullOrWhiteSpace(dir)
                    ? path + "." + Guid.NewGuid().ToString("N") + ".tmp"
                    : Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                // Write temp first
                File.WriteAllText(tempPath, contents, encoding);

                // Replace it if exists, else move into place
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null, true);
                    }
                    catch
                    {
                        // If Replace fails (e.g., permissions edge case), fall back to delete+move.
                        // (Still atomic-ish for the common case, but not perfect across all filesystems.)
                        File.Delete(path);
                        File.Move(tempPath, path);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                // Best-effort cleanup in case something throws mid-way
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>
        ///     Serializes a value to JSON and writes it atomically to disk.
        /// </summary>
        /// <typeparam name="T">Value type being serialized.</typeparam>
        /// <param name="path">Destination file path.</param>
        /// <param name="value">Value to serialize.</param>
        /// <param name="error">Error details when writing fails.</param>
        /// <returns><see langword="true" /> when serialization and writing succeed; otherwise <see langword="false" />.</returns>
        private static bool TryWriteJsonAtomic<T>(string path, T value, out string? error)
        {
            try
            {
                string json = JsonConvert.SerializeObject(value, Formatting.Indented);
                WriteAllTextAtomic(path, json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        ///     Validates whether a URI matches supported Epic launcher launch forms.
        /// </summary>
        /// <param name="uri">URI string to validate.</param>
        /// <returns><see langword="true" /> when the URI is an allowed Epic launch endpoint; otherwise <see langword="false" />.</returns>
        private static bool IsAllowedEpicUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
            {
                return false;
            }

            if (!u.Scheme.Equals("com.epicgames.launcher", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Supported forms:
            // 1) com.epicgames.launcher://apps/<AppNameOrId>?action=launch...
            //    Host = "apps", AbsolutePath = "/<AppNameOrId>"
            // 2) com.epicgames.launcher:/apps/<AppNameOrId>?action=launch...
            //    Host = "", AbsolutePath = "/apps/<AppNameOrId>"
            bool isAppsEndpoint =
                u.Host.Equals("apps", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.StartsWith("/apps/", StringComparison.OrdinalIgnoreCase);

            if (!isAppsEndpoint)
            {
                return false;
            }

            return u.Query.Contains("action=launch", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Determines whether interactive console input is available.
        /// </summary>
        /// <returns><see langword="true" /> when prompts can be shown; otherwise <see langword="false" />.</returns>
        private static bool IsInteractiveConsole()
        {
            try
            {
                // If redirected, we don't want to prompt.
                return !Console.IsInputRedirected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Safely retrieves processes by name and returns an empty array on failures.
        /// </summary>
        /// <param name="exeName">Process name without extension.</param>
        /// <returns>Matching process array, or an empty array when enumeration fails.</returns>
        private static Process[] SafeGetProcessesByName(string exeName)
        {
            try
            {
                return Process.GetProcessesByName(exeName);
            }
            catch
            {
                return [];
            }
        }

        /// <summary>
        ///     Captures current process IDs and best-effort start times for diagnostics baselining.
        /// </summary>
        /// <returns>Map of process ID to optional local start time.</returns>
        private static Dictionary<int, DateTime?> CaptureAllProcessStartTimes()
        {
            var map = new Dictionary<int, DateTime?>();

            try
            {
                foreach (var p in Process.GetProcesses())
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

                    var start = TryGetStartTimeLocal(p);
                    map[pid] = start;
                }
            }
            catch
            {
                // ignore
            }

            return map;
        }

        /// <summary>
        ///     Runs diagnostics candidate discovery and lets the user choose a process name.
        /// </summary>
        /// <param name="baselineAll">Baseline process map captured before launch.</param>
        /// <param name="launchStartLocal">Launch start local time.</param>
        /// <param name="minStartTimeLocal">Minimum candidate start-time threshold.</param>
        /// <param name="installLocationHint">Optional install-location hint used for candidate ranking.</param>
        /// <returns>Selected process name, or <see langword="null" /> when canceled or unavailable.</returns>
        private static string? RunDiagnosticsPickProcessName(
            Dictionary<int, DateTime?> baselineAll,
            DateTime launchStartLocal,
            DateTime minStartTimeLocal,
            string installLocationHint)
        {
            _ = launchStartLocal; // no-op, just to silence the unused warning.

            // Gather candidates: processes that appear "new" compared to baseline OR started after launch.
            var candidates = CollectNewProcessCandidates(
                baselineAll,
                minStartTimeLocal,
                installLocationHint
            );

            if (candidates.Count == 0)
            {
                Console.WriteLine("No new process candidates found.");
                Console.WriteLine("This can happen if StartTime/path access is restricted or the game spawns very quickly then exits.");
                return null;
            }

            Console.WriteLine("Possible game processes (newly started):");
            Console.WriteLine("  [0] Cancel");

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                string locTag = c.IsUnderInstallLocation ? " [install]" : "";
                string timeTag = c.StartTimeLocal.HasValue ? c.StartTimeLocal.Value.ToString("HH:mm:ss") : "??:??:??";
                Console.WriteLine($"  [{i + 1}] {c.ProcessName} (PID {c.Pid}) {timeTag}{locTag}");

                if (!string.IsNullOrWhiteSpace(c.ExePath))
                {
                    Console.WriteLine($"      {c.ExePath}");
                }
            }

            Console.WriteLine();
            Console.Write("Pick the correct process: ");
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (!int.TryParse(input, out int selected))
            {
                return null;
            }

            if (selected == 0)
            {
                return null;
            }

            if (selected < 1 || selected > candidates.Count)
            {
                return null;
            }

            return candidates[selected - 1].ProcessName;
        }

        /// <summary>
        ///     Collects newly started process candidates for diagnostics fallback selection.
        /// </summary>
        /// <param name="baselineAll">Baseline process map captured before launch.</param>
        /// <param name="minStartTimeLocal">Minimum candidate start-time threshold.</param>
        /// <param name="installLocationHint">Optional install-location hint used for scoring.</param>
        /// <returns>Sorted list of process candidates.</returns>
        private static List<ProcessCandidate> CollectNewProcessCandidates(
            Dictionary<int, DateTime?> baselineAll,
            DateTime minStartTimeLocal,
            string installLocationHint)
        {
            string installRoot = NormalizeDirectory(installLocationHint);

            var results = new List<ProcessCandidate>();

            Process[] allNow;

            try
            {
                allNow = Process.GetProcesses();
            }
            catch
            {
                return results;
            }

            foreach (var p in allNow)
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

                // Skip processes that definitely existed before.
                if (baselineAll != null && baselineAll.TryGetValue(pid, out var baselineStart))
                {
                    // If baseline start time unknown, we still allow "new" only if the current start time says so.
                    // Otherwise, skip.
                    var currentStart = TryGetStartTimeLocal(p);

                    if (baselineStart.HasValue)
                    {
                        continue;
                    }

                    if (currentStart.HasValue && currentStart.Value < minStartTimeLocal)
                    {
                        continue;
                    }
                }

                var start = TryGetStartTimeLocal(p);

                if (start.HasValue && start.Value < minStartTimeLocal)
                {
                    continue;
                }

                string procName;

                try
                {
                    procName = p.ProcessName;
                }
                catch
                {
                    continue;
                }

                string exePath = TryGetProcessExePath(p);
                bool underInstall = IsPathUnderRoot(exePath, installRoot);

                results.Add(
                    new ProcessCandidate
                    {
                        Pid = pid,
                        ProcessName = procName,
                        StartTimeLocal = start,
                        ExePath = exePath,
                        IsUnderInstallLocation = underInstall
                    }
                );
            }

            // Sort:
            //  1) Under install location first (if known),
            //  2) Newest start time,
            //  3) Higher PID.
            results = results
                .OrderByDescending(r => r.IsUnderInstallLocation)
                .ThenByDescending(r => r.StartTimeLocal ?? DateTime.MinValue)
                .ThenByDescending(r => r.Pid)
                .Take(Defaults.DiagnosticsMaxCandidates)
                .ToList();

            return results;
        }

        /// <summary>
        ///     Attempts to read a process executable path.
        /// </summary>
        /// <param name="process">Process to inspect.</param>
        /// <returns>Executable path when available; otherwise an empty string.</returns>
        private static string TryGetProcessExePath(Process process)
        {
            try
            {
                // This may throw if access is denied (common without admin).
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        ///     Normalizes a directory path for case-insensitive prefix comparisons.
        /// </summary>
        /// <param name="dir">Directory path to normalize.</param>
        /// <returns>Normalized path with trailing separator, or an empty string on failure.</returns>
        private static string NormalizeDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return string.Empty;
            }

            try
            {
                string full = Path.GetFullPath(dir.Trim());
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        ///     Checks whether a candidate path is under a normalized root directory.
        /// </summary>
        /// <param name="candidatePath">Candidate file path.</param>
        /// <param name="rootDirNormalized">Normalized root directory with trailing separator.</param>
        /// <returns><see langword="true" /> when the candidate path is under the root; otherwise <see langword="false" />.</returns>
        private static bool IsPathUnderRoot(string candidatePath, string rootDirNormalized)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirNormalized))
            {
                return false;
            }

            try
            {
                string full = Path.GetFullPath(candidatePath);
                return full.StartsWith(rootDirNormalized, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Updates and persists a profile's process name after diagnostics confirmation.
        /// </summary>
        /// <param name="profilePath">Profile file path.</param>
        /// <param name="profile">Profile instance to update.</param>
        /// <param name="newProcessName">Process name selected by diagnostics.</param>
        private static void TryUpdateProfileProcessName(string profilePath, GameProfile profile, string newProcessName)
        {
            try
            {
                profile.GameProcessName = NormalizeProcessName(newProcessName);

                // Re-serialize and overwrite.
                if (!TryWriteJsonAtomic(profilePath, profile, out string? err))
                {
                    Console.WriteLine($"WARNING: Failed to update profile. {err}");
                    return;
                }

                Console.WriteLine("Profile updated successfully.");
                Console.WriteLine($"Path: {profilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Failed to update profile. {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        ///     Captures process IDs currently matching a process name baseline.
        /// </summary>
        /// <param name="exeName">Process name without extension.</param>
        /// <returns>Set of matching process IDs at capture time.</returns>
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
        ///     Selects the best candidate process considered newly started after launch.
        /// </summary>
        /// <param name="matches">Current matching processes.</param>
        /// <param name="baselinePids">Process IDs known before launch.</param>
        /// <param name="minStartTimeLocal">Minimum candidate start-time threshold.</param>
        /// <returns>Best new process candidate, or <see langword="null" /> when none qualify.</returns>
        private static Process? SelectBestNewProcess(Process[]? matches, HashSet<int> baselinePids, DateTime minStartTimeLocal)
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

            var withTime = candidates.Where(c => c.StartTimeLocal.HasValue).ToList();

            if (withTime.Count > 0)
            {
                return withTime.OrderByDescending(c => c.StartTimeLocal ?? default).First().Proc;
            }

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
        ///     Selects the newest process from a set when start times are accessible.
        /// </summary>
        /// <param name="matches">Process candidates.</param>
        /// <returns>Newest process candidate, or <see langword="null" /> when none can be evaluated.</returns>
        private static Process? SelectNewestProcessIfPossible(Process[] matches)
        {
            var bestTime = DateTime.MinValue;
            Process? best = null;

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
        ///     Reads process start time using best-effort error handling.
        /// </summary>
        /// <param name="process">Process to inspect.</param>
        /// <returns>Local start time when available; otherwise <see langword="null" />.</returns>
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
        ///     Waits until a root process and its discovered child processes have exited.
        /// </summary>
        /// <param name="rootPid">Root process identifier.</param>
        private static void WaitForProcessTreeExit(int rootPid)
        {
            // Track PID + (optional) start time to avoid PID-reuse hangs.
            // If a PID is reused for a different process, we treat it as "exited" for our purposes.
            var tracked = new Dictionary<int, DateTime?>
            {
                [rootPid] = TryGetStartTimeLocalSafe(rootPid)
            };

            while (true)
            {
                // 1) Expand the child tree (best-effort; WMI can fail)
                // Take a snapshot of keys to avoid modifying while iterating.
                var currentPids = tracked.Keys.ToList();

                foreach (int childPid in currentPids.SelectMany(pid => GetChildProcessIds(pid).Where(childPid => !tracked.ContainsKey(childPid))))
                {
                    tracked[childPid] = TryGetStartTimeLocalSafe(childPid);
                }

                // 2) Remove exited / replaced (PID reused) PIDs
                var toRemove = new List<int>(tracked.Count);
                toRemove.AddRange(from kvp in tracked let pid = kvp.Key let expectedStart = kvp.Value where !IsSameProcessInstanceStillRunning(pid, expectedStart) select pid);

                foreach (int pid in toRemove)
                {
                    tracked.Remove(pid);
                }

                if (tracked.Count == 0)
                {
                    return;
                }

                Thread.Sleep(500);
            }

            // Local helper that safely captures a process start time by PID.
            static DateTime? TryGetStartTimeLocalSafe(int pid)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);

                    if (p.HasExited)
                    {
                        return null;
                    }

                    return TryGetStartTimeLocal(p); // your existing helper (returns DateTime?)
                }
                catch
                {
                    return null;
                }
            }

            // Local helper that treats PID reuse as process replacement rather than continuity.
            static bool IsSameProcessInstanceStillRunning(int pid, DateTime? expectedStartLocal)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);

                    // If it already exited, it's gone.
                    if (p.HasExited)
                    {
                        return false;
                    }

                    // If we don't know the start time, we can only rely on HasExited.
                    if (expectedStartLocal is null)
                    {
                        return true;
                    }

                    // Compare start time to protect against PID reuse.
                    var actualStartLocal = TryGetStartTimeLocal(p);

                    if (actualStartLocal is null)
                    {
                        // If we can't read the start time, err on the side of "still running"
                        // to avoid prematurely ending a Steam session.
                        return true;
                    }

                    // StartTime precision can vary; treat close-enough as match.
                    // (Exact comparison can be too strict on some systems.)
                    var delta = (actualStartLocal.Value - expectedStartLocal.Value).Duration();
                    return delta <= TimeSpan.FromSeconds(2);
                }
                catch
                {
                    // Can't find it => treat as exited
                    return false;
                }
            }
        }

        /// <summary>
        ///     Gets the profiles directory path under the launcher base directory.
        /// </summary>
        /// <returns>Profiles directory path.</returns>
        private static string GetProfilesDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, ProfilesFolderName);
        }

        /// <summary>
        ///     Prints command usage and profile workflow help text.
        /// </summary>
        private static void PrintUsage()
        {
            Console.WriteLine("EpicSteamLauncher Usage:");
            Console.WriteLine();
            Console.WriteLine("Profiles:");
            Console.WriteLine("  EpicSteamLauncher.exe                      (bootstrap + menu)");
            Console.WriteLine("  EpicSteamLauncher.exe --wizard             (create profile interactively)");
            Console.WriteLine("  EpicSteamLauncher.exe --import-installed   (auto-create profiles from installed Epic games)");
            Console.WriteLine("  EpicSteamLauncher.exe --sync-nonsteam      (sync profiles into Steam shortcuts.vdf)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile \"Name\"   (launch profiles/Name.esl)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile            (select from valid profiles)");
            Console.WriteLine("  EpicSteamLauncher.exe --validate-profiles  (print validation report)");
            Console.WriteLine();
            Console.WriteLine("Legacy:");
            Console.WriteLine("  EpicSteamLauncher.exe \"<EpicLaunchUrl>\" <GameExeName>");
        }

        /// <summary>
        ///     Retrieves direct child process IDs for a parent process using WMI.
        /// </summary>
        /// <param name="parentPid">Parent process identifier.</param>
        /// <returns>Set of child process IDs.</returns>
        private static HashSet<int> GetChildProcessIds(int parentPid)
        {
            var children = new HashSet<int>();

            try
            {
                using var searcher =
                    new ManagementObjectSearcher(
                        $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid}"
                    );

                foreach (var obj in searcher.Get())
                {
                    int pid = Convert.ToInt32(obj["ProcessId"]);
                    children.Add(pid);
                }
            }
            catch
            {
                // ignore – WMI can fail without admin
            }

            return children;
        }

        /// <summary>
        ///     Normalizes a process token by trimming and removing a trailing <c>.exe</c> suffix.
        /// </summary>
        /// <param name="raw">Raw process token.</param>
        /// <returns>Normalized process name.</returns>
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

        /// <summary>
        ///     Reads an integer from console input and falls back to a default value.
        /// </summary>
        /// <param name="defaultValue">Default value when input is empty or invalid.</param>
        /// <returns>Parsed integer or the default value.</returns>
        private static int ReadIntOrDefault(int defaultValue)
        {
            string input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            return int.TryParse(input, out int value) ? value : defaultValue;
        }

        /// <summary>
        ///     Attempts to open a folder in the system shell.
        /// </summary>
        /// <param name="folderPath">Folder path to open.</param>
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

        /// <summary>
        ///     Finds an existing downloaded icon path for a shortcut app ID.
        /// </summary>
        /// <param name="gridFolderPath">Steam grid folder path.</param>
        /// <param name="appId">Shortcut app ID.</param>
        /// <returns>Absolute icon path when found; otherwise <see langword="null" />.</returns>
        private static string? FindExistingIconPath(string gridFolderPath, uint appId)
        {
            // We download icons from SGDB as "{appid}_icon.png" (preferred),
            // but we support a few other formats just in case.
            string baseName = $"{appId}_icon";

            // Prefer PNG first (recommended), then ICO, then others.
            string[] exts = [".png", ".ico", ".webp", ".jpg", ".jpeg"];

            return (from ext in exts select Path.Combine(gridFolderPath, baseName + ext) into candidate where File.Exists(candidate) select Path.GetFullPath(candidate))
                .FirstOrDefault();
        }

        /// <summary>
        ///     Builds the default README content for the profiles directory.
        /// </summary>
        /// <param name="profilesDir">Profiles directory path shown in the README.</param>
        /// <returns>README text content.</returns>
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
  - If the process name is wrong, the tool can help you fix it after a timeout
    when launching from a profile (interactive mode).
  - You can validate all profiles from the app menu or via:
       EpicSteamLauncher.exe --validate-profiles
";
        }

        /// <summary>
        ///     Pauses on exit when configured and interactive input is available.
        /// </summary>
        private static void PauseIfInteractive()
        {
            try
            {
                if (!_pauseOnExit)
                {
                    return;
                }

                if (!Console.IsInputRedirected && Environment.UserInteractive)
                {
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey(true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
