// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI
import UserNotifications

// MARK: - What the engine reports

struct Status: Decodable {
    var version: String
    var home: String
    var configPath: String
    var musicFolder: String
    var username: String
    var hasPassword: Bool
    var hasToken: Bool
    var formats: [String]
    var minBitrate: Int
    var everyHours: Double
    var timer: TimerState
    var driveMissing: String?
    var busy: Int?
    var waiting: Int?
    var folders: [Folder]
    var lastChecks: [Check]?

    struct TimerState: Decodable {
        var on: Bool
        var installed: Bool
        var program: String
        var thisHome: Bool?
        /// On, and checking this setup's folders (not another copy of soulget's).
        var active: Bool { on && (thisHome ?? true) }
    }

    struct Folder: Decodable, Identifiable, Hashable {
        var name: String
        var path: String
        var kind: String
        var url: String
        var total: Int
        var have: Int
        var looking: Int
        var gaveUp: Int
        var updated: String
        var id: String { path }
        var discogsKey: String? { DiscogsLink.key(url) }
    }

    struct Check: Decodable {
        var when: String
        var folder: String
        var searched: Int
        var found: Int
        var stillMissing: Int
        var trigger: String
    }
}

struct FoundItem: Decodable, Identifiable, Hashable {
    var kind: String          // artist, label, master, release
    var discogsId: Int
    var name: String
    var discogsName: String
    var detail: String
    var thumb: String
    var url: String

    enum CodingKeys: String, CodingKey { case kind, discogsId = "id", name, discogsName, detail, thumb, url }
    var id: String { "\(kind):\(discogsId)" }
    var isRecord: Bool { kind == "master" || kind == "release" }
    var kindLabel: String { kind == "artist" ? "Artist" : kind == "label" ? "Record label" : "Album, EP or single" }
}

struct FindResponse: Decodable {
    var results: [FoundItem]
    var error: String?
    var hasToken: Bool?
}

enum DiscogsLink {
    /// "artist:307" for any Discogs link to that page (with or without the name part).
    static func key(_ url: String) -> String? {
        guard let m = url.range(of: #"(artist|label|master|release)s?/(\d+)"#, options: .regularExpression) else { return nil }
        let parts = url[m].split(separator: "/")
        guard parts.count == 2 else { return nil }
        let kind = parts[0].hasSuffix("s") ? String(parts[0].dropLast()) : String(parts[0])
        return "\(kind):\(parts[1])"
    }
}

// MARK: - Choices

enum LoginMode: String { case auto, own }

enum Quality: String, CaseIterable, Identifiable {
    case best, smaller, lossless, mp3
    var id: String { rawValue }

    var formats: [String] {
        switch self {
        case .best: ["flac", "wav", "aiff", "mp3"]
        case .smaller: ["mp3", "flac"]
        case .lossless: ["flac", "wav", "aiff"]
        case .mp3: ["mp3"]
        }
    }

    var title: String {
        switch self {
        case .best: "Best quality"
        case .smaller: "Smaller files"
        case .lossless: "Lossless only"
        case .mp3: "MP3 only"
        }
    }

    var detail: String {
        switch self {
        case .best: "Lossless (FLAC, WAV, AIFF) first; MP3 when that's all anyone has"
        case .smaller: "MP3 first; lossless when there's no good MP3"
        case .lossless: "FLAC, WAV or AIFF; skips tracks that only exist as MP3"
        case .mp3: "MP3 only, best bitrate first"
        }
    }

    static func from(_ formats: [String]) -> Quality? { allCases.first { $0.formats == formats } }

    /// The lowest MP3 bitrates on offer. 320 is the default; lower ones are only used when chosen.
    static let bitrates = [320, 256, 192, 128]

