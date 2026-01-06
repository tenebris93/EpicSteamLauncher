/*
    EpicSteamLauncher

    This utility allows Epic Games Store titles to be launched through Steam.
    It is intended to be added to Steam as a Non-Steam Game.

    Steam launches this executable with two arguments:

        1) The Epic Games launch URL
           Example:
           com.epicgames.launcher://apps/Fortnite?action=launch&silent=true

        2) The executable name of the game process (with or without ".exe")
           Example:
           FortniteClient-Win64-Shipping

    The launcher opens the Epic URL, waits for the game process to start,
    then blocks until the game exits so Steam treats the session as an
    active game for Steam Link and Big Picture Mode.
*/

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EpicSteamLauncher
{
    internal class Program
    {
        /// <summary>
        ///     Exit codes allow Steam or scripts to detect failure states.
        /// </summary>
        private const int ExitSuccess = 0;

        private const int ExitBadArgs = 1;
        private const int ExitLaunchFailed = 2;
        private const int ExitProcessNotFound = 3;

        /// <summary>
        ///     Entry point for EpicSteamLauncher.
        ///     Expects exactly two command-line arguments:
        ///     - Epic Games launch URL
        ///     - Game executable process name
        /// </summary>
        private static int Main(string[] args)
        {
            // Validate argument count.
            if (args == null || args.Length != 2)
            {
                PrintUsage();
                return ExitBadArgs;
            }

            string epicUrl = args[0]?.Trim();
            string exeName = NormalizeProcessName(args[1]);

            if (string.IsNullOrWhiteSpace(epicUrl) || string.IsNullOrWhiteSpace(exeName))
            {
                PrintUsage();
                return ExitBadArgs;
            }

            // Attempt to launch the Epic URL using the system shell.
            try
            {
                var ps = new ProcessStartInfo(epicUrl)
                {
                    UseShellExecute = true,
                    Verb = "open"
                };

                Console.WriteLine($"Starting Epic URL: {epicUrl}");
                Process.Start(ps);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to launch Epic URL. {ex.Message}");
                return ExitLaunchFailed;
            }

            // Wait for the game process to appear.
            var timeout = TimeSpan.FromSeconds(60);
            var pollInterval = TimeSpan.FromMilliseconds(500);
            var launchTimeUtc = DateTime.UtcNow;

            var gameProcess = WaitForMostRecentProcess(
                exeName,
                launchTimeUtc,
                timeout,
                pollInterval
            );

            if (gameProcess == null)
            {
                Console.WriteLine($"ERROR: Could not find running process: {exeName}");
                return ExitProcessNotFound;
            }

            Console.WriteLine("Game detected. Waiting for it to exit...");

            // Block until the game closes so Steam keeps the session alive.
            try
            {
                gameProcess.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Unable to wait for game process. {ex.Message}");
            }

            return ExitSuccess;
        }

        /// <summary>
        ///     Prints proper usage instructions to the console.
        /// </summary>
        private static void PrintUsage()
        {
            Console.WriteLine("Usage: EpicSteamLauncher.exe \"<EpicLaunchUrl>\" <GameExeName>");
            Console.WriteLine("Note: <GameExeName> should be the process name (with or without .exe).");
            Console.WriteLine("Example: EpicSteamLauncher.exe \"com.epicgames.launcher://apps/Fortnite?action=launch&silent=true\" FortniteClient-Win64-Shipping");
        }

        /// <summary>
        ///     Normalizes a process name so it is compatible with Process.GetProcessesByName().
        ///     Strips '.exe' if present.
        /// </summary>
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
        ///     Polls the system for the most recently started matching process.
        ///     This avoids false positives when older instances exist.
        /// </summary>
        private static Process WaitForMostRecentProcess(
            string exeName,
            DateTime launchTimeUtc,
            TimeSpan timeout,
            TimeSpan pollInterval)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                Process[] matches;

                try
                {
                    matches = Process.GetProcessesByName(exeName);
                }
                catch
                {
                    matches = Array.Empty<Process>();
                }

                if (matches.Length > 0)
                {
                    var candidate = matches
                        .Select(p => new { Process = p, Start = TryGetStartTimeUtc(p) })
                        .Where(x => x.Start.HasValue && x.Start.Value >= launchTimeUtc.AddSeconds(-2))
                        .OrderByDescending(x => x.Start)
                        .Select(x => x.Process)
                        .FirstOrDefault();

                    return candidate ?? matches[0];
                }

                Thread.Sleep(pollInterval);
            }

            return null;
        }

        /// <summary>
        ///     Safely attempts to read the UTC start time of a process.
        ///     Some system processes may throw permission exceptions.
        /// </summary>
        private static DateTime? TryGetStartTimeUtc(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                return null;
            }
        }
    }
}
