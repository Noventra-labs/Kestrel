# Kestrel for Windows (v2.3.0)
**Published by Noventra Labs** • [GitHub Repository](https://github.com/Noventra-labs/Kestrel)

Kestrel is a native Windows 11 command bar, launcher, window manager, clipboard vault, local file indexer, Mem Reduct system RAM cleaner, Microsoft PowerToys suite, and embedded Google Gemini AI suite engineered with .NET 10 WPF, Win32 DWM Acrylic/Mica, and a cloud-ready FastAPI + Supabase backend.

---

## 📁 Project Structure

```text
Raycast/
├── Kestrel-Setup.exe       # Standalone Windows Setup Wizard & Uninstaller (Noventra Labs)
├── Kestrel.exe             # Portable Single-File Executable
├── Beam/                   # Native .NET 10 WPF Desktop Client Source Code
├── BeamInstaller/          # Standalone Windows GUI Installer Source Code
├── beam-backend/           # Cloud Run FastAPI Backend + Encrypted Supabase API Key Vault
│   ├── main.py             # FastAPI SSE streaming & /v1/keys Supabase Vault endpoints
│   ├── supabase_schema.sql # Supabase PostgreSQL table & Row-Level Security (RLS) schema
│   ├── deploy-gcp.ps1      # Automated 1-command Google Cloud Run deployment script
│   ├── Dockerfile          # Production Python 3.12 container image
│   ├── .env.example        # Template environment configuration
│   └── services/
│       ├── llm_router.py       # Multi-provider SSE streamer (Gemini, OpenAI, Anthropic, Groq)
│       └── supabase_vault.py   # Authenticated encryption-at-rest Supabase Key Vault
└── README.md
```

---

## 🚀 Installing Kestrel on Windows

Run **[`Kestrel-Setup.exe`](./Kestrel-Setup.exe)** to launch the **Noventra Labs Setup Wizard**:
1. Installs `Kestrel.exe` and `Uninstall-Kestrel.exe` to `%LOCALAPPDATA%\Programs\Kestrel`.
2. Creates **Start Menu** (`Programs\Noventra Labs\Kestrel.lnk`) and **Desktop** shortcuts.
3. Configures **Launch at Windows Login** (`HKCU\...\Run`).
4. Registers **Kestrel** (Publisher: **Noventra Labs**) in **Windows Settings → Apps → Installed Apps**.

---

## ⚡ Supercharged Power Features

- **Sub-5ms Search Engine**: Non-blocking asynchronous query ranking, pre-computed token caching, zero-disk extension icon indexing, and recycling UI virtualization.
- **Mem Reduct Engine**: Low-level Windows NT kernel memory management (`NtSetSystemInformation` + `EmptyWorkingSet` + `AdjustTokenPrivileges`) to purge Standby memory caches, flush modified pages, and sweep working sets on demand.
- **Microsoft PowerToys Suite**:
  - **Color Picker**: 8x magnifying screen loupe with live `#HEX`, `rgb(...)`, and `hsl(...)` eyedropper.
  - **Text Extractor (Screen OCR)**: Built-in Windows 10/11 WinRT OCR to snip and extract non-selectable screen text.
  - **Awake**: Win32 `SetThreadExecutionState` keep-awake inhibitor (Indefinite, 1h, 30m, Off).
  - **Always on Top**: Pin any application window (`HWND_TOPMOST`) above all others.
  - **Developer Tools**: Inline SHA256/MD5/Base64/GUID/Epoch evaluators, DNS resolver flush, and Hosts file editor.

---

## 🔐 Supabase Encrypted API Key Vault & Backend Deployment

The `beam-backend` service stores your LLM provider API keys (`gemini`, `openai`, `anthropic`, `groq`) **encrypted at rest** inside your Supabase database (`public.beam_api_keys`).

### Live Google Cloud Run Service
- **Service URL**: `https://beam-cloud-ai-77889729488.asia-south1.run.app`
- **Health Check**: `https://beam-cloud-ai-77889729488.asia-south1.run.app/health`
- **Region**: `asia-south1` (Mumbai)
- **Status**: Live, autoscaling (0 to 5 instances), connected to Supabase Vault

### Save Keys Directly from Kestrel Settings (`Ctrl + ,`)
1. Open Kestrel (`Win` or `Alt + Space`) and press **`Ctrl + ,`** to open **Preferences**.
2. Go to **`✦ Gemini & Cloud AI`**.
3. Set your **Cloud AI Backend URL** and click **Test Connection**.
4. Paste your API key and click **`☁ Save to Supabase Vault`** — Kestrel encrypts and stores the key in Supabase immediately.
