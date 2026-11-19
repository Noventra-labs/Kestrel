<p align="center">
  <img src="assets/kestrel-banner.jpg" alt="Kestrel — Fast, keyboard-driven command bar for Windows" width="100%" />
</p>

<p align="center">
  <strong>Native command bar and productivity omnibar for Windows.</strong><br />
  App and inline file search, clipboard history, Memory Reduct engine, native multi-provider AI chat, and developer utilities in a .NET 10 WPF and Win32/DWM shell.
</p>

<p align="center">
  <a href="https://github.com/Noventra-labs/Kestrel/releases/tag/v2.4.2"><img src="https://img.shields.io/badge/Release-v2.4.2-ff4444?style=flat-square" alt="Version 2.4.2" /></a>
  <img src="https://img.shields.io/badge/License-MIT-10b981?style=flat-square" alt="MIT License" />
  <img src="https://img.shields.io/badge/Platform-Windows%2011%20%7C%20Windows%2010%20(x64)-0078d4?style=flat-square" alt="Windows 11 / 10" />
  <img src="https://img.shields.io/badge/Telemetry-Zero-10b981?style=flat-square" alt="Zero Telemetry" />
</p>

---

## Overview

Kestrel is a keyboard-driven desktop command bar built for Windows 11 and Windows 10.

Pressing the <kbd>Win</kbd> key summons an omnibar for launching applications, searching local files, launching incognito browser sessions, reclaiming background working-set memory, browsing clipboard history, capturing screen text via OCR, using developer utilities, and opening a native streaming AI Assistant.

<p align="center">
  <img src="assets/kestrel-command-bar.png" alt="Kestrel Command Bar Interface" width="680" />
</p>

---

## Download

