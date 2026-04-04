namespace EpicSteamLauncher.Application.Models
{
    /// <summary>
    ///     Holds installed Epic game metadata discovered from local launcher manifests.
    /// </summary>
    internal sealed class EpicInstalledGame
    {
        /// <summary>
        ///     Gets or sets the human-readable game display name.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        ///     Gets or sets the Epic app identifier/name.
        /// </summary>
        public string? AppName { get; set; }

        /// <summary>
        ///     Gets or sets the installation directory path when available.
        /// </summary>
        public string? InstallLocation { get; set; }

        /// <summary>
        ///     Gets or sets the launch executable path when available.
        /// </summary>
        public string? LaunchExecutable { get; set; }

        /// <summary>
        ///     Gets or sets a short source marker indicating where this record was discovered.
        /// </summary>
        public string? Source { get; set; }
    }
}
