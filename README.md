# Kestrel for Windows

**Version 2.3.1 | Developed by Noventra-Labs**

Kestrel is a fast, lightweight productivity application for Windows 11. It combines an app launcher, file search, AI assistant, clipboard manager, RAM cleaner, and useful system tools into one convenient command bar.

## Features

- **Instant Search** – Quickly search applications, files, and commands.
- **App Launcher** – Launch installed applications in seconds.
- **AI Assistant** – Access Google Gemini and other supported AI models.
- **Clipboard Manager** – View and reuse previously copied content.
- **RAM Cleaner** – Manage and free up system memory.
- **Window Manager** – Keep important windows always on top.
- **Color Picker** – Capture colors from anywhere on your screen.
- **Text Extractor** – Extract text from images and screenshots using OCR.
- **Keep Awake** – Prevent Windows from automatically going to sleep.
- **Developer Utilities** – Access hashing, Base64, GUID, DNS, and other tools.
- **Modern Interface** – Clean Windows 11 design with Acrylic and Mica effects.

## Installation

1. Download `Kestrel-Setup.exe`.
2. Run the installer.
3. Follow the installation instructions.
4. Launch Kestrel from your desktop or Start Menu.

The installer supports desktop shortcuts, automatic startup, and easy uninstallation.

### Portable Version

Prefer not to install? Run `Kestrel.exe` directly as a portable application.

## Project Structure

```text
Raycast/
├── Kestrel-Setup.exe    # Windows Installer
├── Kestrel.exe          # Portable Application
├── Beam/                # Windows Desktop Application
├── BeamInstaller/       # Installer Source Code
├── beam-backend/        # FastAPI Cloud Backend
└── README.md
```

## Tech Stack

| Component | Technology |
|---|---|
| Desktop Application | .NET 10, WPF |
| Windows Integration | Win32 APIs |
| AI Integration | Gemini, OpenAI, Anthropic, Groq |
| Backend | Python, FastAPI |
| Database | Supabase PostgreSQL |
| Cloud Hosting | Google Cloud Run |
| Interface | Windows 11 Acrylic / Mica |

## About

Kestrel is developed and maintained by **Noventra-Labs**, with a focus on making everyday Windows workflows faster and more convenient.

**GitHub:** https://github.com/Noventra-labs/Kestrel

---

**Kestrel — One command bar. All your tools.**