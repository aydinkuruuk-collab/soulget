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
            NSApp.terminate(nil)
        }
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