    static func bitrateLabel(_ kbps: Int) -> String {
        kbps >= 320 ? "320 kbps (best)" : "\(kbps) kbps"
    }
}

// MARK: - Runs

struct Request: Identifiable, Equatable {
    enum Kind { case get, retry }
    let id = UUID()
    let title: String
    let args: [String]
    let kind: Kind
}

struct ActiveTrack: Identifiable {
    let id: String
    var title = ""
    var detail = ""
    var percent: Double?
    var note: String?
}

struct Summary {
    var downloaded = 0, had = 0, notFound = 0, failed = 0, gaveUp = 0
}

struct RunState {
    var request: Request
    var phase = "Starting…"
    var lastLine = ""
    var folder: String?
    var folderPath: String?
    var pages: (done: Int, of: Int)?
    var total: Int?
    var todo: Int?
    var had = 0
    var processed = 0, downloaded = 0, missing = 0, alreadyHad = 0
    var retryTotal = 0
    var retryFound: Int?
    var nothingToDo = false
    var active: [ActiveTrack] = []
    var loggedIn = false
    var stopping = false
    var stopped = false
    var stopNote: String?
    var queued: Int?
    var skipped: String?
    var loginFailed: String?
    var error: String?
    var summary: Summary?

    init(request: Request) { self.request = request }

    mutating func upsert(_ key: String, _ change: (inout ActiveTrack) -> Void) {
        if let i = active.firstIndex(where: { $0.id == key }) { change(&active[i]) } else {
            var a = ActiveTrack(id: key)
            change(&a)
            active.append(a)
        }
    }

    /// 0...1 when it's known how far along this is.
    var fraction: Double? {
        if let p = pages, p.of > 0, !loggedIn, total == nil { return Double(p.done) / Double(p.of) }
        if request.kind == .retry { return retryTotal > 0 ? min(1, Double(processed) / Double(retryTotal)) : nil }
        if let todo, todo > 0, loggedIn { return min(1, Double(processed) / Double(todo)) }
        return nil
    }

    var countLine: String? {
        if let p = pages, !loggedIn, total == nil { return "Discogs pages read: \(p.done) of \(p.of)" }
        if request.kind == .retry {
            guard retryTotal > 0, loggedIn else { return nil }
            return "Looked for \(processed) of \(retryTotal) missing tracks · found \(downloaded)"
        }
        guard let todo, loggedIn else { return nil }
        var parts = ["\(processed) of \(todo) tracks", "\(downloaded) downloaded"]
        if missing > 0 { parts.append("\(missing) not found yet") }
        if had > 0 { parts.append("\(had) you already had") }
        return parts.joined(separator: " · ")
    }
}

struct Finished: Identifiable {
    let id = UUID()
    let title: String
    let text: String
    let ok: Bool
    let folderPath: String?
    let date = Date()
}

// MARK: - The app's state

@MainActor
final class AppModel: ObservableObject {
    static let shared = AppModel()

    @Published var ready = false
    @Published var setupProblem: String?
    @Published var status: Status?
    @Published var query = ""
    @Published var searchedFor: String?
    @Published var searching = false
    @Published var results: [FoundItem] = []
    @Published var searchError: String?
    @Published var run: RunState?
    @Published var upNext: [Request] = []
    @Published var finished: [Finished] = []
    @Published var log: [String] = []
    @Published var loginMode: LoginMode = .auto
    @Published var settingsError: String?
    @Published var working = false   // a settings change is being saved
    @Published var checks: [CheckRun] = []
    @Published var openFolder: Status.Folder?
    @Published var showHistory = false
    @Published var optionsItem: FoundItem?
    @Published var preview: PreviewState?

    private(set) var engine: Engine?
    private var process: Process?
    private var bootstrapped = false
    private var searchTask: Task<Void, Never>?
    private var loginRetries = 0
    private var whenIdle: (() -> Void)?
    private var previewProcess: Process?
    var demo = false   // screenshots: no engine

    var isRunning: Bool { run != nil }

    // MARK: start-up

