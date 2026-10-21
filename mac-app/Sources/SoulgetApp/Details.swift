// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI

// MARK: - Data

struct FolderDetail: Decodable {
    var name: String
    var path: String
    var job: JobInfo?
    var counts: [String: Int]
    var tracks: [FolderTrack]

    func count(_ k: String) -> Int { counts[k] ?? 0 }
    var missingCount: Int { count("missing") + count("gave_up") + count("not_tried") }
    var haveCount: Int { count("have") + count("elsewhere") }
}

struct JobInfo: Decodable {
    var kind: String
    var url: String
    var formats: [String]
    var minBitrate: Int
    var strict: Bool
    var noRemixes: Bool
    var noCompilations: Bool
    var theirRemixes: Bool
    var years: String?

    var summary: String {
        var parts = [Quality.from(formats)?.title ?? formats.joined(separator: ", ")]
        if noRemixes { parts.append("originals only") }
        if noCompilations { parts.append("no compilations") }
        if theirRemixes { parts.append("with their remixes for others") }
        if strict { parts.append("strict name matching") }
        if let years { parts.append(years.replacingOccurrences(of: "-", with: "–")) }
        if minBitrate < 320 { parts.append("MP3 down to \(minBitrate) kbps") }
        return parts.joined(separator: " · ")
    }
}

struct FolderTrack: Decodable {
    var artist: String
    var title: String
    var release: String
    var year: Int
    var catno: String
    var position: String
    var length: String
    var remix: Bool
    var status: String       // have, elsewhere, missing, gave_up, not_tried
    var file: String?
    var format: String?
    var note: String?
    var checks: Int?

    var isMissing: Bool { status == "missing" || status == "gave_up" || status == "not_tried" }
}

struct CheckRun: Decodable, Identifiable {
    var started: String
    var trigger: String
    var result: String       // done, nothing, skipped, crashed, login_failed, unfinished, running
    var reason: String
    var stoppedEarly: Bool
    var searched: Int
    var found: Int
    var stillMissing: Int
    var folders: [CheckFolder]
    var id: String { started + trigger }

    var date: Date? { DateFormatter.localISO.date(from: started) }

    /// One line, as shown under "Your music" and in the history.
    var line: String {
        switch result {
        case "done":
            return found > 0 ? "found \(found) of \(searched) missing tracks" : "searched \(searched) missing tracks, found none"
        case "nothing": return "nothing was missing"
        case "skipped": return "skipped: \(reason)"
        case "crashed": return "stopped: \(reason)"
        case "login_failed": return "couldn't log in to Soulseek"
        case "running": return "running now"
        default: return "didn't finish (the Mac may have slept or restarted)"
        }
    }
}

struct CheckFolder: Decodable {
    var folder: String
    var searched: Int
    var found: Int
    var stillMissing: Int
}

/// Choices for one download (the Get button's "More options").
struct GetOptions: Equatable {
    var originalsOnly = false
    var noCompilations = false
    var theirRemixes = false
    var strict = false
    var later = false
    var yearsOn = false
    var yearFrom = 1990
    var yearTo = Calendar.current.component(.year, from: Date())
    var quality: Quality?    // nil: as in Settings
    var minBitrate: Int?     // nil: as in Settings

    static let everything = GetOptions()
    static let originals = GetOptions(originalsOnly: true)
    static let noComps = GetOptions(noCompilations: true)
    static let laterOnly = GetOptions(later: true)

    func args(for item: FoundItem) -> [String] {
        var a = item.isRecord ? ["--album", item.url] : [item.url]
        if originalsOnly { a.append("--no-remixes") }
        if noCompilations, !item.isRecord { a.append("--no-compilations") }
        if theirRemixes, item.kind == "artist" { a.append("--their-remixes") }
        if strict, item.kind == "artist" { a.append("--strict") }
        if yearsOn, !item.isRecord { a += ["--years", "\(min(yearFrom, yearTo))-\(max(yearFrom, yearTo))"] }
        if let q = quality { a += ["--formats", q.formats.joined(separator: ",")] }
        if let kbps = minBitrate { a += ["--min-bitrate", String(kbps)] }
        if later { a.append("--later") }
        return a
    }

    /// Added to the title of the run, so the line says what was asked.
    var label: String {
        var parts: [String] = []
        if originalsOnly { parts.append("originals only") }
        if noCompilations { parts.append("no compilations") }
        if strict { parts.append("strict") }
        if yearsOn { parts.append("\(min(yearFrom, yearTo))–\(max(yearFrom, yearTo))") }
        if let q = quality { parts.append(q.title.lowercased()) }
        if let kbps = minBitrate, kbps < 320 { parts.append("MP3 down to \(kbps) kbps") }
        return parts.isEmpty ? "" : " (\(parts.joined(separator: ", ")))"
    }
}

