#!/bin/sh
# Builds soulget as one self-contained program per system, into dist/<system>/.
# Needs the .NET 10 SDK (https://dotnet.microsoft.com/download). Usage: ./build.sh [system ...]
# Systems: mac-apple-silicon mac-intel windows linux linux-arm   (default: all of them)
set -eu
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

[ $# -eq 0 ] && set -- mac-apple-silicon mac-intel windows linux linux-arm

dotnet test Soulget.Tests --nologo -v q
for s in "$@"; do
  case "$s" in
    mac-apple-silicon) rid=osx-arm64 ;;
    mac-intel)         rid=osx-x64 ;;
    windows)           rid=win-x64 ;;
    linux)             rid=linux-x64 ;;
    linux-arm)         rid=linux-arm64 ;;
    *) echo "unknown system: $s" >&2; exit 1 ;;
  esac
  out="dist/$s"
  rm -rf "$out"
  dotnet publish Soulget -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$out" --nologo -v q
  # The license must travel next to the program (Soulseek.NET Additional Terms, section 2d).
  cp LICENSE NOTICE THIRD-PARTY-NOTICES.txt "$out/"
  echo "built $out/$(ls "$out" | grep -E '^soulget(\.exe)?$')"
done
