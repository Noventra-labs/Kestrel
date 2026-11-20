# Changelog

All notable changes to Kestrel are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [2.4.3] - 2026-11-20

### Security
- **DPAPI Credential Encryption**: Migrated local secrets (such as custom AI provider keys) in `SettingsService` from plaintext storage to Windows Data Protection API (DPAPI) via `ProtectedData.Protect` (`DataProtectionScope.CurrentUser`), with transparent backwards-compatible migration for legacy configurations.
- **Fail-Closed Updater**:
  - Enforced strict SHA-256 cryptographic verification of all downloaded update binaries against manifest checksums prior to execution. Missing or mismatching hashes immediately abort installation and delete staged files.
  - Hardened HTTP client against cross-host and insecure scheme redirects (rejecting HTTP downgrades and untrusted redirect targets).
  - Enforced atomic staging in protected `%LOCALAPPDATA%\Kestrel\Updates` directories.
- **Process & Directory Isolation**:
  - Bound process termination routines against critical system PIDs (PID 0, system idle, session manager) and Kestrel's own PID to prevent denial of service or self-termination races.
  - Added junction and reparse point checks during temp directory cleanup to prevent symlink traversal attacks.
- **Parser & Tool Hardening**:
  - Restricted inline calculator and developer expression parsers with strict recursion depth bounds and input length caps to prevent stack exhaustion.
  - Sanitized UWP package family protocol URIs and launch parameters before dispatching to `ShellExecute`.
- **Automated Verification**:
  - Added unit and integration security test suites covering DPAPI encryption, hash verification, redirect policies, and process guardrails (153/153 checks passing).

---

## [2.4.2] - 2026-11-20

### Fixed
- **Dynamic Version Derivation**: Updater now dynamically resolves current application version from assembly metadata (`InformationalVersion` / `AssemblyVersion`), eliminating hardcoded version discrepancies across builds.
- **Progress UI Simplification**: Removed redundant percentage pill indicator in the update progress dialog for a cleaner minimalist presentation.

### Performance
- **CI Build Pipeline**: Optimized release build scripts and documented local source build instructions.

---

## [2.4.1] - 2026-11-19

### Added
- **UI Micro-Animations**: Introduced smooth micro-transitions and fluid easing curves for item selection, search result rendering, and window summon states.

### Changed
- Synchronized version metadata across application client, installer, test harness, and build scripts.

---

## [2.4.0] - 2026-11-19

### Added
- **Complete Rebranding to Kestrel**: Renamed product from Beam to Kestrel across the WPF desktop shell, setup wizard, benchmark suite, unit tests, and documentation.
- **In-App Auto-Updater**:
  - Integrated `UpdateService` to automatically query GitHub Releases for new updates in the background.
  - Background downloading with stage-and-execute silent installer switches (`/SILENT /NORESTART`).
- **Glassmorphism UI Redesign**:
  - Modern dark glassmorphic styling utilizing Windows DWM acrylic blur.
  - Glowing diamond brand iconography and vector-rendered Lucide icon suite.
  - Interactive appearance preferences (custom accent color blending, background opacity controls, dark/light presets).
- **Memory Reduction Engine (Mem Reduct)**:
  - NT kernel subsystem integration for live RAM optimization, working set sweeping, and standby list purging.
  - Real-time memory telemetry display in the command bar and preferences.
- **Self-Healing Installer**:
  - Enforced administrator elevation manifest (`requireAdministrator`) and runtime elevation checks.
  - Auto-diagnose and repair routines for locked files, process conflicts, and NTFS directory permissions.
  - Automatic registration in Windows Installed Apps (Add/Remove Programs) and Start Menu shortcuts.

### Removed
- **WebView2 Dependency**: Completely removed WebView2 runtime dependency, drastically reducing binary size, startup latency, and memory footprint.

---

## [2.3.1] - 2026-11-09

### Added
- **Windows Store & Packaged App Indexing**: Comprehensive indexing and search support for modern UWP and MSIX packaged applications.
- **Tokenized Search Scoring**: Keyword-aware, acronym-matching, and prefix-prioritized scoring pipeline achieving sub-5ms query response times.
- **Power Developer Tools Suite**: Integrated developer quick actions, hash calculators, base64 encoders/decoders, and process utilities.
- **Automated CI/CD**: Added GitHub Actions build and release automation workflow.

### Fixed
- **Windows 11 Start Menu Race Condition**: Eliminated the alternating Start menu popup bug on physical Win key tap via Left Control chord masking and synthetic `0xE8` unassigned keyup injection.
- **Packaged App Launch Collision**: Replaced secondary Explorer process spawning with direct protocol activation via `ShellExecute`, preventing `0xC0000142` startup collisions.
- **Uninstaller Cleanup**: Added automated file cleanup and Windows shell icon refresh upon uninstallation.

---

## [2.3.0] - 2026-11-09

