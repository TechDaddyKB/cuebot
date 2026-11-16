import CuebotKit
import Foundation
import Observation

/// The Chat tab: the paired Cuebot's combined chat (GET /api/v1/chat, then `chat.received` and `chat.moderated`
/// events), sending as the broadcaster or the bot account, and moderating as the streamer. This is the streamer's own
/// phone, paired with the REST API token; mods moderate through the mod portal instead.
@MainActor @Observable
final class ChatStore {
    /// One line of the list: a message, or a ban or timeout someone made.
    enum Row: Identifiable, Hashable {
        case message(ChatLine)
        case notice(id: String, platform: String, time: String, text: String)

        var id: String {
            switch self {
            case .message(let line): "m:\(line.key)"
            case .notice(let id, _, _, _): id
            }
        }

        var platform: String {
            switch self {
            case .message(let line): line.platform
            case .notice(_, let platform, _, _): platform
            }
        }

        var time: String {
            switch self {
            case .message(let line): line.time
            case .notice(_, _, let time, _): time
            }
        }
    }

    /// What the last moderation or send did. `websiteUrl`: "On the website" opens it.
    struct Outcome: Equatable {
        var text: String
        var ok: Bool
        var websiteUrl: URL?
    }

    static let all = "All"
    private static let maxRows = 300

    private(set) var rows: [Row] = []
    private(set) var platforms: [String] = []
    private(set) var sendablePlatforms: [String] = []
    private(set) var botPlatforms: [String] = []
    private(set) var capabilities = ChatCapabilities()
    private(set) var loaded = false
    /// Cuebot answered 404: it is older than the Chat tab.
    private(set) var tooOld = false
    private(set) var error: String?
    private(set) var streamState: StreamState = .connecting
    private(set) var sending = false
    var outcome: Outcome?

    /// The platform shown, or nil for all of them.
    var filter: String?
    /// Where a message goes: a platform, or `ChatStore.all`.
    var sendTo = ChatStore.all { didSet { fixSendAs() } }
    var sendAs = SendAs.broadcaster { didSet { fixSendAs() } }

    private var client: CuebotClient?
    private var streamTask: Task<Void, Never>?
    private var generation = 0
    private var noticeCount = 0

    var isReconnecting: Bool {
        if case .reconnecting = streamState { return true }
        return false
    }

    var visibleRows: [Row] {
        guard let filter else { return rows }
        return rows.filter { $0.platform.caseInsensitiveCompare(filter) == .orderedSame }
    }

    /// "Bot" is offered only where a bot account is signed in (for All: on some platform).
    var canSendAsBot: Bool {
        sendTo == Self.all ? !botPlatforms.isEmpty : botPlatforms.contains { $0.caseInsensitiveCompare(sendTo) == .orderedSame }
    }

    var sendTargets: [String] {
        sendablePlatforms.count > 1 ? [Self.all] + sendablePlatforms : (sendablePlatforms.isEmpty ? [Self.all] : sendablePlatforms)
    }

    func capabilities(for platform: String) -> PlatformCapabilities { capabilities.of(platform) }

    func load(client: CuebotClient?) async {
        stop()
        generation += 1
        self.client = client
        rows = []
        loaded = false
        tooOld = false
        error = nil
        outcome = nil
        guard client != nil else { return }
        if await refresh() { startStream() }
    }

    /// The recent chat and what each tool does; false when this Cuebot has no Chat endpoints.
    @discardableResult
    func refresh() async -> Bool {
        guard let client else { return false }
        let mine = generation
        do {
            let snapshot = try await client.chat()
            // Best effort: without moderation (503) chat still reads and sends, and every tool shows as not available.
            var capabilities = ChatCapabilities()
            do { capabilities = try await client.chatCapabilities() } catch .unauthorized { throw CuebotError.unauthorized } catch {}
            guard generation == mine else { return false }
            let messages = snapshot.messages.map(Row.message)
            let notices = snapshot.moderation.compactMap(noticeRow)
            rows = Array((messages + notices).sorted { $0.time < $1.time }.suffix(Self.maxRows))
            platforms = snapshot.platforms
            sendablePlatforms = snapshot.sendablePlatforms
            botPlatforms = snapshot.botPlatforms
            self.capabilities = capabilities
            if sendTo != Self.all && !sendablePlatforms.contains(where: { $0.caseInsensitiveCompare(sendTo) == .orderedSame }) { sendTo = Self.all }
            fixSendAs()
            loaded = true
            error = nil
            return true
        } catch .notFound {
            if generation == mine { tooOld = true; loaded = true }
        } catch {
            guard generation == mine, !Task.isCancelled else { return false }
            self.error = ConnectionStore.describe(error)
            loaded = true
        }
        return false
    }