struct PreviewSummary {
    var total = 0, releases = 0, duplicates = 0, remixes = 0, had = 0
    var remixesLeftOut = false
    var path = ""
}

struct PreviewState: Identifiable {
    let id = UUID()
    let item: FoundItem
    var options: GetOptions
    var phase = "Reading the track list on Discogs…"
    var detail = ""
    var pages: (done: Int, of: Int)?
    var summary: PreviewSummary?
    var tracks: FolderDetail?
    var error: String?
    var finished = false
}

extension DateFormatter {
    static let localISO: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        return f
    }()

    static let shortDay: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "EEE d MMM, HH:mm"
        return f
    }()
}

func ago(_ date: Date) -> String { RelativeDateTimeFormatter().localizedString(for: date, relativeTo: Date()) }

// MARK: - A folder's tracks

struct FolderSheet: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let folder: Status.Folder
    @State var detail: FolderDetail?
    @State private var filter = "missing"
    @State private var text = ""
    @State private var failed = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 12) {
                Thumb(url: "", kind: folder.kind)
                VStack(alignment: .leading, spacing: 3) {
                    Text(folder.name).font(.title3.weight(.semibold))
                    Text(headline).foregroundStyle(.secondary)
                    if let job = detail?.job { Text("Settings for this folder: \(job.summary)").font(.caption).foregroundStyle(.tertiary) }
                }
                Spacer()
                Button("Show in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: folder.path)) }
                if let u = URL(string: folder.url), !folder.url.isEmpty {
                    Link(destination: u) { Image(systemName: "arrow.up.right.square") }.help("Open on Discogs")
                }
            }
            .padding(18)

            if let d = detail, d.count("not_tried") > 0 {
                Banner(icon: "info.circle.fill",
                       text: (d.count("not_tried") == 1 ? "1 track was" : "\(d.count("not_tried")) tracks were") +
                             " never searched for (a test run with a limit, or a download that stopped early). The 7-hour check only looks again for tracks it has searched before.",
                       tint: .blue)
                    .padding(.horizontal, 18).padding(.bottom, 10)
            }

            if let d = detail, let job = d.job, lowQuality(d) > 0, !folder.url.isEmpty {
                HStack(alignment: .center, spacing: 10) {
                    Image(systemName: "dial.low").foregroundStyle(.orange)
                    Text("For \(lowQuality(d)) of the missing tracks, people do have copies, but below the \(job.minBitrate) kbps this folder asks for.")
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Menu("Accept Lower Quality") {
                        ForEach(Quality.bitrates.filter { $0 < job.minBitrate }, id: \.self) { kbps in
                            Button("Down to \(kbps) kbps") {
                                Task { await model.getAgain(folder, minBitrate: kbps) }
                                dismiss()
                            }
                        }
                    }
                    .fixedSize()
                    .disabled(!model.ready && !model.demo)
                    .help("Searches this folder again, now also taking MP3s down to that quality. Saved for this folder, so the 7-hour check does the same. Better copies still come first.")
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color.orange.opacity(0.12)))
                .padding(.horizontal, 18).padding(.bottom, 10)
            }

            HStack {
                Picker("Show", selection: $filter) {
                    Text("Missing (\(detail?.missingCount ?? folder.looking))").tag("missing")
                    Text("Have (\(detail?.haveCount ?? folder.have))").tag("have")
                    Text("All (\(detail?.tracks.count ?? folder.total))").tag("all")
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 360)
                Spacer()
                TextField("Filter", text: $text).textFieldStyle(.roundedBorder).frame(width: 180)
            }
            .padding(.horizontal, 18).padding(.bottom, 10)
            Divider()

            if let d = detail {
                let rows = shown(d)
                if rows.isEmpty {
                    Text(filter == "missing" ? "Nothing is missing here." : "No tracks to show.")
                        .foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(rows.enumerated()), id: \.offset) { i, t in
                                if i > 0 { Divider().padding(.leading, 44) }
                                TrackRow(track: t)
                            }
                        }
                    }
                }
            } else if failed {
                Text("Couldn't read this folder.").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }

            Divider()
            HStack {
                if let d = detail, d.missingCount > 0, !folder.url.isEmpty {
                    Button("Search for the \(d.missingCount) missing tracks now") {
                        Task { await model.getAgain(folder) }
                        dismiss()
                    }
                    .disabled(!model.ready)
                    .help("Runs this folder again: missing tracks, and anything new on Discogs")
                }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(14)
        }
        .frame(width: 760, height: 600)
        .task {
            guard detail == nil else { return }
            detail = await model.loadFolder(folder.path)
            failed = detail == nil
            if let d = detail, d.missingCount == 0 { filter = "all" }
        }
    }

    var headline: String {
        var parts = ["\(folder.have) of \(folder.total) tracks"]
        if folder.looking > 0 { parts.append("still looking for \(folder.looking)") }
        if folder.gaveUp > 0 { parts.append("\(folder.gaveUp) given up on") }
        return parts.joined(separator: " · ")
    }

    /// Missing tracks that were only turned down for being below the folder's minimum MP3 quality.
    func lowQuality(_ d: FolderDetail) -> Int {
        d.tracks.filter { $0.isMissing && ($0.note ?? "").contains("bitrate too low") }.count
    }

    func shown(_ d: FolderDetail) -> [FolderTrack] {
        let q = text.trimmingCharacters(in: .whitespaces).lowercased()
        return d.tracks.filter { t in
            (filter == "all" || (filter == "missing") == t.isMissing) &&
            (q.isEmpty || "\(t.artist) \(t.title) \(t.release) \(t.catno)".lowercased().contains(q))
        }
    }
}

