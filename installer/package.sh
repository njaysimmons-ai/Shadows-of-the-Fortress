#!/usr/bin/env bash
# Builds the mod and zips it with the installer: ./installer/package.sh /path/to/Erenshor_Data/Managed
set -euo pipefail
cd "$(dirname "$0")/.."
managed="${1:?usage: package.sh <Erenshor_Data/Managed folder>}"
version=$(grep -oP 'Version = "\K[^"]+' LivingNPCs/Plugin.cs)
dotnet build LivingNPCs -c Release -p:ManagedDir="$managed"
out="dist/LivingNPCs-$version"
mkdir -p "$out"
cp LivingNPCs/bin/Release/LivingNPCs.dll installer/*.bat installer/*.ps1 "installer/READ ME FIRST.txt" "$out/"
(cd dist && zip -qr "LivingNPCs-$version.zip" "LivingNPCs-$version")
echo "dist/LivingNPCs-$version.zip"
