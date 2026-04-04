using System.Text.Json;
using EpicSteamLauncher.Tests.TestInfrastructure;
using Xunit;

namespace EpicSteamLauncher.Tests.EntryPoint
{
    /// <summary>
    ///     Verifies profile normalization and validation behaviors exercised by CLI commands.
    /// </summary>
    public sealed class ProfileNormalizationTests
    {
        /// <summary>
        ///     Verifies profile launch trims URL/process values and normalizes process name by removing .exe.
        /// </summary>
        [Fact]
        public void ProfileLaunch_TrimsAndNormalizesProcessName()
        {
            var result = LauncherTestHost.Run(
                ["--profile", "NormalizedProfile"],
                () => LauncherTestHost.WriteProfile(
                    "NormalizedProfile",
                    BuildProfileJson(
                        "NormalizedProfile",
                        "  https://example.invalid/launch  ",
                        "  MyGame.exe  ",
                        30,
                        500,
                        0
                    )
                )
            );

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Epic URL: https://example.invalid/launch", result.Output);
            Assert.Contains("Process:  MyGame", result.Output);
        }

        /// <summary>
        ///     Verifies validate-profiles rejects launch URLs that do not contain a URI scheme delimiter.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WithoutUriScheme_ReturnsProfileInvalid()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile(
                    "NoScheme",
                    BuildProfileJson(
                        "NoScheme",
                        "not-a-uri",
                        "GameProc",
                        30,
                        500,
                        0
                    )
                )
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("EpicLaunchUrl does not look like a valid URI", result.Output);
        }

        /// <summary>
        ///     Verifies validate-profiles accepts non-positive timing values by applying launcher defaults.
        /// </summary>
        [Fact]
        public void ValidateProfiles_NonPositiveTimingValues_AreAccepted()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile(
                    "TimingFallback",
                    BuildProfileJson(
                        "TimingFallback",
                        "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true",
                        "FortniteClient-Win64-Shipping",
                        0,
                        0,
                        -1
                    )
                )
            );

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("[OK]   TimingFallback.esl", result.Output);
            Assert.Contains("Summary: 1 valid, 0 invalid", result.Output);
        }

        /// <summary>
        ///     Verifies the equals syntax strips surrounding quotes from profile name values.
        /// </summary>
        [Fact]
        public void ProfileEqualsSyntax_WithQuotedValue_IsParsed()
        {
            var result = LauncherTestHost.Run(["--profile=\"MissingQuoted\""]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("Profile 'MissingQuoted' is invalid or could not be loaded.", result.Output);
        }

        /// <summary>
        ///     Verifies validate-profiles reports a missing process name for JSON payloads that deserialize successfully.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WithMissingProcessName_ReturnsProfileInvalid()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile(
                    "MissingProcess",
                    BuildProfileJson(
                        "MissingProcess",
                        "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true",
                        "",
                        30,
                        500,
                        0
                    )
                )
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("Missing required field: GameProcessName", result.Output);
        }

        /// <summary>
        ///     Builds a profile JSON payload for test setup.
        /// </summary>
        /// <param name="name">Profile display name.</param>
        /// <param name="epicLaunchUrl">Epic launch URL.</param>
        /// <param name="gameProcessName">Game process name.</param>
        /// <param name="startTimeoutSeconds">Process start timeout in seconds.</param>
        /// <param name="pollIntervalMs">Polling interval in milliseconds.</param>
        /// <param name="launchDelayMs">Launch delay before scanning in milliseconds.</param>
        /// <returns>Serialized profile JSON payload.</returns>
        private static string BuildProfileJson(
            string name,
            string epicLaunchUrl,
            string gameProcessName,
            int startTimeoutSeconds,
            int pollIntervalMs,
            int launchDelayMs)
        {
            var payload = new
            {
                Name = name,
                EpicLaunchUrl = epicLaunchUrl,
                GameProcessName = gameProcessName,
                StartTimeoutSeconds = startTimeoutSeconds,
                PollIntervalMs = pollIntervalMs,
                LaunchDelayMs = launchDelayMs
            };

            return JsonSerializer.Serialize(payload);
        }
    }
}
