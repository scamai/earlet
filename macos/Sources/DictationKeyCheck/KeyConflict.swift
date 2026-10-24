import Foundation

public enum KeyConflictKind: String, Codable, Sendable { case recorder, foreground, window }
public enum KeyConflictSeverity: String, Codable, Sendable { case recorder, foreground, window }
public enum KeyConflictOS: String, Codable, Sendable { case none, unknown }

public struct KeyConflictPress: Codable, Sendable {
    public var time: TimeInterval
    public var duration: TimeInterval
    public var test: Bool
    public var doubleTap: Bool
    public init(time: TimeInterval, duration: TimeInterval, test: Bool = false, doubleTap: Bool = false) {
        self.time = time; self.duration = duration; self.test = test; self.doubleTap = doubleTap
    }
}

public struct KeyConflictRecorder: Codable, Sendable {
    public var time: TimeInterval
    public var pid: Int32?
    public var identity: String?
    public var active: Bool
    public init(time: TimeInterval, pid: Int32?, identity: String?, active: Bool) {
        self.time = time; self.pid = pid; self.identity = identity; self.active = active
    }
}

public struct KeyConflictReaction: Codable, Sendable {
    public var kind: KeyConflictKind
    public var time: TimeInterval
    public var pid: Int32?
    public var identity: String?
    public init(kind: KeyConflictKind, time: TimeInterval, pid: Int32?, identity: String?) {
        self.kind = kind; self.time = time; self.pid = pid; self.identity = identity
    }
}

public struct KeyConflictTimeline: Codable, Sendable {
    public var presses: [KeyConflictPress]
    public var recorders: [KeyConflictRecorder]
    public var reactions: [KeyConflictReaction]
    public init(presses: [KeyConflictPress], recorders: [KeyConflictRecorder] = [], reactions: [KeyConflictReaction] = []) {
        self.presses = presses; self.recorders = recorders; self.reactions = reactions
    }
}

public struct KeyConflictFinding: Equatable, Codable, Sendable {
    public var kind: KeyConflictKind
    public var severity: KeyConflictSeverity
    public var identity: String?
    public var pid: Int32?
    public var name: String?
    public var candidateCount: Int
    public init(kind: KeyConflictKind, severity: KeyConflictSeverity, identity: String?, pid: Int32?, name: String?, candidateCount: Int) {
        self.kind = kind; self.severity = severity; self.identity = identity; self.pid = pid; self.name = name; self.candidateCount = candidateCount
    }
}

public struct KeyConflictVerdict: Equatable, Codable, Sendable {
    public var finding: KeyConflictFinding?
    public var confirmed: Bool
    public var qualifyingPress: Bool
    public var os: KeyConflictOS
    public init(finding: KeyConflictFinding?, confirmed: Bool, qualifyingPress: Bool, os: KeyConflictOS) {
        self.finding = finding; self.confirmed = confirmed; self.qualifyingPress = qualifyingPress; self.os = os
    }
}

public struct KeyConflictMemory: Sendable {
    private struct Observation: Sendable, Equatable {
        let kind: KeyConflictKind
        let identity: String?
    }

    private var recent: [Observation?] = []
    public init() {}
    public mutating func observe(_ verdict: KeyConflictVerdict) {
        recent.append(verdict.finding.map { Observation(kind: $0.kind, identity: $0.identity) })
        recent = Array(recent.suffix(3))
    }
    public func confirmed(identity: String, kind: KeyConflictKind) -> Bool {
        recent.compactMap { $0 }.filter { $0.kind == kind && $0.identity == identity }.count >= 2
    }
}

public enum KeyConflictClassifier {
    public static func classify(_ timeline: KeyConflictTimeline, ownPID: Int32, ownChildren: Set<Int32> = [], runtime: Bool = true) -> KeyConflictVerdict {
        var memory = KeyConflictMemory()
        return classify(timeline, ownPID: ownPID, ownChildren: ownChildren, runtime: runtime, memory: &memory)
    }

