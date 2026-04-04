namespace EpicSteamLauncher.Application.Internal
{
    /// <summary>
    ///     Represents the post-action result used to drive menu continuation or exit behavior.
    /// </summary>
    internal readonly struct MenuOutcome
    {
        /// <summary>
        ///     Initializes a new menu outcome instance.
        /// </summary>
        /// <param name="shouldExit">Indicates whether the menu loop should terminate.</param>
        /// <param name="lastResultCode">Optional result code from the most recent action.</param>
        private MenuOutcome(bool shouldExit, int? lastResultCode)
        {
            ShouldExit = shouldExit;
            LastResultCode = lastResultCode;
        }

        /// <summary>
        ///     Gets a value indicating whether the menu loop should exit.
        /// </summary>
        public bool ShouldExit { get; }

        /// <summary>
        ///     Gets an optional result code from the most recent action.
        /// </summary>
        public int? LastResultCode { get; }

        /// <summary>
        ///     Creates a non-exit menu outcome with an optional result code.
        /// </summary>
        /// <param name="lastResultCode">Optional result code from the completed action.</param>
        /// <returns>A menu outcome that continues the loop.</returns>
        public static MenuOutcome Continue(int? lastResultCode = null)
        {
            return new MenuOutcome(false, lastResultCode);
        }

        /// <summary>
        ///     Creates an exit menu outcome.
        /// </summary>
        /// <returns>A menu outcome that exits the loop.</returns>
        public static MenuOutcome Exit()
        {
            return new MenuOutcome(true, null);
        }
    }
}
