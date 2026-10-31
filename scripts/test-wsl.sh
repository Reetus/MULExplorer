#!/usr/bin/env bash
# Builds and tests MULExplorer on Linux (run inside WSL, or call from Windows with: wsl.exe -e scripts/test-wsl.sh).
#
# The repo is rsync'd to a Linux-native directory first: building on /mnt/<drive> is slow and
# would share obj/ with the Windows build.
#
#   UO_DATA_FOLDER   client folder (default: /mnt/d/Games/OSI/UO if it exists)
#   WSL_BUILD_DIR    where to mirror the repo (default: ~/mulexplorer-wsl)
#   extra args are passed to `dotnet test`, e.g. --filter "FullyQualifiedName~DataTests"
set -euo pipefail

src="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="${WSL_BUILD_DIR:-$HOME/mulexplorer-wsl}"

if [ -z "${UO_DATA_FOLDER:-}" ] && [ -d /mnt/d/Games/OSI/UO ]; then
  export UO_DATA_FOLDER=/mnt/d/Games/OSI/UO
fi

mkdir -p "$dest"
rsync -a --delete \
  --exclude '.git/' --exclude 'bin/' --exclude 'obj/' --exclude 'artifacts/' \
  --exclude 'TestResults/' --exclude 'uofiddler-src/' \
  "$src/" "$dest/"

cd "$dest"
echo "Testing in $dest (UO_DATA_FOLDER=${UO_DATA_FOLDER:-unset})"
dotnet test MULExplorer.Tests "$@"
