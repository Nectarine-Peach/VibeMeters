# VibeMeters

**Your AI subscription quotas, in a small Windows tray window.**

[한국어](README.ko.md) · **English** · [Verification notes](VERIFICATION.md) · [Design reference](docs/design/README.md)

Keep Codex, Claude, and Gemini usage visible without opening their dashboards. Pin a compact window above your work, choose the services you use, and see remaining or used quota at a glance.

Built with native WPF and .NET 8 for **Windows 10/11 x64**. No embedded browser. No account required for VibeMeters itself.

## The same layout, at any size

Standard and mini windows share the same service cards: **service heading → quota label, meter, percentage**. Resizing adjusts type and spacing while keeping quota rows stacked vertically.

| Standard window | Mini window |
| :---: | :---: |
| <img src="docs/screenshots/window-standard-dark.png" alt="Standard dark window with vertically stacked quota rows" width="360"> | <img src="docs/screenshots/window-mini-dark.png" alt="Mini dark window preserving the same quota row layout" width="220"> |

<details>
<summary>Settings</summary>

<img src="docs/screenshots/settings-dark.png" alt="Provider selection, display basis, theme, refresh, and account settings" width="400">

</details>

Images are rendered from the native Windows app using **demo data**, not AI-generated mockups or live account quotas. Capture details are documented in the verification notes.

## Features

- **Codex, Claude, and Gemini** in one window. Show any combination, including just one service.
- **Remaining or used percentage per service**, reflected in both the window and tray meters.
- **Resizable, scroll-free cards** starting at 220 × 140 logical pixels. Minimum height grows when more quota rows need room; wide windows can arrange cards side by side.
- **Always on top**, tray residence, optional taskbar icon, and optional Windows startup.
- **Dark, light, or system theme** across the main window, settings, menus, and tooltips.
- **Automatic or manual refresh**, cached results, request cooldowns, and optional alerts at 20% / 5% remaining quota.
- **Native rendering without continuous animation.** Codex and Claude use in-process HTTP requests; Gemini uses a short-lived hidden Node process rather than a resident CLI.

## Get started

