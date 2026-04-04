namespace EpicSteamLauncher.Application.Models
{
    /// <summary>
    ///     Represents a process candidate displayed by diagnostics fallback when process detection fails.
    /// </summary>
    internal sealed class ProcessCandidate
    {
        /// <summary>
        ///     Gets or sets the process identifier.
        /// </summary>
        public int Pid { get; set; }

        /// <summary>
        ///     Gets or sets the process name.
        /// </summary>
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets the local start time when available.
        /// </summary>
        public DateTime? StartTimeLocal { get; set; }

        /// <summary>
        ///     Gets or sets the executable path when accessible.
        /// </summary>
        public string ExePath { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets a value indicating whether this process executable is under the profile install location.
        /// </summary>
        public bool IsUnderInstallLocation { get; set; }
    }
}
