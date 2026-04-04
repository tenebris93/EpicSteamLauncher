namespace EpicSteamLauncher.Infrastructure.Steam
{
    /// <summary>
    /// Indicates whether a Steam shortcut entry was created or updated.
    /// </summary>
    internal enum ShortcutUpsertResult
    {
        Created,
        Updated
    }

    /// <summary>
    /// Loads, mutates, and saves Steam's <c>shortcuts.vdf</c> content.
    /// </summary>
    internal static class SteamShortcutsEditor
    {
        /// <summary>
        /// Loads Steam shortcuts from disk or creates an empty root structure when missing.
        /// </summary>
        /// <param name="shortcutsPath">Path to the shortcuts VDF file.</param>
        /// <returns>Mutable shortcuts root map.</returns>
        public static Dictionary<string, object> LoadOrCreateShortcuts(string shortcutsPath)
        {
            if (!File.Exists(shortcutsPath))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                };
            }

            using var fs = File.Open(shortcutsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var root = BinaryVdf.Read(fs);

            if (!root.TryGetValue("shortcuts", out object? shortcutsObj) || shortcutsObj is not Dictionary<string, object>)
            {
                root["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            return root;
        }

        /// <summary>
        /// Saves shortcuts to disk atomically to reduce risk of corruption.
        /// </summary>
        /// <param name="shortcutsPath">Path to the shortcuts VDF file.</param>
        /// <param name="root">Root shortcuts map to persist.</param>
        public static void SaveAtomic(string shortcutsPath, Dictionary<string, object> root)
        {
            string dir = Path.GetDirectoryName(shortcutsPath) ?? ".";
            Directory.CreateDirectory(dir);

            string tmp = Path.Combine(dir, Path.GetFileName(shortcutsPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var fs = File.Open(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    BinaryVdf.Write(fs, root);
                }

                if (File.Exists(shortcutsPath))
                {
                    try
                    {
                        File.Replace(tmp, shortcutsPath, null, true);
                    }
                    catch
                    {
                        File.Delete(shortcutsPath);
                        File.Move(tmp, shortcutsPath);
                    }
                }
                else
                {
                    File.Move(tmp, shortcutsPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        /// <summary>
        /// Creates or updates a Steam shortcut entry for a profile-backed launcher command.
        /// </summary>
        /// <param name="root">Root shortcuts map.</param>
        /// <param name="appName">Display name shown by Steam.</param>
        /// <param name="iconPath">Optional icon path value.</param>
        /// <param name="exeQuoted">Quoted executable path.</param>
        /// <param name="startDirQuoted">Quoted startup directory path.</param>
        /// <param name="launchOptions">Launch options passed to the executable.</param>
        /// <param name="appId">Deterministic app ID.</param>
        /// <param name="tags">Shortcut tag list.</param>
        /// <returns>The upsert result indicating create or update.</returns>
        public static ShortcutUpsertResult UpsertShortcutForProfile(
            Dictionary<string, object> root,
            string appName,
            string? iconPath,
            string exeQuoted,
            string startDirQuoted,
            string launchOptions,
            uint appId,
            string[] tags)
        {
            var shortcuts = (Dictionary<string, object>)root["shortcuts"];

            string? existingKey = null;

            foreach (var kvp in shortcuts)
            {
                if (kvp.Value is not Dictionary<string, object> entry)
                {
                    continue;
                }

                if (entry.TryGetValue("appid", out object? idObj) && idObj is uint id && id == appId)
                {
                    existingKey = kvp.Key;
                    break;
                }
            }

            var shortcutEntry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["appid"] = appId,
                ["appname"] = appName,
                ["icon"] = iconPath ?? "",
                ["exe"] = exeQuoted,
                ["StartDir"] = startDirQuoted,
                ["LaunchOptions"] = launchOptions,
                ["IsHidden"] = 0u,
                ["AllowDesktopConfig"] = 1u,
                ["OpenVR"] = 0u,
                ["Devkit"] = 0u,
                ["DevkitGameID"] = "",
                ["LastPlayTime"] = 0u,
                ["tags"] = BuildTagsMap(tags)
            };

            if (existingKey != null)
            {
                shortcuts[existingKey] = shortcutEntry;
                return ShortcutUpsertResult.Updated;
            }

            int next = 0;

            foreach (string k in shortcuts.Keys)
            {
                if (int.TryParse(k, out int n) && n >= next)
                {
                    next = n + 1;
                }
            }

            shortcuts[next.ToString()] = shortcutEntry;
            return ShortcutUpsertResult.Created;
        }

        /// <summary>
        /// Sets the icon path for an existing shortcut by app ID.
        /// </summary>
        /// <param name="root">Root shortcuts map.</param>
        /// <param name="appId">Shortcut app ID to update.</param>
        /// <param name="iconPath">Absolute icon path.</param>
        /// <returns><see langword="true" /> when the icon value changed; otherwise <see langword="false" />.</returns>
        public static bool TrySetIconPath(Dictionary<string, object> root, uint appId, string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return false;
            }

            if (!root.TryGetValue("shortcuts", out object? shortcutsObj) ||
                shortcutsObj is not Dictionary<string, object> shortcuts)
            {
                return false;
            }

            // Steam expects a Windows path string here. We'll normalize it just in case.
            // (Program.cs already passes GetFullPath, but we keep this defensive.)
            string normalized = iconPath.Trim();

            foreach (var kvp in shortcuts)
            {
                if (kvp.Value is not Dictionary<string, object> entry)
                {
                    continue;
                }

                if (entry.TryGetValue("appid", out object? idObj) && idObj is uint id && id == appId)
                {
                    // If the icon is already set to this value, avoid rewriting shortcuts.vdf.
                    if (entry.TryGetValue("icon", out object? existingObj) &&
                        existingObj is string existing &&
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    entry["icon"] = normalized;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds Steam's expected numeric-key tag map from plain string tags.
        /// </summary>
        /// <param name="tags">Optional list of tag values.</param>
        /// <returns>Tag map keyed from zero-based index.</returns>
        private static Dictionary<string, object> BuildTagsMap(string[]? tags)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (tags == null)
            {
                return map;
            }

            int i = 0;

            foreach (string t in tags)
            {
                if (string.IsNullOrWhiteSpace(t))
                {
                    continue;
                }

                map[i.ToString()] = t.Trim();
                i++;
            }

            return map;
        }
    }
}

