using System.Text;

namespace EpicSteamLauncher.Infrastructure.Steam
{
    /// <summary>
    ///     Minimal Binary VDF (Steam shortcuts.vdf) codec supporting:
    ///     - Map (0x00)
    ///     - String (0x01)
    ///     - Int32 (0x02) stored little-endian (we treat as uint32)
    ///     - End (0x08)
    ///     This mirrors the format documented for Steam Library Shortcuts (shortcuts.vdf).
    /// </summary>
    /// <remarks>
    ///     Does not support:
    ///     - Other data types (e.g. binary blobs, int64)
    ///     - Comments
    /// </remarks>
    internal static class BinaryVdf
    {
        private const byte TypeMap = 0x00;
        private const byte TypeString = 0x01;
        private const byte TypeInt32 = 0x02;
        private const byte TypeEnd = 0x08;

        /// <summary>
        ///     Reads a Binary VDF document from a stream and returns the root map.
        /// </summary>
        /// <param name="input">Input stream containing Binary VDF content.</param>
        /// <returns>Root key/value map parsed from the input stream.</returns>
        public static Dictionary<string, object> Read(Stream input)
        {
            ArgumentNullException.ThrowIfNull(input);

            using var br = new BinaryReader(input, Encoding.UTF8, true);
            return ReadMap(br);
        }

        /// <summary>
        ///     Writes a Binary VDF document to a stream from the provided root map.
        /// </summary>
        /// <param name="output">Destination stream for Binary VDF bytes.</param>
        /// <param name="root">Root key/value map to serialize.</param>
        public static void Write(Stream output, Dictionary<string, object> root)
        {
            ArgumentNullException.ThrowIfNull(output);

            ArgumentNullException.ThrowIfNull(root);

            using var bw = new BinaryWriter(output, Encoding.UTF8, true);
            WriteMap(bw, root);
        }

        /// <summary>
        ///     Reads a map node until an end marker is encountered.
        /// </summary>
        /// <param name="br">Binary reader positioned at map content.</param>
        /// <returns>Parsed map for the current node.</returns>
        private static Dictionary<string, object> ReadMap(BinaryReader br)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                int t = br.Read();

                if (t < 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading VDF type.");
                }

                byte type = (byte)t;

                if (type == TypeEnd)
                {
                    return map;
                }

                string key = ReadNullTerminatedString(br);

                switch (type)
                {
                    case TypeMap:
                        map[key] = ReadMap(br);
                        break;

                    case TypeString:
                        map[key] = ReadNullTerminatedString(br);
                        break;

                    case TypeInt32:
                        map[key] = br.ReadUInt32(); // little-endian by BinaryReader
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported VDF type byte: 0x{type:X2} (key={key})");
                }
            }
        }

        /// <summary>
        ///     Writes a map node and all children to the binary writer.
        /// </summary>
        /// <param name="bw">Binary writer used for output.</param>
        /// <param name="map">Map node to serialize.</param>
        private static void WriteMap(BinaryWriter bw, Dictionary<string, object> map)
        {
            foreach (var kvp in map)
            {
                string key = kvp.Key;
                object value = kvp.Value;

                switch (value)
                {
                    case Dictionary<string, object> child:
                        bw.Write(TypeMap);
                        WriteNullTerminatedString(bw, key);
                        WriteMap(bw, child);
                        break;

                    case string s:
                        bw.Write(TypeString);
                        WriteNullTerminatedString(bw, key);
                        WriteNullTerminatedString(bw, s);
                        break;

                    case uint u:
                        bw.Write(TypeInt32);
                        WriteNullTerminatedString(bw, key);
                        bw.Write(u);
                        break;

                    case int i when i >= 0:
                        bw.Write(TypeInt32);
                        WriteNullTerminatedString(bw, key);
                        bw.Write((uint)i);
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported VDF value type: {value?.GetType().FullName ?? "null"} (key={key})");
                }
            }

            bw.Write(TypeEnd);
        }

        /// <summary>
        ///     Reads a UTF-8 null-terminated string value from the stream.
        /// </summary>
        /// <param name="br">Binary reader used to read bytes.</param>
        /// <returns>Decoded string value.</returns>
        private static string ReadNullTerminatedString(BinaryReader br)
        {
            using var ms = new MemoryStream();

            while (true)
            {
                int b = br.Read();

                if (b < 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading null-terminated string.");
                }

                if (b == 0)
                {
                    break;
                }

                ms.WriteByte((byte)b);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        ///     Writes a UTF-8 string followed by a null terminator.
        /// </summary>
        /// <param name="bw">Binary writer used for output.</param>
        /// <param name="s">String value to write.</param>
        private static void WriteNullTerminatedString(BinaryWriter bw, string s)
        {
            if (s.Contains('\0'))
            {
                throw new InvalidDataException("Strings in VDF cannot contain null characters.");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(s);
            bw.Write(bytes);
            bw.Write((byte)0);
        }
    }
}