    private func startStream() {
        guard let client else { return }
        streamTask = Task { [weak self] in
            let events = client.chatEvents { state in
                Task { @MainActor in
                    guard let self else { return }
                    let wasReconnecting = self.isReconnecting
                    self.streamState = state
                    if state == .connected && wasReconnecting { await self.refresh() }   // catch up on what was missed
                }
            }
            do {
                for try await event in events {
                    guard let self else { return }
                    let e = event.apiEvent
                    guard let data = try? JSONEncoder().encode(e.args) else { continue }
                    switch e.type {
                    case CuebotClient.chatReceived:
                        if let line = try? JSONDecoder().decode(ChatLine.self, from: data) { self.add(line) }
                    case CuebotClient.chatModerated:
                        if let notice = try? JSONDecoder().decode(ModerationNotice.self, from: data) { self.apply(notice) }
                    default:
                        break
                    }
                }
            } catch {
                self?.error = ConnectionStore.describe(error as? CuebotError ?? .unreachable("\(error)"))
            }
        }
    }

    func stop() {
        streamTask?.cancel()
        streamTask = nil
    }

    func resume() {
        guard streamTask == nil, client != nil, !tooOld else { return }
        Task {
            if await refresh() { startStream() }
        }
    }

    private func add(_ line: ChatLine) {
        guard !line.id.isEmpty else { return }
        let row = Row.message(line)
        guard !rows.contains(where: { $0.id == row.id }) else { return }
        rows.append(row)
        if rows.count > Self.maxRows { rows.removeFirst(rows.count - Self.maxRows) }
    }

    private func apply(_ notice: ModerationNotice) {
        if notice.kind == .messageDeleted {
            rows = rows.map { row in
                if case .message(var line) = row, line.id == notice.messageId, line.platform.caseInsensitiveCompare(notice.platform) == .orderedSame {
                    line.deleted = true
                    return .message(line)
                }
                return row
            }
        } else if let row = noticeRow(notice) {
            rows.append(row)
            if rows.count > Self.maxRows { rows.removeFirst(rows.count - Self.maxRows) }
        }
    }

    private func noticeRow(_ n: ModerationNotice) -> Row? {
        let who = n.user.isEmpty ? n.userId : n.user
        let by = n.moderator.isEmpty ? "" : " by \(n.moderator)"
        let text: String
        switch n.kind {
        case .userBanned: text = "\(who) was banned\(by)"
        case .userTimedOut: text = "\(who) was timed out" + (n.durationSeconds.map { " for \(durationLabel($0))" } ?? "") + by
        default: return nil
        }
        noticeCount += 1
        return .notice(id: "n:\(noticeCount):\(n.time)", platform: n.platform, time: n.time, text: text)
    }

    /// Bot is only kept while the chosen target has a bot account.
    private func fixSendAs() {
        if sendAs == .bot && !canSendAsBot { sendAs = .broadcaster }
    }

    // MARK: Sending

    /// Sends `text`; true when Cuebot took it (the box can be cleared).
    func send(_ text: String) async -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let client, !trimmed.isEmpty else { return false }
        sending = true
        defer { sending = false }
        do {
            try await client.sendChat(platform: sendTo, text: trimmed, as: sendAs)
            outcome = nil
            return true
        } catch {
            outcome = Outcome(text: Self.errorText(error), ok: false)
            return false
        }
    }

    // MARK: Moderation

    /// Runs `action` on `line`'s message or author as the streamer; the result is shown as `outcome`.
    func moderate(_ line: ChatLine, _ action: ModAction, durationSeconds: Int? = nil, reason: String? = nil) async {
        guard let client else { return }
        do {
            let result = try await client.moderate(platform: line.platform, action: action, messageId: line.id, userId: line.userId,
                                                   userName: line.user, durationSeconds: durationSeconds, reason: reason)
            let text = result.message.isEmpty ? (result.ok ? "\(action.label): done." : "\(action.label) did not work.") : result.message
            let website = URL(string: result.websiteUrl).flatMap { $0.scheme == "https" ? $0 : nil }
            outcome = Outcome(text: text, ok: result.ok, websiteUrl: website)
        } catch {
            outcome = Outcome(text: Self.errorText(error), ok: false)
        }
    }

    /// The `error` of an API error answer, else the usual description.
    static func errorText(_ error: CuebotError) -> String {
        if case .http(_, let message) = error {
            let text = (try? JSONDecoder().decode(JSONValue.self, from: Data(message.utf8)))?["error"]?.string
            if let text, !text.isEmpty { return text }
        }
        return ConnectionStore.describe(error)
    }
}
