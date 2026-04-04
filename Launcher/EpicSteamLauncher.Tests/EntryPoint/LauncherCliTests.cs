using EpicSteamLauncher.Tests.TestInfrastructure;
using Xunit;

namespace EpicSteamLauncher.Tests.EntryPoint
{
    /// <summary>
    ///     Verifies CLI command behavior and exit-code mapping for core launcher flows.
    /// </summary>
    public sealed class LauncherCliTests
    {
        /// <summary>
        ///     Verifies unknown commands return bad-arguments exit code and print usage text.
        /// </summary>
        [Fact]
        public void UnknownCommand_ReturnsBadArgs_AndPrintsUsage()
        {
            var result = LauncherTestHost.Run(["--not-a-real-command"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies invalid legacy URI input returns bad-arguments exit code and prints validation error.
        /// </summary>
        [Fact]
        public void LegacyInvalidUri_ReturnsBadArgs_AndPrintsError()
        {
            var result = LauncherTestHost.Run(["not_a_uri", "SomeGame.exe"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("ERROR: Invalid Epic launch URL:", result.Output);
        }

        /// <summary>
        ///     Verifies global pause flag parsing does not change unknown-command behavior when no-pause wins.
        /// </summary>
        [Fact]
        public void PauseThenNoPause_WithUnknownCommand_ReturnsBadArgs()
        {
            var result = LauncherTestHost.Run(["--pause", "--no-pause", "--not-a-real-command"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies profile selector mode exits successfully when no valid profiles are available.
        /// </summary>
        [Fact]
        public void ProfileSelector_WhenNoProfiles_ReturnsSuccess()
        {
            var result = LauncherTestHost.Run(["--profile"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No valid profiles found.", result.Output);
        }

        /// <summary>
        ///     Verifies explicit missing profile launch returns profile-not-found exit code.
        /// </summary>
        [Fact]
        public void ProfileByName_WhenMissing_ReturnsProfileNotFound()
        {
            var result = LauncherTestHost.Run(["--profile", "MissingProfile"]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("is invalid or could not be loaded", result.Output);
        }

        /// <summary>
        ///     Verifies profile validation command reports not-found when no candidate profiles exist.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WhenNoCandidates_ReturnsProfileNotFound()
        {
            var result = LauncherTestHost.Run(["--validate-profiles"]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("No candidate profile JSON files found.", result.Output);
        }

        /// <summary>
        ///     Verifies invalid profile JSON returns profile-invalid exit code during validation command.
        /// </summary>
        [Fact]
        public void ValidateProfiles_WithInvalidProfile_ReturnsProfileInvalid()
        {
            var result = LauncherTestHost.Run(
                ["--validate-profiles"],
                () => LauncherTestHost.WriteProfile("BrokenProfile", "{ this is not json }")
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("[FAIL] BrokenProfile.esl", result.Output);
        }

        /// <summary>
        ///     Verifies invalid profile JSON returns profile-invalid exit code for direct profile launch.
        /// </summary>
        [Fact]
        public void ProfileByName_WithInvalidJson_ReturnsProfileInvalid()
        {
            var result = LauncherTestHost.Run(
                ["--profile", "BrokenProfile"],
                () => LauncherTestHost.WriteProfile("BrokenProfile", "{ invalid json }")
            );

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("is invalid or could not be loaded", result.Output);
        }
    }
}