| Edition | Package Size | Target Environment | Direct Download |
| :--- | :--- | :--- | :--- |
| **Windows Installer** (`Kestrel-Setup.exe`) | **~167.1 MB** | Recommended for primary workstations. Provides a guided setup wizard, Start Menu and Desktop shortcuts, optional startup on boot, and Windows Add/Remove Programs registration. | [**Download Installer (.exe)**](https://github.com/Noventra-labs/Kestrel/releases/download/v2.4.2/Kestrel-Setup.exe) |
| **Portable Executable** (`Kestrel.exe`) | **~95.1 MB** | Self-contained single binary. Runs directly from any directory or removable drive and supports in-app self-updates. | [**Download Portable (.exe)**](https://github.com/Noventra-labs/Kestrel/releases/download/v2.4.2/Kestrel.exe) |

### System Requirements
- **Operating System:** Windows 11 (Build 22621+ for DWM Acrylic backdrop and rounded window corners) or Windows 10 (build 19041+ / version 2004+) 64-bit (`x64`).
- **Dependencies:** Self-contained `.NET 10` executable. No external runtime or WebView2 installation is required.
- **Permissions:** Runs under standard user rights for solo <kbd>Win</kbd> key activation, app/file search, clipboard history, AI Assistant, and utilities. Administrator privileges are only required for opt-in NT kernel standby/file-cache purges, `hosts` editing, running apps elevated (<kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Enter</kbd>), or self-updating inside a read-only installation folder.

---

## Getting Started

1. **Launch:** Run `Kestrel-Setup.exe` or execute the standalone `Kestrel.exe`.
2. **Summon:** Press <kbd>Win</kbd> (single tap) from any application.
3. **Execute:**
   - Type an application or file name (or prefix with `file <query>` / `f <query>`) and press <kbd>Enter</kbd> to open.
   - Type `incognito` (or `incognito <url or search>`) to launch a private browser window.
   - Type `ram` to view live RAM telemetry and open Memory Reduct Studio (<kbd>Ctrl</kbd> + <kbd>M</kbd>).
   - Type `clip` or press <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>V</kbd> to browse and paste clipboard history entries.
   - Press <kbd>Tab</kbd> or type `ai <prompt>` to open the native AI Assistant overlay.
4. **Dismiss:** Press <kbd>Esc</kbd> to clear the query or dismiss the command bar.

---

### Instant Application and Inline File Search
- **Indexed App & File Search:** Indexes Start Menu, Desktop, and UWP apps alongside local files across `Desktop`, `Downloads`, `Documents`, and `%USERPROFILE%\Projects` (up to depth 4, max 3,500 files).
- **Inline File Results:** Queries of 2+ characters in `All` mode automatically surface up to 6 local file matches inline; explicit `file`, `files`, `find`, or `f ` prefixes prioritize up to 12 file matches at the top of results and offer a quick jump to dedicated File Search (<kbd>Ctrl</kbd> + <kbd>F</kbd>).
- **Fuzzy Matching & Optional Web Fallbacks:** Matches partial names and abbreviations (e.g. `ps` for PowerShell, `term` for Windows Terminal), with optional Google, YouTube, and GitHub web search fallbacks configurable in Settings -> Extensions.

### Incognito / Private Browser Launch
- **Browser Auto-Detection:** Detects installed browsers (Zen Browser, Google Chrome, Microsoft Edge, Brave, Firefox, Vivaldi, Opera) and prioritizes your default Windows HTTP browser.
- **Quick Private Navigation:** Enter `incognito`, `inprivate`, `private`, or `incog` — optionally followed by a URL, domain, or search query (e.g. `incognito github.com`) — to open a private browsing window directly.

### In-App Auto-Updater (`Software Updates`)
- **Automatic & On-Demand Checks:** Checks GitHub Releases for newer versions on startup (with a 4-hour cache) and manually under Settings -> General -> `Software Updates` or via the footer update badge.
- **Verified In-App Replacement:** When running `Kestrel.exe` directly, downloads the new `Kestrel.exe` with live progress and cancel support, verifies its SHA-256 checksum against release metadata or `checksums.txt`, performs a backup-protected swap (prompting UAC only when the target folder is read-only), updates `DisplayVersion`, and relaunches cleanly. Falls back to opening the release page in your browser if running from another host or on error.

### Memory Reduct Engine
- **Smart Reclaim:** Trims working sets of inactive background processes (>= 25 MB) without requiring UAC while protecting the foreground window and critical Windows system processes.
- **Opt-In Kernel Standby & Cache Purge:** Invokes `NtSetSystemInformation` (with UAC elevation) inside Memory Reduct Studio (<kbd>Ctrl</kbd> + <kbd>M</kbd>) when explicitly triggered.
- **Auto Memory Reduct:** Optional sustained-pressure monitor in Extensions that runs Smart Reclaim only after 30+ seconds of sustained high RAM pressure with cooldown protection.

### Clipboard History
- **Event-Driven Capture:** Uses `WM_CLIPBOARDUPDATE` to capture copied plain-text entries (up to 60 items, 64 KB cap per entry, with pin support).
- **Local JSON Storage:** Stored locally in `%APPDATA%\Kestrel\clipboard_history.json` for instant search and plain-text pasting via <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>V</kbd>.

### Native Streaming AI Assistant
- **WebView-Free Native UI:** Streams responses directly in the built-in AI Assistant overlay (<kbd>Tab</kbd> or `ai <prompt>`) with no embedded WebView2 runtime.
- **Multi-Provider Support:** Supports **Google Gemini**, **OpenAI**, **Groq**, and **Anthropic Claude**, plus clipboard commands (`Fix Spelling & Grammar`, `Summarize Clipboard Text`, `Explain Code in Clipboard`).

### Screen OCR and Color Eyedropper
- **Text Extractor (OCR):** Uses Windows WinRT OCR to select any screen rectangle and copy text from images, videos, or dialogs.
- **Color Eyedropper:** Pixel-level screen sampler with a magnification loupe that copies values in **HEX** (`#FF6363`) or **RGB** (`rgb(255, 99, 99)`).

### Developer and PowerToys Utilities
- **Generators:** `GUID` / `UUID` v4, `NanoID`, `ULID`, and cryptographic password generation.
- **Encoders & Decoders:** `Base64` encode/decode, `URL` encode/decode, tracking-parameter URL cleaner, and offline `JWT` Base64Url claim decoder (explicitly marked unverified).
- **Hashes & Converters:** `MD5`, `SHA-1`, and `SHA-256` digests; inline `#RGB`/`#RRGGBB`, `rgb()`, and `hsl()` color converter; `epoch` / `unix` timestamp reader and Unix-to-local/UTC converter; clipboard `JSON` pretty-print and minify.
- **System & Network Tools:** `Port Inspector` (<kbd>Ctrl</kbd> + <kbd>P</kbd>) via native `iphlpapi.dll`, countdown `timer`, `Always on Top`, `Keep Awake`, 15 multi-monitor window snap layouts, `Temp Cleanup` preview/execution, and `Unresponsive Apps` scanner.

---

## Keyboard Shortcuts

Category filter pills (`All`, `Apps`, `Commands`, `Windows`, `System`) can be clicked directly below the search bar.

| Key Combination | Action |
| :--- | :--- |
| <kbd>Win</kbd> (solo tap) | Summon or dismiss the Kestrel command bar |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Navigate through search results or overlays |
| <kbd>Enter</kbd> | Execute selected command, launch application, or copy inline result |
| <kbd>Shift</kbd> + <kbd>Enter</kbd> | Paste inline Calculator result directly into the previous window |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Enter</kbd> | Launch selected application or terminal **as Administrator** |
| <kbd>Tab</kbd> | Open the native AI Assistant overlay (sending current query if non-empty) |
| <kbd>Ctrl</kbd> + <kbd>K</kbd> | Open the contextual Actions menu |
| <kbd>Ctrl</kbd> + <kbd>F</kbd> | Switch to dedicated File Search mode |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>V</kbd> | Open Clipboard History |
| <kbd>Ctrl</kbd> + <kbd>M</kbd> | Open Memory Reduct Studio |
| <kbd>Ctrl</kbd> + <kbd>P</kbd> | Open TCP/UDP Port Inspector |
| <kbd>Ctrl</kbd> + <kbd>R</kbd> | Reveal selected application or file in File Explorer |
| <kbd>Ctrl</kbd> + <kbd>,</kbd> | Open Kestrel Preferences |
| <kbd>Esc</kbd> | Clear query, return to main search, or dismiss Kestrel |

---

## Configuration and Settings

Open Preferences from the tray icon context menu, the footer gear button, typing `settings`, or pressing <kbd>Ctrl</kbd> + <kbd>,</kbd>. Settings are organized into five tabs:

- **General:** Toggle solo <kbd>Win</kbd> key activation, `Launch at Login`, `Dismiss on Outside Click`, `HUD Notifications`, `Low-Memory Background Mode`, and manage `Software Updates`.
- **Appearance:** Adjust `Background Opacity` (`25%` Clear Glass to `100%` Solid Dark, default `40%`) on the DWM Acrylic backdrop and choose a `Theme Accent Color` from 7 presets (`Coral`, `Blue`, `Emerald`, `Violet`, `Amber`, `Rose`, `Cyan`) or any custom `#RRGGBB` hex value.
- **Extensions:** Enable or disable AI, File Search, Clipboard History, Window Management, Calculator & Converter, Process Manager, System Commands, Web Search Fallbacks, Memory Reduct Engine, Auto Memory Reduct, and PowerToys & Developer Utilities.
- **AI:** Select the active provider (`Gemini`, `OpenAI`, `Groq`, `Claude`), save API keys locally in `%APPDATA%\Kestrel\settings.json` (or via environment variables) with optional `Sync Vault`, set a custom Model ID, and configure/test the Cloud Backend Endpoint.
- **Storage:** Inspect app/file/clipboard/frecency index counts and clear or rebuild local caches.

---

## Build from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows x64.

```powershell
# Portable Kestrel.exe
dotnet publish Kestrel/Kestrel.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o Kestrel/publish

# Setup installer (embeds Kestrel/publish/Kestrel.exe)
dotnet publish KestrelInstaller/KestrelInstaller.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o KestrelInstaller/publish

# Tests
dotnet build Kestrel.Tests/Kestrel.Tests.csproj
dotnet exec Kestrel.Tests/bin/Debug/net10.0-windows/Kestrel.Tests.dll
```

Pushing a `v*` tag triggers [`.github/workflows/release.yml`](.github/workflows/release.yml) to publish `Kestrel.exe`, `Kestrel-Setup.exe`, and `checksums.txt`.

---

## Frequently Asked Questions

<details>
<summary><strong>Windows SmartScreen displays "Windows protected your PC". How should this be handled?</strong></summary>

Kestrel is open-source software distributed without an enterprise EV code-signing certificate. Consequently, Windows SmartScreen may present an informational notice on initial launch. Select **More info** followed by **Run anyway**. Source code and reproducible build instructions are accessible for inspection within the repository.
</details>

<details>
<summary><strong>When are Administrator privileges needed?</strong></summary>

Core search, solo <kbd>Win</kbd> key summoning (for standard-privilege foreground windows), clipboard history, AI Assistant, and utilities run without elevation. Elevated permissions are only needed to:
1. Run opt-in NT kernel standby and file-cache purges via `NtSetSystemInformation`.
2. Intercept the <kbd>Win</kbd> key when an elevated window holds focus (Windows UIPI restriction) or launch apps elevated via <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Enter</kbd>.
3. Edit `hosts` or replace `Kestrel.exe` inside a read-only directory during an in-app update.
</details>

<details>
<summary><strong>How does Kestrel behave when idle?</strong></summary>

Kestrel remains resident in the Windows System Tray notification area. When **Low-Memory Background Mode** is enabled in General settings, Kestrel trims its own working set when hidden to the tray.
</details>

<details>
<summary><strong>How do I exit or uninstall Kestrel?</strong></summary>

- **Exit:** Right-click the Kestrel system tray icon and select **Exit Kestrel**.
- **Uninstall:** For installer deployments, navigate to **Windows Settings > Apps > Installed apps**, locate **Kestrel**, and select **Uninstall**. For portable deployments, delete the `Kestrel.exe` binary.
</details>

---

## Privacy and Network Boundaries

- **Zero Telemetry:** No analytics or tracking beacons are collected.
- **Local Execution:** App indexing, file indexing, clipboard history, memory reduction, and inline developer tools run locally.
- **Network Activity:** Outbound requests occur only for:
  1. Checking GitHub Releases (`api.github.com`) for updates on startup (4-hour cache) or when checking manually, and downloading `Kestrel.exe` / `checksums.txt` when you initiate an update.
  2. Sending prompts to your selected AI provider or configured Cloud Backend Endpoint (and optional Vault key sync) when you invoke AI features.
  3. Opening Google, YouTube, or GitHub searches in your browser when you select a web fallback or run an incognito search query.

---

## License and Credits

Developed and maintained by [**Noventra-labs**](https://github.com/Noventra-labs).  
Distributed under the **MIT License**.

---

**Kestrel — Fast, keyboard-driven command bar for Windows.**
