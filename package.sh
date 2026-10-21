#!/bin/sh
# Makes one shareable zip per system in release/: the program, its license files, the guide, a
# settings template, and the full source (as the GPL and Soulseek.NET's terms require).
# Usage: ./package.sh [system ...]   (same system names as build.sh; default: all)
set -eu
cd "$(dirname "$0")"
version=$(grep -o 'AppVersion = "[^"]*"' Soulget/Program.cs | cut -d'"' -f2)

./build.sh "$@"
rm -rf release
mkdir -p release/stage

# The source, without build output.
src=release/stage/source
mkdir -p "$src/Soulget" "$src/Soulget.Tests"
cp -R LICENSE NOTICE THIRD-PARTY-NOTICES.txt README.md CHANGELOG.md config.example.toml build.sh package.sh third-party "$src/"
cp Soulget/*.cs Soulget/Soulget.csproj "$src/Soulget/"
cp Soulget.Tests/*.cs Soulget.Tests/Soulget.Tests.csproj "$src/Soulget.Tests/"

for d in dist/*/; do
  system=$(basename "$d")
  name="soulget-$version-$system"
  dir="release/stage/$name"
  mkdir -p "$dir"
  cp "$d"* "$dir/"
  cp README.md config.example.toml "$dir/"
  cp -R "$src" "$dir/source"
  (cd release/stage && zip -qr "../$name.zip" "$name")
  echo "made release/$name.zip"
done
rm -rf release/stage
