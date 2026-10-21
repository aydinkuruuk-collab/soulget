// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

import CryptoKit
import Foundation

struct AppError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

enum Paths {
    /// ~/Library/Application Support/soulget: settings for new setups, the engine copy, the app's own notes.
    static let support: URL = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("soulget", isDirectory: true)
    static let engine = support.appendingPathComponent("engine/soulget")
    static let autoLogin = support.appendingPathComponent("app-login.json")
    static let timerPlist = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/LaunchAgents/com.soulget.retry.plist")
}

/// The engine (the tested soulget program) lives inside the app. It is copied to Application Support
/// so the 7-hour timer keeps working wherever the app is moved, and is replaced when the app is updated.
enum EngineSetup {
    static func install() throws -> URL {
        #if arch(arm64)
        let name = "soulget-arm64"
        #else
        let name = "soulget-x64"
        #endif
        let bundled = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/\(name)")
        let fm = FileManager.default
        guard fm.fileExists(atPath: bundled.path) else {
            throw AppError("The download engine is missing from the app. Please download soulget again.")
        }
        try fm.createDirectory(at: Paths.engine.deletingLastPathComponent(), withIntermediateDirectories: true)
        if fm.fileExists(atPath: Paths.engine.path), (try? digest(bundled)) == (try? digest(Paths.engine)) {
            return Paths.engine
        }
        let tmp = Paths.engine.appendingPathExtension("new")
        try? fm.removeItem(at: tmp)
        try fm.copyItem(at: bundled, to: tmp)
        removexattr(tmp.path, "com.apple.quarantine", 0)   // the copy is ours; the app itself was already approved
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: tmp.path)
        if fm.fileExists(atPath: Paths.engine.path) {
            _ = try fm.replaceItemAt(Paths.engine, withItemAt: tmp)   // a running timer check keeps its old copy
        } else {
            try fm.moveItem(at: tmp, to: Paths.engine)
        }
        return Paths.engine
    }

    static func digest(_ url: URL) throws -> Data {
        Data(SHA256.hash(data: try Data(contentsOf: url, options: .mappedIfSafe)))
    }
}

/// Where config.toml, the Discogs cache and the logs live.
enum Home {
    static func choose() -> URL {
        let defaults = UserDefaults.standard
        if let saved = defaults.string(forKey: "home"), !saved.isEmpty {
            return URL(fileURLWithPath: (saved as NSString).expandingTildeInPath, isDirectory: true)
        }
        let chosen = existingSetup() ?? Paths.support
        defaults.set(chosen.path, forKey: "home")
        return chosen
    }

    /// Someone who already used the command-line soulget: its timer knows where the settings are.
    static func existingSetup() -> URL? {
        guard let data = try? Data(contentsOf: Paths.timerPlist),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any]
        else { return nil }
        var candidates: [String] = []
        if let env = plist["EnvironmentVariables"] as? [String: String], let home = env["SOULGET_HOME"] { candidates.append(home) }
        if let args = plist["ProgramArguments"] as? [String], let program = args.first {
            candidates.append((program as NSString).deletingLastPathComponent)
        }
        for c in candidates {
            let url = URL(fileURLWithPath: (c as NSString).expandingTildeInPath, isDirectory: true)
            if FileManager.default.fileExists(atPath: url.appendingPathComponent("config.toml").path) { return url }
        }
        return nil
    }
}

final class DataBox: @unchecked Sendable { var data = Data() }

struct CommandResult {
    let code: Int32
    let out: String
    let err: String
}

/// Runs the engine. Short commands return their output; downloads stream it line by line.
final class Engine: @unchecked Sendable {
    let program: URL
    let home: URL

    init(program: URL, home: URL) {
        self.program = program
        self.home = home
    }

    private func makeProcess(_ args: [String]) -> Process {
        let p = Process()
        p.executableURL = program
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        env["SOULGET_HOME"] = home.path
        p.environment = env
        p.currentDirectoryURL = home
        return p
    }

    func run(_ args: [String], input: Data? = nil) async -> CommandResult {
        let p = makeProcess(args)
        return await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .userInitiated).async {
                let out = Pipe(), err = Pipe(), inp = Pipe()
                p.standardOutput = out
                p.standardError = err
                p.standardInput = inp
                do { try p.run() } catch {
                    cont.resume(returning: CommandResult(code: -1, out: "", err: error.localizedDescription))
                    return
                }
                if let input { inp.fileHandleForWriting.write(input) }
                try? inp.fileHandleForWriting.close()
                // Read both at once, so a full error pipe can't block the program.
                let errBox = DataBox()
                let group = DispatchGroup()
                group.enter()
                DispatchQueue.global().async { errBox.data = err.fileHandleForReading.readDataToEndOfFile(); group.leave() }
                let outData = out.fileHandleForReading.readDataToEndOfFile()
                group.wait()
                p.waitUntilExit()
                cont.resume(returning: CommandResult(code: p.terminationStatus,
                                                     out: String(decoding: outData, as: UTF8.self),
                                                     err: String(decoding: errBox.data, as: UTF8.self)))
            }
        }
    }

    /// A long run (download or check). Lines arrive in order on the main thread; onExit comes after the last one.
    func start(_ args: [String],
               onLine: @escaping @MainActor (String) -> Void,
               onExit: @escaping @MainActor (Int32) -> Void) throws -> Process {
        let p = makeProcess(args)
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        p.standardInput = FileHandle.nullDevice   // never waits for typed answers
        try p.run()
        let handle = pipe.fileHandleForReading
        Task.detached {
            do {
                for try await line in handle.bytes.lines { await onLine(line) }
            } catch {}
            p.waitUntilExit()
            await onExit(p.terminationStatus)
        }
        return p
    }
}

/// The last line of output that is a JSON object, decoded (a first run may print a note before it).
func decodeLastJSON<T: Decodable>(_ output: String, as type: T.Type = T.self) -> T? {
    guard let line = output.split(separator: "\n").last(where: { $0.hasPrefix("{") }) else { return nil }
    let decoder = JSONDecoder()
    decoder.keyDecodingStrategy = .convertFromSnakeCase
    return try? decoder.decode(T.self, from: Data(line.utf8))
}

func firstLine(_ s: String) -> String {
    s.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty } ?? ""
}

/// The Soulseek login soulget makes for itself (Soulseek creates an account the first time a new name logs in).
struct AutoLogin: Codable {
    var username: String
    var password: String

    static func make() -> AutoLogin {
        let letters = Array("abcdefghijkmnpqrstuvwxyz23456789")
        let alnum = Array("abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789")
        let name = "soulget-" + String((0..<6).map { _ in letters.randomElement()! })
        let pass = String((0..<20).map { _ in alnum.randomElement()! })
        return AutoLogin(username: name, password: pass)
    }

    static func load() -> AutoLogin? {
        guard let data = try? Data(contentsOf: Paths.autoLogin) else { return nil }
        return try? JSONDecoder().decode(AutoLogin.self, from: data)
    }

    func save() {
        try? FileManager.default.createDirectory(at: Paths.support, withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(self) else { return }
        FileManager.default.createFile(atPath: Paths.autoLogin.path, contents: data, attributes: [.posixPermissions: 0o600])
    }
}
