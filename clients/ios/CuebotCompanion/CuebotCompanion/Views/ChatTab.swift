import CuebotKit
import SwiftUI

/// The combined chat of every connected platform, text only: a platform filter, a send box ("Send as: Broadcaster /
/// Bot", Bot only where a bot account is signed in), and per message the moderation tools each platform allows.
struct ChatTab: View {
    @Environment(ChatStore.self) private var store
    @Environment(DashboardStore.self) private var dashboard
    @State private var acting: ChatLine?
    @AppStorage(EventsCardSettings.visibleKey) private var eventsVisible = true
    @AppStorage(EventsCardSettings.groupsKey) private var eventGroups = EventsCardSettings.defaultGroups
    @AppStorage(EventsCardSettings.expandedKey) private var eventsExpanded = false

    var body: some View {
        NavigationStack {
            ConnectionGate {
                withEvents
            }
            .navigationTitle("Chat")
            .navigationBarTitleDisplayMode(.inline)
            .sideMenuButton()
            .toolbarBackground(Color.cuebotGround, for: .navigationBar)
        }
        .sheet(item: $acting) { line in
            ModerationSheet(line: line, capabilities: store.capabilities(for: line.platform))
                .presentationDetents([.medium, .large])
        }
    }

    /// The events card (issue #45, from the Dashboard's live feed): beside chat on wide screens, a strip above it on
    /// narrow ones; Settings hides it or chooses which events it shows.
    @ViewBuilder private var withEvents: some View {
        if eventsVisible {
            let events = FeedGroup.filter(dashboard.dashboard?.feed ?? [], groups: FeedGroup.parse(eventGroups))
            GeometryReader { geometry in
                if geometry.size.width >= EventsCardSettings.wideWidth {
                    HStack(spacing: 0) {
                        content
                        EventsPanel(events: events)
                    }
                } else {
                    VStack(spacing: 0) {
                        EventsStrip(events: events, expanded: $eventsExpanded)
                        content
                    }
                }
            }
        } else {
            content
        }
    }

    @ViewBuilder private var content: some View {
        if store.tooOld {
            ContentUnavailableView {
                Label("Update Cuebot", systemImage: "arrow.down.circle")
            } description: {
                Text("This Cuebot is too old for the Chat tab. Update Cuebot on your computer to read and moderate chat here.")
            }
        } else {
            VStack(spacing: 0) {
                if store.isReconnecting { ReconnectingBar() }
                PlatformFilter()
                messages
                if let error = store.error {
                    Label(error, systemImage: "exclamationmark.triangle").font(.footnote).foregroundStyle(Color.cuebotBad).padding(.horizontal)
                }
                OutcomeLine()
                Divider()
                SendBox()
            }
        }
    }

    @ViewBuilder private var messages: some View {
        let rows = store.visibleRows
        if store.loaded && rows.isEmpty {
            ContentUnavailableView("No chat yet", systemImage: "bubble.left.and.bubble.right",
                                   description: Text("Messages from every connected platform show up here."))
                .frame(maxHeight: .infinity)
        } else {
            ScrollViewReader { proxy in
                List(rows) { row in
                    switch row {
                    case .message(let line):
                        Button { acting = line } label: { MessageText(line: line) }
                            .buttonStyle(.plain)
                            .accessibilityIdentifier("chat-line-\(line.id)")
                            .accessibilityHint("Moderate")
                            .listRowBackground(Color.cuebotGround)
                    case .notice(_, _, _, let text):
                        Text(text).font(.footnote.italic()).foregroundStyle(Color.cuebotMuted)
                            .listRowBackground(Color.cuebotGround)
                    }
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
                .onChange(of: rows.last?.id) { _, last in
                    if let last { withAnimation { proxy.scrollTo(last, anchor: .bottom) } }
                }
            }
        }
    }
}

/// One message as plain text (no links, markup or images from viewers).
private struct MessageText: View {
    let line: ChatLine

    var body: some View {
        var platform = AttributedString(String(line.platform.prefix(2)).uppercased())
        platform.foregroundColor = Color.platform(line.platform)
        platform.font = Font.footnote.bold()
        var user = AttributedString(" \(line.user): ")
        user.font = Font.body.bold()
        var text = AttributedString(line.text)
        if line.deleted {
            text.strikethroughStyle = Text.LineStyle(pattern: .solid)
            text.foregroundColor = Color.cuebotMuted
        }
        var tail = AttributedString(line.deleted ? "  (deleted)" : "")
        tail.foregroundColor = Color.cuebotMuted
        tail.font = Font.footnote.italic()
        return Text(platform + user + text + tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
    }
}

private struct PlatformFilter: View {
    @Environment(ChatStore.self) private var store

    var body: some View {
        if store.platforms.count > 1 {
            @Bindable var store = store
            Picker("Platform", selection: $store.filter) {
                Text("All").tag(String?.none)
                ForEach(store.platforms, id: \.self) { Text($0).tag(String?.some($0)) }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal)
            .padding(.vertical, 6)
            .accessibilityIdentifier("chat-filter")
        }
    }
}

private struct OutcomeLine: View {
    @Environment(ChatStore.self) private var store
    @Environment(\.openURL) private var openURL