struct TrackRow: View {
    let track: FolderTrack
    var preview = false   // a track list not downloaded yet: show lengths, not "not searched yet"

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon.0).foregroundStyle(icon.1).frame(width: 20).padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text("\(track.artist) – \(track.title)").lineLimit(1)
                    if track.remix { Tag(text: "version") }
                }
                Text(where_).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 12)
            Text(trailing)
                .font(.caption)
                .foregroundStyle(track.isMissing ? .secondary : .tertiary)
                .multilineTextAlignment(.trailing)
                .frame(maxWidth: 300, alignment: .trailing)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 7)
        .textSelection(.enabled)
    }

    var icon: (String, Color) {
        switch track.status {
        case "have": ("checkmark.circle.fill", .green)
        case "elsewhere": ("checkmark.circle", .green)
        case "missing": ("magnifyingglass.circle", .orange)
        case "gave_up": ("xmark.circle", .secondary)
        default: (preview ? "circle" : "circle.dashed", .secondary)
        }
    }

    var where_: String {
        var parts: [String] = []
        if !track.release.isEmpty { parts.append(track.release) }
        if track.year > 0 { parts.append(String(track.year)) }
        if !track.catno.isEmpty { parts.append(track.catno) }
        return parts.joined(separator: " · ")
    }

    var trailing: String {
        switch track.status {
        case "have": return (track.format ?? "").uppercased()
        case "elsewhere": return track.note ?? "in another folder"
        case "not_tried": return preview ? track.length : "not searched yet"
        default:
            var s = Self.plain(track.note ?? "")
            if let c = track.checks, c > 1 { s += " · looked \(c) times" }
            return track.status == "gave_up" ? "given up: " + s : s
        }
    }

    /// Why a copy on Soulseek wasn't taken (the engine's words -> plain words).
    static let reasons: [(String, String)] = [
        ("bitrate too low", "quality too low"),
        ("unknown bitrate", "quality unknown"),
        ("format", "wrong file type"),
        ("title not in file name", "different title"),
        ("artist not in path", "different artist"),
        ("can't confirm it's this artist", "maybe another artist"),
        ("different release", "from another record"),
        ("different version", "another version"),
        ("length doesn't match", "wrong length"),
    ]

    /// The engine's notes, in plainer words.
    static func plain(_ note: String) -> String {
        if note == "no results on Soulseek" { return "nobody had it" }
        if note == "waiting for the first check" { return "waiting for the first check" }
        if let r = note.range(of: #"^(\d+) files seen, none suitable: "#, options: .regularExpression) {
            let n = note[r].split(separator: " ").first ?? ""
            var why = String(note[r.upperBound...])
            for (engine, plain) in Self.reasons { why = why.replacingOccurrences(of: engine, with: plain) }
            return "\(n) copies seen, none good enough: \(why)"
        }
        if let r = note.range(of: #"^(\d+) users tried, none delivered"#, options: .regularExpression) {
            let n = note[r].split(separator: " ").first ?? ""
            return "\(n) \(n == "1" ? "person had it" : "people had it") but didn't send it"
        }
        return note
    }
}

struct Tag: View {
    let text: String
    var body: some View {
        Text(text).font(.caption2).padding(.horizontal, 5).padding(.vertical, 1)
            .background(Capsule().fill(Color.primary.opacity(0.08)))
            .foregroundStyle(.secondary)
    }
}