    func bootstrap() async {
        guard !bootstrapped, !demo else { return }
        bootstrapped = true
        do {
            // -engine <path>: a different engine program (developer testing).
            let program = try UserDefaults.standard.string(forKey: "engine").map { URL(fileURLWithPath: $0) } ?? EngineSetup.install()
            let home = Home.choose()
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
            engine = Engine(program: program, home: home)
        } catch {
            setupProblem = error.localizedDescription
            return
        }
        await refresh()
        await ensureLogin()
        ready = status != nil
        await loadChecks()
        Task { await keepStatusFresh() }
    }

    /// First start: soulget makes its own Soulseek login, so there's nothing to fill in.
    private func ensureLogin() async {
        guard let s = status else { return }
        if s.username.isEmpty || !s.hasPassword {
            let login = AutoLogin.load() ?? AutoLogin.make()
            var changes: [String: Any] = ["soulseek.username": login.username, "soulseek.password": login.password]
            // A brand-new setup starts with the best quality; an existing one keeps its choice.
            if s.folders.isEmpty && s.formats == Quality.smaller.formats { changes["download.formats"] = Quality.best.formats }
            if await set(changes) { login.save() }
            await refresh()
        }
        let auto = AutoLogin.load()
        loginMode = (auto != nil && auto?.username == status?.username) ? .auto : .own
    }

    func refresh() async {
        guard let engine else { return }
        let r = await engine.run(["--app-status"])
        if let s = decodeLastJSON(r.out, as: Status.self) {
            status = s
            setupProblem = nil
        } else {
            setupProblem = "soulget couldn't read its settings: " + firstLine(r.err.isEmpty ? r.out : r.err)
        }
    }

    private func keepStatusFresh() async {
        var n = 0
        while true {
            try? await Task.sleep(nanoseconds: 30_000_000_000)
            n += 1
            if run == nil { await refresh() }
            if n % 10 == 0 { await loadChecks() }   // a timed check may have run
        }
    }

    func loadChecks() async {
        guard let engine else { return }
        struct Answer: Decodable { var runs: [CheckRun] }
        if let a = decodeLastJSON((await engine.run(["--app-checks"])).out, as: Answer.self) { checks = a.runs }
    }

    func loadFolder(_ path: String) async -> FolderDetail? {
        guard let engine else { return nil }
        return decodeLastJSON((await engine.run(["--app-folder", path])).out, as: FolderDetail.self)
    }

    // MARK: search

    func search() {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, let engine else { return }
        searchTask?.cancel()
        searching = true
        searchedFor = q
        searchError = nil
        results = []
        searchTask = Task {
            let r = await engine.run(["--app-find", q])
            guard !Task.isCancelled, searchedFor == q else { return }
            searching = false
            guard let found = decodeLastJSON(r.out, as: FindResponse.self) else {
                searchError = "Couldn't search Discogs. " + firstLine(r.err)
                return
            }
            results = found.results
            if let e = found.error { searchError = e } else if found.results.isEmpty {
                searchError = "Nothing on Discogs matches “\(q)”. Check the spelling, or paste the Discogs link."
            }
        }
    }

    func clearSearch() {
        searchTask?.cancel()
        searching = false
        searchedFor = nil
        results = []
        searchError = nil
        query = ""
    }

    /// The folder you already have for this Discogs artist, label or record.
    func folder(for item: FoundItem) -> Status.Folder? {
        status?.folders.first { $0.discogsKey == item.id }
    }

    // MARK: downloads

    func get(_ item: FoundItem, _ options: GetOptions = .everything) {
        let what = item.kind == "label" ? "the \(item.name) label" : item.name
        enqueue(Request(title: (options.later ? "Adding \(what) to the list" : "Getting \(what)") + options.label,
                        args: options.args(for: item) + ["--events"], kind: .get))
    }

    // MARK: preview (reads Discogs only: no login, so it runs alongside downloads)

