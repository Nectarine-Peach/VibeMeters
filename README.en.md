# VibeMeters

[한국어](README.md) · **English**

Keep your **Codex, Claude, Gemini, and Grok quotas** visible in a small Windows tray window.

**Windows 10/11 x64 · Always on top · Dark / light / system theme**

**Personal Gemini via Antigravity and Grok Build subscription quotas are displayed inside the app.**

[Download ZIP for Windows](https://github.com/Nectarine-Peach/VibeMeters/releases/latest/download/VibeMeters-win-x64.zip) · [Release notes](https://github.com/Nectarine-Peach/VibeMeters/releases/latest)

<img src="docs/screenshots/window-mini-dark.png" alt="Mini window showing Codex, Claude, Gemini Antigravity and Grok Build" width="274">

*Actual WPF rendering with synthetic data for all four services. Gemini shows a personal Antigravity example; Grok shows Build-only quota. These are not live account values.*

## Supported accounts and quota types

Alongside personal subscription quotas, VibeMeters displays **Codex Business individual limits and Claude Enterprise monthly additional usage**.

| Service / account | Quota information shown |
| --- | --- |
| **Codex personal subscription** | Supplied subscription windows, such as five-hour and weekly limits |
| **Codex Business** | **Individual** remaining/used percentage and the supplied reset time |
| **Claude personal subscription** | Five-hour and weekly quotas, plus enabled monthly additional usage |
| **Claude Enterprise** | **Monthly additional usage** percentage, separate from personal subscription windows |
| **Personal Gemini / Antigravity** | Shared Gemini five-hour, weekly and supplied Argon quotas, separate from Gemini web/app limits |
| **Business Gemini / Code Assist** | Model quota buckets returned for supported accounts and projects |
| **Grok Build / X subscription** | Build-only or shared subscription percentages and reset information supplied by the official CLI |

Codex Business uses the supplied `spend_control.individual_limit`; Claude Enterprise uses enabled `extra_usage.utilization`. When a recognized connected-account response supplies no percentage, the app reports that state without inventing a value. Service permissions, SSO and corporate network policies can restrict access; support does not guarantee every organization configuration. Actual account reauthentication and SSO approval were not separately exercised for v1.4.4. See [account details](docs/account-usage.md) and [verification scope](VERIFICATION.md).

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
| **Claude** | Choose **Claude 로그인** in Settings. If the CLI is missing, choose official installation before sign-in. | Subscription windows and enabled monthly additional usage, including Enterprise. |
| **Gemini** | Personal accounts: sign in to installed Antigravity CLI 1.4.7 or newer. Business accounts: existing Code Assist authentication/project. | Antigravity shared Gemini quotas or business Code Assist quota buckets and reset information. |
| **Grok** | Install official Grok Build CLI and use **Grok 로그인**, or run `grok login --oauth`. | Included subscription percentages/reset times supplied by the official CLI. Build-only and shared scope remain distinct; API costs and extra credits are excluded. |

Codex and Claude usage polling works without the CLI when a valid credential file already exists. If a CLI is missing when you click sign-in, the app offers installation or cancellation. Only your explicit install choice downloads the official user-scoped installer, with progress, retry and cancellation. It detects the installed CLI before continuing to browser sign-in; no administrator elevation is requested. See [CLI installation scope](docs/cli-installation.md).

Codex and Claude login buttons run their installed official CLI in the background. Complete sign-in in the browser it opens; no terminal window is shown. The app checks the login result before refreshing usage and stops waiting after five minutes. Signing in to a provider's website or desktop app alone may not create its CLI credentials. Credential-file changes also trigger a new check. Gemini's personal-account login opens the official Antigravity window; refresh after completing sign-in or switching accounts. Gemini quotas appear **inside VibeMeters**; normal polling does not open a terminal or browser or send model-generation requests. Gemini is off by default.

This app shows quota percentages, **not API-key billing, token costs, or a guaranteed number of prompts remaining**. Missing values are shown as unavailable rather than estimated.

Grok is off by default and uses the official installed Grok Build CLI; no browser extension is required. Polling sends only ACP initialization and the billing query, with no model, session, authentication, or refresh command. The app watches only the auth file modification time; the CLI owns its credentials. Build-only quotas are visibly labeled **Build**, shared quotas **공유**. Separate Grok Bot quotas are not fabricated. See the [scope and login guide](docs/grok-usage.md).

</details>

<details>
<summary>Settings and controls</summary>

Choose any combination of Codex, Claude, Gemini and Grok, and set **remaining or used percentage per service**. Gemini and Grok are off by default. Dark, light, and system themes are available. The scroll-free window starts at 220 × 140 logical pixels; more quota rows increase the minimum height. Optional alerts notify you at 20% / 5% remaining.

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

| All four services | Four-service minimum width |
| :---: | :---: |
| <img src="docs/screenshots/window-all-services-dark.png" alt="Codex, Claude, Gemini Antigravity and Grok Build" width="360"> | <img src="docs/screenshots/window-minimum-dark.png" alt="220 by 223 logical pixels with every quota row visible" width="220"> |

### Settings

<img src="docs/screenshots/settings-dark.png" alt="Four-service visibility, display basis, theme, refresh and account settings" width="400">

These images render v1.4.5 WPF controls with synthetic data on 2026-10-26. Standard size is 360×551, mini is 274×300, and minimum width is 220×223 logical pixels with every row visible. No real account, credentials or user preferences were accessed. These are not desktop/compositor/tray screenshots or regression-test results. See the [capture method and scope](docs/screenshots/README.md).

</details>

<details>
<summary>Refresh limits and troubleshooting</summary>

Updates use polling rather than a real-time server stream. Provider cooldowns can make the effective interval longer than the selected setting.

| Situation | What to do |
| --- | --- |
| Codex or Claude is not connected | Sign in through the official CLI or Settings, then refresh. Credential-file changes trigger a new check. |
| Claude reports a temporary query limit | Wait for the retry time in the tooltip. HTTP 429 restricts the **usage lookup**; it does not by itself mean the subscription quota is exhausted. |
| A previous percentage remains visible after an error | It is the last successful value. Read the status tooltip before treating it as current. |
| Gemini has no quota information | Personal accounts require a signed-in Antigravity CLI 1.4.7 or newer; business accounts retain the bundled Code Assist runtime and project access. See [account details](docs/account-usage.md). |
| Startup stops working after moving the app | Toggle Windows startup off and on in Settings to update the saved path. |

Claude successful lookups have a **two-minute minimum**. After HTTP 429, VibeMeters waits **5 → 10 → 20 → 30 → 60 minutes**, or longer if requested by the server. Manual refresh does not bypass this wait. Gemini business results are cached for **five minutes**; Antigravity reads the current account on each refresh and drops old values on failure. Both wait **ten minutes** after rate limiting. Grok caches successful results for **two minutes** and waits **ten minutes** after rate limiting. These are app policies, not published provider request allowances.

</details>

<details>
<summary>Privacy and compatibility</summary>

Native WPF/.NET 8, with no embedded browser or continuous animation. Codex and Claude use in-process HTTP. Gemini business polling uses a short-lived hidden Node process; personal Gemini and Grok polling use their installed official CLI. The package includes .NET and is not code-signed.

- Codex reads `%USERPROFILE%\.codex\auth.json` (`CODEX_HOME` supported); Claude reads `%USERPROFILE%\.claude\.credentials.json` (`CLAUDE_CONFIG_DIR` supported). VibeMeters does not persist or refresh these tokens. Credential-manager-only Codex/Claude logins are currently unsupported.
- Gemini business accounts use bundled official `@google/gemini-cli-core` **0.65.0**. Unsupported personal accounts use the installed official Antigravity CLI's `/usage` JSON command. The card explicitly shows **Gemini · Antigravity**: these shared Gemini model quotas are separate from Gemini web/app limits. Its Login button opens the official Antigravity client. Each official client manages its own authentication; VibeMeters does not read keyring tokens or forward raw tokens to settings or quota output.
- Grok uses the installed official Build CLI. The app watches auth-file modification time without reading credentials or tokens. The package does not bundle or automatically install Grok CLI or a browser extension.
- Preferences live in `%LOCALAPPDATA%\VibeMeters\settings.json`. Optional startup registration uses the current user's Windows Run key. Credentials are not included in source or release packages.
- Codex and Claude use account endpoints, Gemini uses Code Assist internals or the official Antigravity usage command, and Grok uses the official Build CLI ACP billing contract. These integrations may change or be restricted by provider policies. VibeMeters is an independent project, unaffiliated with OpenAI, Anthropic, Google, or xAI.

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
