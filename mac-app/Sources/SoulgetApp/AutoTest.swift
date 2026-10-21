// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit

/// Developer test, driving the real app the way a person would:
///   -autotest <search> [-autotestKind master] [-autotestOption later] [-autotestStopAfter 6] -autotestShots <folder>
/// Searches, clicks Get on the first result of that kind, takes pictures while it runs, then quits.
@MainActor
enum AutoTest {
    static func run(query: String) async {
        let m = AppModel.shared
        let d = UserDefaults.standard
        let shots = URL(fileURLWithPath: d.string(forKey: "autotestShots") ?? NSTemporaryDirectory())
        try? FileManager.default.createDirectory(at: shots, withIntermediateDirectories: true)
        let kind = d.string(forKey: "autotestKind")
        let optionName = d.string(forKey: "autotestOption") ?? ""
        let option: GetOptions = optionName == "later" ? .laterOnly : .everything
        let stopAfter = d.double(forKey: "autotestStopAfter")
        func say(_ s: String) { print("autotest: \(s)"); fflush(stdout) }
        func wait(_ seconds: Double) async { try? await Task.sleep(nanoseconds: UInt64(seconds * 1e9)) }

        var waited = 0.0
        while !m.ready, waited < 90 { await wait(0.3); waited += 0.3; if m.setupProblem != nil { break } }
        guard m.ready, let window = NSApp.windows.first(where: { $0.title == "soulget" }) else {
            say("not ready: \(m.setupProblem ?? "timeout")"); NSApp.terminate(nil); return
        }
        say("ready; home \(m.engine?.home.path ?? "?"); login mode \(m.loginMode); folders \(m.status?.folders.count ?? -1)")
        await Snapshots.shoot(window, shots, "a-ready")

        m.query = query
        m.search()
        waited = 0
        while m.searching, waited < 90 { await wait(0.3); waited += 0.3 }
        say("search: \(m.results.count) results, error: \(m.searchError ?? "none")")
        await Snapshots.shoot(window, shots, "b-results")
        guard let item = m.results.first(where: { kind == nil || $0.kind == kind }) else {
            say("no result of kind \(kind ?? "any")")
            await shootFolderAndHistory(m, window, shots, say)
            NSApp.terminate(nil); return
        }
        m.clearSearch()
        if optionName == "preview" {
            // Preview, take pictures, then Cancel: the folder made for the preview must disappear again.
            say("preview: \(item.name) (\(item.kind))")
            m.startPreview(item)
            var w = 0.0
            while !(m.preview?.finished ?? true), w < 600 { await wait(0.5); w += 0.5; if Int(w * 2) % 8 == 0 { await shootSheet(window, shots, "p-preview-\(Int(w))") } }
            await wait(1)
            await shootSheet(window, shots, "p-preview-done")
            let s = m.preview?.summary
            say("preview: \(s.map { "\($0.total) tracks, \($0.releases) records, \($0.duplicates) duplicates, \($0.remixes) versions, had \($0.had)" } ?? "no summary"); error \(m.preview?.error ?? "none"); rows \(m.preview?.tracks?.tracks.count ?? -1)")
            let path = s?.path ?? ""
            m.closePreview(keep: false)
            say("after Cancel the preview folder \(FileManager.default.fileExists(atPath: path) ? "is still there" : "is gone")")
            NSApp.terminate(nil)
            return
        }
        if optionName == "again", let f = m.folder(for: item) {
            // "Get missing" on a folder you have: its own saved settings, plus -autotestMinBitrate if given.
            let kbps = d.integer(forKey: "autotestMinBitrate")
            say("again: \(f.name) min bitrate \(kbps > 0 ? String(kbps) : "as saved")")
            await m.getAgain(f, minBitrate: kbps > 0 ? kbps : nil)
        } else {
            say("get: \(item.name) (\(item.kind)) option \(optionName)")
            m.get(item, option)
        }

        var t = 0.0, n = 0
        while m.run != nil || !m.upNext.isEmpty {
            await wait(0.5); t += 0.5
            if t.truncatingRemainder(dividingBy: 3) == 0 { await Snapshots.shoot(window, shots, String(format: "c-run-%02d", n)); n += 1 }
            if stopAfter > 0, t >= stopAfter, let r = m.run, !r.stopping { say("pressing Stop"); m.stop() }
            if t > 900 { say("timeout"); break }
        }
        await m.refresh()
        await wait(1)
        await Snapshots.shoot(window, shots, "d-finished")
        for f in m.finished { say("finished: \(f.title) | ok=\(f.ok) | \(f.text)") }
        for f in m.status?.folders ?? [] { say("folder: \(f.name) have \(f.have) of \(f.total), looking \(f.looking)") }
        await shootFolderAndHistory(m, window, shots, say)
        say("log tail:\n" + m.log.suffix(15).joined(separator: "\n"))
        NSApp.terminate(nil)
    }

    /// Opens a folder's tracks (-autotestFolder <name>, or the first folder) and the check history, with pictures.
    static func shootFolderAndHistory(_ m: AppModel, _ window: NSWindow, _ shots: URL, _ say: (String) -> Void) async {
        func wait(_ seconds: Double) async { try? await Task.sleep(nanoseconds: UInt64(seconds * 1e9)) }
        let wanted = UserDefaults.standard.string(forKey: "autotestFolder")
        if let f = m.status?.folders.first(where: { wanted == nil || $0.name == wanted }) {
            let t0 = Date()
            let detail = await m.loadFolder(f.path)
            say("folder \(f.name): \(detail?.tracks.count ?? -1) tracks \(detail?.counts ?? [:]) read in \(String(format: "%.1f", Date().timeIntervalSince(t0))) s")
            m.openFolder = f
            await wait(3)
            await shootSheet(window, shots, "e-folder")
            m.openFolder = nil
            await wait(0.8)
        }
        await m.loadChecks()
        say("history: \(m.checks.count) checks; newest: \(m.checks.first.map { "\($0.started) \($0.result)" } ?? "none")")
        if !m.checks.isEmpty {
            m.showHistory = true
            await wait(1.5)
            await shootSheet(window, shots, "f-history")
            m.showHistory = false
            await wait(0.5)
        }
    }

    /// A picture of the sheet on top of the window, or of the window if there's none.
    static func shootSheet(_ window: NSWindow, _ dir: URL, _ name: String) async {
        await Snapshots.shoot(window.attachedSheet ?? window, dir, name)
    }
}
