// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI

/// Developer tool: `soulget.app/Contents/MacOS/soulget -snapshot <folder>` shows each screen with sample
/// data (no engine, no network) and saves pictures of the windows, then quits.
@MainActor
enum Snapshots {
    static func run(into dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let m = AppModel.shared
        m.demo = true
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_200_000_000)
            guard let window = NSApp.windows.first(where: { $0.title == "soulget" }) else {
                print("snapshot: main window not found"); NSApp.terminate(nil); return
            }
            window.setContentSize(NSSize(width: 780, height: 680))

            // 1. First start: nothing downloaded yet.
            m.status = sample(folders: false)
            m.ready = true
            await shoot(window, dir, "1-first-start")

            // 2. Search results, with a library.
            m.status = sample(folders: true)
            m.query = "boards of canada"
            m.searchedFor = "boards of canada"
            m.results = sampleResults()
            await shoot(window, dir, "2-search")

            // 3. Downloading.
            m.clearSearch()
            var r = RunState(request: Request(title: "Getting Boards Of Canada", args: [], kind: .get))
            r.phase = "Searching and downloading"
            r.loggedIn = true
            r.total = 87; r.todo = 84; r.had = 3; r.processed = 23; r.downloaded = 21; r.missing = 2
            r.folderPath = "/tmp"
            r.active = [
                ActiveTrack(id: "a", title: "Boards Of Canada - Roygbiv", detail: "flac from wavelength_dub", percent: 64, note: "812 KB/s"),
                ActiveTrack(id: "b", title: "Boards Of Canada - Telephasic Workshop", detail: "flac from nyctalopia", percent: 12, note: "240 KB/s"),
                ActiveTrack(id: "c", title: "Boards Of Canada - Olson", detail: "mp3 320 kbps from orangeyfuzz", percent: nil, note: "waiting in their queue"),
            ]
            m.run = r
            m.upNext = [Request(title: "Getting the Primate label", args: [], kind: .get)]
            m.finished = [Finished(title: "Getting Plastikman - Sheet One", text: "11 new tracks · 1 not found yet. soulget keeps looking for them every 7 hours.", ok: true, folderPath: "/tmp")]
            await shoot(window, dir, "3-downloading")

            // 4. The same in dark mode.
            NSApp.appearance = NSAppearance(named: .darkAqua)
            await shoot(window, dir, "4-downloading-dark")
            NSApp.appearance = NSAppearance(named: .aqua)

