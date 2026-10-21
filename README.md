# soulget

Type an artist, a record label or an album, and a minimum bitrate. soulget gets
the full track list from Discogs, removes duplicates, and downloads **one copy
of each track** from Soulseek.

```
soulget "Boards of Canada" 320
soulget --label "Ghostly International" 256
soulget --album "Plastikman - Sheet One" 320
```

Prefer clicking to typing? On a Mac, the soulget app does the same with a search box and a Get
button, and needs no setup (`soulget-<version>-mac-app.zip`).

soulget is a single program file: nothing else needs installing. It runs on
Mac (Apple chip or Intel), Windows and Linux. The Mac versions are the most
tested; the Windows and Linux versions are new.

## One-time setup

1. **Unzip** the download for your computer and put the folder wherever you
   like. Settings, the Discogs cache and the timer log live in this folder,
   next to the program.
2. **Open a terminal in that folder:**
   - **Mac:** open Terminal, type `cd ` (with a space), drag the folder into
     the window, press Enter.
   - **Windows:** open the folder in File Explorer, right-click an empty spot
     and choose *Open in Terminal* (Windows 10: hold Shift while
     right-clicking, then *Open PowerShell window here*).
   - **Linux:** open a terminal there, and once run `chmod +x soulget`.
3. **Run it once:** `./soulget --help` (on Windows: `.\soulget.exe --help`).
   This creates `config.toml` next to it.
   - **Mac, first time:** macOS may say it "cannot verify the developer"
     (soulget isn't sold through Apple). Run
     `xattr -d com.apple.quarantine soulget` once in that folder, or allow
     it under System Settings > Privacy & Security.
   - **Windows, first time:** if SmartScreen warns, choose *More info* >
     *Run anyway*.
   - **Linux:** needs the ICU library, which most systems have (package
     `libicu` or `libicu74` and similar).
4. **Open `config.toml`** in any text editor and fill in your Soulseek
   `username` and `password`.
   - Soulseek allows one login per name at a time. **Close your other Soulseek
     app** (SoulseekQt, Nicotine+...) before running soulget, or use a second
     username. A name that doesn't exist yet is created the first time you log
     in.
5. Optional: paste a Discogs token into `config.toml` (free; go to
   https://www.discogs.com/settings/developers and click "Generate new token").
   Without one everything still works; reading track lists is about twice as
   slow, which matters mainly for big labels.

In the examples below, type `./soulget` on Mac and Linux and `.\soulget.exe`
on Windows (or put the folder on your PATH and just type `soulget`).

## Everyday use

| You type | What happens |
|---|---|
| `soulget "Artist Name" 320` | every track by the artist, lossy files at least 320 kbps |
| `soulget --label "Label Name" 256` | every track released on the label |
| `soulget "https://www.discogs.com/artist/307-Boards-Of-Canada" 320` | use an exact Discogs page when the name matches more than one artist |
| `soulget --album "Plastikman - Sheet One" 320` | one album, EP or single (or paste its Discogs link) |
| `soulget --track "Mould Impression - Lenk's 808"` | one track; single tracks are collected in a `Wanted tracks` folder (repeat `--track` for several) |
| `... --later` | don't search now: put it all on the list for the timer (see below) |
| `... --dry-run` | only build the track list and show it; download nothing |
| `... --limit 5` | only the first 5 tracks (good for a first test) |
| `... --min-bitrate 192` | lowest MP3 quality to accept, like the number after the name (also works with `--album`) |
| `... --no-remixes` | originals only: skip remixes, live and alternate versions |
| `... --years 1995-2002` | only releases from those years |
| `... --formats flac,wav,mp3` | allowed file types, best first (default: mp3, then flac). Every copy in an earlier format is tried before any later one |
| `... --no-compilations` | skip DJ-mix CDs and CD/download compilations; vinyl is always kept (for an artist: tracks found only on other people's compilations) |
| `... --their-remixes` | artist mode: also get remixes the artist made for others |
| `... --strict` | for artist names other artists share (e.g. "Katran"): a file only counts if its folder shows the record's name or catalogue number, or its length matches Discogs within 10 s. The setting is kept for later retries |

If you leave out the bitrate, the one in `config.toml` is used (320).
FLAC/WAV/AIFF always pass the bitrate check, since they are lossless.

Run the **same command again** any time. Tracks you already have are skipped,
and only the missing ones are searched again. **Nothing is downloaded twice,
across folders too:** if an album's tracks are already in an artist or label
folder, they show as `already had` ("already in Boards Of Canada").

Ctrl+C stops after the downloads in progress, keeping everything finished so
far; Ctrl+C a second time stops at once.

## Missing tracks: trying again later, automatically

Who has a file depends on who is online, which changes with time zones and
weekdays. So soulget remembers what it couldn't find.

| You type | What happens |
|---|---|
| `soulget --retry-missing` | searches again, once, for every missing track in every folder, with the same settings as the first time |
| `soulget --album "Artist - Title" --later` | adds an album to the list without searching now (also works with `--track`, an artist or `--label`) |
| `soulget --queue` | per folder: tracks you have, still being looked for, and given up on |
| `soulget --schedule on` | your computer does the retry by itself every 7 hours |
| `soulget --schedule status` | whether the timer is on, which program it runs, and the last checks |
| `soulget --schedule off` | stops the timer |

- **Why every 7 hours:** 7 doesn't divide evenly into 24, so the check time
  drifts. Over one week it lands on every hour of the day and every day of the
  week, covering evenings in Europe, the Americas and Asia.
- **The timer** is a launchd agent on a Mac, a Task Scheduler task on Windows
  (runs without a window, while you're signed in), and a systemd user timer on
  Linux. A check missed while the computer slept runs when it can.
- **Giving up:** a track still missing 14 days after it was first missed stops
  being searched, and shows as `gave up` in `report.csv`. A normal run still
  tries everything.
- **One login per name:** if SoulseekQt is open with the same login, a timed
  check skips itself instead of kicking SoulseekQt off. A second Soulseek login
  under `[retry]` in `config.toml` avoids that altogether.
- **Speed:** to stay within Soulseek's search limit, retry checks send one main
  search per track. About 400 missing tracks take roughly 45 minutes.
- **Results:** found files go into the usual folders; a notification appears
  when the timer finds something (Mac, and Linux with `notify-send`).
  `retry-log.csv` in the music folder has one line per check: `when`,
  `weekday`, `hour`, `folder`, `searched`, `found`, `still_missing`,
  `given_up`, `trigger`. `retry-output.log` next to the program holds the full
  output of timed checks.

## What counts as a duplicate

- The same song on the single, the album, a reissue, a Japanese edition and a
  compilation is **one track**. The earliest release is shown as its source.
- Edition tags are ignored: *Remastered*, *Radio Edit*, *Original Mix*,
  *Album Version*, *Extended*, *Mono/Stereo* and so on.
- Small spelling differences in spacing count as the same song
  ("Hi Scores" = "Hiscores").
- **Remixes and other versions are separate tracks** ("Song (Bibio Remix)" is
  different music). Add `--no-remixes` to skip them.
- Music videos, silent tracks and data tracks are skipped.
- On a label, a record released on CD, vinyl and download is read only once.

## What you get

Everything goes into the music folder from `config.toml` (default
`~/Music/soulget`), one folder per artist, label (`<Label> (label)`), album
(`<Artist - Album>`) or `Wanted tracks`. Files are named so a folder sorts in
release order:

```
1996 [PRMT 010] A1 Mould Impression - Broken Monitor (6 Hours).flac
1993 [NoMu 22 CD] 05 Plastikman - Helikopter (Sheet One).flac
1995 [BOARD1] A1 Boards Of Canada - Twoism.mp3     (release left out: same name as the track)
```

Year, catalogue number and position are those of the earliest record the
track appeared on; parts Discogs doesn't know are left out. Change the pattern
with `file_name` in `config.toml` (`{year}` `{catno}` `{position}` `{artist}`
`{title}` `{release}`); `soulget --rename` previews renaming older files and
`soulget --rename --yes` does it.

- **tracklist.csv**: the de-duplicated list from Discogs (`artist`, `title`,
  `first_release`, `catno`, `position`, `year`, `length`, `also_on`).
- **report.csv**: what happened to each track on the last run (`status`:
  downloaded / already had / not found / failed / gave up, then `file`,
  `format`, `bitrate_kbps`, `from_user`, and `note`, which explains a miss:
  "34 files seen, none suitable: bitrate too low (20)" means people had it,
  but only below your minimum).

**External drive:** point `download_folder` at it (a Mac example:
`/Volumes/MyDrive/Music`). If it isn't connected, soulget stops with "Connect it
and try again" and timer checks skip their turn; nothing is written elsewhere.
Keep each folder whole: its hidden `.soulget-...` files are its memory.

## How it picks a file

It searches Soulseek for "artist title", waits 8 seconds for answers, and drops
files that are the wrong format or below your bitrate, lack the song title in
the file name, are a remix or live version you didn't ask for, or have a very
different length from Discogs. If that finds nothing, it searches for the
whole record ("artist + release"), which also finds tracks with code-like
titles ("Untitled 1", "A1") by their position in the record's folder.

From what's left it prefers your format order, users with a free upload slot,
fast users and clean file names. A user whose download doesn't start within
2 minutes is skipped (up to 5 users per quality level per track). Progress is
shown every 30 seconds; a user sending under 50 KB/s is swapped for another
one who has the file, unless the download is nearly done. All these numbers
are in `config.toml`. Downloading pauses when the disk has less than 10 GB
free, and the rest waits on the timer's list.

Only one soulget logs in at a time; others wait in line, first come first
served. Timer checks don't wait: they skip a turn instead.

## Moving from the Python version of soulget

Put this program in the same folder as your old `config.toml` (or start it
with `--home <that folder>`). It reads and writes the same files: your music
folders, records, Discogs cache, retry log and timer. Running
`soulget --schedule on` moves the timer to this program; both versions share
one lock, so they never log in at the same time.

## Limits worth knowing

- Discogs is the source of truth: a track it doesn't list isn't searched.
- Large labels mean many Discogs pages the first time (1,000 releases: about
  40 minutes without a token, 18 with one); soulget asks before anything over
  20 minutes. Repeat runs use the cache.
- Soulseek's server blocks searches for a few heavily-protected artists.
- Searches are paced to 34 per 220 seconds, so the server doesn't disconnect you.
- soulget downloads only; it doesn't share files back.

## License and source code

soulget is free software under the **GNU General Public License v3 only**, with
**no warranty**. It is built on Soulseek.NET (used unmodified) and is not
maintained by, endorsed by, or affiliated with the Soulseek.NET project. The
full license, including Soulseek.NET's Additional Terms, is in `LICENSE`; see
`NOTICE` for details, including soulget's own Soulseek client version
(170.20261020). `soulget --license` shows the license too.

You are responsible for using soulget lawfully and within the Soulseek
network's rules, including copyright law where you live.

The complete source code is in the `source` folder. To build it yourself,
install the .NET 10 SDK and run `./build.sh` there (it runs the tests, then
builds every system's version into `dist/`).
