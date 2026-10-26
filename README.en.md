# VibeMeters

[한국어](README.md) · **English**

Keep your **Codex, Claude, and Gemini quotas** visible in a small Windows tray window.

**Windows 10/11 x64 · Always on top · Dark / light / system theme**

[Download ZIP for Windows](https://github.com/Nectarine-Peach/VibeMeters/releases/latest/download/VibeMeters-win-x64.zip) · [Release notes](https://github.com/Nectarine-Peach/VibeMeters/releases/latest)

<img src="docs/screenshots/window-mini-dark.png" alt="VibeMeters mini window" width="274">

*Native app capture with demo data.*

## Quick start

1. Extract the **entire package**, keeping its `runtime` folder, and run `VibeMeters.exe`.
2. Open **Settings (gear)**, choose your services, and sign in.
3. Resize the window and use the **pin** to keep it on top.

No installer is required. To build it yourself, expand **Build and verification** below.

## Detailed guides

<details>
<summary>Account connections</summary>

| Service | Setup | What is shown |
| --- | --- | --- |
| **Codex** | Choose **Codex 로그인** in Settings. If the CLI is missing, choose official installation before sign-in. | Supplied subscription windows and Business individual limits. |
| **Claude** | Choose **Claude 로그인** in Settings. If the CLI is missing, choose official installation before sign-in. | Subscription windows and enabled monthly additional usage. |
| **Gemini** | Enable Gemini in Settings and choose **Gemini 로그인** for official browser sign-in. A supported Google account and Code Assist project are required. | Model quota buckets and reset information returned by the official Gemini CLI core. |

Codex and Claude usage polling works without the CLI when a valid credential file already exists. If a CLI is missing when you click sign-in, the app offers installation or cancellation. Only your explicit install choice downloads the official user-scoped installer, with progress, retry and cancellation. It detects the installed CLI before continuing to browser sign-in; no administrator elevation is requested. See [CLI installation scope](docs/cli-installation.md).

Login buttons run the installed official CLI in the background. Complete sign-in in the browser it opens; no terminal window is shown. The app checks the login result before refreshing usage and stops waiting after five minutes. Signing in to a provider's website or desktop app alone may not create the CLI credentials VibeMeters needs. Credential-file changes also trigger a new check. Gemini quotas appear **inside VibeMeters**; normal polling does not open a terminal or browser or send model-generation requests. Gemini is off by default.

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

Settings save immediately. Window moves and resizing are saved automatically, with a final save on close, Windows shutdown, or sleep. Windows startup restores the saved size, position, and visible-window or tray state. Updates do not reset the window to a default size; if a previous monitor is missing, the window moves onto an available screen.

The taskbar option provides a normal app icon and minimize behavior, not a quota widget or progress overlay. The minimize button appears in sufficiently wide windows.

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

Requirements: Windows x64, the **.NET 8 SDK**, and **Node.js 24.22.0/npm**. The publish script restores locked build dependencies and bundles the official Gemini authentication/quota adapter with esbuild. Only the bundle, Node executable and notices ship; `node_modules` stays on the build machine. The Node version is pinned in `runtime/gemini-quota/package.json`.

```powershell
git clone https://github.com/Nectarine-Peach/VibeMeters.git
cd VibeMeters
dotnet build -c Release
.\scripts\publish.ps1
.\dist\VibeMeters-portable-win-x64\VibeMeters.exe
```

Output: `dist/VibeMeters-portable-win-x64/` and its ZIP. Existing outputs are never overwritten; choose a new `-OutputDirectory` for another build. Use `-Node` to select the pinned Node executable. Include the runtime folder when distributing this self-contained Windows x64 package; end users do not need .NET, Node or npm installed. Settings and CLI authentication locations are unchanged. See [packaging notes](docs/packaging.md) for boundaries and offline checks.

Run the isolated provider/parser/settings regression suite with:

```powershell
dotnet run --project tests/VibeMeters.Tests.csproj -c Release
```

To check the shown native window with isolated demo data and save rendered captures:

```powershell
dotnet build -c Release
dotnet .\bin\Release\net8.0-windows\VibeMeters.dll --smoke-test
```

Results and PNGs are written to the build folder's `artifacts/` directory. See the [verification notes](VERIFICATION.md) for results and limitations by version.

`VibeMeters.exe --demo` previews sample data without accessing provider accounts. When [reporting a bug](https://github.com/Nectarine-Peach/VibeMeters/issues), include the version, Windows display scaling, window size, provider, and visible error. **Never attach credential files or access tokens.**

</details>

[Report a bug](https://github.com/Nectarine-Peach/VibeMeters/issues) · [Verification](VERIFICATION.md) · [Integration notes](RESEARCH.md)
