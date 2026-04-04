using EpicSteamLauncher.Infrastructure.Steam;
using Xunit;

namespace EpicSteamLauncher.Tests.Infrastructure.Steam
{
    /// <summary>
    ///     Verifies Steam shortcut loading, upsert behavior, icon mutation semantics, and atomic persistence flows.
    /// </summary>
    public sealed class SteamShortcutsEditorTests
    {
        /// <summary>
        ///     Ensures missing shortcut files yield an empty shortcuts root map.
        /// </summary>
        [Fact]
        public void LoadOrCreateShortcuts_WhenMissing_ReturnsEmptyShortcutsRoot()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".vdf");

            try
            {
                var root = SteamShortcutsEditor.LoadOrCreateShortcuts(tempPath);
                var shortcuts = Assert.IsType<Dictionary<string, object>>(root["shortcuts"]);
                Assert.Empty(shortcuts);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        /// <summary>
        ///     Ensures upsert creates once and then updates the same logical shortcut entry by AppId.
        /// </summary>
        [Fact]
        public void UpsertShortcutForProfile_CreateThenUpdate_KeepsSingleEntry()
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            };

            uint appId = 42u;

            var created = SteamShortcutsEditor.UpsertShortcutForProfile(
                root,
                "My Game (Epic)",
                null,
                "\"C:\\Games\\EpicSteamLauncher.exe\"",
                "\"C:\\Games\"",
                "--profile \"My Game\"",
                appId,
                ["Epic", "Imported"]
            );

            var updated = SteamShortcutsEditor.UpsertShortcutForProfile(
                root,
                "My Game (Epic)",
                "C:\\Icons\\mygame.png",
                "\"C:\\Games\\EpicSteamLauncher.exe\"",
                "\"C:\\Games\"",
                "--profile \"My Game\" --pause",
                appId,
                ["Epic"]
            );

            Assert.Equal(ShortcutUpsertResult.Created, created);
            Assert.Equal(ShortcutUpsertResult.Updated, updated);

            var shortcuts = Assert.IsType<Dictionary<string, object>>(root["shortcuts"]);
            Assert.Single(shortcuts);

            var entry = Assert.IsType<Dictionary<string, object>>(shortcuts.Values.Single());
            Assert.Equal("--profile \"My Game\" --pause", Assert.IsType<string>(entry["LaunchOptions"]));
            Assert.Equal("C:\\Icons\\mygame.png", Assert.IsType<string>(entry["icon"]));
        }

        /// <summary>
        ///     Ensures icon path updates report a change only when the value actually changes.
        /// </summary>
        [Fact]
        public void TrySetIconPath_UpdatesOnChange_ThenNoOpsWhenSame()
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["0"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["appid"] = 1337u,
                        ["icon"] = ""
                    }
                }
            };

            bool changed = SteamShortcutsEditor.TrySetIconPath(root, 1337u, "C:\\Grid\\1337_icon.png");
            bool changedAgain = SteamShortcutsEditor.TrySetIconPath(root, 1337u, "C:\\Grid\\1337_icon.png");

            Assert.True(changed);
            Assert.False(changedAgain);
        }

        /// <summary>
        ///     Ensures atomic save output can be read back with expected shortcut content.
        /// </summary>
        [Fact]
        public void SaveAtomic_PersistsAndCanBeReadBack()
        {
            string dir = Path.Combine(Path.GetTempPath(), "esl-shortcuts-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "shortcuts.vdf");

            Directory.CreateDirectory(dir);

            try
            {
                var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["shortcuts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["0"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["appid"] = 7u,
                            ["appname"] = "Readback Test"
                        }
                    }
                };

                SteamShortcutsEditor.SaveAtomic(path, root);
                var loaded = SteamShortcutsEditor.LoadOrCreateShortcuts(path);

                var shortcuts = Assert.IsType<Dictionary<string, object>>(loaded["shortcuts"]);
                var entry = Assert.IsType<Dictionary<string, object>>(shortcuts["0"]);
                Assert.Equal("Readback Test", Assert.IsType<string>(entry["appname"]));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }
    }
}
