using EpicSteamLauncher.Tests.TestInfrastructure;
using Xunit;

namespace EpicSteamLauncher.Tests.EntryPoint
{
    /// <summary>
    ///     Verifies profile-related command behavior for valid, invalid, and mixed profile sets.
    /// </summary>
    public sealed class ProfileCommandTests
    {
        /// <summary>
        ///     Verifies validation succeeds when a single valid profile exists.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WithValidProfile_ReturnsSuccessAndReportsOk()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile("GoodProfile", BuildValidProfileJson("GoodProfile"))
            );

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("[OK]   GoodProfile.esl", result.Output);
            Assert.Contains("Summary: 1 valid, 0 invalid", result.Output);
        }

        /// <summary>
        ///     Verifies validation fails and reports summary counts when profiles are mixed valid and invalid.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WithMixedProfiles_ReturnsProfileInvalidAndCounts()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () =>
                {
                    LauncherTestHost.WriteProfile("GoodProfile", BuildValidProfileJson("GoodProfile"));
                    LauncherTestHost.WriteProfile("BrokenProfile", "{ invalid json }");
                }
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("[OK]   GoodProfile.esl", result.Output);
            Assert.Contains("[FAIL] BrokenProfile.esl", result.Output);
            Assert.Contains("Summary: 1 valid, 1 invalid", result.Output);
        }

        /// <summary>
        ///     Verifies profile launch returns bad-arguments when the launch URL scheme is unsupported.
        /// </summary>
        [Fact]
        public void ProfileByName_WithUnsupportedEpicScheme_ReturnsBadArgs()
        {
            var result = LauncherTestHost.Run(
                ["--profile", "UnsupportedScheme"],
                () => LauncherTestHost.WriteProfile("UnsupportedScheme", BuildUnsupportedSchemeProfileJson("UnsupportedScheme"))
            );

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ERROR: Invalid Epic launch URL:", result.Output);
        }

        /// <summary>
        ///     Verifies profile launch returns profile-invalid when required fields are missing.
        /// </summary>
        [Fact]
        public void ProfileByName_WithMissingProcessName_ReturnsProfileInvalid()
        {
            var result = LauncherTestHost.Run(
                ["--profile", "MissingProcess"],
                () => LauncherTestHost.WriteProfile("MissingProcess", BuildMissingProcessProfileJson("MissingProcess"))
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("Missing required field: GameProcessName", result.Output);
        }

        /// <summary>
        ///     Verifies profile launch supports the equals form for profile selection and reports missing profile correctly.
        /// </summary>
        [Fact]
        public void ProfileEqualsSyntax_WhenMissing_ReturnsProfileNotFound()
        {
            var result = LauncherTestHost.Run(["--profile=MissingProfile"]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("Profile 'MissingProfile' is invalid or could not be loaded.", result.Output);
        }

        /// <summary>
        ///     Verifies selector mode ignores the generated example profile file.
        /// </summary>
        [Fact]
        public void ProfileSelector_IgnoresExampleProfile()
        {
            var result = LauncherTestHost.Run(
                ["--profile"],
                () => LauncherTestHost.WriteProfile("example.profile", BuildValidProfileJson("example.profile"))
            );

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No valid profiles found.", result.Output);
        }

        /// <summary>
        ///     Verifies validation ignores the generated example profile file when it is the only profile present.
        /// </summary>
        [Fact]
        public void ValidateProfiles_IgnoresExampleProfile()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile("example.profile", BuildValidProfileJson("example.profile"))
            );

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("No candidate profile JSON files found.", result.Output);
        }

        /// <summary>
        ///     Builds a minimal valid profile payload.
        /// </summary>
        /// <param name="name">Profile display name.</param>
        /// <returns>Serialized JSON profile content.</returns>
        private static string BuildValidProfileJson(string name)
        {
            return "{" +
                   "\"Name\":\"" +
                   name +
                   "\"," +
                   "\"EpicLaunchUrl\":\"com.epicgames.launcher://apps/Fortnite?action=launch&silent=true\"," +
                   "\"GameProcessName\":\"FortniteClient-Win64-Shipping\"," +
                   "\"StartTimeoutSeconds\":30," +
                   "\"PollIntervalMs\":500," +
                   "\"LaunchDelayMs\":0" +
                   "}";
        }

        /// <summary>
        ///     Builds a profile payload with an unsupported URI scheme that passes profile deserialization checks.
        /// </summary>
        /// <param name="name">Profile display name.</param>
        /// <returns>Serialized JSON profile content.</returns>
        private static string BuildUnsupportedSchemeProfileJson(string name)
        {
            return "{" +
                   "\"Name\":\"" +
                   name +
                   "\"," +
                   "\"EpicLaunchUrl\":\"https://example.invalid/launch\"," +
                   "\"GameProcessName\":\"FortniteClient-Win64-Shipping\"," +
                   "\"StartTimeoutSeconds\":30," +
                   "\"PollIntervalMs\":500," +
                   "\"LaunchDelayMs\":0" +
                   "}";
        }

        /// <summary>
        ///     Builds a profile payload missing the required process-name field.
        /// </summary>
        /// <param name="name">Profile display name.</param>
        /// <returns>Serialized JSON profile content.</returns>
        private static string BuildMissingProcessProfileJson(string name)
        {
            return "{" +
                   "\"Name\":\"" +
                   name +
                   "\"," +
                   "\"EpicLaunchUrl\":\"com.epicgames.launcher://apps/Fortnite?action=launch&silent=true\"," +
                   "\"GameProcessName\":\"\"," +
                   "\"StartTimeoutSeconds\":30," +
                   "\"PollIntervalMs\":500," +
                   "\"LaunchDelayMs\":0" +
                   "}";
        }
    }
}