// MARK: - What each check did

struct HistorySheet: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Checks for missing tracks").font(.title3.weight(.semibold))
                Text(summary).foregroundStyle(.secondary)
            }
            .padding(18)
            Divider()
            if model.checks.isEmpty {
                Text("No checks yet.").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(model.checks.enumerated()), id: \.element.id) { i, run in
                            if i > 0 { Divider().padding(.leading, 44) }
                            CheckRow(run: run)
                        }
                    }
                }
            }
            Divider()
            HStack {
                Text("Who has which file depends on who's online, so checks at different times find different things.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(14)
        }
        .frame(width: 640, height: 560)
        .task { await model.loadChecks() }
    }

    var summary: String {
        let recent = model.checks.prefix(10)
        let found = recent.reduce(0) { $0 + $1.found }
        let skipped = recent.filter { $0.result != "done" && $0.result != "nothing" && $0.result != "running" }.count
        guard !recent.isEmpty else { return "" }
        var s = "The last \(recent.count) checks found \(found) track\(found == 1 ? "" : "s")"
        if skipped > 0 { s += "; \(skipped) didn't run fully" }
        return s + "."
    }
}

struct CheckRow: View {
    let run: CheckRun
    @State private var open = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: icon.0).foregroundStyle(icon.1).frame(width: 20).padding(.top, 1)
                VStack(alignment: .leading, spacing: 2) {
                    Text(when).font(.headline)
                    Text(run.line.prefix(1).uppercased() + run.line.dropFirst()).foregroundStyle(.secondary)
                }
                Spacer()
                if !run.folders.isEmpty {
                    Button(open ? "Hide folders" : "Folders") { open.toggle() }.buttonStyle(.link)
                }
            }
            if open {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(Array(run.folders.enumerated()), id: \.offset) { _, f in
                        HStack {
                            Text(f.folder).lineLimit(1)
                            Spacer()
                            Text(f.found > 0 ? "found \(f.found) of \(f.searched)" : "\(f.searched) searched")
                                .foregroundStyle(f.found > 0 ? .primary : .secondary)
                        }
                        .font(.callout)
                    }
                }
                .padding(.leading, 30)
            }
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 9)
    }

    var when: String {
        guard let d = run.date else { return run.started }
        return DateFormatter.shortDay.string(from: d) + " · " + ago(d) + (run.trigger == "manual" ? " · started by you" : "")
    }

    var icon: (String, Color) {
        switch run.result {
        case "done": run.found > 0 ? ("arrow.down.circle.fill", .green) : ("checkmark.circle", .secondary)
        case "nothing": ("checkmark.circle", .secondary)
        case "skipped": ("pause.circle", .orange)
        case "running": ("arrow.triangle.2.circlepath.circle", .accentColor)
        case "crashed", "login_failed": ("exclamationmark.triangle.fill", .red)
        default: ("questionmark.circle", .secondary)
        }
    }
}

// MARK: - More options