    func startPreview(_ item: FoundItem, _ options: GetOptions = .everything) {
        guard let engine else { return }
        closePreview(keep: false)
        var o = options
        o.later = false
        preview = PreviewState(item: item, options: o)
        do {
            previewProcess = try engine.start(o.args(for: item) + ["--dry-run", "--events"],
                                              onLine: { [weak self] line in self?.previewLine(line) },
                                              onExit: { [weak self] code in Task { await self?.previewDone(code) } })
        } catch { preview?.error = "Couldn't start: \(error.localizedDescription)" }
    }

    private func previewLine(_ line: String) {
        guard var p = preview else { return }
        if line.hasPrefix("@@ ") {
            guard let e = try? JSONSerialization.jsonObject(with: Data(line.dropFirst(3).utf8)) as? [String: Any] else { return }
            func s(_ k: String) -> String { e[k] as? String ?? "" }
            func n(_ k: String) -> Int { (e[k] as? NSNumber)?.intValue ?? 0 }
            switch s("ev") {
            case "found": p.phase = "Found on Discogs: \(s("name")). Reading its records…"
            case "discogs": p.pages = (0, n("pages")); p.phase = "Reading \(n("pages")) record pages on Discogs (about \(n("minutes")) min the first time)"
            case "discogs_progress": p.pages = (n("done"), n("of"))
            case "list": p.phase = "Making the list…"
            case "preview":
                p.summary = PreviewSummary(total: n("total"), releases: n("releases"), duplicates: n("duplicates"),
                                           remixes: n("remixes"), had: n("had"),
                                           remixesLeftOut: (e["remixes_left_out"] as? Bool) ?? false, path: s("path"))
            case "error": p.error = s("message")
            default: break
            }
        } else if !line.trimmingCharacters(in: .whitespaces).isEmpty {
            p.detail = line.trimmingCharacters(in: .whitespaces)
        }
        preview = p
    }

    private func previewDone(_ code: Int32) async {
        previewProcess = nil
        guard var p = preview else { return }
        p.finished = true
        if let s = p.summary {
            p.tracks = await loadFolder(s.path)
            if p.tracks == nil { p.error = "Couldn't read the track list." }
        } else if p.error == nil {
            p.error = code == 0 ? "Discogs gave no track list." : "Couldn't read the track list (code \(code))."
        }
        if preview?.id == p.id { preview = p }
    }

    /// keep: the folder is about to be used. Otherwise a folder made only for the preview is removed again.
    func closePreview(keep: Bool) {
        previewProcess?.interrupt()
        previewProcess = nil
        if !keep, let path = preview?.summary?.path {
            let fm = FileManager.default
            let files = (try? fm.contentsOfDirectory(atPath: path)) ?? []
            if files.allSatisfy({ $0 == "tracklist.csv" || $0 == ".DS_Store" }) { try? fm.removeItem(atPath: path) }
        }
        preview = nil
    }

    func getFromPreview(originalsOnly: Bool) {
        guard let p = preview else { return }
        var o = p.options
        if originalsOnly { o.originalsOnly = true }
        closePreview(keep: true)
        get(p.item, o)
    }

    /// Run a folder again with ITS OWN saved settings (quality, strict, years...), not today's defaults.
    /// minBitrate: accept lower MP3 quality from now on (saved with the folder, so the 7-hour check uses it too).
    func getAgain(_ folder: Status.Folder, minBitrate: Int? = nil) async {
        guard !folder.url.isEmpty else { return checkMissingNow() }
        var args = folder.kind == "master" || folder.kind == "release" ? ["--album", folder.url] : [folder.url]
        let job = await loadFolder(folder.path)?.job
        if let job {
            args += ["--formats", job.formats.joined(separator: ","), "--min-bitrate", String(minBitrate ?? job.minBitrate)]
            if job.noRemixes { args.append("--no-remixes") }
            if job.noCompilations { args.append("--no-compilations") }
            if job.theirRemixes { args.append("--their-remixes") }
            if job.strict { args.append("--strict") }
            if let years = job.years { args += ["--years", years] }
        } else if let minBitrate {
            args += ["--min-bitrate", String(minBitrate)]
        }
        let extra = minBitrate.map { " (MP3 down to \($0) kbps)" } ?? ""
        enqueue(Request(title: "Getting \(folder.name)" + extra, args: args + ["--events"], kind: .get))
    }

