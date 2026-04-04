using System.Reflection;
using System.Text;

namespace EpicSteamLauncher.Tests.TestInfrastructure
{
    /// <summary>
    ///     Provides deterministic launcher invocation utilities for xUnit tests.
    /// </summary>
    internal static class LauncherTestHost
    {
        private static readonly object Gate = new();

        /// <summary>
        ///     Gets the profiles directory used by launcher commands in tests.
        /// </summary>
        internal static string ProfilesDirectory => Path.Combine(AppContext.BaseDirectory, "profiles");

        /// <summary>
        ///     Invokes launcher main with optional setup and captures console output.
        /// </summary>
        /// <param name="args">Command-line arguments for launcher execution.</param>
        /// <param name="setup">Optional setup callback executed after cleanup and before invocation.</param>
        /// <returns>Exit code and captured output.</returns>
        internal static LauncherRunResult Run(string[] args, Action? setup = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            lock (Gate)
            {
                ResetProfilesDirectory();
                setup?.Invoke();

                var originalOut = Console.Out;
                var originalErr = Console.Error;
                var output = new StringBuilder();

                using var writer = new StringWriter(output);

                try
                {
                    Console.SetOut(writer);
                    Console.SetError(writer);
                    int exitCode = InvokeLauncherMain(args);
                    writer.Flush();

                    return new LauncherRunResult(exitCode, NormalizeNewLines(output.ToString()));
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
            }
        }

        /// <summary>
        ///     Writes a profile file directly into the launcher profiles directory.
        /// </summary>
        /// <param name="profileName">Profile base file name without extension.</param>
        /// <param name="jsonContent">Raw JSON content to write.</param>
        internal static void WriteProfile(string profileName, string jsonContent)
        {
            Directory.CreateDirectory(ProfilesDirectory);
            string path = Path.Combine(ProfilesDirectory, profileName + ".esl");
            File.WriteAllText(path, jsonContent, new UTF8Encoding(false));
        }

        /// <summary>
        ///     Deletes and recreates the profiles directory to isolate each test case.
        /// </summary>
        private static void ResetProfilesDirectory()
        {
            if (Directory.Exists(ProfilesDirectory))
            {
                Directory.Delete(ProfilesDirectory, true);
            }
        }

        /// <summary>
        ///     Invokes <c>EpicSteamLauncher.Program.Main</c> via reflection.
        /// </summary>
        /// <param name="launcherArgs">Command-line arguments to pass to main.</param>
        /// <returns>Launcher exit code.</returns>
        private static int InvokeLauncherMain(string[] launcherArgs)
        {
            var launcherAssembly = Assembly.Load("EpicSteamLauncher");
            var programType = launcherAssembly.GetType("EpicSteamLauncher.Program");

            if (programType == null)
            {
                throw new InvalidOperationException("Could not resolve EpicSteamLauncher.Program type.");
            }

            var mainMethod = programType.GetMethod(
                "Main",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                [typeof(string[])],
                null
            );

            if (mainMethod == null)
            {
                throw new InvalidOperationException("Could not resolve EpicSteamLauncher.Program.Main method.");
            }

            object? rawResult = mainMethod.Invoke(null, [launcherArgs]);

            if (rawResult is not int exitCode)
            {
                throw new InvalidOperationException("EpicSteamLauncher.Program.Main did not return an int exit code.");
            }

            return exitCode;
        }

        /// <summary>
        ///     Normalizes line endings for stable cross-environment string assertions.
        /// </summary>
        /// <param name="text">Raw console output.</param>
        /// <returns>Output with Unix newlines.</returns>
        private static string NormalizeNewLines(string text)
        {
            return text.Replace("\r\n", "\n");
        }
    }

    /// <summary>
    ///     Represents the observable result of a launcher invocation.
    /// </summary>
    /// <param name="ExitCode">Process-style exit code returned by launcher main.</param>
    /// <param name="Output">Captured combined standard output and error output.</param>
    internal readonly record struct LauncherRunResult(int ExitCode, string Output);
}