struct OptionsSheet: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let item: FoundItem
    @State var options = GetOptions()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                Thumb(url: item.thumb, kind: item.kind)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.name).font(.title3.weight(.semibold))
                    Text(item.kindLabel).foregroundStyle(.secondary)
                }
            }
            .padding([.horizontal, .top], 18)
            Form {
                Section("Which tracks") {
                    Toggle("Originals only (no remixes, live or other versions)", isOn: $options.originalsOnly)
                    if !item.isRecord {
                        Toggle("Leave out compilations and DJ mixes", isOn: $options.noCompilations)
                    }
                    if item.kind == "artist" {
                        Toggle("Also get remixes they made for other artists", isOn: $options.theirRemixes)
                    }
                    if !item.isRecord {
                        Toggle("Only releases from certain years", isOn: $options.yearsOn)
                        if options.yearsOn {
                            HStack {
                                Stepper("From \(String(options.yearFrom))", value: $options.yearFrom, in: 1940...2100)
                                Spacer()
                                Stepper("to \(String(options.yearTo))", value: $options.yearTo, in: 1940...2100)
                            }
                        }
                    }
                }
                if item.kind == "artist" {
                    Section("Name shared with other artists?") {
                        Toggle("Strict: only files that clearly belong to this artist", isOn: $options.strict)
                        Text("A file only counts if its folder shows the record's name or catalogue number, or its length matches Discogs. For names like Katran that other artists also use.")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                Section("Quality for this download") {
                    Picker("Quality", selection: $options.quality) {
                        Text("As in Settings (\(model.status.flatMap { Quality.from($0.formats) }?.title ?? "your setting"))").tag(Quality?.none)
                        ForEach(Quality.allCases) { q in Text("\(q.title): \(q.detail)").tag(Optional(q)) }
                    }
                    .labelsHidden()
                    Picker("Lowest MP3 quality", selection: $options.minBitrate) {
                        Text("As in Settings (\(model.status?.minBitrate ?? 320) kbps)").tag(Int?.none)
                        ForEach(Quality.bitrates, id: \.self) { kbps in Text(Quality.bitrateLabel(kbps)).tag(Optional(kbps)) }
                    }
                    Text("Lower lets soulget take an MP3 below 320 kbps when nobody has a better copy. Better copies still come first.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Section {
                    Toggle("Don't download now: add to the list for the 7-hour check", isOn: $options.later)
                }
            }
            .formStyle(.grouped)
            HStack {
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Preview Track List…") {
                    dismiss()
                    model.startPreview(item, options)
                }
                .disabled(options.later)
                Button(options.later ? "Add to List" : "Get") {
                    dismiss()
                    model.get(item, options)
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding(14)
        }
        .frame(width: 560, height: 760)
        .disabled(!model.ready && !model.demo)
    }
}

// MARK: - Preview

struct PreviewSheet: View {
    @EnvironmentObject var model: AppModel
    let preview: PreviewState
    @State private var filter = "all"

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                Thumb(url: preview.item.thumb, kind: preview.item.kind)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Preview: \(preview.item.name)").font(.title3.weight(.semibold))
                    Text(preview.summary.map(Self.describe) ?? preview.phase).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(18)

            if let s = preview.summary, let d = preview.tracks {
                HStack {
                    Picker("Show", selection: $filter) {
                        Text("All (\(s.total))").tag("all")
                        Text("New to you (\(d.missingCount))").tag("new")
                        if s.remixes > 0, !s.remixesLeftOut { Text("Remixes and versions (\(s.remixes))").tag("versions") }
                    }
                    .pickerStyle(.segmented).labelsHidden().frame(maxWidth: 480)
                    Spacer()
                }
                .padding(.horizontal, 18).padding(.bottom, 10)
                Divider()
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(shown(d).enumerated()), id: \.offset) { i, t in
                            if i > 0 { Divider().padding(.leading, 44) }
                            TrackRow(track: t, preview: true)
                        }
                    }
                }
            } else if let e = preview.error {
                Banner(icon: "exclamationmark.triangle.fill", text: e, tint: .orange).padding(18)
                Spacer()
            } else {
                VStack(alignment: .leading, spacing: 8) {
                    if let p = preview.pages, p.of > 0 {
                        ProgressView(value: Double(p.done), total: Double(p.of))
                        Text("Discogs pages read: \(p.done) of \(p.of)").font(.callout).foregroundStyle(.secondary)
                    } else {
                        ProgressView().progressViewStyle(.linear)
                    }
                    if !preview.detail.isEmpty { Text(preview.detail).font(.callout).foregroundStyle(.tertiary).lineLimit(1) }
                }
                .padding(18)
                Spacer()
            }

            Divider()
            HStack {
                Button("Cancel") { model.closePreview(keep: false) }.keyboardShortcut(.cancelAction)
                Spacer()
                if let s = preview.summary {
                    if s.remixes > 0, !s.remixesLeftOut {
                        Button("Get Originals Only (\(s.total - s.remixes))") { model.getFromPreview(originalsOnly: true) }
                    }
                    Button("Get All \(s.total)") { model.getFromPreview(originalsOnly: false) }.keyboardShortcut(.defaultAction)
                }
            }
            .padding(14)
        }
        .frame(width: 760, height: 600)
    }

    static func describe(_ s: PreviewSummary) -> String {
        var parts = ["\(s.total) tracks"]
        if s.releases > 1 { parts[0] += " from \(s.releases) records" }
        if s.duplicates > 0 { parts.append("\(s.duplicates) duplicate copies left out") }
        if s.remixes > 0 { parts.append(s.remixesLeftOut ? "\(s.remixes) remixes and versions left out" : "\(s.remixes) of them remixes or other versions") }
        if s.had > 0 { parts.append("you already have \(s.had)") }
        return parts.joined(separator: " · ")
    }

    func shown(_ d: FolderDetail) -> [FolderTrack] {
        switch filter {
        case "new": d.tracks.filter { $0.isMissing }
        case "versions": d.tracks.filter { $0.remix }
        default: d.tracks
        }
    }
}