    var body: some View {
        if let outcome = store.outcome {
            HStack {
                Text(outcome.text).font(.footnote).foregroundStyle(outcome.ok ? Color.cuebotGood : Color.cuebotWarn)
                    .accessibilityIdentifier("chat-outcome")
                Spacer()
                if let url = outcome.websiteUrl { Button("On the website") { openURL(url) }.font(.footnote) }
                Button("OK") { store.outcome = nil }.font(.footnote)
            }
            .padding(.horizontal)
            .padding(.vertical, 4)
        }
    }
}

private struct SendBox: View {
    @Environment(ChatStore.self) private var store
    @State private var text = ""

    var body: some View {
        @Bindable var store = store
        VStack(spacing: 6) {
            HStack {
                Menu {
                    ForEach(store.sendTargets, id: \.self) { target in
                        Button(target) { store.sendTo = target }
                    }
                } label: {
                    Label(store.sendTo, systemImage: "chevron.down").labelStyle(.titleAndIcon)
                }
                .accessibilityIdentifier("chat-send-to")
                Spacer()
                Text("Send as").font(.footnote).foregroundStyle(Color.cuebotMuted)
                Picker("Send as", selection: $store.sendAs) {
                    Text("Broadcaster").tag(SendAs.broadcaster)
                    if store.canSendAsBot { Text("Bot").tag(SendAs.bot) }
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .accessibilityIdentifier("chat-send-as")
            }
            HStack {
                TextField("Send a message", text: $text)
                    .textFieldStyle(.roundedBorder)
                    .submitLabel(.send)
                    .onSubmit(send)
                    .accessibilityIdentifier("chat-input")
                Button(action: send) { Image(systemName: "paperplane.fill") }
                    .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty || store.sending)
                    .accessibilityLabel("Send")
                    .accessibilityIdentifier("chat-send")
            }
        }
        .padding(8)
    }

    private func send() {
        let message = text
        Task { if await store.send(message) { text = "" } }
    }
}

/// The tools for one message and its author, each enabled as the platform allows, with the reason when it is not.
private struct ModerationSheet: View {
    let line: ChatLine
    let capabilities: PlatformCapabilities
    @Environment(ChatStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var confirmingBan = false
    @State private var warning: String?

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(line.text)
                    Text("\(line.platform) · \(line.userId)").font(.footnote).foregroundStyle(Color.cuebotMuted)
                }
                Section {
                    tool(.delete, enabled: line.canDelete && !line.deleted,
                         note: line.canDelete ? nil : "\(line.platform) gave this message no id, so it cannot be deleted.") { run(.delete) }
                    if capabilities.timeoutPresets.isEmpty {
                        tool(.timeout, label: "Timeout 10 min", id: "mod-timeout-600") { run(.timeout, seconds: 600) }
                    }
                    ForEach(capabilities.timeoutPresets) { preset in
                        tool(.timeout, label: "Timeout \(preset.label)", id: "mod-timeout-\(preset.requestedSeconds)",
                             note: preset.note.isEmpty ? nil : preset.note) { run(.timeout, seconds: preset.requestedSeconds) }
                    }
                    tool(.ban) { confirmingBan = true }
                    tool(.unban) { run(.unban) }
                    tool(.warn) { warning = "" }
                }
            }
            .navigationTitle(line.user)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } } }
            .confirmationDialog("Ban \(line.user)?", isPresented: $confirmingBan, titleVisibility: .visible) {
                Button("Ban", role: .destructive) { run(.ban) }
            } message: {
                Text("\(line.user) can no longer chat on \(line.platform) until you unban them.")
            }
            .alert("Warn \(line.user)", isPresented: Binding(get: { warning != nil }, set: { if !$0 { warning = nil } })) {
                TextField("Warning", text: Binding(get: { warning ?? "" }, set: { value in
                    warning = capabilities.tool(.warn).maxReasonLength.map { String(value.prefix($0)) } ?? value
                }))
                Button("Warn") { run(.warn, reason: warning) }
                Button("Cancel", role: .cancel) { warning = nil }
            } message: {
                Text(capabilities.tool(.warn).note)
            }
        }
    }

    @ViewBuilder
    private func tool(_ action: ModAction, label: String? = nil, id: String? = nil, enabled: Bool = true, note: String? = nil,
                      run: @escaping () -> Void) -> some View {
        let capability = capabilities.tool(action)
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Button(label ?? action.label, role: action == .ban ? .destructive : nil, action: run)
                    .disabled(!(capability.canRun && enabled))
                    .accessibilityIdentifier(id ?? "mod-\(action.rawValue)")
                if capability.state == .onWebsite, let url = URL(string: capability.websiteUrl), url.scheme == "https" {
                    Spacer()
                    Button("On the website") { openURL(url) }.buttonStyle(.borderless)
                }
            }
            let why = note ?? ((!capability.canRun || capability.state == .closest) && !capability.note.isEmpty ? capability.note : nil)
            if let why { Text(why).font(.footnote).foregroundStyle(Color.cuebotMuted) }
        }
    }

    private func run(_ action: ModAction, seconds: Int? = nil, reason: String? = nil) {
        let store = store, line = line
        Task { await store.moderate(line, action, durationSeconds: seconds, reason: reason) }
        dismiss()
    }
}