A downloadable GitHub release has not been published yet. [Build from source](#build-from-source) to create `dist/VibeMeters-win-x64.zip`. Future packages will be listed on the [Releases page](https://github.com/Nectarine-Peach/VibeMeters/releases).

1. Extract the **entire ZIP** into a permanent folder and run `VibeMeters.exe`. The Windows package includes .NET; running it needs no separate .NET installation.
2. Open Settings with the gear icon, choose your services, and connect your accounts.
3. Resize the window and use the pin icon to keep it above other apps.

Keep the EXE and bundled `runtime` folder together; Gemini requires that folder. The package is not code-signed, so Windows may show an unknown-publisher warning.

### Connect your accounts

| Service | Setup | What is shown |
| --- | --- | --- |
| **Codex** | Install the official Codex CLI and run `codex login`, or choose **Codex 로그인** in Settings. | Subscription quota windows returned for your ChatGPT account. |
| **Claude** | Install Claude Code and run `claude auth login --claudeai`, or choose **Claude 로그인** in Settings. | Available subscription windows, such as five-hour and weekly quotas. |
| **Gemini** | Enable Gemini in Settings and choose **Gemini 로그인** for official browser sign-in. A supported Google account and Code Assist project are required. | Model quota buckets and reset information returned by the official Gemini CLI core. |

Codex and Claude login buttons launch the installed official CLI's login flow; complete any terminal/browser steps it opens. Signing in to a provider's website or desktop app alone may not create the CLI credentials VibeMeters needs. Gemini quotas appear **inside VibeMeters**; normal polling does not open a terminal or browser or send model-generation requests. Gemini is off by default.

This app shows quota percentages, **not API-key billing, token costs, or a guaranteed number of prompts remaining**. Missing values are shown as unavailable rather than estimated.

## Everyday use

| Action | How |
| --- | --- |
| Refresh | Top refresh icon, **F5**, or tray menu |
| Choose services, theme, or display basis | Top gear icon → Settings |
| Keep the window on top | Top pin icon or tray menu |
| Hide to tray | Close button or **Esc** |
| Restore the window | Click the tray icon, or launch the app again |
| Quit | Right-click the tray icon → **종료** |

Settings save immediately; window size and position are remembered. The taskbar option provides a normal app icon and minimize behavior, not a quota widget or progress overlay. The minimize button appears in sufficiently wide windows.

Refresh options are **30 seconds, 1, 2, 5, or 15 minutes, or manual**; the default is two minutes. Polling pauses while Windows is locked or sleeping. Hover over quota rows or status indicators for reset times and connection details. A warning indicator can mean cached data, a login problem, or a temporary server restriction.

> **Language:** Documentation is available in English and Korean. Current application labels are Korean; an in-app English language switch is not included.

## Refresh limits and troubleshooting

Updates use polling rather than a real-time server stream. Provider cooldowns can make the effective interval longer than the selected setting.

| Situation | What to do |
| --- | --- |
| Codex or Claude is not connected | Sign in through the official CLI or Settings, then refresh. Credential-file changes trigger a new check. |
| Claude reports a temporary query limit | Wait for the retry time in the tooltip. HTTP 429 restricts the **usage lookup**; it does not by itself mean the subscription quota is exhausted. |
| A previous percentage remains visible after an error | It is the last successful value. Read the status tooltip before treating it as current. |
| Gemini has no quota information | Check Google login and project access. Some accounts do not return supported buckets. Keep the bundled runtime beside the EXE. |
| Startup stops working after moving the app | Toggle Windows startup off and on in Settings to update the saved path. |

Claude successful lookups have a **two-minute minimum**. After HTTP 429, VibeMeters waits **5 → 10 → 20 → 30 → 60 minutes**, or longer if requested by the server. Manual refresh does not bypass this wait. Gemini results are cached for at least **five minutes**, with a **ten-minute wait** after rate limiting. These are app policies, not published provider request allowances, and do not guarantee that throttling will never occur.

## Privacy and compatibility

- Codex reads `%USERPROFILE%\.codex\auth.json` (`CODEX_HOME` supported); Claude reads `%USERPROFILE%\.claude\.credentials.json` (`CLAUDE_CONFIG_DIR` supported). VibeMeters does not persist or refresh these tokens. Credential-manager-only Codex/Claude logins are currently unsupported.
- Gemini bundles official `@google/gemini-cli-core` **0.65.0**. Its normal authentication flow may store and refresh its own login cache. Raw tokens are not forwarded to the .NET app's settings or quota output.
- Preferences live in `%LOCALAPPDATA%\VibeMeters\settings.json`. Optional startup registration uses the current user's Windows Run key. Credentials are not included in source or release packages.
- Codex and Claude use provider account endpoints; Gemini uses an internal interface of the official CLI core. These integrations may change or be restricted by provider policies. VibeMeters is an independent project, unaffiliated with OpenAI, Anthropic, or Google.

See [implementation research](RESEARCH.md) and [verification notes](VERIFICATION.md) for integration details and the scope of testing. No specific memory or CPU benchmark is claimed.

## Build from source

Requirements: Windows, the **.NET 8 SDK**, and **Node.js/npm** compatible with the pinned Gemini CLI core. The publish script installs locked npm dependencies if needed and bundles the available `node.exe` runtime.

```powershell
git clone https://github.com/Nectarine-Peach/VibeMeters.git
cd VibeMeters
dotnet build -c Release
.\scripts\publish.ps1
.\dist\VibeMeters-win-x64\VibeMeters.exe
```

Output: `dist/VibeMeters-win-x64/` and `dist/VibeMeters-win-x64.zip`. This is a self-contained Windows x64 package; include its runtime folder when distributing it.

Run the isolated provider/parser/settings regression suite with:

```powershell
dotnet run --project tests/VibeMeters.Tests.csproj -c Release
```

`VibeMeters.exe --demo` previews sample data without accessing provider accounts. When [reporting a bug](https://github.com/Nectarine-Peach/VibeMeters/issues), include the version, Windows display scaling, window size, provider, and visible error. **Never attach credential files or access tokens.**