### Added
- **Standalone GUI Setup Wizard**: Built `Kestrel-Setup.exe` with license agreement, customizable target directory, progress bar, and finish actions.
- **Lucide Vector Icons**: Crisp vector path rendering across all launcher items via `IconGenerator`.
- **Run as Administrator**: `Ctrl+Shift+Enter` shortcut to launch any indexed application or command with elevated privileges.
- **DWM Acrylic Blur**: Live Windows desktop acrylic glass effect with configurable accent blending.

### Fixed
- Handled instance collisions by bringing existing foreground window into focus rather than launching duplicate instances.
- Cleared legacy build artifacts from `%LOCALAPPDATA%\Programs` during setup to prevent assembly resolution errors.

---

## [2.2.5] - 2026-11-08

### Added
- **Cloud Run Backend**: High-performance FastAPI backend (`beam-backend`) hosted on Google Cloud Run providing SSE `/v1/chat/stream`.
- **Encrypted Key Vault**: Multi-provider LLM router (Gemini, OpenAI, Claude, Groq) with authenticated AES/HMAC encryption at rest in Supabase and PostgreSQL RLS policies.
- **Keepalive & Caching**: 300-second in-memory key TTL caching and SSE keepalive heartbeats to eliminate 504 gateway timeouts.

---

## [2.2.0] - 2026-11-08

### Added
- **Embedded AI Chat Interface**: Inline Google Gemini AI conversation panel directly inside the launcher command bar.
- **Streaming Client**: Asynchronous Server-Sent Events (SSE) token streaming with live markdown rendering and syntax-highlighted code blocks.
- **Multi-Turn Context**: Session-persistent conversation state and one-click copy response as Markdown.

### Fixed
- Resolved focus conflicts between the primary launcher search input and the AI prompt box.
- Fixed horizontal layout clipping on high-DPI displays for formatted code blocks.

---

## [2.1.0] - 2026-11-08

### Added
- **Windows Key Suppression**: Low-level `WH_KEYBOARD_LL` hook capturing physical Windows key presses and defusing the Start menu using an injected dummy `0xE8` keyup event.
- **Secondary Hotkey**: Registered `Alt + Space` global summon hotkey.
- **Preferences Center**: 4-tab configuration interface (General, Shortcuts, AI Backend, About).
- **Atomic Settings Engine**: Persistent JSON configuration with atomic temp-swap writes.
- **In-App Toast Notifications**: Native non-intrusive toast notifications for background tasks and clipboard actions.

---

## [2.0.0] - 2026-11-07

### Added
- **System Tray Daemon**: Background persistence via `NotifyIcon` with custom tray context menu and quick toggle.
- **Fast Process Killer**: Built-in process management with real-time CPU and RAM inspection, search filtering, and instant termination.
- **Local File Search**: Indexer for user Documents, Downloads, Desktop, and Pictures folders with asynchronous traversal.
- **System Commands**: Quick commands for Sleep, Lock Screen, Restart, Shutdown, Empty Recycle Bin, and Log Off.

### Fixed
- Changed window close behavior to hide to system tray instead of terminating the background process.
- Handled Win32 `Access Denied` exceptions gracefully when inspecting system-protected processes.

---

## [1.5.0] - 2026-11-07

### Added
- **DWM Window Snapping Manager**: Window layout management service with 15 snap presets (Halves, Quadrants, Vertical Thirds, Center, Maximize, Minimize).
- **Multi-Monitor Work Area Detection**: Accurate display boundary calculation via Win32 `MonitorFromWindow` and `GetMonitorInfo`.

### Fixed
- Corrected Windows 11 7-pixel invisible window frame gap using `DwmGetWindowAttribute` with `DWMWA_EXTENDED_FRAME_BOUNDS`.
- Fixed window snapping coordinate calculations across mixed-DPI multi-monitor environments.

---

## [1.2.0] - 2026-11-07

### Added
- **Zero-CPU Clipboard History**: Event-driven clipboard manager utilizing Win32 `AddClipboardFormatListener` and `WM_CLIPBOARDUPDATE` for 0% idle CPU usage.
- **Smart Inline Calculator**: Real-time expression evaluation supporting arithmetic, trigonometry, exponents, percentages, and units.
- **Direct Paste Action**: Instant paste of calculation results or clipboard items into the previously focused application.

### Fixed
- Prevented recursive clipboard loops during app copy actions by tracking SHA-256 hashes of copied contents.
- Fixed expression tokenizer parsing errors for negative numbers and deeply nested parentheses.

---

## [1.0.0] - 2026-11-06

### Added
- **Core Architecture**: Initial release of the keyboard-first command bar built on .NET 10 and WPF.
- **App Indexer**: Asynchronous indexer for Classic Win32 Start Menu shortcuts (`.lnk`, `.url`) and UWP/MSIX packages.
- **High-Performance Fuzzy Search**: Sub-millisecond search algorithm with acronym matching, prefix priority, and character jump scoring.
- **Command Shell**: Clean keyboard navigation, action dispatching, and extensible item models (`LauncherItem`, `ActionItem`).
