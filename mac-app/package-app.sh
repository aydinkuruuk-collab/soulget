#!/bin/sh
# Builds soulget.app and makes the zip to share: release/soulget-<version>-mac.zip, holding the app,
# a short guide, the license files and the complete source (as the GPL and Soulseek.NET's terms require).
set -eu
cd "$(dirname "$0")"
VERSION=$(grep -o '^VERSION=.*' build-app.sh | cut -d= -f2)
# The engine's source: ../standalone here, the repository's top folder on GitHub, ../engine in the zip.
for e in ../standalone ../engine ..; do
  if [ -f "$e/Soulget/Soulget.csproj" ]; then ENGINE=$e; break; fi
done

./build-app.sh "$@"
rm -rf release
name="soulget-$VERSION-mac-app"
stage="release/stage/$name"
mkdir -p "$stage"
ditto dist/soulget.app "$stage/soulget.app"
cp "Resources/READ ME FIRST.txt" "$stage/"
cp "$ENGINE/LICENSE" "$stage/LICENSE.txt"
cp "$ENGINE/NOTICE" "$stage/NOTICE.txt"
cp "$ENGINE/THIRD-PARTY-NOTICES.txt" "$stage/"

# The source: the engine (with the Soulseek.NET source it uses) and this app.
src="$stage/source"
mkdir -p "$src/engine/Soulget" "$src/engine/Soulget.Tests" "$src/mac-app/Sources/SoulgetApp" "$src/mac-app/Resources"
cp -R "$ENGINE/LICENSE" "$ENGINE/NOTICE" "$ENGINE/THIRD-PARTY-NOTICES.txt" "$ENGINE/README.md" \
      "$ENGINE/config.example.toml" "$ENGINE/build.sh" "$ENGINE/package.sh" "$ENGINE/third-party" "$src/engine/"
cp "$ENGINE"/Soulget/*.cs "$ENGINE/Soulget/Soulget.csproj" "$src/engine/Soulget/"
cp "$ENGINE"/Soulget.Tests/*.cs "$ENGINE/Soulget.Tests/Soulget.Tests.csproj" "$src/engine/Soulget.Tests/"
cp Package.swift README.md build-app.sh package-app.sh make-icon.swift .gitignore "$src/mac-app/"
cp Sources/SoulgetApp/*.swift "$src/mac-app/Sources/SoulgetApp/"
cp Resources/AppIcon.icns Resources/Info.plist "Resources/READ ME FIRST.txt" "$src/mac-app/Resources/"

(cd release/stage && ditto -c -k --keepParent "$name" "../$name.zip")
rm -rf release/stage
shasum -a 256 "release/$name.zip"
echo "made release/$name.zip ($(du -h "release/$name.zip" | cut -f1))"
