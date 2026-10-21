// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI

struct MainView: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            SearchBar()
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    if let problem = model.setupProblem { Banner(icon: "exclamationmark.triangle.fill", text: problem, tint: .orange) }
                    if let drive = model.status?.driveMissing {
                        Banner(icon: "externaldrive.badge.xmark",
                               text: "Your music drive isn't connected (\(drive)). Connect it to download; the 7-hour check waits for it.",
                               tint: .orange)
                    }
                    if model.searchedFor != nil { ResultsSection() }
                    if model.run != nil || !model.upNext.isEmpty || !model.finished.isEmpty { ActivitySection() }
                    LibrarySection()
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(minWidth: 640, minHeight: 540)
        .background(Color(nsColor: .windowBackgroundColor))
        .sheet(item: $model.openFolder) { f in FolderSheet(folder: f).environmentObject(model) }
        .background(EmptyView().sheet(isPresented: $model.showHistory) { HistorySheet().environmentObject(model) })
        .background(EmptyView().sheet(item: $model.optionsItem) { item in OptionsSheet(item: item).environmentObject(model) })
        .background(EmptyView().sheet(item: $model.preview, onDismiss: { model.closePreview(keep: false) }) { p in
            PreviewSheet(preview: p).environmentObject(model)
        })
    }
}

// MARK: - Search

struct SearchBar: View {
    @EnvironmentObject var model: AppModel
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                HStack(spacing: 8) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Artist, record label or album", text: $model.query)
                        .textFieldStyle(.plain)
                        .font(.title3)
                        .focused($focused)
                        .onSubmit { model.search() }
                    if !model.query.isEmpty {
                        Button { model.clearSearch() } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                            .buttonStyle(.plain)
                            .help("Clear")
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .background(RoundedRectangle(cornerRadius: 9).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 9).stroke(Color.primary.opacity(focused ? 0.35 : 0.12)))

                Button("Search") { model.search() }
                    .controlSize(.large)
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.query.trimmingCharacters(in: .whitespaces).isEmpty || !model.ready)
            }
            Text("Type a name and press Return. A Discogs link works too.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 20)
        .padding(.top, 18)
        .padding(.bottom, 14)
        .onAppear { focused = true }
    }
}

struct ResultsSection: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Results for “\(model.searchedFor ?? "")”").font(.title3.weight(.semibold))
                Spacer()
                Button("Close") { model.clearSearch() }.buttonStyle(.link)
            }
            if model.searching {
                HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Looking on Discogs…").foregroundStyle(.secondary) }
            }
            if let e = model.searchError { Text(e).foregroundStyle(.secondary) }
            ResultGroup(title: "Artists", items: model.results.filter { $0.kind == "artist" })
            ResultGroup(title: "Record labels", items: model.results.filter { $0.kind == "label" })
            ResultGroup(title: "Albums, EPs and singles", items: model.results.filter { $0.isRecord })
            if !model.results.isEmpty, model.status?.hasToken == false {
                Text("Pictures appear once you add a free Discogs key in Settings.").font(.caption).foregroundStyle(.tertiary)
            }
        }
    }
}

struct ResultGroup: View {
    let title: String
    let items: [FoundItem]

    var body: some View {
        if !items.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text(title.uppercased()).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                Card {
                    ForEach(Array(items.enumerated()), id: \.element.id) { i, item in
                        if i > 0 { Divider().padding(.leading, 66) }
                        ResultRow(item: item)
                    }
                }
            }
        }
    }
}

struct ResultRow: View {
    @EnvironmentObject var model: AppModel
    let item: FoundItem

    var body: some View {
        HStack(spacing: 12) {
            Thumb(url: item.thumb, kind: item.kind)
            VStack(alignment: .leading, spacing: 2) {
                Text(item.name).font(.headline).lineLimit(2)
                Text(subtitle).font(.callout).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            Link(destination: URL(string: item.url) ?? URL(string: "https://www.discogs.com")!) {
                Image(systemName: "arrow.up.right.square")
            }
            .help("Open on Discogs, to check it's the right one")
            .foregroundStyle(.secondary)
            GetButton(item: item)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
    }

    var subtitle: String {
        var parts = [item.kindLabel]
        if !item.detail.isEmpty { parts.append(item.detail) }
        if let f = model.folder(for: item) {
            parts = ["In your music: \(f.have) of \(f.total) tracks"]
        }
        return parts.joined(separator: " · ")
    }
}

struct GetButton: View {
    @EnvironmentObject var model: AppModel
    let item: FoundItem

    var body: some View {
        Menu {
            Button("Get everything") { model.get(item) }
            Button("Originals only (no remixes or live versions)") { model.get(item, .originals) }
            if !item.isRecord {
                Button("Leave out compilations and DJ mixes") { model.get(item, .noComps) }
            }
            Divider()
            Button("Preview the track list first…") { model.startPreview(item) }
            Button("More options…") { model.optionsItem = item }
            Divider()
            Button("Don't download now: add to the list for the 7-hour check") { model.get(item, .laterOnly) }
        } label: {
            Text(model.folder(for: item) != nil ? "Get missing" : "Get")
        } primaryAction: {
            // A folder you already have: run it again with its own saved settings.
            if let f = model.folder(for: item) { Task { await model.getAgain(f) } } else { model.get(item) }
        }
        .fixedSize()
        .disabled(!model.ready)
        .help("Click to get every track. The arrow has more choices.")
    }
}

struct Thumb: View {
    let url: String
    let kind: String

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.07))
            if let u = URL(string: url), !url.isEmpty {
                AsyncImage(url: u) { img in img.resizable().scaledToFill() } placeholder: { symbol }
            } else {
                symbol
            }
        }
        .frame(width: 42, height: 42)
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }

    var symbol: some View {
        Image(systemName: Self.symbol(kind)).font(.system(size: 17)).foregroundStyle(.secondary)
    }

    static func symbol(_ kind: String) -> String {
        switch kind {
        case "artist": "person.fill"
        case "label": "building.2.fill"
        case "tracks": "music.note.list"
        default: "opticaldisc"
        }
    }
}