    func checkMissingNow() {
        guard !upNext.contains(where: { $0.kind == .retry }), run?.request.kind != .retry else { return }
        enqueue(Request(title: "Looking for missing tracks", args: ["--retry-missing", "--events"], kind: .retry))
    }

    private func enqueue(_ r: Request) {
        if run == nil { start(r) } else { upNext.append(r) }
    }

    func remove(_ r: Request) { upNext.removeAll { $0.id == r.id } }

    private func start(_ r: Request) {
        guard let engine else { return }
        run = RunState(request: r)
        appendLog("")
        appendLog("── \(r.title) ──")
        do {
            process = try engine.start(r.args,
                                       onLine: { [weak self] line in self?.handle(line) },
                                       onExit: { [weak self] code in self?.finish(code) })
        } catch {
            run = nil
            finished.insert(Finished(title: r.title, text: "Couldn't start: \(error.localizedDescription)", ok: false, folderPath: nil), at: 0)
        }
    }

    /// First press: finish the downloads in progress, then stop. Second press: stop at once.
    func stop() {
        guard let p = process, run != nil else { return }
        run?.stopping = true
        p.interrupt()
    }

    /// Quitting: empty the line, stop, and call back when the engine has ended (or after 15 s).
    func stopAll(then done: @escaping () -> Void) {
        upNext.removeAll()
        guard let p = process else { return done() }
        whenIdle = done
        run?.stopping = true
        p.interrupt()
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in
            guard let self, let cb = self.whenIdle else { return }
            self.process?.interrupt()
            self.whenIdle = nil
            cb()
        }
    }

    private func handle(_ line: String) {
        if line.hasPrefix("@@ ") {
            guard let ev = try? JSONSerialization.jsonObject(with: Data(line.dropFirst(3).utf8)) as? [String: Any] else { return }
            apply(ev)
        } else {
            appendLog(line)
            let t = line.trimmingCharacters(in: .whitespaces)
            if !t.isEmpty { run?.lastLine = t }
        }
    }

    private func appendLog(_ line: String) {
        log.append(line)
        if log.count > 4000 { log.removeFirst(1000) }
    }

    private func apply(_ e: [String: Any]) {
        guard var r = run else { return }
        func s(_ k: String) -> String { e[k] as? String ?? "" }
        func n(_ k: String) -> Int { (e[k] as? NSNumber)?.intValue ?? 0 }
        switch s("ev") {
        case "found":
            r.phase = "Found on Discogs: \(s("name"))"
        case "discogs":
            r.pages = (0, n("pages"))
            r.phase = "Reading \(n("pages")) record pages on Discogs (about \(n("minutes")) min, faster next time)"
        case "discogs_progress":
            r.pages = (n("done"), n("of"))
        case "list":
            r.folder = s("folder")
            r.folderPath = s("path")
            r.total = n("total")
            r.phase = "\(n("total")) tracks on the list"
        case "todo":
            r.todo = n("todo")
            r.had = n("had")
            if n("todo") == 0 { r.phase = "You already have every track" }
        case "queued":
            r.queued = n("count")
        case "waiting":
            r.phase = "Waiting its turn: \(s("reason"))"
        case "login":
            switch s("state") {
            case "start": r.phase = "Logging in to Soulseek…"
            case "ok": r.loggedIn = true; r.phase = r.request.kind == .retry ? "Looking for missing tracks" : "Searching and downloading"
            case "failed": r.loginFailed = s("message"); r.phase = "Couldn't log in"
            default: break
            }
        case "folder":
            r.folder = s("folder")
            r.folderPath = s("path")
            if r.request.kind == .retry { r.phase = "Looking for \(s("folder"))’s missing tracks" }
        case "retry":
            switch s("state") {
            case "start": r.phase = "Checking what's still missing…"
            case "nothing": r.nothingToDo = true; r.phase = "Nothing is missing"
            case "done": r.retryFound = n("found")
            default: break
            }
        case "retry_folder":
            r.retryTotal += n("missing")
        case "track":
            let key = s("key")
            switch s("state") {
            case "downloading":
                r.upsert(key) { $0.title = s("title"); $0.detail = "\(s("quality")) from \(s("from"))"; $0.percent = nil; $0.note = nil }
            case "progress":
                let m = s("message")
                r.upsert(key) { a in
                    if a.title.isEmpty { a.title = s("title") }
                    if let pct = Self.percent(m) { a.percent = pct; a.note = Self.speed(m) } else if m.hasPrefix("waiting") { a.note = "waiting in their queue" }
                }
            case "retrying":
                r.upsert(key) { $0.title = s("title"); $0.percent = nil; $0.note = "trying someone else" }
            case "done", "missing":
                r.active.removeAll { $0.id == key }
            default: break
            }
        case "result":
            r.processed += 1
            switch s("status") {
            case "downloaded": r.downloaded += 1
            case "not found", "failed": r.missing += 1
            default: r.alreadyHad += 1
            }
        case "stopping":
            r.stopNote = s("reason")
        case "summary":
            r.summary = Summary(downloaded: n("downloaded"), had: n("had"), notFound: n("not_found"), failed: n("failed"), gaveUp: n("gave_up"))
        case "skipped":
            r.skipped = s("reason")
            r.phase = "Skipped: \(s("reason"))"
        case "stopped":
            r.stopped = true
        case "error":
            r.error = s("message")
        default:
            break
        }
        run = r
    }

