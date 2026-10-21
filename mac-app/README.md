# soulget for Mac

A small Mac app around the soulget engine (the C# program one folder up): a search box, a Get button, progress,
your folders, and a menu-bar icon. The engine is the same tested command-line soulget; the app runs
it with `--events` (progress as JSON lines) and the app-only commands `--app-find`, `--app-status`,
`--app-set`, `--app-folder` (a folder's tracks and why the missing ones are missing) and `--app-checks`
(what each check for missing tracks did).

- **No setup:** on first start the app makes its own Soulseek login and uses `~/Music/soulget`.
  If the command-line soulget was already set up (its timer points at its folder), the app uses
  that folder, its settings and its music instead.
- **Settings** live where the command-line version keeps them (`config.toml`), so both work on the
  same folders. A new setup keeps them in `~/Library/Application Support/soulget`.
- **The engine** is copied out of the app into `~/Library/Application Support/soulget/engine`, so the
  7-hour timer keeps working wherever the app is moved; it's replaced when the app is updated.

## Building

Needs Xcode (15 or later) and the .NET 10 SDK.

```
./build-app.sh            # tests the engine, builds both engines and the app -> dist/soulget.app
./build-app.sh --app-only # app only, reusing the engines from last time
./package-app.sh          # the zip to share -> release/soulget-<version>-mac-app.zip (with the source)
```

Developer checks, without touching real settings (`-home` points at a separate folder):

```
dist/soulget.app/Contents/MacOS/soulget -snapshot /tmp/shots     # pictures of each screen, sample data
dist/soulget.app/Contents/MacOS/soulget -home /tmp/h -autotest "Plastikman Sheet One" \
    -autotestKind master -autotestOption later -autotestShots /tmp/shots   # search, Get, pictures
```

`-engine <program>` swaps in another engine (for example a script that replays a download).

## License

GPL-3.0-only, like the engine; see `LICENSE` and `NOTICE` next to the engine. The app is ad-hoc signed,
not notarized, so macOS asks once before opening it (see `Resources/READ ME FIRST.txt`).
