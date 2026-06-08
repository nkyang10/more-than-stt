# More Than STT 🎙️

SenseVoice-powered dictation app for Windows — high-accuracy Cantonese + Mandarin + English speech-to-text with self-learning hotwords.

Powered by [Sherpa-ONNX](https://github.com/k2-fsa/sherpa-onnx) (12.8k ★) — no internet required, all inference runs locally via ONNX Runtime.

## Features

- 🎤 **Record & Transcribe** — 5-second recording, auto-transcribe via Sherpa-ONNX
- 🧠 **Self-learning** — Correct mistakes, click "Teach Me!", it learns your words with reinforcement (+5 weight per correction)
- 🔥 **Hotword Biasing** — Learned words are boosted during decoding via Sherpa-ONNX contextual biasing
- 🌐 **Multi-language** — auto, yue (Cantonese), zh, en, ja, ko
- 📂 **Load Audio Files** — WAV/MP3/OGG/M4A (auto-resampled to 16kHz)
- 📋 **Detailed Logging** — Every run logged for debugging
- 🔄 **Auto-Update** — One-click update from GitHub Releases (stable + beta channels)
- 🎛️ **Mic Gain** — Adjustable amplification (1–10x) for quiet microphones
- 🔍 **Hotword Manager** — Add/remove/search hotwords, filter by source (manual vs learned)
- ⌨️ **Keyboard Shortcuts** — F5 (record), Ctrl+Enter (Teach Me!), Ctrl+L (load audio)
- 🚀 **Performance** — RTF ~0.01 (100x realtime) on modern CPUs

## Quick Start

1. Go to [Releases](https://github.com/nkyang10/more-than-stt/releases)
2. Download the latest `CantoneseDictation_v*.zip`
3. Download the Sherpa-ONNX Cantonese model from [GitHub](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models): `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2`
4. Extract the model folder `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09/` **inside** the app folder
5. Run `CantoneseDictation.exe`
6. Click **🔴 開始錄音 (F5)** and speak!
7. Correct any mistakes in the editor → click **🧠 Teach Me!** → it learns

## Build from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Quick Build

```bash
# Self-contained publish
dotnet publish -c Release -r win-x64 --self-contained true -o dist/

# Or use the build script
.\build.ps1
```

### Model Setup

The model is **not** included in the repo (226 MB, gitignored). Download and extract:

```bash
# Download Cantonese-optimized model (21.8k hours Cantonese fine-tune)
curl -L -o model.tar.bz2 https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2

# Or use PowerShell
Invoke-WebRequest -Uri "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2" -OutFile model.tar.bz2

# Extract using tar
tar -xf model.tar.bz2

# Place the folder alongside CantoneseDictation.exe
```

Required files:
```
sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09/
├── model.int8.onnx   (226 MB, quantized ONNX model)
├── tokens.txt        (308 KB, BPE vocabulary)
└── test_wavs/        (sample test files for verification)
```

## How It Works

```
You speak "ComfyUI"      → ASR outputs "comefi u i"
You correct to "ComfyUI" → Click Teach Me!
                         → System learns: ComfyUI added to hotword list (weight: 20)
                         → Next time → Sherpa-ONNX contextual biasing → "ComfyUI" ✅
```

**Tech Stack:** .NET 10 WinForms • [Sherpa-ONNX](https://github.com/k2-fsa/sherpa-onnx) (12.8k ★) • NAudio

## Performance

| Metric | Value |
|---|---|
| Model | SenseVoice (int8 quantized) |
| Languages | zh, en, yue, ja, ko |
| RTF (CPU) | ~0.01–0.02 (50–100x realtime) |
| RAM usage | ~500 MB |
| Cantonese accuracy | Fine-tuned on 21.8k hours |

## Project Structure

```
CantoneseDictationNet2/
├── .github/workflows/       # GitHub Actions CI/CD
├── TestRunner/               # Unit tests (28 tests)
├── sherpa-onnx-...-09/       # Model folder (download separately)
├── CantoneseDictation.csproj # .NET project (Sherpa-ONNX + NAudio)
├── AppPaths.cs               # Shared path constants
├── Program.cs                # Entry point
├── MainForm.cs               # UI (dark theme, WinForms)
├── SenseVoiceEngine.cs       # Sherpa-ONNX wrapper
├── HotwordManager.cs         # Self-learning hotword system (with source tracking)
├── AutoUpdater.cs            # GitHub Releases auto-update
├── Logger.cs                 # File logging
├── build.ps1                 # Windows build script
└── LICENSE
```

## Testing

```bash
dotnet run --project TestRunner\TestRunner.csproj
```

Requires model folder in the project root. Tests cover:
- HotwordManager (learn, reinforce, manual add/remove, persistence, sources)
- Full Sherpa-ONNX pipeline (model load, audio processing, inference, hotword biasing)
- Logger (file creation, levels, exception detail)

## License

MIT
