# EpicSteamLauncher

EpicSteamLauncher is a lightweight C# command-line utility that allows Epic Games Store titles to be launched through Steam. This enables Epic-exclusive games
to run properly inside Steam, Big Picture Mode, and Steam Link.

It works by launching the Epic Games URL, detecting the spawned game process, and keeping the launcher alive until the game exits so Steam treats the session as
an active game.

---

## Features

- Launches Epic Games Store titles using their native Epic launch URLs
- Detects the running game process by executable name
- Blocks until the game closes so Steam sessions behave correctly
- Works with Steam Link and Big Picture Mode
- No external dependencies or background services

---

## How It Works

1. Steam launches EpicSteamLauncher with two arguments
    - Epic Games launch URL
    - Game executable name
2. EpicSteamLauncher opens the Epic URL using the system shell
3. It waits briefly for the game process to start
4. The game process is detected by name
5. EpicSteamLauncher waits until the game exits, then closes

---

## Usage

Add EpicSteamLauncher to Steam as a **Non-Steam Game** and set the launch arguments.

### Syntax

```bash
EpicSteamLauncher.exe "<EpicLaunchURL>" <GameExeName>
```

### Example

```bash
EpicSteamLauncher.exe "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true" FortniteClient-Win64-Shipping
```

---

## Requirements

- Windows
- .NET Framework or compatible runtime

---

## Inspiration

This project was inspired by:

https://seanzwrites.com/posts/how-to-play-epic-games-on-steam-and-steamlink/

---

## License

MIT License. See the LICENSE file for details.
