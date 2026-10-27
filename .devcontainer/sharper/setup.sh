#!/usr/bin/env bash
# Installs the Sharper language server and compiler toolset package from a GitHub release into /opt/sharper.
#
#   setup.sh                 the latest release
#   setup.sh sharper-<id>    a given release
#
# SHARPER_REPO picks the repository (default ok3mad/roslyn). Run it again to update; then reload the VS Code window.
set -euo pipefail

repo="${SHARPER_REPO:-ok3mad/roslyn}"
tag="${1:-latest}"
root=/opt/sharper

if [[ "$tag" == latest ]]; then
  url="https://github.com/$repo/releases/latest/download/sharper-vscode.tar.gz"
else
  url="https://github.com/$repo/releases/download/$tag/sharper-vscode.tar.gz"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $url"
curl -fsSL "$url" -o "$tmp/sharper-vscode.tar.gz"
mkdir "$tmp/extract"
tar -xzf "$tmp/sharper-vscode.tar.gz" -C "$tmp/extract"

sudo rm -rf "$root"
sudo mkdir -p "$root"
sudo cp -R "$tmp/extract/." "$root/"
sudo chown -R "$(id -u):$(id -g)" "$root"

# Restore the playground so the language server finds the new compiler package.
# --force-evaluate re-resolves the floating version to the newest package.
dotnet restore --force-evaluate "$(dirname "$0")/../../docs/sharper/playground/Playground.csproj"

echo "Installed $(cat "$root/VERSION") into $root."
