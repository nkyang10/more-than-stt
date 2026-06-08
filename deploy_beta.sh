#!/bin/bash
# Build & deploy CI/beta release to GitHub
# Usage: ./deploy_beta.sh [version_message]
# Overwrites the same 'ci' release tag each time — no Release page spam.

set -e

cd "$(dirname "$0")"

VERSION_MSG="${1:-CI build $(date '+%Y%m%d-%H%M')}"
RELEASE_TAG="ci"

echo "=== Building..."
dotnet publish -c Release -r win-x64 --self-contained true -o dist/ 2>&1 | grep -E "error|Build|->"

# Extract version from csproj for CI release notes
CI_VER=$(grep '<Version>' CantoneseDictation.csproj | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/')
echo "Version from csproj: $CI_VER"

echo "=== Packaging..."
mkdir -p release_beta
cp dist/CantoneseDictation.exe release_beta/

# Include model download instructions
cat > release_beta/README.txt << 'EOF'
CI/Beta build — for testing only
Sherpa-ONNX SenseVoice model required separately:
  https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09.tar.bz2
Extract sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09/ into the app folder.
EOF

cd release_beta
zip -r ../CantoneseDictation_beta.zip ./*
cd ..
rm -rf release_beta

echo "=== Deploying to GitHub (tag: $RELEASE_TAG)..."

# Delete old release if exists (ignore error if first time)
gh release delete "$RELEASE_TAG" --yes 2>/dev/null || true
git push origin --delete "$RELEASE_TAG" 2>/dev/null || true

# Create new release with same tag
gh release create "$RELEASE_TAG" \
  --title "CI Build $(date '+%Y-%m-%d %H:%M')" \
  --notes "$VERSION_MSG

Model required separately:
  https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models" \
  "CantoneseDictation_beta.zip"

echo ""
echo "✅ Deployed to: https://github.com/nkyang10/more-than-stt/releases/tag/$RELEASE_TAG"
echo "   Asset: CantoneseDictation_beta.zip"