// MARK: - Now

struct ActivitySection: View {
    @EnvironmentObject var model: AppModel
    @State private var showDetails = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let run = model.run {
                Text("Now").font(.title3.weight(.semibold))
                RunCard(run: run, showDetails: $showDetails)
            }
            if !model.upNext.isEmpty {
                Text("UP NEXT").font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                Card {
                    ForEach(Array(model.upNext.enumerated()), id: \.element.id) { i, r in
                        if i > 0 { Divider() }
                        HStack {
                            Image(systemName: "clock").foregroundStyle(.secondary)
                            Text(r.title)
                            Spacer()
                            Button { model.remove(r) } label: { Image(systemName: "xmark") }.buttonStyle(.borderless).help("Remove from the line")
                        }
                        .padding(.horizontal, 12).padding(.vertical, 8)
                    }
                }
            }
            if !model.finished.isEmpty {
                Text("FINISHED").font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                Card {
                    ForEach(Array(model.finished.prefix(3).enumerated()), id: \.element.id) { i, f in
                        if i > 0 { Divider() }
                        HStack(alignment: .top, spacing: 10) {
                            Image(systemName: f.ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                                .foregroundStyle(f.ok ? .green : .orange)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(f.title).font(.headline)
                                Text(f.text).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            }
                            Spacer()
                            if let path = f.folderPath {
                                Button("Show in Finder") { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: path) }
                            }
                        }
                        .padding(.horizontal, 12).padding(.vertical, 9)
                    }
                }
                if model.run == nil, !model.log.isEmpty {
                    DetailsLog(show: $showDetails)
                }
            }
        }
    }
}

struct RunCard: View {
    @EnvironmentObject var model: AppModel
    let run: RunState
    @Binding var showDetails: Bool

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(run.request.title).font(.headline)
                        Text(run.stopping ? "Stopping after the downloads in progress…" : run.phase)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    if let path = run.folderPath {
                        Button("Show in Finder") { NSWorkspace.shared.selectFile(nil, inFileViewerRootedAtPath: path) }
                    }
                    Button(run.stopping ? "Stop Now" : "Stop") { model.stop() }
                        .help(run.stopping ? "Stop at once; unfinished files are dropped" : "Finish what's downloading, then stop. Nothing finished is lost.")
                }
                if let f = run.fraction {
                    ProgressView(value: f)
                } else {
                    ProgressView().progressViewStyle(.linear)
                }
                if let line = run.countLine {
                    Text(line).font(.callout).foregroundStyle(.secondary)
                } else if !run.loggedIn, !run.lastLine.isEmpty, run.pages == nil {
                    Text(run.lastLine).font(.callout).foregroundStyle(.tertiary).lineLimit(1)
                }
                if !run.active.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(run.active) { a in
                            HStack(spacing: 8) {
                                Image(systemName: "arrow.down.circle").foregroundStyle(Color.accentColor)
                                Text(a.title).lineLimit(1).truncationMode(.middle)
                                Spacer(minLength: 8)
                                Text([a.detail, a.note].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                                if let p = a.percent {
                                    ProgressView(value: p, total: 100).frame(width: 70)
                                    Text("\(Int(p))%").font(.caption.monospacedDigit()).frame(width: 34, alignment: .trailing)
                                }
                            }
                        }
                    }
                    .padding(.top, 2)
                }
                if let note = run.stopNote { Text(note).font(.callout).foregroundStyle(.orange) }
                DetailsLog(show: $showDetails)
            }
            .padding(14)
        }
    }
}

struct DetailsLog: View {
    @EnvironmentObject var model: AppModel
    @Binding var show: Bool

    var body: some View {
        DisclosureGroup(isExpanded: $show) {
            ScrollViewReader { proxy in
                ScrollView {
                    Text(model.log.suffix(400).joined(separator: "\n"))
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8)
                    Color.clear.frame(height: 1).id("end")
                }
                .frame(height: 180)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
                .onAppear { proxy.scrollTo("end") }
                .onChange(of: model.log.count) { _ in proxy.scrollTo("end") }
            }
        } label: {
            Text("Details").font(.callout).foregroundStyle(.secondary)
        }
    }
}

