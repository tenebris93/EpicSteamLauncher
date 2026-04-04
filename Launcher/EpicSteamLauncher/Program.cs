using EpicSteamLauncher.Application;

namespace EpicSteamLauncher
{
    /// <summary>
    ///     Application entry point responsible for startup argument preparation, top-level failure handling, and execution dispatch.
    /// </summary>
    internal static class Program
    {
        private const int ExitUnexpectedError = 8;

        /// <summary>
        ///     Normalizes startup arguments, selects startup mode, and invokes the launcher workflow.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Process exit code.</returns>
        private static int Main(string[]? args)
        {
            try
            {
                string[] normalizedArgs = NormalizeArgs(args);
                return normalizedArgs.Length == 0
                    ? LauncherApplication.RunInteractive()
                    : LauncherApplication.RunCommandLine(normalizedArgs);
            }
            catch (Exception ex)
            {
                WriteStartupFailure(ex);
                return ExitUnexpectedError;
            }
        }

        /// <summary>
        ///     Normalizes raw command-line arguments into a non-null array.
        /// </summary>
        /// <param name="args">Raw arguments from the process entry point.</param>
        /// <returns>A non-null argument array.</returns>
        private static string[] NormalizeArgs(string[]? args)
        {
            // Keep launcher behavior intact while ensuring downstream code always gets a non-null array.
            return args ?? [];
        }

        /// <summary>
        ///     Writes a concise startup-level failure message for unexpected unhandled exceptions.
        /// </summary>
        /// <param name="ex">Unhandled exception encountered during startup dispatch.</param>
        private static void WriteStartupFailure(Exception ex)
        {
            Console.WriteLine($"ERROR: Unexpected startup failure. {ex.GetType().Name}: {ex.Message}");
        }
    }
}
