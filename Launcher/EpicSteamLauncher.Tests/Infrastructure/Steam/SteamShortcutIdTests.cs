using EpicSteamLauncher.Infrastructure.Steam;
using Xunit;

namespace EpicSteamLauncher.Tests.Infrastructure.Steam
{
    /// <summary>
    ///     Verifies deterministic and guard behavior of non-Steam AppId generation.
    /// </summary>
    public sealed class SteamShortcutIdTests
    {
        /// <summary>
        ///     Ensures identical inputs produce identical AppIds and set Steam's non-Steam high bit.
        /// </summary>
        [Fact]
        public void GenerateAppId_IsDeterministic_AndSetsNonSteamHighBit()
        {
            uint first = SteamShortcutId.GenerateAppId("C:\\Tools\\EpicSteamLauncher.exe", "Warframe");
            uint second = SteamShortcutId.GenerateAppId("C:\\Tools\\EpicSteamLauncher.exe", "Warframe");

            Assert.Equal(first, second);
            Assert.NotEqual(0u, first & 0x8000_0000u);
        }

        /// <summary>
        ///     Ensures changing identity inputs results in different generated AppIds.
        /// </summary>
        [Fact]
        public void GenerateAppId_DifferentInputs_ProduceDifferentIds()
        {
            uint a = SteamShortcutId.GenerateAppId("C:\\Tools\\EpicSteamLauncher.exe", "GameA");
            uint b = SteamShortcutId.GenerateAppId("C:\\Tools\\EpicSteamLauncher.exe", "GameB");

            Assert.NotEqual(a, b);
        }

        /// <summary>
        ///     Ensures null argument guards are enforced.
        /// </summary>
        [Fact]
        public void GenerateAppId_NullInputs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => SteamShortcutId.GenerateAppId(null!, "Game"));
            Assert.Throws<ArgumentNullException>(() => SteamShortcutId.GenerateAppId("C:\\Tools\\EpicSteamLauncher.exe", null!));
        }
    }
}
