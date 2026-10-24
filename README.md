# AI Usage Tracker

A small Windows 11 tray app, in the spirit of RunCat and [limitbar](https://github.com/dancik/limitbar), that shows how much of your **Claude Pro**, **ChatGPT Plus** and **Google AI Pro** limits you have used.

![The AI Usage Tracker panel showing Claude, ChatGPT and Gemini limits](UI.png)

- **Taskbar:** three small number icons, one per AI, each showing that AI's highest current usage (5-hour or weekly window). In Settings you can make them show only the 5-hour or only the weekly number.
  - Digits are **white** below 80 %, **amber** from 80 % and **red** from 90 %.
  - The colored underline tells them apart: orange = Claude, green = ChatGPT, blue = Gemini.
  - Hovering shows `Claude 36% · ChatGPT 13% · Gemini 0%`.
- **Click (left or right) any icon:** a translucent panel with every limit (how much is used and how much is left), its bar, and when it resets. Starting the app again from the Start Menu also opens the panel.
- **Footer:** Refresh Now · Settings · Quit.
- **Settings:** Open at Login, how often to refresh (5–60 min), the panel's transparency, and which window the tray numbers show (Highest, 5 hours or Week). Changes apply at once.
- **Credits:** any credit above 0 shows under its card: ChatGPT's free full resets (each with its expiry date) and paid credit balance, and Gemini's AI credits. Claude's CLI has no read-only credits command, and the other balances come without an expiry date.
- **Keys:** Esc closes the panel; Ctrl+R refreshes; Ctrl+Q quits.

## Install

1. Run `dist\AIUsageTracker-Setup-1.1.0.exe`. It installs for your user only, with no admin prompt, to `%LOCALAPPDATA%\Programs\AI Usage Tracker`.
   The installer is not code-signed, so SmartScreen may warn: click **More info › Run anyway**.
2. Windows hides new tray icons at first. Open **Settings › Personalization › Taskbar › Other system tray icons** and turn on the three *AI Usage Tracker* entries, or drag them out of the `^` overflow onto the taskbar.

Uninstall from **Settings › Apps › Installed apps › AI Usage Tracker**. This removes the app, the shortcut, the startup entry and the settings.

## Where the numbers come from

Like limitbar, the app **asks each AI's own command-line tool** for its usage. It never reads a token, cookie or password, and it makes no network calls of its own. None of these calls uses a model, so they cost nothing. It refreshes every 10 minutes (change it in Settings), and whenever you click *Refresh Now*.

| Card | Command | What it measures |
|---|---|---|
| Claude | `claude -p /usage` (Claude Code) | Your Claude plan's 5-hour session and weekly limits. These are shared by claude.ai and Claude Code. |
| ChatGPT | `codex app-server` → `account/rateLimits/read` (Codex CLI) | Your ChatGPT plan's **Codex** 5-hour and weekly limits, plus free full resets and paid credits. No tool exposes the chat-message caps. |
| Gemini | `agy -p /quota` and `agy -p /credits` (Google Antigravity CLI) | Your Google AI Pro **Antigravity** quota (Gemini models: 5-hour and weekly), plus Gemini 4 Argon and your remaining AI credits. It does not cover the Gemini app's chat limits. |

All three CLIs must be installed and signed in. On this PC they live in WSL (distro `Ubuntu-20.04`), so the app runs them through `wsl.exe`.

- If a card says **"Not signed in — run `…`"**, open that CLI in WSL and sign in.
- **"No data"** means the CLI is missing, timed out, or changed its output. The last good numbers stay visible, dimmed.

## Settings

The Settings page saves to `%LOCALAPPDATA%\AIUsageTracker\config.json`, which is created on first run:

```json
{ "wslDistro": "Ubuntu-20.04", "refreshMinutes": 10, "transparency": 40, "trayShows": "max" }
```

`trayShows` is `"max"`, `"5h"` or `"week"`. `wslDistro` is only in this file: set it to `""` if the CLIs are installed natively on Windows. Quit and restart the app after editing the file by hand.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Inno Setup 6 (`winget install --id JRSoftware.InnoSetup -e --scope user`). Then:

```powershell
.\build.ps1 -Version 1.1.0     # tests → dotnet publish → dist\AIUsageTracker-Setup-1.1.0.exe
```

The parser tests also run on Linux/WSL: `dotnet test tests`.

| File | What it does |
|---|---|
| `src/Usage.cs` | Runs the CLIs and parses their answers into cards (the only logic with tests) |
| `src/App.cs` | Entry point: tray icons, refresh timer, config, Open at Login |
| `src/Flyout.xaml(.cs)` | The detail panel (Windows 11 acrylic, dark) and its Settings page |
| `src/Marks.cs` | Brand marks ([simple-icons](https://github.com/simple-icons/simple-icons), CC0) |
| `tests/ParseTests.cs` | Parser tests on real, redacted CLI output |
| `installer/setup.iss` | Inno Setup script |

The marks are trademarks of their owners and only identify each service. This project is not affiliated with Anthropic, OpenAI or Google.
