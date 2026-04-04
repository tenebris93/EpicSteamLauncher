using Newtonsoft.Json;

namespace EpicSteamLauncher.Configuration
{
    /// <summary>
    ///     Stores SteamGridDB integration preferences and API key settings.
    /// </summary>
    internal sealed class SteamGridDbConfig
    {
        /// <summary>
        ///     Gets or sets the SteamGridDB API key.
        /// </summary>
        [JsonProperty("apiKey")]
        public string ApiKey { get; set; } = "";

        /// <summary>
        ///     Gets or sets a value indicating whether key prompts should be skipped.
        /// </summary>
        [JsonProperty("dontAskAgain")]
        public bool DontAskAgain { get; set; }

        /// <summary>
        ///     Gets or sets the last successful API validation timestamp in UTC.
        /// </summary>
        [JsonProperty("lastValidatedUtc")]
        public DateTime? LastValidatedUtc { get; set; }

        /// <summary>
        ///     Loads an existing configuration file or creates a default one when missing or invalid.
        /// </summary>
        /// <param name="path">Configuration file path.</param>
        /// <param name="created">Outputs whether a new default configuration was created.</param>
        /// <returns>Loaded or newly created configuration instance.</returns>
        public static SteamGridDbConfig LoadOrCreate(string path, out bool created)
        {
            created = false;

            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<SteamGridDbConfig>(json);

                    if (cfg != null)
                    {
                        return cfg;
                    }
                }
            }
            catch
            {
                // If parse fails, fall through and recreate
            }

            var fresh = new SteamGridDbConfig();
            created = true;
            SaveAtomic(path, fresh);
            return fresh;
        }

        /// <summary>
        ///     Saves configuration atomically to prevent partial writes.
        /// </summary>
        /// <param name="path">Configuration file path.</param>
        /// <param name="cfg">Configuration data to persist.</param>
        public static void SaveAtomic(string path, SteamGridDbConfig cfg)
        {
            ArgumentNullException.ThrowIfNull(cfg);

            string dir = Path.GetDirectoryName(path) ?? ".";
            Directory.CreateDirectory(dir);

            string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            string json = JsonConvert.SerializeObject(cfg, Formatting.Indented);

            try
            {
                File.WriteAllText(tmp, json);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null, true);
                    }
                    catch
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
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
    }
}
