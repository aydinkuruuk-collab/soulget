#!/bin/sh
# Builds soulget.app (Apple chip and Intel in one app) and the zip to share:
#   1. tests the engine   2. builds the engine for both Mac processors
#   3. builds the app     4. puts it all together, signs it (ad hoc) and zips it with the source.
# Needs Xcode (or its command line tools) and the .NET 10 SDK.
set -eu
cd "$(dirname "$0")"
VERSION=2.2.0
# The engine's source: ../standalone here, the repository's top folder on GitHub, ../engine in the zip.
for e in ../standalone ../engine ..; do
  if [ -f "$e/Soulget/Soulget.csproj" ]; then ENGINE=$e; break; fi
done
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
APP=dist/soulget.app

# --app-only: reuse the engines built last time (quicker while working on the app).
if [ "${1:-}" != "--app-only" ]; then
echo "1/4 engine tests"
(cd "$ENGINE" && dotnet test Soulget.Tests --nologo -v q)

echo "2/4 engine for Apple chip and Intel"
for rid in osx-arm64 osx-x64; do
  rm -rf "build/engine-$rid"
  dotnet publish "$ENGINE/Soulget" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "build/engine-$rid" --nologo -v q
done
fi

echo "3/4 app"
swift build -c release --arch arm64 --arch x86_64

echo "4/4 bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Helpers" "$APP/Contents/Resources"
cp .build/apple/Products/Release/SoulgetApp "$APP/Contents/MacOS/soulget"
strip -S -x "$APP/Contents/MacOS/soulget"   # drop debug info, which names the folders it was built in
cp build/engine-osx-arm64/soulget "$APP/Contents/Helpers/soulget-arm64"
cp build/engine-osx-x64/soulget "$APP/Contents/Helpers/soulget-x64"
cp Resources/AppIcon.icns "$APP/Contents/Resources/"
cp "$ENGINE/LICENSE" "$APP/Contents/Resources/LICENSE.txt"
cp "$ENGINE/NOTICE" "$APP/Contents/Resources/NOTICE.txt"
cp "$ENGINE/THIRD-PARTY-NOTICES.txt" "$APP/Contents/Resources/THIRD-PARTY-NOTICES.txt"
sed "s/@VERSION@/$VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
codesign --force --sign - "$APP/Contents/Helpers/soulget-arm64" "$APP/Contents/Helpers/soulget-x64"
codesign --force --sign - "$APP"
codesign --verify --strict "$APP"
echo "built $APP ($(du -sh "$APP" | cut -f1))"