            // 5-7. Settings tabs.
            m.run = nil; m.upNext = []
            m.loginMode = .auto
            for tab in ["general", "soulseek", "discogs", "about"] {
                UserDefaults.standard.set(tab, forKey: "settingsTab")
                let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView().environmentObject(m)))
                w.title = "soulget Settings"
                w.styleMask = [.titled, .closable]
                w.setFrameOrigin(NSPoint(x: 120, y: 120))
                w.orderFront(nil)
                await shoot(w, dir, "5-settings-\(tab)")
                w.close()
            }
            // 6-9. The new screens, each in its own window.
            m.checks = sampleChecks()
            let folder = sample(folders: true).folders[1]
            let screens: [(String, AnyView)] = [
                ("6-folder", AnyView(FolderSheet(folder: folder, detail: sampleFolder()))),
                ("7-history", AnyView(HistorySheet())),
                ("8-options", AnyView(OptionsSheet(item: sampleResults()[1], options: GetOptions(strict: true, yearsOn: true, yearFrom: 2014, yearTo: 2021, minBitrate: 192)))),
                ("9-preview", AnyView(PreviewSheet(preview: samplePreview()))),
            ]
            for (name, view) in screens {
                let w = NSWindow(contentViewController: NSHostingController(rootView: view.environmentObject(m)))
                w.styleMask = [.titled]
                w.setFrameOrigin(NSPoint(x: 140, y: 100))
                w.orderFront(nil)
                await shoot(w, dir, name)
                w.close()
            }
            NSApp.terminate(nil)
        }
    }

    static func decode<T: Decodable>(_ json: String) -> T {
        let d = JSONDecoder()
        d.keyDecodingStrategy = .convertFromSnakeCase
        return try! d.decode(T.self, from: Data(json.utf8))
    }

    static func sampleFolder() -> FolderDetail {
        decode("""
            {"name":"Primate (label)","path":"/tmp","job":{"kind":"label","url":"https://www.discogs.com/label/1","formats":["flac","wav","aiff","mp3"],
             "min_bitrate":320,"strict":false,"no_remixes":false,"no_compilations":true,"their_remixes":false,"years":null},
             "counts":{"have":3,"elsewhere":1,"missing":3,"gave_up":1,"not_tried":1},
             "tracks":[
              {"artist":"Mould Impression","title":"Broken Monitor (6 Hours)","release":"Lenk's 808","year":1996,"catno":"PRMT 010","position":"A1","length":"6:12","remix":false,"status":"missing","note":"34 files seen, none suitable: bitrate too low (20), format (14)","checks":5},
              {"artist":"Mould Impression","title":"Lenk's 808","release":"Lenk's 808","year":1996,"catno":"PRMT 010","position":"B1","length":"5:40","remix":false,"status":"missing","note":"no results on Soulseek","checks":9},
              {"artist":"Steve Stoll","title":"Damage (Primate Remix)","release":"Damage","year":1997,"catno":"PRMT 014","position":"B2","length":"6:01","remix":true,"status":"missing","note":"1 users tried, none delivered (last: didn't start within 120s)","checks":2},
              {"artist":"Joe Mitchell","title":"Dusk","release":"Sunfire EP","year":1998,"catno":"PRMT 022","position":"A2","length":"","remix":false,"status":"gave_up","note":"no results on Soulseek","checks":40},
              {"artist":"Joe Mitchell","title":"Untitled","release":"Sunfire EP","year":1998,"catno":"PRMT 022","position":"B3","length":"","remix":false,"status":"not_tried"},
              {"artist":"Mould Impression","title":"Pressure","release":"Primate 1","year":1995,"catno":"PRMT 001","position":"A1","length":"7:02","remix":false,"status":"have","file":"x.flac","format":"flac"},
              {"artist":"Mould Impression","title":"Low Grade","release":"Primate 1","year":1995,"catno":"PRMT 001","position":"B1","length":"6:44","remix":false,"status":"have","file":"x.flac","format":"flac"},
              {"artist":"Cari Lekebusch","title":"Obtuse","release":"Primate 5","year":1996,"catno":"PRMT 005","position":"A","length":"","remix":false,"status":"have","file":"x.mp3","format":"mp3"},
              {"artist":"Mould Impression","title":"Twisted","release":"Primate 3","year":1995,"catno":"PRMT 003","position":"B2","length":"","remix":false,"status":"elsewhere","note":"in Mould Impression"}]}
            """)
    }

    static func sampleChecks() -> [CheckRun] {
        func f(_ name: String, _ searched: Int, _ found: Int) -> String { "{\"folder\":\"\(name)\",\"searched\":\(searched),\"found\":\(found),\"still_missing\":\(searched - found)}" }
        return decode("""
            [{"started":"2026-10-21T06:08:00","trigger":"scheduled","result":"done","reason":"","stopped_early":false,"searched":587,"found":6,"still_missing":581,
              "folders":[\(f("Primate (label)", 138, 4)),\(f("Disturbance (label)", 195, 2)),\(f("Katran", 4, 0))]},
             {"started":"2026-10-20T23:08:00","trigger":"scheduled","result":"crashed","reason":"the music drive went away during the check","stopped_early":false,"searched":0,"found":0,"still_missing":0,"folders":[]},
             {"started":"2026-10-20T14:07:00","trigger":"scheduled","result":"done","reason":"","stopped_early":false,"searched":587,"found":1,"still_missing":586,"folders":[\(f("Primate (label)", 138, 1))]},
             {"started":"2026-10-20T11:30:00","trigger":"manual","result":"done","reason":"","stopped_early":false,"searched":12,"found":0,"still_missing":12,"folders":[\(f("Katran", 12, 0))]},
             {"started":"2026-10-19T07:59:00","trigger":"scheduled","result":"skipped","reason":"the drive 'MusicDrive' isn't connected","stopped_early":false,"searched":0,"found":0,"still_missing":0,"folders":[]},
             {"started":"2026-10-17T08:11:00","trigger":"scheduled","result":"done","reason":"","stopped_early":false,"searched":768,"found":89,"still_missing":679,"folders":[\(f("Primate (label)", 402, 60))]}]
            """)
    }

    static func samplePreview() -> PreviewState {
        var p = PreviewState(item: sampleResults()[0], options: GetOptions())
        p.summary = PreviewSummary(total: 87, releases: 45, duplicates: 130, remixes: 12, had: 3, remixesLeftOut: false, path: "/tmp")
        p.tracks = sampleFolder()
        p.finished = true
        return p
    }

    static func shoot(_ window: NSWindow, _ dir: URL, _ name: String) async {
        window.orderFrontRegardless()
        try? await Task.sleep(nanoseconds: 900_000_000)
        guard let cg = CGWindowListCreateImage(.null, .optionIncludingWindow, CGWindowID(window.windowNumber),
                                               [.boundsIgnoreFraming, .bestResolution]) else {
            print("snapshot: could not capture \(name)"); return
        }
        let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])
        try? png?.write(to: dir.appendingPathComponent("\(name).png"))
        print("snapshot: \(name).png")
    }

    static func sample(folders: Bool) -> Status {
        let list = folders ? """
            [{"name":"Boards Of Canada","path":"/tmp","kind":"artist","url":"https://www.discogs.com/artist/307","total":87,"have":84,"looking":3,"gave_up":0,"updated":"2026-10-21T09:12:00Z"},
             {"name":"Primate (label)","path":"/tmp","kind":"label","url":"https://www.discogs.com/label/1","total":553,"have":404,"looking":149,"gave_up":0,"updated":"2026-10-20T21:40:00Z"},
             {"name":"Plastikman - Sheet One","path":"/tmp","kind":"master","url":"https://www.discogs.com/master/2","total":12,"have":12,"looking":0,"gave_up":0,"updated":"2026-10-19T18:02:00Z"},
             {"name":"Wanted tracks","path":"/tmp","kind":"tracks","url":"","total":4,"have":2,"looking":1,"gave_up":1,"updated":"2026-10-15T11:30:00Z"}]
            """ : "[]"
        let json = """
            {"version":"2.1.0","home":"~/Library/Application Support/soulget","config_path":"/tmp/config.toml",
             "music_folder":"\(FileManager.default.homeDirectoryForCurrentUser.path)/Music/soulget","username":"soulget-k7m2qp",
             "has_password":true,"has_token":false,"formats":["flac","wav","aiff","mp3"],"min_bitrate":320,"every_hours":7,
             "timer":{"on":\(folders),"installed":\(folders),"program":""},"drive_missing":null,"busy":null,"waiting":0,
             "folders":\(list),"last_checks":[]}
            """
        let d = JSONDecoder()
        d.keyDecodingStrategy = .convertFromSnakeCase
        return try! d.decode(Status.self, from: Data(json.utf8))
    }

    static func sampleResults() -> [FoundItem] {
        func item(_ kind: String, _ id: Int, _ name: String, _ detail: String) -> FoundItem {
            FoundItem(kind: kind, discogsId: id, name: name, discogsName: name, detail: detail, thumb: "",
                      url: "https://www.discogs.com/\(kind)/\(id)")
        }
        return [
            item("artist", 307, "Boards Of Canada", ""),
            item("artist", 11326229, "Boards Of Canada", "listed on Discogs as \"Boards Of Canada (3)\""),
            item("label", 52, "Music70", ""),
            item("master", 2129, "Boards Of Canada - Geogaddi", "2002 · Warp Records · Album"),
            item("master", 5590, "Boards Of Canada - Music Has The Right To Children", "1998 · Warp Records · LP"),
        ]
    }
}
