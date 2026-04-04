using Microsoft.Win32;

namespace EpicSteamLauncher.Infrastructure.Steam
{
    /// <summary>
    /// Resolves Steam install and user data paths required for shortcut and artwork synchronization.
    /// </summary>
    internal static class SteamLocator
    {
        /// <summary>
        /// Attempts to resolve the Steam installation directory from the current user's registry hive.
        /// </summary>
        /// <returns>The Steam installation path when found; otherwise <see langword="null" />.</returns>
        public static string? TryGetSteamPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steamPath = key?.GetValue("SteamPath") as string;

            if (string.IsNullOrWhiteSpace(steamPath))
            {
                return null;
            }

            steamPath = steamPath.Replace('/', Path.DirectorySeparatorChar).Trim();
            return Directory.Exists(steamPath) ? steamPath : null;
        }

        /// <summary>
        /// Attempts to resolve the active user's <c>shortcuts.vdf</c> path even when the file does not exist yet.
        /// </summary>
        /// <param name="steamPath">Steam installation directory.</param>
        /// <returns>Expected <c>shortcuts.vdf</c> path when a userdata config folder is found; otherwise <see langword="null" />.</returns>
        public static string? TryGetShortcutsVdfPathEvenIfMissing(string steamPath)
        {
            string? configDir = TryFindBestUserdataConfigDirectory(steamPath);

            return configDir == null ? null : Path.Combine(configDir, "shortcuts.vdf");
        }

        /// <summary>
        /// Attempts to resolve the Steam grid artwork folder path even when the folder does not exist yet.
        /// </summary>
        /// <param name="steamPath">Steam installation directory.</param>
        /// <returns>Expected grid folder path when a userdata config folder is found; otherwise <see langword="null" />.</returns>
        public static string? TryGetGridFolderEvenIfMissing(string steamPath)
        {
            string? configDir = TryFindBestUserdataConfigDirectory(steamPath);

            return configDir == null ? null : Path.Combine(configDir, "grid");
        }

        /// <summary>
        /// Picks the most recently active numeric userdata config directory under Steam.
        /// </summary>
        /// <param name="steamPath">Steam installation directory.</param>
        /// <returns>Resolved userdata config folder or <see langword="null" /> if no suitable directory is found.</returns>
        private static string? TryFindBestUserdataConfigDirectory(string steamPath)
        {
            string userdataRoot = Path.Combine(steamPath, "userdata");

            if (!Directory.Exists(userdataRoot))
            {
                return null;
            }

            var userDirs = Directory.EnumerateDirectories(userdataRoot)
                .Select(d => new DirectoryInfo(d))
                .Where(di => di.Name.All(char.IsDigit) && di.Name != "0")
                .ToList();

            if (userDirs.Count == 0)
            {
                return null;
            }

            var scored = userDirs
                .Select(di =>
                    {
                        string configDir = Path.Combine(di.FullName, "config");
                        string localConfig = Path.Combine(configDir, "localconfig.vdf");

                        var scoreTime =
                            File.Exists(localConfig) ? File.GetLastWriteTimeUtc(localConfig) :
                            Directory.Exists(configDir) ? Directory.GetLastWriteTimeUtc(configDir) :
                            di.LastWriteTimeUtc;

                        return new { ConfigDir = configDir, ScoreTime = scoreTime };
                    }
                )
                .OrderByDescending(x => x.ScoreTime)
                .FirstOrDefault();

            return scored?.ConfigDir;
        }
    }
}

