/*
    EpicSteamLauncher

    HOW THIS TOOL IS USED (Steam / Steam Link workflow)
    ---------------------------------------------------
    This executable is intended to be added to Steam as a "Non-Steam Game".

    Steam launches this EXE, and EpicSteamLauncher will:
      1) Open an Epic Games launch URL (via shell / protocol activation).
      2) Detect the game process by name.
      3) Wait until the game exits so Steam treats the session as "running".

    Recommended usage (Profiles):
      - Run with no arguments once to bootstrap:
            EpicSteamLauncher.exe
        This creates a "profiles" folder next to the EXE and writes:
          - profiles/README.txt
          - profiles/example.profile.json
        It then shows a menu (it does NOT automatically open Explorer).

      - Create a per-game profile (wizard):
            EpicSteamLauncher.exe --wizard

      - Launch a game via profile (Steam Launch Options):
            --profile "Fortnite"
        This loads: profiles/Fortnite.json

      - (Optional) Validate profiles:
            EpicSteamLauncher.exe --validate-profiles

    Backward compatible usage (Legacy):
      - Original behavior: (EpicLaunchUrl + process name)
            EpicSteamLauncher.exe "<EpicLaunchUrl>" <GameExeName>

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
        }

        /// <summary>
        ///     Simple model for dynamically numbered menu options.
        /// </summary>
        private sealed class MenuOption
        {
            public MenuOption(string label, Func<MenuOutcome> action)
            {
                Label = label ?? throw new ArgumentNullException(nameof(label));
                Action = action ?? throw new ArgumentNullException(nameof(action));
            }

            public string Label { get; }
            public Func<MenuOutcome> Action { get; }
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

        /// <summary>
        ///     Entry point for EpicSteamLauncher.
        /// </summary>
        private static int Main(string[] args)
        {
            // No args: bootstrap profiles folder + show menu.
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

            // Flag-based modes.
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

                // Steam usage: --profile "Name"
                if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
                {
                    string profileName = args[1].Trim().Trim('"');
                    int rc = LaunchFromProfileName(profileName);
                    PauseIfInteractive();
                    return rc;
                }

                // Manual usage: --profile  (selector screen)
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

            // Unknown args.
            PrintUsage();
            PauseIfInteractive();
            return ExitBadArgs;
        }

        /// <summary>
        ///     Basic flag detection (supports '-' and '/').
        /// </summary>
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

        /// <summary>
        ///     Creates the profiles folder and seeds it with README + example profile (if missing).
        ///     Does NOT open Explorer automatically (menu provides an option for that).
        /// </summary>
        private static void BootstrapProfilesFolder()
        {
            string profilesDir = GetProfilesDirectory();
            Directory.CreateDirectory(profilesDir);

            // Write README if missing.
            string readmePath = Path.Combine(profilesDir, "README.txt");

            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath, BuildProfilesReadmeText(profilesDir));
            }

            // Write example profile if missing.
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

            // Always print path (useful when launched outside a visible console).
            Console.WriteLine("EpicSteamLauncher: Profiles folder is ready.");
            Console.WriteLine($"Location: {profilesDir}");
            Console.WriteLine("Files: README.txt, example.profile.json (created if missing)");
            Console.WriteLine();
        }

        /// <summary>
        ///     Main menu shown when launched with no args.
        ///     Option numbers are assigned dynamically so adding/removing options never requires renumbering.
        /// </summary>
        private static int ShowMainMenu()
        {
            // Build menu options in display order. Numbers are computed when rendered.
            var options = new List<MenuOption>
            {
                new MenuOption(
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

                new MenuOption(
                    "Profiles (select to launch)",
                    () =>
                    {
                        int rc = ProfilesSelectorScreen();
                        return MenuOutcome.Continue(rc);
                    }
                ),

                new MenuOption(
                    "Open profiles folder",
                    () =>
                    {
                        TryOpenFolderInExplorer(GetProfilesDirectory());
                        return MenuOutcome.Continue();
                    }
                ),

                new MenuOption(
                    "Validate profiles (report)",
                    () =>
                    {
                        int rc = ValidateProfilesReport();
                        return MenuOutcome.Continue(rc);
                    }
                ),

                new MenuOption("Exit", () => MenuOutcome.Exit())
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

                // Invalid selection returns to the menu automatically.
                if (!int.TryParse(input, out int selected) || selected < 1 || selected > options.Count)
                {
                    Console.WriteLine("Invalid option. Returning to main menu.");
                    Console.WriteLine();
                    continue;
                }

                var outcome = options[selected - 1].Action();

                // If an action returned a "result code", show a short status line.
                // ExitSuccess is commonly returned by "Back" / "Cancel" actions.
                if (outcome.LastResultCode.HasValue)
                {
                    int rc = outcome.LastResultCode.Value;

                    // Keep this minimal: only print non-success codes, unless you want verbose feedback.
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
        // Profiles: enumeration + validation (single pipeline used everywhere)
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Enumerates candidate profile JSON files in the profiles folder.
        ///     Filters out known non-profiles (example.profile.json).
        /// </summary>
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

                // Ignore the seeded example profile.
                if (filename.Equals("example.profile.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }

        /// <summary>
        ///     Attempts to load + validate + normalize a profile from a JSON file.
        ///     This is the single validation pipeline used for:
        ///     A) listing profiles (selector screen)
        ///     B) running a profile by name (--profile "Name")
        ///     C) wizard creation (validate before writing)
        /// </summary>
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

        /// <summary>
        ///     Validates required fields and normalizes values (applies defaults, strips .exe, etc.).
        /// </summary>
        private static bool TryValidateAndNormalizeProfile(GameProfile profile, out string error)
        {
            error = null;

            if (profile == null)
            {
                error = "Profile is null after deserialization.";
                return false;
            }

            // Required fields.
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

            // Sanity check (intentionally not strict; future URI formats may exist).
            if (!profile.EpicLaunchUrl.Contains("://"))
            {
                error = "EpicLaunchUrl does not look like a valid URI (missing '://').";
                return false;
            }

            // Apply defaults if missing/invalid.
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
        ///     Returns a list of valid profiles (validated and ready to run).
        ///     Invalid JSON and invalid profiles are automatically excluded.
        /// </summary>
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

            return results
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        ///     Selector screen that lists valid profiles and optionally launches one.
        ///     Behavior rules:
        ///     - [0] is always "Back"
        ///     - Any invalid input returns to the main menu automatically
        ///     - Only valid profiles are displayed
        /// </summary>
        private static int ProfilesSelectorScreen()
        {
            var profiles = GetValidProfiles();

            Console.WriteLine("Profiles");
            Console.WriteLine("--------");

            if (profiles.Count == 0)
            {
                Console.WriteLine("No valid profiles found.");
                Console.WriteLine("Tip: Use 'Create a profile (wizard)' to make one.");
                return ExitSuccess; // Return to main menu.
            }

            Console.WriteLine("  [0] Back");

            for (int i = 0; i < profiles.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}] {profiles[i].Name}");
            }

            Console.WriteLine();
            Console.Write("Select a profile number: ");

            string input = (Console.ReadLine() ?? string.Empty).Trim();

            // Any invalid input returns to main menu (as requested).
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

            // Re-use name-based launch path (ensures validation stays consistent).
            return LaunchFromProfileName(chosenName);
        }

        /// <summary>
        ///     Prints a validation report for all candidate JSON files (valid + invalid with reasons).
        /// </summary>
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

            // Non-zero if there are invalid profiles (useful for manual runs).
            return invalidCount == 0 ? ExitSuccess : ExitProfileInvalid;
        }

        // ---------------------------------------------------------------------
        // Profile launching
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Launches a specific profile by name (Steam usage: --profile "Name").
        ///     Always validates before launching.
        /// </summary>
        private static int LaunchFromProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                Console.WriteLine("ERROR: Profile name is required.");
                return ExitBadArgs;
            }

            // profiles/<Name>.json
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
        // Core launch logic
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Core launch routine:
        ///     1) Opens the Epic URL using the system shell.
        ///     2) Waits (optional) before scanning for the process.
        ///     3) Polls until the process appears (with timeout).
        ///     4) Waits for the game process to exit.
        /// </summary>
        private static int Launch(string epicUrl, string exeName, int timeoutSeconds, int pollIntervalMs, int launchDelayMs)
        {
            if (string.IsNullOrWhiteSpace(epicUrl) || string.IsNullOrWhiteSpace(exeName))
            {
                PrintUsage();
                return ExitBadArgs;
            }

            // Open the Epic URL via protocol handler.
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

            // Optional delay before scanning for process.
            if (launchDelayMs > 0)
            {
                Console.WriteLine($"Waiting {launchDelayMs}ms before scanning for process...");
                Thread.Sleep(launchDelayMs);
            }

            // Poll for the process until it appears or timeout is reached.
            var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? Defaults.StartTimeoutSeconds : timeoutSeconds);
            var poll = TimeSpan.FromMilliseconds(pollIntervalMs <= 0 ? Defaults.PollIntervalMs : pollIntervalMs);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                var matches = Array.Empty<Process>();

                try
                {
                    matches = Process.GetProcessesByName(exeName);
                }
                catch
                {
                    // Ignore and keep retrying; process enumeration can transiently fail.
                }

                if (matches.Length > 0)
                {
                    // NOTE: This "first match wins" approach is intentionally conservative for this phase.
                    // Phase 2 can improve this to pick the newest process started after launch.
                    var game = matches[0];

                    Console.WriteLine($"Game detected (PID: {game.Id}). Waiting for exit...");

                    try
                    {
                        game.WaitForExit();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"WARNING: Unable to wait for process. {ex.GetType().Name}: {ex.Message}");
                    }

                    return ExitSuccess;
                }

                Thread.Sleep(poll);
            }

            Console.WriteLine($"ERROR: Could not find process '{exeName}' before timeout ({timeout.TotalSeconds:0}s).");
            return ExitProcessNotFound;
        }

        // ---------------------------------------------------------------------
        // Wizard
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Wizard that creates a single per-game profile JSON file.
        ///     Validation is performed BEFORE writing the file to disk.
        /// </summary>
        private static void RunWizard()
        {
            string profilesDir = GetProfilesDirectory();

            Console.WriteLine("EpicSteamLauncher - Profile Wizard");
            Console.WriteLine("----------------------------------");
            Console.WriteLine("This will create a JSON profile in:");
            Console.WriteLine(profilesDir);
            Console.WriteLine();

            Console.Write("Profile name (file name): ");
            string name = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Profile name cannot be empty.");
            }

            Console.Write("Epic launch URL: ");
            string epicUrl = (Console.ReadLine() ?? string.Empty).Trim();

            Console.Write("Game process name (with or without .exe): ");
            string processName = (Console.ReadLine() ?? string.Empty).Trim();

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

            // Validate/normalize before saving.
            if (!TryValidateAndNormalizeProfile(profile, out string validationError))
            {
                throw new InvalidOperationException($"Profile is invalid: {validationError}");
            }

            string profilePath = Path.Combine(profilesDir, $"{name}.json");

            // Avoid accidental overwrite without warning.
            if (File.Exists(profilePath))
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: A profile named '{name}' already exists.");
                Console.Write("Overwrite it? (y/N): ");
                string overwrite = (Console.ReadLine() ?? string.Empty).Trim();

                if (!overwrite.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !overwrite.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Wizard cancelled (no changes made).");
                    return;
                }
            }

            string json = JsonConvert.SerializeObject(profile, Formatting.Indented);
            File.WriteAllText(profilePath, json);

            Console.WriteLine();
            Console.WriteLine("Profile created:");
            Console.WriteLine(profilePath);
            Console.WriteLine();
            Console.WriteLine("Steam Launch Options to use:");
            Console.WriteLine($"  --profile \"{name}\"");
            Console.WriteLine();

            Console.Write("Open profiles folder now? (y/N): ");
            string open = (Console.ReadLine() ?? string.Empty).Trim();

            if (open.Equals("y", StringComparison.OrdinalIgnoreCase) || open.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                TryOpenFolderInExplorer(profilesDir);
            }
        }

        // ---------------------------------------------------------------------
        // Models / Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        ///     Profile schema stored as JSON.
        ///     This phase keeps it intentionally simple.
        /// </summary>
        private sealed class GameProfile
        {
            /// <summary>
            ///     Friendly name for the profile. (Wizard uses this as the filename.)
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            ///     Epic Games launch URL opened via shell.
            ///     Example: com.epicgames.launcher://apps/YourGameId?action=launch&silent=true
            /// </summary>
            public string EpicLaunchUrl { get; set; }

            /// <summary>
            ///     Process name to wait for (with or without ".exe").
            ///     Note: Process.GetProcessesByName expects the name without ".exe".
            /// </summary>
            public string GameProcessName { get; set; }

            /// <summary>
            ///     Maximum time to wait for the game process to appear.
            /// </summary>
            public int StartTimeoutSeconds { get; set; } = Defaults.StartTimeoutSeconds;

            /// <summary>
            ///     How often to poll for the process.
            /// </summary>
            public int PollIntervalMs { get; set; } = Defaults.PollIntervalMs;

            /// <summary>
            ///     Optional delay after opening the Epic URL before scanning for the process.
            /// </summary>
            public int LaunchDelayMs { get; set; } = Defaults.LaunchDelayMs;
        }

        /// <summary>
        ///     Small container representing a discovered, validated profile.
        /// </summary>
        private readonly struct DiscoveredProfile
        {
            public DiscoveredProfile(string name, string path, GameProfile profile)
            {
                Name = name;
                Path = path;
                Profile = profile;
            }

            public string Name { get; }
            public string Path { get; }
            public GameProfile Profile { get; }
        }

        /// <summary>
        ///     Returns the absolute path to the profiles folder next to the EXE.
        /// </summary>
        private static string GetProfilesDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, ProfilesFolderName);
        }

        /// <summary>
        ///     Prints usage instructions.
        /// </summary>
        private static void PrintUsage()
        {
            Console.WriteLine("EpicSteamLauncher Usage:");
            Console.WriteLine();
            Console.WriteLine("Profiles:");
            Console.WriteLine("  EpicSteamLauncher.exe                      (bootstrap + menu)");
            Console.WriteLine("  EpicSteamLauncher.exe --wizard             (create profile interactively)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile \"Name\"      (launch profiles/Name.json)");
            Console.WriteLine("  EpicSteamLauncher.exe --profile            (select from valid profiles)");
            Console.WriteLine("  EpicSteamLauncher.exe --validate-profiles  (print validation report)");
            Console.WriteLine();
            Console.WriteLine("Legacy:");
            Console.WriteLine("  EpicSteamLauncher.exe \"<EpicLaunchUrl>\" <GameExeName>");
        }

        /// <summary>
        ///     Normalizes a raw process name to a value compatible with Process.GetProcessesByName().
        /// </summary>
        private static string NormalizeProcessName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string name = raw.Trim();

            // Process.GetProcessesByName expects the name without ".exe".
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }

            return name;
        }

        /// <summary>
        ///     Reads an integer from stdin, returning a default on blank/invalid input.
        /// </summary>
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
        ///     Attempts to open a folder path in Windows Explorer.
        ///     Non-fatal if it fails (restricted environment).
        /// </summary>
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
                // Intentionally ignored. We always print the path to the console.
            }
        }

        /// <summary>
        ///     Content for profiles/README.txt created during bootstrap.
        /// </summary>
        private static string BuildProfilesReadmeText(string profilesDir)
        {
            return
                $@"EpicSteamLauncher - Profiles

This folder contains per-game JSON profiles used by EpicSteamLauncher.

Quick Start:
  1) Create a profile:
       EpicSteamLauncher.exe --wizard

  2) In Steam:
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

        /// <summary>
        ///     Light pause so console output is readable when launched manually.
        ///     Avoids forcing a "Press any key" prompt when launched from Steam.
        /// </summary>
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
                // Ignore console capability exceptions.
            }
        }
    }
}
