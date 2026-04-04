namespace EpicSteamLauncher.Application.Internal
{
    /// <summary>
    ///     Defines default timing and diagnostics limits used by launcher workflows.
    /// </summary>
    internal static class Defaults
    {
        /// <summary>
        ///     Default maximum wait time for initial game process detection.
        /// </summary>
        public const int StartTimeoutSeconds = 60;

        /// <summary>
        ///     Default polling interval while waiting for process detection.
        /// </summary>
        public const int PollIntervalMs = 500;

        /// <summary>
        ///     Default pre-scan delay after launch invocation.
        /// </summary>
        public const int LaunchDelayMs = 0;

        /// <summary>
        ///     Legacy-mode launch delay for compatibility with older invocation behavior.
        /// </summary>
        public const int LegacyLaunchDelayMs = 5000;

        /// <summary>
        ///     Start-time tolerance used when determining whether a process is new.
        /// </summary>
        public const int StartTimeToleranceSeconds = 3;

        /// <summary>
        ///     Enables fallback to any matching process when no new process is detected within timeout.
        /// </summary>
        public const bool FallbackToAnyMatchingProcess = true;

        /// <summary>
        ///     Maximum number of diagnostics candidates displayed for interactive process selection.
        /// </summary>
        public const int DiagnosticsMaxCandidates = 25;
    }
}
