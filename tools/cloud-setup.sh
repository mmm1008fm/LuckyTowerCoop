#!/usr/bin/env bash
# Setup for a cloud (Linux) session: installs the .NET 8 SDK if missing and checks that CoopNet builds.
# The plugin itself cannot be built here: it needs the game's assemblies (see CLAUDE.md).
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v dotnet >/dev/null 2>&1; then
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
dotnet --version
dotnet build src/CoopNet/CoopNet.csproj -nologo -v q