// MARK: - Your music

struct LibrarySection: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("Your music").font(.title3.weight(.semibold))
                Spacer()
                if let folder = model.status?.musicFolder {
                    Button("Open Folder") { NSWorkspace.shared.open(URL(fileURLWithPath: folder)) }
                        .disabled(model.status?.driveMissing != nil)
                }
                Button("Look for Missing Tracks") { model.checkMissingNow() }
                    .disabled(!model.ready || (model.status?.folders.isEmpty ?? true) || model.status?.driveMissing != nil)
                    .help("Search Soulseek again for every track still missing, in all folders")
            }
            TimerLine()
            if let folders = model.status?.folders, !folders.isEmpty {
                Card {
                    ForEach(Array(folders.enumerated()), id: \.element.id) { i, f in
                        if i > 0 { Divider().padding(.leading, 66) }
                        FolderRow(folder: f)
                    }
                }
            } else if model.ready, model.status?.driveMissing == nil {
                Welcome()
            } else if !model.ready, model.setupProblem == nil {
                HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Getting ready…").foregroundStyle(.secondary) }
            }
        }
    }
}

struct TimerLine: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        if let s = model.status {
            HStack(spacing: 6) {
                Image(systemName: s.timer.active ? "clock.arrow.circlepath" : "clock")
                    .foregroundStyle(s.timer.active ? Color.accentColor : .secondary)
                Group {
                    if s.busy != nil, model.run == nil {
                        Text("A check for missing tracks is running right now.")
                    } else if s.timer.active {
                        Text("Missing tracks are searched for again every \(Int(s.everyHours)) hours" + lastCheck())
                    } else {
                        Text("Automatic checks for missing tracks are off.")
                    }
                }
                .foregroundStyle(.secondary)
                .lineLimit(1)
                if !s.timer.active, s.busy == nil || model.run != nil {
                    Button("Turn On") { Task { await model.setTimer(true) } }
                        .buttonStyle(.link)
                        .disabled(model.working || !s.hasPassword)
                }
                if !model.checks.isEmpty {
                    Button("History") { model.showHistory = true }.buttonStyle(.link)
                }
            }
            .font(.callout)
        }
    }

    func lastCheck() -> String {
        guard let last = model.checks.first, let date = last.date else { return "." }
        return " · last check \(ago(date)): \(last.line)."
    }
}

struct FolderRow: View {
    @EnvironmentObject var model: AppModel
    let folder: Status.Folder

    var body: some View {
        HStack(spacing: 12) {
            Thumb(url: "", kind: folder.kind)
            VStack(alignment: .leading, spacing: 3) {
                Text(folder.name).font(.headline).lineLimit(1)
                Text(line).font(.callout).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            if folder.total > 0 {
                ProgressView(value: Double(min(folder.have, folder.total)), total: Double(folder.total))
                    .frame(width: 90)
                    .tint(folder.have >= folder.total ? .green : .accentColor)
            }
            Button { NSWorkspace.shared.open(URL(fileURLWithPath: folder.path)) } label: { Image(systemName: "folder") }
                .buttonStyle(.borderless)
                .help("Open in Finder")
            Image(systemName: "chevron.right").foregroundStyle(.tertiary).font(.caption)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .contentShape(Rectangle())
        .onTapGesture { model.openFolder = folder }
        .help("Click to see the tracks: which you have, which are missing and why")
        .contextMenu {
            Button("Show Tracks") { model.openFolder = folder }
            Button("Open in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: folder.path)) }
            if let u = URL(string: folder.url), !folder.url.isEmpty {
                Button("Open on Discogs") { NSWorkspace.shared.open(u) }
                Button("Get Again (new releases, missing tracks)") { Task { await model.getAgain(folder) } }
            }
        }
    }

    var line: String {
        var parts = ["\(folder.have) of \(folder.total) tracks"]
        if folder.looking > 0 { parts.append("still looking for \(folder.looking)") }
        else if folder.have >= folder.total, folder.total > 0 { parts.append("complete") }
        if folder.gaveUp > 0 { parts.append("\(folder.gaveUp) given up on") }
        if let d = ISO8601DateFormatter().date(from: folder.updated) {
            parts.append(RelativeDateTimeFormatter().localizedString(for: d, relativeTo: Date()))
        }
        return parts.joined(separator: " · ")
    }
}

struct Welcome: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                Text("Nothing here yet").font(.headline)
                Text("Search for an artist, a record label or an album above, then click Get. soulget reads the full track list from Discogs, removes duplicates, and downloads one copy of each track from Soulseek.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let f = model.status?.musicFolder {
                    Text("Music goes to \(f.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~")). You can change that in Settings.")
                        .font(.callout).foregroundStyle(.tertiary)
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

// MARK: - Bits

struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) { content }
            .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.08)))
    }
}

struct Banner: View {
    let icon: String
    let text: String
    let tint: Color

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: icon).foregroundStyle(tint)
            Text(text).fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10).fill(tint.opacity(0.12)))
    }
}

extension DateFormatter {
    /// retry-log.csv's "when" column: local time, "2026-10-20 16:08".
    static let checkTime: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()
}
