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
        let option: GetOption = d.string(forKey: "autotestOption") == "later" ? .later : .everything
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
            say("no result of kind \(kind ?? "any")"); NSApp.terminate(nil); return
        }
        say("get: \(item.name) (\(item.kind)) option \(option)")
        m.clearSearch()
        m.get(item, option)

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
        say("log tail:\n" + m.log.suffix(15).joined(separator: "\n"))
        NSApp.terminate(nil)
    }
}
