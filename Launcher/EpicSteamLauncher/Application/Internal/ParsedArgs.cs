namespace EpicSteamLauncher.Application.Internal
{
    /// <summary>
    ///     Represents normalized command-line parsing output after global flag stripping and command detection.
    /// </summary>
    /// <param name="PauseOnExit">Indicates whether the launcher should pause before exiting.</param>
    /// <param name="Command">Resolved command token, or <see langword="null" /> when no known command is present.</param>
    /// <param name="CommandValue">Optional command value (for example, a profile name).</param>
    /// <param name="Positionals">Remaining positional tokens after command parsing.</param>
    internal sealed record ParsedArgs
    (
        bool PauseOnExit,
        string? Command,
        string? CommandValue,
        IReadOnlyList<string> Positionals
    );
}
