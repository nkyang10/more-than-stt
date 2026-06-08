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

# Check required runtime model files
if (-not $SkipModelCheck) {
    $models = @("model_quant.onnx", "tokens.txt", "am.mvn")
    $missing = @()
    foreach ($m in $models) {
        $path = Join-Path $RepoRoot $m
        if (-not (Test-Path $path)) {
            $missing += $m
        }
    }
    if ($missing.Count -gt 0) {
        Write-Warning "Missing model files: $($missing -join ', ')"
        Write-Host "These files are needed at runtime. Download from:" -ForegroundColor Yellow
        Write-Host "  https://github.com/nkyang10/more-than-stt/releases" -ForegroundColor Yellow
        Write-Host "  Or place them in the output directory manually." -ForegroundColor Yellow
        Write-Host ""
        $continue = Read-Host "Continue build anyway? [y/N]"
        if ($continue -ne "y") {
            exit 0
        }
    }
}

# Clean and restore
Write-Host "`n=== Restoring packages ===" -ForegroundColor Cyan
dotnet restore (Join-Path $RepoRoot "CantoneseDictation.csproj")
if (-not $?) { exit 1 }

# Build
Write-Host "`n=== Building ===" -ForegroundColor Cyan
$publishArgs = @(
    "publish"
    (Join-Path $RepoRoot "CantoneseDictation.csproj")
    "-c", $Configuration
    "-r", $Runtime
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

# Copy model files if they exist
$modelFiles = @("model_quant.onnx", "tokens.txt", "am.mvn")
foreach ($m in $modelFiles) {
    $src = Join-Path $RepoRoot $m
    $dst = Join-Path $RepoRoot $OutputDir $m
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $dst -Force
        Write-Host "  Copied: $m" -ForegroundColor Green
    }
}

# Create version file
$version = "1.0.0.0"
$csproj = Join-Path $RepoRoot "CantoneseDictation.csproj"
if (Test-Path $csproj) {
    $content = Get-Content $csproj -Raw
    if ($content -match '<Version>(.*?)<\/Version>') {
        $version = $matches[1]
    }
}
$version | Out-File -FilePath (Join-Path $RepoRoot $OutputDir "version.txt") -Encoding utf8

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output: $(Join-Path $RepoRoot $OutputDir)" -ForegroundColor Green
Write-Host ""
Write-Host "Files:" -ForegroundColor Cyan
Get-ChildItem -Path (Join-Path $RepoRoot $OutputDir) | ForEach-Object {
    Write-Host "  $($_.Name) ($( [math]::Round($_.Length / 1MB, 1) ) MB)" -ForegroundColor Gray
}
