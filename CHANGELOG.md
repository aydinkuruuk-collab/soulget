# What changed in each version

## 2.3.0 (21 October 2026)
- Accept MP3s below 320 kbps when you choose to: in Settings, per download (More options), or per folder ("Accept Lower Quality" when its missing tracks only exist at a lower quality). 320 stays the default, and better copies always come first.
- Fix: "Get missing" and "Search for the missing tracks now" keep the folder's own settings (quality, strict, years) instead of switching to today's defaults.
- Command line: `--min-bitrate KBPS`, which also works with `--album`.

## 2.2.0 (21 October 2026)
- Mac app: click a folder to see which tracks you have and which are missing, and why.
- Preview a track list before downloading: duplicates left out, remixes, what you already have.
- More options per download: years, strict matching, remixes the artist made for others, quality.
- History of the 7-hour checks: what each one found, and which were skipped.

## 2.1.0 (21 October 2026)
- New: the soulget Mac app. Search, click Get, no setup.
- Fix: if the music drive is disconnected during a download or check, soulget stops cleanly instead of crashing.
- The timer status says whether it checks this soulget's folders.

## 2.0.1 (20 October 2026)
- Artist discographies include tracks Discogs lists as appearances on other people's compilations.
- `--strict`, for artist names other artists share.

## 2.0.0 (20 October 2026)
- A single program file for Mac, Windows and Linux: nothing else to install (before, soulget needed Python).
- Its own Soulseek client version, and the GPL v3 license with full source.

## 1.x (14–19 October 2026, Python)
- Download every track by an artist, on a label, or on one album, de-duplicated using Discogs track lists.
- Lossless first, MP3 320 as a fallback, and switching away from slow users.
- A timer that looks again for missing tracks every 7 hours, plus `--later`, `--queue` and `--rename`.
- File names with year, catalogue number and position.
