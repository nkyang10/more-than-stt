# More Than STT 🎙️

Just want to try building a more friendly STT usage.

SenseVoice-powered dictation app for Windows — high-accuracy Cantonese + Mandarin + English speech-to-text with self-learning hotwords.

## Features

- 🎤 **Record & Transcribe** — 5-second recording, auto-transcribe via ONNX
- 🧠 **Self-learning** — Correct mistakes, click "Teach Me!", it learns your words
- 🌐 **Multi-language** — auto, yue (Cantonese), zh, en
- 📂 **Load Audio Files** — WAV/MP3/OGG/M4A
- 📋 **Detailed Logging** — Every run logged for debugging
- 🔄 **Auto-Update** — One-click update from GitHub Releases

## Quick Start

1. Go to [Releases](https://github.com/nkyang10/more-than-stt/releases)
2. Download the latest `CantoneseDictation_v*.zip`
3. Extract and run `CantoneseDictation.exe`
4. Click **🔴 開始錄音** and speak!
5. Correct any mistakes → click **🧠 Teach Me!** → it learns

## Build from Source

```bash
# Prerequisites: .NET 10 SDK
dotnet publish -c Release -r win-x64 --self-contained true -o dist/

# Files needed at runtime (download separately):
# - model_quant.onnx (232MB, SenseVoice quantized ONNX)
# - tokens.txt (BPE vocab)
# - am.mvn (audio normalization params)
```

## How It Works

```
You speak "ComfyUI"      → ASR outputs "comefi u i"
You correct to "ComfyUI" → Click Teach Me!
                         → System learns: ComfyUI added to hotword list
                         → Next time → correct! ✅
```

**Tech Stack:** .NET 10 WinForms • ONNX Runtime • SenseVoice (FunASR) • NAudio

## Project Structure

```
CantoneseDictationNet2/
├── CantoneseDictation.csproj   # .NET project
├── Program.cs                  # Entry point
├── MainForm.cs                 # UI (dark theme, WinForms)
├── SenseVoiceEngine.cs         # ONNX inference + feature extraction
├── HotwordManager.cs           # Self-learning hotword system
├── AutoUpdater.cs              # GitHub Releases auto-update
├── Logger.cs                   # File logging
└── LICENSE
```

## License

MIT
