using EpicSteamLauncher.Infrastructure.Steam;
using Xunit;

namespace EpicSteamLauncher.Tests.Infrastructure.Steam
{
    /// <summary>
    ///     Verifies Binary VDF serialization/deserialization behavior, including error handling on malformed payloads.
    /// </summary>
    public sealed class BinaryVdfTests
    {
        /// <summary>
        ///     Ensures a nested shortcut structure can be written and read back without losing values.
        /// </summary>
        [Fact]
        public void WriteThenRead_RoundTripsNestedValues()
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["0"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["appname"] = "Test Game",
                        ["appid"] = 123u,
                        ["LaunchOptions"] = "--profile \"Test\""
                    }
                }
            };

            using var ms = new MemoryStream();
            BinaryVdf.Write(ms, root);
            ms.Position = 0;

            var read = BinaryVdf.Read(ms);
            var shortcuts = Assert.IsType<Dictionary<string, object>>(read["shortcuts"]);
            var entry = Assert.IsType<Dictionary<string, object>>(shortcuts["0"]);

            Assert.Equal("Test Game", Assert.IsType<string>(entry["appname"]));
            Assert.Equal(123u, Assert.IsType<uint>(entry["appid"]));
            Assert.Equal("--profile \"Test\"", Assert.IsType<string>(entry["LaunchOptions"]));
        }

        /// <summary>
        ///     Ensures unsupported type bytes in input payloads are rejected.
        /// </summary>
        [Fact]
        public void Read_WithUnsupportedType_ThrowsInvalidData()
        {
            byte[] bytes =
            [
                0x05, // unsupported type
                (byte)'x',
                0x00
            ];

            using var ms = new MemoryStream(bytes);
            Assert.Throws<InvalidDataException>(() => BinaryVdf.Read(ms));
        }

        /// <summary>
        ///     Ensures truncated null-terminated string payloads fail fast with EOF errors.
        /// </summary>
        [Fact]
        public void Read_WithUnexpectedEofInString_ThrowsEndOfStream()
        {
            byte[] bytes =
            [
                0x01, // string type
                (byte)'k',
                0x00,     // key terminator
                (byte)'v' // value is missing null terminator
            ];

            using var ms = new MemoryStream(bytes);
            Assert.Throws<EndOfStreamException>(() => BinaryVdf.Read(ms));
        }

        /// <summary>
        ///     Ensures values containing embedded null characters cannot be written to VDF.
        /// </summary>
        [Fact]
        public void Write_WithNullCharacterInString_ThrowsInvalidData()
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["shortcuts"] = "bad\0value"
            };

            using var ms = new MemoryStream();
            Assert.Throws<InvalidDataException>(() => BinaryVdf.Write(ms, root));
        }
    }
}
