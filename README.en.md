# VibeMeters

[한국어](README.md) · **English**

Keep your **Codex, Claude, and Gemini quotas** visible in a small Windows tray window.

**Windows 10/11 x64 · Always on top · Dark / light / system theme**

<img src="docs/screenshots/window-mini-dark.png" alt="VibeMeters mini window" width="274">

*Native app capture with demo data.*

## Quick start

1. Extract the **entire package**, keeping its `runtime` folder, and run `VibeMeters.exe`.
2. Open **Settings (gear)**, choose your services, and sign in.
3. Resize the window and use the **pin** to keep it on top.

A GitHub release is not available yet. Expand **Build and verification** below to create the package.

## Detailed guides

<details>
<summary>Account connections</summary>

| Service | Setup | What is shown |
| --- | --- | --- |
| **Codex** | Install the official Codex CLI and run `codex login`, or choose **Codex 로그인** in Settings. | Subscription quota windows returned for your ChatGPT account. |
| **Claude** | Install Claude Code and run `claude auth login --claudeai`, or choose **Claude 로그인** in Settings. | Available subscription windows, such as five-hour and weekly quotas. |
| **Gemini** | Enable Gemini in Settings and choose **Gemini 로그인** for official browser sign-in. A supported Google account and Code Assist project are required. | Model quota buckets and reset information returned by the official Gemini CLI core. |

Codex and Claude login buttons launch the installed official CLI's login flow; complete any terminal/browser steps it opens. Signing in to a provider's website or desktop app alone may not create the CLI credentials VibeMeters needs. Gemini quotas appear **inside VibeMeters**; normal polling does not open a terminal or browser or send model-generation requests. Gemini is off by default.

This app shows quota percentages, **not API-key billing, token costs, or a guaranteed number of prompts remaining**. Missing values are shown as unavailable rather than estimated.

</details>

<details>
<summary>Settings and controls</summary>

Choose any combination of services and set **remaining or used percentage per service**. Dark, light, and system themes are available. The scroll-free window starts at 220 × 140 logical pixels; more quota rows increase the minimum height. Optional alerts notify you at 20% / 5% remaining.

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

</details>

<details>
<summary>More screenshots</summary>

### Standard and light theme

| Dark | Light |
| :---: | :---: |
| <img src="docs/screenshots/window-standard-dark.png" alt="Standard dark window" width="360"> | <img src="docs/screenshots/window-standard-light.png" alt="Standard light window" width="360"> |

### All services and minimum size

| Codex, Claude, and Gemini | Minimum window |
| :---: | :---: |
| <img src="docs/screenshots/window-all-services-dark.png" alt="All three services" width="360"> | <img src="docs/screenshots/window-minimum-dark.png" alt="Minimum 220 by 140 logical pixel window" width="220"> |

### Settings

<img src="docs/screenshots/settings-dark.png" alt="Service, theme, refresh and account settings" width="400">

These are app-rendered captures using demo data. See the [verification notes](VERIFICATION.md) for capture details.

</details>

<details>
<summary>Refresh limits and troubleshooting</summary>

Updates use polling rather than a real-time server stream. Provider cooldowns can make the effective interval longer than the selected setting.

| Situation | What to do |
| --- | --- |
| Codex or Claude is not connected | Sign in through the official CLI or Settings, then refresh. Credential-file changes trigger a new check. |
| Claude reports a temporary query limit | Wait for the retry time in the tooltip. HTTP 429 restricts the **usage lookup**; it does not by itself mean the subscription quota is exhausted. |
| A previous percentage remains visible after an error | It is the last successful value. Read the status tooltip before treating it as current. |
| Gemini has no quota information | Check Google login and project access. Some accounts do not return supported buckets. Keep the bundled runtime beside the EXE. |
| Startup stops working after moving the app | Toggle Windows startup off and on in Settings to update the saved path. |

Claude successful lookups have a **two-minute minimum**. After HTTP 429, VibeMeters waits **5 → 10 → 20 → 30 → 60 minutes**, or longer if requested by the server. Manual refresh does not bypass this wait. Gemini results are cached for at least **five minutes**, with a **ten-minute wait** after rate limiting. These are app policies, not published provider request allowances, and do not guarantee that throttling will never occur.

</details>

<details>
<summary>Privacy and compatibility</summary>

Native WPF/.NET 8, with no embedded browser or continuous animation. Codex and Claude use in-process HTTP; Gemini uses a short-lived hidden Node process. The package includes .NET and is not code-signed.

- Codex reads `%USERPROFILE%\.codex\auth.json` (`CODEX_HOME` supported); Claude reads `%USERPROFILE%\.claude\.credentials.json` (`CLAUDE_CONFIG_DIR` supported). VibeMeters does not persist or refresh these tokens. Credential-manager-only Codex/Claude logins are currently unsupported.
- Gemini bundles official `@google/gemini-cli-core` **0.65.0**. Its normal authentication flow may store and refresh its own login cache. Raw tokens are not forwarded to the .NET app's settings or quota output.
- Preferences live in `%LOCALAPPDATA%\VibeMeters\settings.json`. Optional startup registration uses the current user's Windows Run key. Credentials are not included in source or release packages.
- Codex and Claude use provider account endpoints; Gemini uses an internal interface of the official CLI core. These integrations may change or be restricted by provider policies. VibeMeters is an independent project, unaffiliated with OpenAI, Anthropic, or Google.

See [implementation research](RESEARCH.md) and [verification notes](VERIFICATION.md) for integration details and the scope of testing. No specific memory or CPU benchmark is claimed.

</details>

<details>
<summary>Build and verification</summary>

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

To check the shown native window with isolated demo data and save rendered captures:

```powershell
dotnet build -c Release
dotnet .\bin\Release\net8.0-windows\VibeMeters.dll --smoke-test
```

Results and PNGs are written to the build folder's `artifacts/` directory. The latest run passed **97 regression checks and 197 window/layout checks** at 125% display scaling; see [scope and limitations](VERIFICATION.md).

`VibeMeters.exe --demo` previews sample data without accessing provider accounts. When [reporting a bug](https://github.com/Nectarine-Peach/VibeMeters/issues), include the version, Windows display scaling, window size, provider, and visible error. **Never attach credential files or access tokens.**

</details>

[Report a bug](https://github.com/Nectarine-Peach/VibeMeters/issues) · [Verification](VERIFICATION.md) · [Integration notes](RESEARCH.md)
