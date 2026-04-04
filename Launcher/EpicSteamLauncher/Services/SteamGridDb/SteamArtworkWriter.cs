using System.Net.Http.Headers;

namespace EpicSteamLauncher.Services.SteamGridDb
{
    /// <summary>
    ///     Downloads missing Steam artwork (grid/hero/logo/wide header) and a shortcut icon from SteamGridDB
    ///     into Steam's <c>grid</c> directory for each shortcut appid.
    /// </summary>
    internal static class SteamArtworkWriter
    {
        // Steam commonly recognizes these in the userdata\...\config\grid folder.
        // (Icons for shortcuts are referenced using an absolute path in shortcuts.vdf.)
        private static readonly string[] KnownExtensions = [".png", ".jpg", ".jpeg", ".webp", ".ico"];

        /// <summary>
        /// Downloads missing artwork variants for each provided shortcut entry.
        /// </summary>
        /// <param name="apiKey">SteamGridDB API key.</param>
        /// <param name="gridFolder">Steam grid folder path.</param>
        /// <param name="items">Shortcut items to process.</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task DownloadMissingArtworkForShortcutsAsync(
            string apiKey,
            string gridFolder,
            IEnumerable<(uint AppId, string GameNameForSearch)> items,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("SteamGridDB API key is missing.", nameof(apiKey));
            }

            if (string.IsNullOrWhiteSpace(gridFolder))
            {
                throw new ArgumentException("Steam grid folder path is missing.", nameof(gridFolder));
            }

            ArgumentNullException.ThrowIfNull(items);

            Directory.CreateDirectory(gridFolder);

            using var sgdbHttp = new HttpClient();
            var sgdb = new SteamGridDbClient(sgdbHttp, apiKey);

