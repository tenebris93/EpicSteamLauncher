using EpicSteamLauncher.Application.Internal;

namespace EpicSteamLauncher.Application.Models
{
    /// <summary>
    ///     Represents a persisted launcher profile loaded from or written to <c>.esl</c> JSON files.
    /// </summary>
    internal sealed class GameProfile
    {
        /// <summary>
        ///     Gets or sets the profile display/file name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets the Epic launch URI.
        /// </summary>
        public string EpicLaunchUrl { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets the expected game process name.
        /// </summary>
        public string GameProcessName { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets the process detection timeout in seconds.
        /// </summary>
        public int StartTimeoutSeconds { get; set; } = Defaults.StartTimeoutSeconds;

        /// <summary>
        ///     Gets or sets the polling interval in milliseconds.
        /// </summary>
        public int PollIntervalMs { get; set; } = Defaults.PollIntervalMs;

        /// <summary>
        ///     Gets or sets the delay before first process scan in milliseconds.
        /// </summary>
        public int LaunchDelayMs { get; set; } = Defaults.LaunchDelayMs;

        /// <summary>
        ///     Gets or sets optional install location metadata.
        /// </summary>
        public string? InstallLocation { get; set; } = string.Empty;

        /// <summary>
        ///     Gets or sets optional launch executable metadata.
        /// </summary>
        public string? LaunchExecutable { get; set; } = string.Empty;
    }
}
