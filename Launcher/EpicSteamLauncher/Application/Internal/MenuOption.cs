namespace EpicSteamLauncher.Application.Internal
{
    /// <summary>
    ///     Represents a selectable interactive menu option.
    /// </summary>
    internal sealed class MenuOption(string label, Func<MenuOutcome> action)
    {
        /// <summary>
        ///     Gets the visible label shown in the menu.
        /// </summary>
        public string Label { get; } = label ?? throw new ArgumentNullException(nameof(label));

        /// <summary>
        ///     Gets the callback executed when this menu option is selected.
        /// </summary>
        public Func<MenuOutcome> Action { get; } = action ?? throw new ArgumentNullException(nameof(action));
    }
}
