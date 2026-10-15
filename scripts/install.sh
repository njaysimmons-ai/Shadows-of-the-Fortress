#!/usr/bin/env bash
# Installs the Hollow Fog mod into a Space Station 14 checkout.
#
#   scripts/install.sh                 # clones SS14 into ./ss14 (at the tested commit) and installs
#   scripts/install.sh /path/to/ss14   # installs into an existing checkout or fork
#
# The mod only adds new files (everything lives in _Hollow folders), so it never overwrites upstream code.
set -euo pipefail

# The upstream commit the mod was built and tested against.
TESTED_COMMIT="bc6e67f75db267f7d733a619ffdfdf48a2f66f23"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="${1:-$repo_root/ss14}"

if [ ! -d "$target/Content.Server" ]; then
    echo "Cloning Space Station 14 into $target..."
    git clone https://github.com/space-wizards/space-station-14 "$target"
    git -C "$target" checkout "$TESTED_COMMIT"
    git -C "$target" submodule update --init --recursive
fi

cp -R "$repo_root/mod/." "$target/"
echo "Hollow Fog installed into $target"
echo "Build it with: cd \"$target\" && python3 RUN_THIS.py && dotnet build"
