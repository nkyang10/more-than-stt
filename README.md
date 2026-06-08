# More Than STT 🎙️

Just want to try building a more friendly STT usage.

SenseVoice-powered dictation app for Windows — high-accuracy Cantonese + Mandarin + English speech-to-text with self-learning hotwords.

## Features

- 🎤 **Record & Transcribe** — 5-second recording, auto-transcribe via ONNX
- 🧠 **Self-learning** — Correct mistakes, click "Teach Me!", it learns your words with reinforcement (+5 weight per correction)
- 🔥 **Hotword Biasing** — Learned words are boosted during CTC decoding (+2.5 logit bias) and post-processed with fuzzy matching
- 🌐 **Multi-language** — auto, yue (Cantonese), zh, en
- 📂 **Load Audio Files** — WAV/MP3/OGG/M4A
- 📋 **Detailed Logging** — Every run logged for debugging
- 🔄 **Auto-Update** — One-click update from GitHub Releases (stable + beta channels)
- 🎛️ **Mic Gain** — Adjustable amplification (1–10x) for quiet microphones
- 🔍 **Hotword Manager** — Add/remove/search hotwords, filter by source (manual vs learned)
- ⌨️ **Keyboard Shortcuts** — F5 (record), Ctrl+Enter (Teach Me!), Ctrl+L (load audio)

## Quick Start

1. Go to [Releases](https://github.com/nkyang10/more-than-stt/releases)
2. Download the latest `CantoneseDictation_v*.zip` and `sensevoice_model_v*.zip`
3. Extract both to the same folder
4. Run `CantoneseDictation.exe`
5. Click **🔴 開始錄音 (F5)** and speak!
6. Correct any mistakes in the editor → click **🧠 Teach Me!** → it learns

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

### Model Files

These are **not** included in the repo (gitignored, large). Download them from [Releases](https://github.com/nkyang10/more-than-stt/releases):

| File | Size | Description |
|---|---|---|
| `model_quant.onnx` | 232 MB | SenseVoice quantized ONNX model |
| `tokens.txt` | 173 KB | BPE vocabulary (25055 tokens) |
| `am.mvn` | 11 KB | Audio normalization params |

Place them alongside `CantoneseDictation.exe`, or in `%LOCALAPPDATA%\CantoneseDictation\`.

## How It Works

```
You speak "ComfyUI"      → ASR outputs "comefi u i"
You correct to "ComfyUI" → Click Teach Me!
                         → System learns: ComfyUI added to hotword list (weight: 20)
                         → Next time → CTC logit boosting + fuzzy post-processing → "ComfyUI" ✅

Each re-correction: weight += 5 (reinforcement learning)
```

### Hotword Biasing Pipeline

1. **CTC logit boosting** — Token IDs matching hotwords get +2.5 added during greedy decoding
2. **Post-decode matching** — Fuzzy string matching substitutes ASR output with correct hotword text
3. **Reinforcement** — Each correction increases hotword weight by 5

**Tech Stack:** .NET 10 WinForms • ONNX Runtime • SenseVoice (FunASR) • NAudio

## Project Structure

```
CantoneseDictationNet2/
├── .github/workflows/       # GitHub Actions CI/CD
├── TestRunner/               # Unit tests (34 tests)
├── CantoneseDictation.csproj # .NET project
├── AppPaths.cs               # Shared path constants
├── Program.cs                # Entry point
├── MainForm.cs               # UI (dark theme, WinForms)
├── SenseVoiceEngine.cs       # ONNX inference + feature extraction + hotword biasing
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

Requires model files in the project root. Tests cover:
- HotwordManager (learn, reinforce, manual add/remove, persistence, sources)
- Hotword processing (direct match, phrase match, token ID computation)
- Full ONNX pipeline (model load, audio processing, inference, decode, hotword bias)
- Logger (file creation, levels, exception detail)

## License

MIT
