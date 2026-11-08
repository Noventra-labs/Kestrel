# Beam for Windows (v2.3.0)
**Published by Noventra Labs**

Beam is a native Windows 11 command bar, launcher, window manager, clipboard vault, local file indexer, and embedded Google Gemini AI suite engineered with .NET 10 WPF, Win32 DWM Acrylic/Mica, and a cloud-ready FastAPI + Supabase backend.

---

## 📁 Clean Project Structure

```text
Raycast/
├── Beam-Setup.exe          # Standalone Windows Setup Wizard & Uninstaller (Noventra Labs)
├── Beam.exe                # Portable Single-File Executable
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

## 🚀 Installing Beam on Windows

Run **[`Beam-Setup.exe`](./Beam-Setup.exe)** to launch the **Noventra Labs Setup Wizard**:
1. Installs `Beam.exe` and `Beam-Uninstall.exe` to `%LOCALAPPDATA%\Programs\Beam`.
2. Creates **Start Menu** (`Programs\Noventra Labs\Beam.lnk`) and **Desktop** shortcuts.
3. Configures **Launch at Windows Login** (`HKCU\...\Run`).
4. Registers **Beam** (Publisher: **Noventra Labs**) in **Windows Settings → Apps → Installed Apps**.

---

## 🔐 Supabase Encrypted API Key Vault & Backend Deployment

The `beam-backend` service stores your LLM provider API keys (`gemini`, `openai`, `anthropic`, `groq`) **encrypted at rest** inside your Supabase database (`public.beam_api_keys`). Even if your Cloud Run container restarts or scales to zero, your API keys remain securely persisted in Supabase.

### Live Google Cloud Run Service
- **Service URL**: `https://beam-cloud-ai-77889729488.asia-south1.run.app`
- **Health Check**: `https://beam-cloud-ai-77889729488.asia-south1.run.app/health`
- **Region**: `asia-south1` (Mumbai)
- **Status**: Live, autoscaling (0 to 5 instances), connected to Supabase Vault

### Step 1: Supabase Table
The table `public.beam_api_keys` is configured on Supabase project `idqcqbkleykfwyqcwmkd`.

### Step 2: Save Keys Directly from Beam Settings (`Ctrl + ,`)
1. Open Beam (`Win` or `Alt + Space`) and press **`Ctrl + ,`** to open **Preferences**.
2. Go to **`✦ Gemini & Cloud AI`**.
3. Set your **Cloud AI Backend URL** (e.g., `http://localhost:8080` or your Cloud Run URL) and click **Test Connection**.
4. Paste your API key and click **`☁ Save to Supabase Vault`** — Beam encrypts and stores the key in Supabase immediately.

