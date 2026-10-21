// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import AppKit
import SwiftUI

@main
struct SoulgetApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = AppModel.shared

    var body: some Scene {
        Window("soulget", id: "main") {
            MainView().environmentObject(model)
        }
        .defaultSize(width: 780, height: 680)
        .commands {
            CommandGroup(replacing: .newItem) {}
        }

        Settings {
            SettingsView().environmentObject(model)
        }

        MenuBarExtra {
            MenuBarMenu().environmentObject(model)
        } label: {
            Image(systemName: model.run == nil ? "arrow.down.circle" : "arrow.down.circle.fill")
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        if let dir = UserDefaults.standard.string(forKey: "snapshot") {   // developer screenshots: -snapshot <folder>
            Snapshots.run(into: URL(fileURLWithPath: dir))
            return
        }
        Task { await AppModel.shared.bootstrap() }
        if let query = UserDefaults.standard.string(forKey: "autotest") {   // developer test: -autotest <search>
            Task { await AutoTest.run(query: query) }
        }
    }

    /// Closing the window keeps soulget in the menu bar, so downloads and checks carry on.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let model = AppModel.shared
        guard model.isRunning else { return .terminateNow }
        let alert = NSAlert()
        alert.messageText = "Stop downloading and quit?"
        alert.informativeText = "Finished files are kept. Run the same download again later to carry on where it stopped."
        alert.addButton(withTitle: "Stop and Quit")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        model.stopAll { NSApp.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}
