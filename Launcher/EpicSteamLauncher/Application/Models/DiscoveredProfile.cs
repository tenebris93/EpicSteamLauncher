namespace EpicSteamLauncher.Application.Models
{
    /// <summary>
    ///     Represents a validated profile discovered on disk with resolved metadata.
    /// </summary>
    internal readonly struct DiscoveredProfile(string name, string path, GameProfile profile)
    {
        /// <summary>
        ///     Gets the profile display name.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        ///     Gets the absolute path to the profile file.
        /// </summary>
        public string Path { get; } = path;

        /// <summary>
        ///     Gets the normalized profile model.
        /// </summary>
        public GameProfile Profile { get; } = profile;
    }
}