            // Separate HttpClient for direct asset downloads (image binaries).
            using var downloadHttp = new HttpClient();
            downloadHttp.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("EpicSteamLauncher", "1.0")
            );

            foreach ((uint appId, string gameName) in items)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(gameName))
                {
                    continue;
                }

                bool needsBanner = !AnyExists(gridFolder, $"{appId}");    // wide header / banner
                bool needsGrid = !AnyExists(gridFolder, $"{appId}p");     // portrait grid
                bool needsHero = !AnyExists(gridFolder, $"{appId}_hero"); // hero
                bool needsLogo = !AnyExists(gridFolder, $"{appId}_logo"); // logo
                bool needsIcon = !AnyExists(gridFolder, $"{appId}_icon"); // shortcut icon

                // IMPORTANT:
                // Previously this only considered grid/hero/logo, which could skip banner-only cases.
                if (!needsBanner && !needsGrid && !needsHero && !needsLogo && !needsIcon)
                {
                    continue;
                }

                int? gameId = null;

                try
                {
                    gameId = await sgdb.SearchFirstGameIdAsync(gameName, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SGDB] Search failed for '{gameName}' (appid {appId}): {ex.Message}");
                }

                if (gameId == null)
                {
                    // Can't download assets without a resolved SteamGridDB game id.
                    continue;
                }

                // --- GRIDS (used for both banner and grid) ---
                SteamGridDbClient.SgdbAssetItem[]? grids = null;

                if (needsBanner || needsGrid)
                {
                    try
                    {
                        grids = await sgdb.GetGridsAsync(gameId.Value, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Grids fetch failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // --- BANNER / WIDE HEADER ---
                if (needsBanner)
                {
                    try
                    {
                        // The "wide header" Steam grid asset typically uses the base appId filename.
                        // We try to pick an image close to ~2.14:1 (Steam header-ish).
                        await TryDownloadBestMatchingAspectAsync(
                            downloadHttp,
                            grids,
                            gridFolder,
                            $"{appId}",
                            920f / 430f, // ~2.14:1
                            0.15f,
                            gameName,
                            ct
                        ).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Banner failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // --- GRID (PORTRAIT) ---
                if (needsGrid)
                {
                    try
                    {
                        await TryDownloadBestAsync(
                            downloadHttp,
                            grids,
                            gridFolder,
                            $"{appId}p",
                            gameName,
                            ct
                        ).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Grid failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // --- HERO ---
                if (needsHero)
                {
                    try
                    {
                        var heroes = await sgdb.GetHeroesAsync(gameId.Value, ct).ConfigureAwait(false);
                        await TryDownloadBestAsync(downloadHttp, heroes, gridFolder, $"{appId}_hero", gameName, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Hero failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // --- LOGO ---
                if (needsLogo)
                {
                    try
                    {
                        var logos = await sgdb.GetLogosAsync(gameId.Value, ct).ConfigureAwait(false);
                        await TryDownloadBestAsync(downloadHttp, logos, gridFolder, $"{appId}_logo", gameName, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Logo failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // --- ICON (for shortcuts.vdf "icon" field) ---
                if (needsIcon)
                {
                    try
                    {
                        var icons = await sgdb.GetIconsAsync(gameId.Value, ct).ConfigureAwait(false);

                        // We download into Steam's grid directory as "{appid}_icon.<ext>".
                        // Your Phase C logic looks for this file pattern and then sets shortcuts.vdf "icon" to that path.
                        await TryDownloadBestAsync(downloadHttp, icons, gridFolder, $"{appId}_icon", gameName, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SGDB] Icon failed for '{gameName}' (appid {appId}): {ex.Message}");
                    }
                }

                // Gentle pacing to reduce the chance of SGDB rate limiting and disk thrash.
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Checks whether a file exists for the provided base name under any known artwork extension.
        /// </summary>
        /// <param name="dir">Directory path to probe.</param>
        /// <param name="baseName">Base file name without extension.</param>
        /// <returns><see langword="true" /> when any supported extension exists; otherwise <see langword="false" />.</returns>
        private static bool AnyExists(string dir, string baseName)
        {
            return KnownExtensions.Any(ext => File.Exists(Path.Combine(dir, baseName + ext)));
        }

        /// <summary>
        /// Selects the highest resolution asset and downloads it if needed.
        /// </summary>
        /// <param name="downloadHttp">HTTP client used for binary downloads.</param>
        /// <param name="assets">Candidate assets.</param>
        /// <param name="gridFolder">Destination folder.</param>
        /// <param name="fileBaseName">Destination file base name.</param>
        /// <param name="gameNameForLog">Game name used for logging.</param>
        /// <param name="ct">Cancellation token.</param>
        private static async Task TryDownloadBestAsync(
            HttpClient downloadHttp,
            SteamGridDbClient.SgdbAssetItem[]? assets,
            string gridFolder,
            string fileBaseName,
            string gameNameForLog,
            CancellationToken ct)
        {
            if (assets == null || assets.Length == 0)
            {
                Console.WriteLine($"[SGDB] No assets returned for '{gameNameForLog}' -> {fileBaseName}");
                return;
            }

            // "Best" heuristic: highest resolution (area).
            var best = assets
                .Where(a => !string.IsNullOrWhiteSpace(a.Url) && a is { Width: > 0, Height: > 0 })
                .OrderByDescending(a => (long)a.Width * a.Height)
                .FirstOrDefault();

            if (best?.Url == null)
            {
                Console.WriteLine($"[SGDB] No valid asset URLs returned for '{gameNameForLog}' -> {fileBaseName}");
                return;
            }

            await DownloadAssetAsync(downloadHttp, best.Url, gridFolder, fileBaseName, ct, gameNameForLog)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Selects the best asset near a preferred aspect ratio and downloads it if needed.
        /// </summary>
        /// <param name="downloadHttp">HTTP client used for binary downloads.</param>
        /// <param name="assets">Candidate assets.</param>
        /// <param name="gridFolder">Destination folder.</param>
        /// <param name="fileBaseName">Destination file base name.</param>
        /// <param name="preferredAspect">Preferred width/height ratio.</param>
        /// <param name="aspectTolerance">Accepted ratio delta.</param>
        /// <param name="gameNameForLog">Game name used for logging.</param>
        /// <param name="ct">Cancellation token.</param>
        private static async Task TryDownloadBestMatchingAspectAsync(
            HttpClient downloadHttp,
            SteamGridDbClient.SgdbAssetItem[]? assets,
            string gridFolder,
            string fileBaseName,
            float preferredAspect,
            float aspectTolerance,
            string gameNameForLog,
            CancellationToken ct)
        {
            if (assets == null || assets.Length == 0)
            {
                Console.WriteLine($"[SGDB] No assets returned for '{gameNameForLog}' -> {fileBaseName}");
                return;
            }

            // Filter candidates by aspect ratio proximity, then pick highest resolution among them.
            var candidates = assets
                .Where(a =>
                    !string.IsNullOrWhiteSpace(a.Url) &&
                    a is { Width: > 0, Height: > 0 }
                )
                .Select(a =>
                    {
                        float aspect = (float)a.Width / a.Height;
                        return new
                        {
                            Asset = a,
                            AspectDelta = Math.Abs(aspect - preferredAspect)
                        };
                    }
                )
                .Where(x => x.AspectDelta <= aspectTolerance)
                .OrderBy(x => x.AspectDelta)
                .ThenByDescending(x => (long)x.Asset.Width * x.Asset.Height)
                .Select(x => x.Asset)
                .ToArray();

            var selected = candidates.FirstOrDefault();

            // If none match the preferred aspect within tolerance, fall back to the overall best by resolution.
            if (selected?.Url == null)
            {
                selected = assets
                    .Where(a => !string.IsNullOrWhiteSpace(a.Url) && a is { Width: > 0, Height: > 0 })
                    .OrderByDescending(a => (long)a.Width * a.Height)
                    .FirstOrDefault();
            }

            if (selected?.Url == null)
            {
                Console.WriteLine($"[SGDB] No valid asset URLs returned for '{gameNameForLog}' -> {fileBaseName}");
                return;
            }

            await DownloadAssetAsync(downloadHttp, selected.Url, gridFolder, fileBaseName, ct, gameNameForLog)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads an individual asset and writes it into the Steam grid directory.
        /// </summary>
        /// <param name="downloadHttp">HTTP client used for binary downloads.</param>
        /// <param name="url">Asset URL.</param>
        /// <param name="gridFolder">Destination folder.</param>
        /// <param name="fileBaseName">Destination file base name.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="gameNameForLog">Game name used for logging.</param>
        private static async Task DownloadAssetAsync(
            HttpClient downloadHttp,
            string url,
            string gridFolder,
            string fileBaseName,
            CancellationToken ct,
            string gameNameForLog)
        {
            var uri = new Uri(url);

            // Determine file extension from the URL. Steam can read PNG/JPG/WEBP in the grid folder.
            string ext = GetExtensionFromUrl(uri);

            string destPath = Path.Combine(gridFolder, fileBaseName + ext);

            // Avoid re-downloading if it exists under any known extension.
            if (AnyExists(gridFolder, fileBaseName))
            {
                return;
            }

            using var resp = await downloadHttp.GetAsync(uri, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(false);

            Console.WriteLine($"[SGDB] Downloaded {Path.GetFileName(destPath)} for '{gameNameForLog}'");
        }

        /// <summary>
        /// Derives a supported local file extension from an asset URL.
        /// </summary>
        /// <param name="uri">Asset URI.</param>
        /// <returns>Normalized extension for local storage.</returns>
        private static string GetExtensionFromUrl(Uri uri)
        {
            try
            {
                string ext = Path.GetExtension(uri.AbsolutePath);

                return ext.ToLowerInvariant() switch
                {
                    ".png" => ".png",
                    ".jpg" => ".jpg",
                    ".jpeg" => ".jpeg",
                    ".webp" => ".webp",
                    ".ico" => ".ico",
                    _ => ".png"
                };
            }
            catch
            {
                return ".png";
            }
        }
    }
}

