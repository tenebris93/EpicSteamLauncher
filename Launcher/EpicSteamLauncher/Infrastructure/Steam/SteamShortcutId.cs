using System.Text;

namespace EpicSteamLauncher.Infrastructure.Steam
{
    /// <summary>
    /// Generates Steam-compatible app IDs for non-Steam shortcuts.
    /// </summary>
    internal static class SteamShortcutId
    {
        // Matches Python: zlib.crc32((exe_path + game_name).encode("utf-8")) | 0x80000000
        /// <summary>
        /// Generates a deterministic non-Steam app ID from executable path and game name.
        /// </summary>
        /// <param name="exePath">Executable path used in shortcut identity.</param>
        /// <param name="gameName">Game name used in shortcut identity.</param>
        /// <returns>Computed app ID with the non-Steam high bit set.</returns>
        public static uint GenerateAppId(string exePath, string gameName)
        {
            ArgumentNullException.ThrowIfNull(exePath);

            ArgumentNullException.ThrowIfNull(gameName);

            byte[] bytes = Encoding.UTF8.GetBytes(exePath + gameName);
            uint crc = Crc32(bytes);
            return crc | 0x8000_0000u;
        }

        // Standard CRC32 (IEEE) polynomial used by zlib.
        /// <summary>
        /// Computes CRC32 (IEEE) for the provided byte sequence.
        /// </summary>
        /// <param name="data">Input bytes.</param>
        /// <returns>CRC32 checksum value.</returns>
        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFF_FFFFu;

            foreach (byte b in data)
            {
                crc ^= b;

                for (int i = 0; i < 8; i++)
                {
                    uint mask = (uint)-(int)(crc & 1u);
                    crc = (crc >> 1) ^ (0xEDB8_8320u & mask);
                }
            }

            return ~crc;
        }
    }
}

