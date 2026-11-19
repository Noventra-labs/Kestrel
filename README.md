<p align="center">
  <img src="assets/kestrel-banner.jpg" alt="Kestrel — One command bar. All your tools." width="100%" />
</p>

<p align="center">
  <strong>Native command bar and productivity omnibar for Windows.</strong><br />
  Instant app search, clipboard history, kernel-level RAM cleaner, AI orchestration, and desktop utilities in a native Win32/DWM shell.
</p>

<p align="center">
  <a href="https://github.com/Noventra-Labs/Kestrel/releases/tag/v2.3.1"><img src="https://img.shields.io/badge/Release-v2.3.1-ff4444?style=flat-square" alt="Version 2.3.1" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-10b981?style=flat-square" alt="MIT License" /></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2011%20%7C%20Windows%2010%20(x64)-0078d4?style=flat-square" alt="Windows 11 / 10" />
  <img src="https://img.shields.io/badge/Telemetry-Zero-10b981?style=flat-square" alt="Zero Telemetry" />
  <img src="https://img.shields.io/badge/Idle%20RAM-48%20MB-blue?style=flat-square" alt="48 MB Idle RAM" />
</p>

---

## Overview

Kestrel is an ultra-fast, native command bar engineered for Windows 11 and Windows 10. 

Designed for keyboard-driven navigation, Kestrel eliminates the latency, web advertisements, and inconsistent indexing of the standard Windows Start Menu. A single keystroke summons an omnibar capable of launching applications, recovering gigabytes of cached standby memory, retrieving past clipboard entries, capturing screen text via optical character recognition, and orchestrating desktop AI models.

<p align="center">
  <img src="assets/kestrel-command-bar.png" alt="Kestrel Command Bar Interface" width="680" />
</p>

---

## Download