    public static func classify(_ timeline: KeyConflictTimeline, ownPID: Int32, ownChildren: Set<Int32> = [], runtime: Bool = true, memory: inout KeyConflictMemory) -> KeyConflictVerdict {
        guard let press = timeline.presses.last else { return .init(finding: nil, confirmed: false, qualifyingPress: false, os: .none) }
        let qualifying = press.duration >= 0.4 || press.doubleTap
        guard qualifying else { return .init(finding: nil, confirmed: false, qualifyingPress: false, os: .none) }
        let end = press.time + press.duration
        let recorder = recorderCandidates(timeline.recorders, press: press, ownPID: ownPID, ownChildren: ownChildren, release: end)
        let reactions = timeline.reactions.filter { reaction in
            guard reaction.time >= press.time, reaction.time <= press.time + 0.5 else { return false }
            guard reaction.pid != ownPID, reaction.pid.map({ !ownChildren.contains($0) }) ?? true else { return false }
            return true
        }
        let reactionCandidates = reactions.sorted {
            reactionPriority($0.kind) == reactionPriority($1.kind)
                ? $0.time < $1.time
                : reactionPriority($0.kind) < reactionPriority($1.kind)
        }.map { Candidate(kind: $0.kind, identity: $0.identity, pid: $0.pid) }
        let candidate = recorder.isEmpty ? reactionCandidates : recorder
        guard let first = candidate.first else {
            let verdict = KeyConflictVerdict(finding: nil, confirmed: false, qualifyingPress: true, os: .none)
            if runtime { memory.observe(verdict) }
            return verdict
        }
        let kind = first.kind
        let identities = Set(candidate.compactMap { candidate in
            if let pid = candidate.pid { return "pid:\(pid)" }
            return candidate.identity.map { "identity:\($0)" }
        })
        let one = identities.count == 1
        let pid = one ? candidate.first?.pid : nil
        let identity = one ? candidate.first?.identity : nil
        let name = one && pid != nil ? identity : nil
        let finding = KeyConflictFinding(kind: kind, severity: kind == .recorder ? .recorder : kind == .foreground ? .foreground : .window,
                                         identity: identity, pid: pid, name: name, candidateCount: identities.count)
        let confirmed: Bool
        if press.test { confirmed = true }
        else if kind == .recorder && recorder.contains(where: { $0.stoppedAfterRelease }) { confirmed = true }
        else { confirmed = !runtime || false }
        var verdict = KeyConflictVerdict(finding: finding, confirmed: confirmed, qualifyingPress: true,
                                         os: one && pid != nil ? .none : .unknown)
        if runtime {
            memory.observe(verdict)
            verdict.confirmed = verdict.confirmed || (identity.map {
                memory.confirmed(identity: $0, kind: kind)
            } ?? false)
        }
        return verdict
    }

    private struct Candidate { var kind: KeyConflictKind; var identity: String?; var pid: Int32?; var stoppedAfterRelease: Bool = false }
    private static func recorderCandidates(_ events: [KeyConflictRecorder], press: KeyConflictPress, ownPID: Int32, ownChildren: Set<Int32>, release: TimeInterval) -> [Candidate] {
        let before = events.filter { $0.time < press.time && press.time - $0.time <= 2 }.sorted { $0.time < $1.time }
        let baseline = Dictionary(grouping: before, by: { $0.pid }).compactMapValues { $0.last }
        var out: [Candidate] = []
        for event in events where event.active && event.time >= press.time - 0.25 && event.time <= press.time + 1.5 {
            guard event.pid != ownPID, event.pid.map({ !ownChildren.contains($0) }) ?? true,
                  baseline[event.pid]?.active == false else { continue }
            if !out.contains(where: { $0.pid == event.pid && $0.identity == event.identity }) {
                let stopped = events.contains { !$0.active && $0.pid == event.pid && $0.time >= release && $0.time <= release + 1.5 }
                out.append(Candidate(kind: .recorder, identity: event.identity, pid: event.pid, stoppedAfterRelease: stopped))
            }
        }
        return out
    }

    private static func reactionPriority(_ kind: KeyConflictKind) -> Int {
        switch kind {
        case .foreground: return 0
        case .window: return 1
        case .recorder: return 2
        }
    }
}
