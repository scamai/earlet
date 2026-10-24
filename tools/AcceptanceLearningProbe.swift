import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

@main
struct AcceptanceLearningProbe {
    private static let markerName = ".acceptance-scratch"
    private static let markerText = "dictation.acceptance/1"

    static func main() async {
        do {
            let input = try parse(CommandLine.arguments)
            let lexicon = WebsterLexicon.system()
            guard lexicon.isAvailable else {
                throw ProbeError.message("the system Webster lexicon is unavailable")
            }

            let learning = DictationLearning(
                store: LearningStore(url: input.storeURL),
                lexicon: lexicon
            )
            let outcome = await learning.observe(
                dictationID: input.dictationID,
                dictated: input.dictated,
                edited: input.edited,
                scene: input.scene
            )
            guard outcome.save == .saved || outcome.save == .unchanged else {
                throw ProbeError.message("learning store save was \(saveName(outcome.save))")
            }
            let snapshot = await learning.snapshot()
            let result: [String: Any] = [
                "words": snapshot.words.count,
                "aliases": snapshot.aliases.count,
                "pending": snapshot.pendingCount,
                "candidates": outcome.candidates,
                "wordsLearned": outcome.wordsLearned,
                "save": saveName(outcome.save)
            ]
            let data = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
            guard let json = String(data: data, encoding: .utf8) else {
                throw ProbeError.message("could not encode result")
            }
            print(json)
        } catch {
            FileHandle.standardError.write(Data("AcceptanceLearningProbe: \(error)\n".utf8))
            exit(EXIT_FAILURE)
        }
    }

    private struct Input {
        let storeURL: URL
        let dictationID: String
        let dictated: String
        let edited: String
        let scene: LearningScene
    }

    private enum ProbeError: Error, CustomStringConvertible {
        case usage
        case message(String)

        var description: String {
            switch self {
            case .usage:
                return "usage: AcceptanceLearningProbe <learning.json> <dictation-id> <dictated-text> <edited-text> <terminal|wechat|other>"
            case .message(let text):
                return text
            }
        }
    }

    private static func parse(_ arguments: [String]) throws -> Input {
        guard arguments.count == 6 else { throw ProbeError.usage }
        guard arguments[1].hasPrefix("/") else {
            throw ProbeError.message("store path must be an absolute learning.json path")
        }
        let url = URL(fileURLWithPath: arguments[1]).standardizedFileURL
        try validateScratchStore(url)
        guard !arguments[2].isEmpty else { throw ProbeError.message("dictation ID must not be empty") }
        guard let scene = LearningScene(rawValue: arguments[5]) else {
            throw ProbeError.message("scene must be terminal, wechat, or other")
        }
        return Input(storeURL: url, dictationID: arguments[2], dictated: arguments[3], edited: arguments[4], scene: scene)
    }

    private static func validateScratchStore(_ url: URL) throws {
        guard url.lastPathComponent == "learning.json", url.path.hasPrefix("/") else {
            throw ProbeError.message("store path must be an absolute learning.json path")
        }
        if let type = try fileType(url), type == .symlink {
            throw ProbeError.message("store file must not be a symlink")
        }
        if let type = try fileType(url), type != .regular {
            throw ProbeError.message("store path must be an ordinary file")
        }
        let parent = url.deletingLastPathComponent()
        let canonicalParent = parent.resolvingSymlinksInPath().standardizedFileURL
        guard canonicalParent.lastPathComponent.hasPrefix("dictation-acceptance-") else {
            throw ProbeError.message("store parent must be a dictation-acceptance-* directory")
        }
        guard try fileType(parent) == .directory, try fileType(canonicalParent) == .directory else {
            throw ProbeError.message("store parent must be an ordinary directory")
        }
        let marker = canonicalParent.appendingPathComponent(markerName)
        guard try fileType(marker) == .regular,
              try String(contentsOf: marker, encoding: .utf8) == markerText else {
            throw ProbeError.message("store parent marker is missing or invalid")
        }
        guard parent == canonicalParent else {
            throw ProbeError.message("store parent path must resolve directly to the scratch directory")
        }
    }

    private enum FileType: Equatable {
        case regular, directory, symlink, other
    }

    private static func fileType(_ url: URL) throws -> FileType? {
        var info = stat()
        let result = url.path.withCString { lstat($0, &info) }
        guard result == 0 else {
            if errno == ENOENT { return nil }
            throw ProbeError.message("could not inspect store path")
        }
        switch info.st_mode & S_IFMT {
        case S_IFREG: return .regular
        case S_IFDIR: return .directory
        case S_IFLNK: return .symlink
        default: return .other
        }
    }

    private static func saveName(_ save: DictationLearning.SaveOutcome) -> String {
        switch save {
        case .saved: return "saved"
        case .unchanged: return "unchanged"
        case .failed: return "failed"
        case .refused: return "refused"
        }
    }
}