| Edition | Package Size | Target Environment | Direct Download |
| :--- | :--- | :--- | :--- |
| **Windows Installer** (`Kestrel-Setup.exe`) | **~27.8 MB** | Recommended for primary workstations. Provides a guided setup wizard, Start Menu and Desktop shortcuts, optional startup on boot, and clean Windows Add/Remove Programs registration. | [**Download Installer (.exe)**](https://github.com/Noventra-Labs/Kestrel/releases/download/v2.3.1/Kestrel-Setup.exe) |
| **Portable Executable** (`Kestrel.exe`) | **~26.6 MB** | Self-contained single binary. Requires no installation or registry modifications. Runs directly from any directory or removable drive. | [**Download Portable (.exe)**](https://github.com/Noventra-Labs/Kestrel/releases/download/v2.3.1/Kestrel.exe) |

### System Requirements
- **Operating System:** Windows 11 (21H2, 22H2, 23H2, 24H2) or Windows 10 (22H2+) 64-bit (`x64`).
- **Dependencies:** Self-contained executable. No external runtimes or framework installations are required.
- **Permissions:** Runs under standard user rights for application launching and utilities. Administrator privileges are recommended for NT kernel memory list purging and single Windows Key defusal.

---

## Getting Started

1. **Launch:** Run `Kestrel-Setup.exe` or execute the portable `Kestrel.exe`.
2. **Summon:** Press <kbd>Win</kbd> (single tap) or <kbd>Alt</kbd> + <kbd>Space</kbd> from any application.
3. **Execute:**
   - Type an application or tool name (e.g. `code`, `terminal`, `calc`) and press <kbd>Enter</kbd> to launch.
   - Type `ram` and press <kbd>Enter</kbd> to purge kernel standby lists and reclaim locked memory.
   - Type `clip` to browse and restore encrypted clipboard history entries.
   - Type `ai` to query language models directly from the prompt.
4. **Dismiss:** Press <kbd>Esc</kbd> to dismiss the command bar.

---

## Features

<p align="center">
  <img src="assets/kestrel-preview.png" alt="Kestrel Capabilities and Overview" width="480" />
</p>

### Instant Application and File Search
- **Low-Latency Indexing:** Queries execute in under 5 milliseconds with direct memory traversal.
- **Fuzzy Search Algorithm:** Tolerates imprecise typing and abbreviations (e.g. `ps` for PowerShell, `term` for Windows Terminal).
- **Ad-Free Execution:** Traverses purely local binaries, Control Panel applets, and system utilities without web searches or sponsored suggestions.

### Kernel Standby RAM Purge (Memory Reduct)
- **Direct NT Syscalls:** Invokes `NtSetSystemInformation` to clear system working sets and purge standby page lists on demand.
- **Immediate Memory Recovery:** Reclaims gigabytes of locked physical memory without requiring a system restart.
- **Live Memory Telemetry:** Displays real-time physical memory allocation (Active, Standby, Free) directly within the interface.

### Encrypted Clipboard History Vault
- **Automatic Caching:** Captures copied text strings, hyperlinks, code snippets, and color codes.
- **Local Cryptographic Protection:** Encrypted at rest via the Windows Data Protection API (DPAPI) tied to the current user token.
- **Flexible Pasting:** Allows restoration in original formatted state or sanitized plain text.

### Desktop AI Assistant
- **Integrated Query Bar:** Supports Google Gemini, Anthropic Claude, and OpenAI GPT models directly from the command palette.
- **Workspace Continuity:** Draft shell scripts, summarize technical documentation, or format structured data without navigating away from the active task.

### Screen OCR and Precision Color Eyedropper
- **Text Extractor (OCR):** Select any screen rectangle to extract unselectable text from video streams, image assets, or system dialogs.
- **Color Eyedropper:** Pixel-accurate screen sampler with interactive magnification loupe. Exports values in HEX (`#ff6363`), RGB, and OKLCH formats.

### Developer Utilities
- **UUID / GUID Generation:** Instantly outputs valid RFC 4122 v4 identifiers.
- **Base64 Processing:** Fast encoding and decoding of UTF-8 strings.
- **JSON Validation and Prettification:** Formats unformatted payloads and flags syntax errors.
- **Unix Epoch Conversion:** Real-time bi-directional conversion between Unix timestamps and ISO 8601 strings.
- **Cryptographic Hashes:** Generates MD5, SHA-1, SHA-256, and SHA-512 digests.

### Window Management and System State
- **Always on Top:** Toggles the foreground window to remain pinned above other visual surfaces.
- **Keep Awake:** Prevents display sleep and system idle states during long-running builds, file transfers, or presentations.

---

## Keyboard Shortcuts

| Key Combination | Action |
| :--- | :--- |
| <kbd>Win</kbd> or <kbd>Alt</kbd> + <kbd>Space</kbd> | Summon or dismiss the Kestrel command bar |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Navigate vertically through search results |
| <kbd>Enter</kbd> | Execute selected command, launch application, or copy result |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Enter</kbd> | Launch selected application or terminal **as Administrator** |
| <kbd>Esc</kbd> | Clear current query, or dismiss Kestrel if query input is empty |
| <kbd>Tab</kbd> / <kbd>Shift</kbd> + <kbd>Tab</kbd> | Cycle category filters (`All`, `Apps`, `Utilities`, `Clipboard`, `AI`, `Developer`) |
| <kbd>Ctrl</kbd> + <kbd>,</kbd> | Open Kestrel Preferences and Configuration |

---

## Configuration and Settings

Configuration options can be adjusted via the tray icon context menu or by entering `settings` into the omnibar:

- **Summon Hotkey:** Toggle between the dedicated single <kbd>Win</kbd> key, <kbd>Alt</kbd> + <kbd>Space</kbd>, <kbd>Ctrl</kbd> + <kbd>Space</kbd>, or custom key combinations.
- **Visual Backdrop:** Select between Windows 11 Acrylic frosted glass, Mica material, or solid dark mode.
- **Startup Integration:** Enable or disable automatic launch on Windows startup.
- **AI Credentials:** Securely store personal API keys for supported AI providers within the Windows Credential Manager.

---

## Comparison

| Metric / Capability | Kestrel | Windows Start Menu | PowerToys Run | Flow Launcher |
| :--- | :---: | :---: | :---: | :---: |
| **Search Response Latency** | **< 5 ms** | 400 ms – 1200 ms | ~280 ms | ~200 ms |
| **Idle Memory Footprint** | **~48 MB** | System Default | ~320 MB | ~210 MB |
| **Kernel RAM Cleaner** | **Built-in** | No | No | No |
| **Encrypted Clipboard Vault** | **Built-in (DPAPI)** | Basic (<kbd>Win</kbd>+<kbd>V</kbd>) | No | Plugin Dependent |
| **Web Advertisements** | **Zero** | Yes (Bing / MSN) | No | No |
| **Screen OCR and Eyedropper** | **Built-in** | No | Modular Utility | No |
| **Native DWM Acrylic Composition** | **Built-in** | System Default | No | No |
| **Telemetry and Tracking** | **Zero** | Active | Optional | Optional |

---

## Frequently Asked Questions

<details>
<summary><strong>Windows SmartScreen displays "Windows protected your PC". How should this be handled?</strong></summary>

Kestrel is open-source software distributed without an enterprise EV code-signing certificate. Consequently, Windows SmartScreen may present an informational notice on initial launch. Select **More info** followed by **Run anyway**. Source code and reproducible build instructions are accessible for inspection within the repository.
</details>

<details>
<summary><strong>Why are Administrator privileges recommended?</strong></summary>

While core search and productivity functions operate normally under standard privileges, elevated permissions are required to:
1. Purge kernel standby lists via the NT system call `NtSetSystemInformation`.
2. Cleanly suppress the Windows 11 Start Menu when intercepting the standalone <kbd>Win</kbd> key.
3. Execute external applications with elevated rights via <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Enter</kbd>.
</details>

<details>
<summary><strong>How does Kestrel behave when idle?</strong></summary>

Kestrel resides in the Windows System Tray notification area. It maintains a footprint of approximately 48 MB of RAM and consumes 0.0% CPU when not actively processing keystrokes.
</details>

<details>
<summary><strong>How do I exit or uninstall Kestrel?</strong></summary>

- **Exit:** Right-click the Kestrel system tray icon and select **Exit Kestrel**.
- **Uninstall:** For installer deployments, navigate to **Windows Settings > Apps > Installed apps**, locate **Kestrel**, and select **Uninstall**. For portable deployments, delete the `Kestrel.exe` binary.
</details>

---

## Privacy and Offline Architecture

- **Zero Telemetry:** No user analytics, session metrics, or diagnostic pings are collected or transmitted.
- **Local Operation:** Search indexing, clipboard records, memory purging, and utility calculations run locally without network access.
- **Explicit Network Boundaries:** Network requests occur exclusively when the user configures an AI provider API key and explicitly submits a query.

---

## License and Credits

Developed and maintained by [**Noventra-Labs**](https://github.com/Noventra-Labs).  
Distributed under the **[MIT License](LICENSE)**.

---

**Kestrel — One command bar. All your tools.**
