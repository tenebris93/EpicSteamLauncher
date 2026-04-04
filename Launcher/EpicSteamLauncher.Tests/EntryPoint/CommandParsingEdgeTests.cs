using EpicSteamLauncher.Tests.TestInfrastructure;
using Xunit;

namespace EpicSteamLauncher.Tests.EntryPoint
{
    /// <summary>
    ///     Verifies command-line parser edge cases and command precedence behavior.
    /// </summary>
    public sealed class CommandParsingEdgeTests
    {
        /// <summary>
        ///     Verifies a single positional token is treated as bad arguments and prints usage.
        /// </summary>
        [Fact]
        public void SinglePositionalToken_ReturnsBadArgs()
        {
            var result = LauncherTestHost.Run(["OnlyOneToken"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies three positional tokens are treated as bad arguments and print usage.
        /// </summary>
        [Fact]
        public void ThreePositionalTokens_ReturnBadArgs()
        {
            var result = LauncherTestHost.Run(["One", "Two", "Three"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies an unrecognized flag token is not treated as a command and returns bad arguments.
        /// </summary>
        [Fact]
        public void UnknownFlagToken_ReturnsBadArgs()
        {
            var result = LauncherTestHost.Run(["--foo"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies global pause flags are stripped while unknown flags still return bad arguments.
        /// </summary>
        [Fact]
        public void PauseFlagsWithUnknownFlag_ReturnBadArgs()
        {
            var result = LauncherTestHost.Run(["--pause", "--foo", "--no-pause"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies an empty equals-style profile token is currently treated as an unknown command form.
        /// </summary>
        [Fact]
        public void EmptyProfileEqualsValue_ReturnsBadArgs()
        {
            var result = LauncherTestHost.Run(["--profile="]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("EpicSteamLauncher Usage:", result.Output);
        }

        /// <summary>
        ///     Verifies a quoted-empty equals-style profile value falls back to selector mode.
        /// </summary>
        [Fact]
        public void QuotedEmptyProfileEqualsValue_UsesSelectorMode()
        {
            var result = LauncherTestHost.Run(["--profile=\"\""]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No valid profiles found.", result.Output);
        }

        /// <summary>
        ///     Verifies profile command remains active when followed only by another flag.
        /// </summary>
        [Fact]
        public void ProfileFollowedByCommandFlag_StaysInSelectorMode()
        {
            var result = LauncherTestHost.Run(["--profile", "--wizard"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No valid profiles found.", result.Output);
        }

        /// <summary>
        ///     Verifies the first recognized command wins when validate appears before profile.
        /// </summary>
        [Fact]
        public void FirstRecognizedCommandValidate_WinsOverProfile()
        {
            var result = LauncherTestHost.Run(["--validate-profiles", "--profile", "MissingProfile"]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("No candidate profile JSON files found.", result.Output);
        }

        /// <summary>
        ///     Verifies the first recognized command wins when profile appears before validate.
        /// </summary>
        [Fact]
        public void FirstRecognizedCommandProfile_WinsOverValidate()
        {
            var result = LauncherTestHost.Run(["--profile", "MissingProfile", "--validate-profiles"]);

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("Profile 'MissingProfile' is invalid or could not be loaded.", result.Output);
        }
    }
}
