// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var model: AppModel
    @AppStorage("settingsTab") private var tab = "general"

    var body: some View {
        TabView(selection: $tab) {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }.tag("general")
            SoulseekSettings().tabItem { Label("Soulseek", systemImage: "person.crop.circle") }.tag("soulseek")
            DiscogsSettings().tabItem { Label("Discogs", systemImage: "opticaldisc") }.tag("discogs")
            AboutSettings().tabItem { Label("About", systemImage: "info.circle") }.tag("about")
        }
        .frame(width: 560)
        .overlay(alignment: .bottom) {
            if let e = model.settingsError {
                Banner(icon: "exclamationmark.triangle.fill", text: e, tint: .orange).padding(12)
            }
        }
    }
}

struct GeneralSettings: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                LabeledContent("Music folder") {
                    VStack(alignment: .trailing, spacing: 4) {
                        Text(tilde(model.status?.musicFolder ?? "…")).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                        Button("Change…") { chooseFolder() }.disabled(model.working)
                    }
                }
                Text("Each artist, label or album gets its own folder inside. Music you already have stays where it is.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Section("Quality") {
                Picker("Quality", selection: qualityBinding) {
                    ForEach(Quality.allCases) { q in
                        VStack(alignment: .leading, spacing: 1) {
                            Text(q.title)
                            Text(q.detail).font(.caption).foregroundStyle(.secondary)
                        }
                        .tag(Optional(q))
                    }
                }
                .pickerStyle(.radioGroup)
                .labelsHidden()
                Picker("Lowest MP3 quality", selection: minBitrateBinding) {
                    ForEach(Quality.bitrates, id: \.self) { kbps in Text(Quality.bitrateLabel(kbps)).tag(kbps) }
                    if let b = model.status?.minBitrate, !Quality.bitrates.contains(b) { Text("\(b) kbps").tag(b) }
                }
                Text("Lower lets soulget take an MP3 below 320 kbps when nobody has a better copy; better copies still come first. A track is downloaded once, so a lower-quality copy isn't replaced later. Lossless files always pass.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if let s = model.status, Quality.from(s.formats) == nil {
                    Text("Your own setting from the settings file: \(s.formats.joined(separator: ", ")), at least \(s.minBitrate) kbps.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Text("Applies to new downloads. Folders you already have keep the quality they started with.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Section("Missing tracks") {
                Toggle("Keep looking for missing tracks every 7 hours", isOn: timerBinding)
                    .disabled(model.working || model.status == nil)
                Text("Who has which file depends on who is online. Every 7 hours, while your Mac is awake, soulget searches again for anything still missing, even with this window closed. Over a week the checks land on every hour of the day.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .frame(height: 760)
    }

    var qualityBinding: Binding<Quality?> {
        Binding(get: { model.status.flatMap { Quality.from($0.formats) } },
                set: { q in if let q { Task { await model.setQuality(q) } } })
    }

    var minBitrateBinding: Binding<Int> {
        Binding(get: { model.status?.minBitrate ?? 320 }, set: { kbps in Task { await model.setMinBitrate(kbps) } })
    }

    var timerBinding: Binding<Bool> {
        Binding(get: { model.status?.timer.active ?? false }, set: { on in Task { await model.setTimer(on) } })
    }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use This Folder"
        panel.message = "Choose where soulget saves music"
        if let f = model.status?.musicFolder { panel.directoryURL = URL(fileURLWithPath: f) }
        if panel.runModal() == .OK, let url = panel.url { Task { await model.setMusicFolder(url) } }
    }
}

struct SoulseekSettings: View {
    @EnvironmentObject var model: AppModel
    @State private var user = ""
    @State private var password = ""

    var body: some View {
        Form {
            Section {
                Picker("Login", selection: modeBinding) {
                    Text("Automatic (recommended)").tag(LoginMode.auto)
                    Text("Use my own Soulseek name").tag(LoginMode.own)
                }
                .pickerStyle(.radioGroup)
            }
            if model.loginMode == .auto {
                Section {
                    LabeledContent("Name soulget uses", value: model.status?.username ?? "…")
                    Text("soulget made this Soulseek login for itself. There's nothing to set up, and it never clashes with SoulseekQt or Nicotine+.")
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Section {
                    TextField("Name", text: $user)
                    SecureField("Password", text: $password, prompt: Text(model.status?.hasPassword == true ? "unchanged" : ""))
                    HStack {
                        Spacer()
                        Button("Save") { Task { await model.useOwnLogin(user, password); password = "" } }
                            .disabled(user.isEmpty || password.isEmpty || model.working)
                    }
                    Text("Soulseek allows one connection per name. If SoulseekQt or Nicotine+ is open with this name, one of them gets disconnected. A name that doesn't exist yet is created the first time it logs in.")
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .formStyle(.grouped)
        .frame(height: 360)
        .onAppear { user = model.loginMode == .own ? (model.status?.username ?? "") : "" }
    }

    var modeBinding: Binding<LoginMode> {
        Binding(get: { model.loginMode }, set: { mode in
            if mode == .auto { Task { await model.useAutoLogin() } } else {
                model.loginMode = .own
                user = ""
            }
        })
    }
}

struct DiscogsSettings: View {
    @EnvironmentObject var model: AppModel
    @State private var key = ""

    var body: some View {
        Form {
            Section {
                Text("soulget reads track lists from Discogs. It works without a Discogs account. A free Discogs key adds pictures to search results and reads Discogs about twice as fast, which helps with big labels.")
                    .fixedSize(horizontal: false, vertical: true)
            }
            if model.status?.hasToken == true {
                Section {
                    LabeledContent("Discogs key") {
                        HStack {
                            Label("Saved", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                            Button("Remove") { Task { await model.setDiscogsKey("") } }
                        }
                    }
                }
            } else {
                Section("Add a free key (optional)") {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("1. Log in to Discogs (or make a free account).")
                        HStack(spacing: 4) {
                            Text("2. Open")
                            Link("your Discogs developer settings", destination: URL(string: "https://www.discogs.com/settings/developers")!)
                            Text("and click “Generate new token”.")
                        }
                        Text("3. Copy the token and paste it here:")
                    }
                    .font(.callout)
                    HStack {
                        SecureField("Discogs token", text: $key, prompt: Text("Paste the token here"))
                            .labelsHidden()
                            .textFieldStyle(.roundedBorder)
                        Button("Save") { Task { await model.setDiscogsKey(key); key = "" } }
                            .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty || model.working)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(height: 360)
    }
}

struct AboutSettings: View {
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 56, height: 56)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("soulget \(appVersion)").font(.title3.weight(.semibold))
                        Text("Free software under the GNU General Public License v3, with no warranty.").font(.callout)
                        Text("Built on Soulseek.NET; not maintained by, endorsed by, or affiliated with the Soulseek.NET project or its author. You are responsible for using soulget lawfully and within the Soulseek network's rules.")
                            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                HStack {
                    Button("What's New") { openBundled("CHANGELOG.txt") }
                    Button("License") { openBundled("LICENSE.txt") }
                    Button("Notices") { openBundled("NOTICE.txt") }
                    Link("Source Code", destination: URL(string: "https://github.com/aydinkuruuk-collab/soulget")!)
                }
            }
            Section("Where things are") {
                LabeledContent("Settings and logs") {
                    Button(tilde(model.engine?.home.path ?? "…")) {
                        if let h = model.engine?.home { NSWorkspace.shared.open(h) }
                    }.buttonStyle(.link)
                }
                LabeledContent("Settings file") {
                    Button("Open in TextEdit") {
                        guard let p = model.status?.configPath else { return }
                        NSWorkspace.shared.open([URL(fileURLWithPath: p)],
                                                withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"),
                                                configuration: NSWorkspace.OpenConfiguration())
                    }
                }
                Text("Every option of the command-line version is in the settings file. Changes there apply to the next download.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(height: 400)
    }

    var appVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "" }

    func openBundled(_ name: String) {
        guard let url = Bundle.main.url(forResource: name, withExtension: nil) else { return }
        NSWorkspace.shared.open([url], withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"),
                                configuration: NSWorkspace.OpenConfiguration())
    }
}

func tilde(_ path: String) -> String {
    let home = FileManager.default.homeDirectoryForCurrentUser.path
    return path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
}

// MARK: - Menu bar

struct MenuBarMenu: View {
    @EnvironmentObject var model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Text(model.menuStatus)
        if let r = model.run {
            Button(r.stopping ? "Stop Now" : "Stop") { model.stop() }
        }
        Divider()
        Button("Open soulget") {
            openWindow(id: "main")
            NSApp.activate(ignoringOtherApps: true)
        }
        Button("Look for Missing Tracks Now") { model.checkMissingNow() }
            .disabled(!model.ready || model.status?.driveMissing != nil)
        Divider()
        if #available(macOS 14, *) {
            SettingsLink { Text("Settings…") }
        } else {
            Button("Settings…") {
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
                NSApp.activate(ignoringOtherApps: true)
            }
        }
        Button("Quit soulget") { NSApp.terminate(nil) }
    }
}
