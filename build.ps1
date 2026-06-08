param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SelfContained = $true,
    [string]$OutputDir = "release",
    [string]$Runtime = "win-x64",
    [switch]$SkipModelCheck
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== CantoneseDictation Build Script ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Gray
Write-Host "Runtime:       $Runtime" -ForegroundColor Gray
Write-Host "Output:        $OutputDir" -ForegroundColor Gray
Write-Host ""

# Check prerequisites
$dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error "dotnet SDK not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download"
    exit 1
}

$sdkVersion = dotnet --version
Write-Host "dotnet SDK version: $sdkVersion" -ForegroundColor Green

# Check required Sherpa-ONNX model
if (-not $SkipModelCheck) {
    $modelDir = Join-Path $RepoRoot "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09"
    $modelPath = Join-Path $modelDir "model.int8.onnx"
    if (-not (Test-Path $modelPath)) {
        Write-Warning "Sherpa-ONNX model not found at: $modelPath"
        Write-Host "Download from:" -ForegroundColor Yellow
        Write-Host "  curl -L -o model.tar.bz2 https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2" -ForegroundColor Yellow
        Write-Host "  tar -xf model.tar.bz2" -ForegroundColor Yellow
        Write-Host ""
        $continue = Read-Host "Continue build anyway? [y/N]"
        if ($continue -ne "y") {
            exit 0
        }
    }
}

# Restore and publish
Write-Host "`n=== Publishing ===" -ForegroundColor Cyan
$publishArgs = @(
    "publish",
    (Join-Path $RepoRoot "CantoneseDictation.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", (Join-Path $RepoRoot $OutputDir)
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
}
else {
    $publishArgs += "--self-contained", "false"
}

dotnet $publishArgs
if (-not $?) { exit 1 }

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output: $(Join-Path $RepoRoot $OutputDir)" -ForegroundColor Green
Write-Host ""
Write-Host "Files:" -ForegroundColor Cyan
Get-ChildItem -Path (Join-Path $RepoRoot $OutputDir) -Include "*.exe", "*.dll", "*.txt" | ForEach-Object {
    Write-Host "  $($_.Name) ($( [math]::Round($_.Length / 1MB, 1) ) MB)" -ForegroundColor Gray
}