    static func percent(_ m: String) -> Double? {
        guard let r = m.range(of: #"^\d+(?=% of)"#, options: .regularExpression) else { return nil }
        return Double(m[r])
    }

    static func speed(_ m: String) -> String? {
        guard let r = m.range(of: #"\d+ KB/s"#, options: .regularExpression) else { return nil }
        return String(m[r])
    }

    private func finish(_ code: Int32) {
        guard let r = run else { return }
        process = nil
        // The made-up name was taken by someone else: make another one and try again.
        if let failed = r.loginFailed, loginMode == .auto, loginRetries < 2, failed.contains("rejected the login"), whenIdle == nil {
            loginRetries += 1
            appendLog("That Soulseek name is taken; making a new one and trying again.")
            Task {
                await self.useAutoLogin(fresh: true)
                self.run = nil
                self.start(r.request)
            }
            return
        }
        loginRetries = 0
        let (text, ok) = describe(r, code: code)
        finished.insert(Finished(title: r.request.title, text: text, ok: ok, folderPath: r.folderPath), at: 0)
        if finished.count > 6 { finished.removeLast(finished.count - 6) }
        if r.request.kind == .get || (r.retryFound ?? 0) > 0 { notify(r.request.title, text) }
        run = nil
        if let cb = whenIdle { whenIdle = nil; cb(); return }
        let wasRetry = r.request.kind == .retry
        Task {
            await refresh()
            if wasRetry { await loadChecks() }
        }
        if !upNext.isEmpty { start(upNext.removeFirst()) }
    }

    private func describe(_ r: RunState, code: Int32) -> (String, Bool) {
        if let e = r.error { return (e, false) }
        if let lf = r.loginFailed { return ("Couldn't log in to Soulseek: \(lf)", false) }
        if let sk = r.skipped { return ("Skipped: \(sk).", true) }
        let timerOn = status?.timer.active ?? false
        if let q = r.queued {
            return (q == 0 ? "You already have every track." :
                    "Added \(q) tracks to the list. " + (timerOn ? "The 7-hour check will look for them." : "Press “Look for missing tracks” to search for them."), true)
        }
        if r.request.kind == .retry {
            if r.nothingToDo { return ("Nothing was missing.", true) }
            let found = r.retryFound ?? r.downloaded
            return ((r.stopped ? "Stopped. " : "") + "Found \(found) of \(r.retryTotal) missing tracks.", true)
        }
        if let s = r.summary {
            var parts = ["\(s.downloaded) new track\(s.downloaded == 1 ? "" : "s")"]
            if s.had > 0 { parts.append("\(s.had) you already had") }
            let miss = s.notFound + s.failed
            if miss > 0 { parts.append("\(miss) not found yet") }
            var t = (r.stopped ? "Stopped. " : "") + parts.joined(separator: " · ") + "."
            if miss > 0 { t += timerOn ? " soulget keeps looking for them every 7 hours." : " Turn on the 7-hour check in Settings to keep looking." }
            return (t, true)
        }
        if r.stopped { return ("Stopped. Finished files are kept.", true) }
        return code == 0 ? ("Done.", true) : ("Stopped unexpectedly (code \(code)). Open Details for the last messages.", false)
    }

    private func notify(_ title: String, _ text: String) {
        guard !NSApp.isActive else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = text
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    // MARK: settings

    @discardableResult
    func set(_ changes: [String: Any]) async -> Bool {
        guard let engine, let data = try? JSONSerialization.data(withJSONObject: changes) else { return false }
        working = true
        defer { working = false }
        let r = await engine.run(["--app-set"], input: data)
        if r.out.contains("\"ok\":true") { settingsError = nil; return true }
        struct Answer: Decodable { var error: String? }
        settingsError = decodeLastJSON(r.out, as: Answer.self)?.error ?? firstLine(r.err)
        return false
    }

    func setQuality(_ q: Quality) async {
        await set(["download.formats": q.formats])
        await refresh()
    }

    func setMinBitrate(_ kbps: Int) async {
        await set(["download.default_min_bitrate": kbps])
        await refresh()
    }

    func setMusicFolder(_ url: URL) async {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        var path = url.path
        if path.hasPrefix(home + "/") { path = "~" + path.dropFirst(home.count) }
        await set(["soulseek.download_folder": path])
        await refresh()
    }

    func setTimer(_ on: Bool) async {
        guard let engine else { return }
        if on, let t = status?.timer, t.on, t.thisHome == false {
            // A Mac has one soulget timer; another copy of soulget set it up for its own folder.
            let alert = NSAlert()
            alert.messageText = "Move the 7-hour check to this soulget?"
            alert.informativeText = "Your Mac already runs the 7-hour check for another soulget (\(tilde(t.program))). There is one check per Mac, so that one's folders won't be checked any more."
            alert.addButton(withTitle: "Move It Here")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        working = true
        let r = await engine.run(["--schedule", on ? "on" : "off"])
        working = false
        settingsError = r.code == 0 ? nil : firstLine(r.err.isEmpty ? r.out : r.err)
        await refresh()
    }

    func setDiscogsKey(_ key: String) async {
        await set(["discogs.token": key.trimmingCharacters(in: .whitespacesAndNewlines)])
        await refresh()
    }

    func useOwnLogin(_ user: String, _ password: String) async {
        let u = user.trimmingCharacters(in: .whitespaces)
        guard !u.isEmpty, !password.isEmpty else { settingsError = "Fill in both the name and the password."; return }
        if await set(["soulseek.username": u, "soulseek.password": password]) { loginMode = .own }
        await refresh()
    }

    func useAutoLogin(fresh: Bool = false) async {
        let login = fresh ? AutoLogin.make() : (AutoLogin.load() ?? AutoLogin.make())
        if await set(["soulseek.username": login.username, "soulseek.password": login.password]) {
            login.save()
            loginMode = .auto
        }
        await refresh()
    }

    // MARK: menu bar

    var menuStatus: String {
        if let r = run {
            if let line = r.countLine { return "\(r.request.title): \(line)" }
            return "\(r.request.title): \(r.phase)"
        }
        if status?.busy != nil { return "A check for missing tracks is running" }
        if let d = status?.driveMissing { return "Music drive not connected (\(d))" }
        return "Nothing running"
    }
}
