import CuebotKit
import SwiftUI

/// Connections, subathon timers, goals, top spenders, stream totals, the activity feed and pinned quick actions,
/// live from /api/v1/dashboard. On iPad the cards sit side by side.
struct DashboardTab: View {
    @Environment(DashboardStore.self) private var store
    @Environment(ConnectionStore.self) private var connection
    @State private var editingActions = false

    private let columns = [GridItem(.adaptive(minimum: 340), spacing: 16, alignment: .top)]

    var body: some View {
        NavigationStack {
            ConnectionGate {
                content
            }
            .navigationTitle("Dashboard")
            .toolbarBackground(Color.cuebotGround, for: .navigationBar)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { editingActions = true } label: { Label("Quick actions", systemImage: "bolt.badge.clock") }
                }
            }
        }
        .sheet(isPresented: $editingActions) { QuickActionsEditor() }
    }

    @ViewBuilder private var content: some View {
        if store.tooOld {
            ContentUnavailableView {
                Label("Update Cuebot", systemImage: "arrow.down.circle")
            } description: {
                Text("This Cuebot is too old for the Dashboard. The Deck tab still works; update Cuebot on your computer to see timers, goals, top spenders and the feed here.")
            }
        } else if let dashboard = store.dashboard {
            ScrollView {
                VStack(spacing: 0) {
                    if store.isReconnecting { ReconnectingBar() }
                    VStack(alignment: .leading, spacing: 16) {
                        ConnectionsStrip(connections: dashboard.connections)
                        if let error = store.controlError {
                            Label(error, systemImage: "exclamationmark.triangle").font(.footnote).foregroundStyle(Color.cuebotBad)
                        }
                        LazyVGrid(columns: columns, alignment: .leading, spacing: 16) {
                            QuickActionsCard(editing: $editingActions)
                            ForEach(dashboard.subathon.timers) { SubathonCard(timer: $0) }
                            ForEach(dashboard.subathon.goals) { GoalCard(goal: $0) }
                            if !dashboard.topSpenders.periodKeys.isEmpty { TopSpendersCard(spenders: dashboard.topSpenders) }
                            TotalsCard(totals: dashboard.totals)
                            FeedCard(feed: dashboard.feed)
                        }
                    }
                    .padding()
                }
            }
            .background(Color.cuebotGround)
            .refreshable { await store.refresh() }
        } else if let error = store.error {
            ContentUnavailableView("Dashboard unavailable", systemImage: "exclamationmark.triangle", description: Text(error))
        } else {
            ProgressView("Loading the dashboard…")
        }
    }
}

/// A card: a titled, rounded group.
struct Card<Content: View>: View {
    let title: String
    var systemImage: String?
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label {
                Text(title).font(.headline)
            } icon: {
                if let systemImage { Image(systemName: systemImage).foregroundStyle(.tint) }
            }
            content()
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.cuebotCard, in: RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.cuebotLine, lineWidth: 0.5))
    }
}

// MARK: Connections

struct ConnectionsStrip: View {
    let connections: [ConnectionInfo]
    @State private var shown: ConnectionInfo?

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(connections) { item in
                    Button { shown = item } label: {
                        HStack(spacing: 6) {
                            Circle().fill(item.state.color).frame(width: 9, height: 9)
                            Text(item.name).font(.footnote.weight(.medium))
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 6)
                        .background(Color.cuebotCard, in: Capsule())
                        .overlay(Capsule().strokeBorder(Color.cuebotLine, lineWidth: 0.5))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(item.name), \(item.state.rawValue)")
                }
            }
        }
        .alert(shown?.name ?? "", isPresented: Binding(get: { shown != nil }, set: { if !$0 { shown = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(shown.map { $0.status.isEmpty ? $0.state.rawValue : "\($0.state.rawValue): \($0.status)" } ?? "")
        }
    }
}

extension ConnectionState {
    var color: Color {
        switch self {
        case .connected: .cuebotGood
        case .connecting: .cuebotWarn
        case .error: .cuebotBad
        case .disconnected, .unknown: .cuebotMuted
        }
    }
}

// MARK: Subathon

struct SubathonCard: View {
    @Environment(DashboardStore.self) private var store
    let timer: SubathonTimer
    @State private var asking: Ask?

    enum Ask: Identifiable { case minutes, multiplier; var id: Self { self } }

    var body: some View {
        Card(title: timer.name.isEmpty ? "Subathon" : timer.name, systemImage: "timer") {
            // Counted down here from endsAt: no update from Cuebot is needed every second.
            TimelineView(.periodic(from: .now, by: 1)) { context in
                Text(SubathonTimer.format(timer.remaining(at: context.date)))
                    .font(.system(size: 44, weight: .bold, design: .rounded).monospacedDigit())
                    .contentTransition(.numericText(countsDown: true))
                    .frame(maxWidth: .infinity, alignment: .center)
                    .accessibilityLabel("Time left")
            }
            HStack {
                Text(timer.state.label).font(.caption.weight(.semibold))
                    .padding(.horizontal, 8).padding(.vertical, 3)
                    .background(timer.state.color.opacity(0.2), in: Capsule())
                    .foregroundStyle(timer.state.color)
                Spacer()
                Text("Added \(SubathonTimer.format(timer.added))").font(.caption).foregroundStyle(Color.cuebotMuted)
                Text(Self.multiplierText(timer.multiplier)).font(.caption.weight(.bold)).foregroundStyle(Color.cuebotMuted)
            }
            HStack(spacing: 8) {
                Group {
                switch timer.state {
                case .running:
                    Button { Task { await store.subathon(timer, .pause) } } label: { Label("Pause", systemImage: "pause.fill") }
                case .paused:
                    Button { Task { await store.subathon(timer, .resume) } } label: { Label("Resume", systemImage: "play.fill") }
                default:
                    Button { Task { await store.subathon(timer, .start) } } label: { Label("Start", systemImage: "play.fill") }
                }
                }
                .fixedSize()
                Spacer()
                Button("+1 min") { Task { await store.addTime(timer, seconds: 60) } }
                Button("+5 min") { Task { await store.addTime(timer, seconds: 300) } }
                Menu {
                    Button("Add or remove minutes…") { asking = .minutes }
                    Divider()
                    ForEach([0.5, 1, 2], id: \.self) { value in
                        Button(Self.multiplierText(value)) { Task { await store.setMultiplier(timer, value) } }
                    }
                    Button("Other multiplier…") { asking = .multiplier }
                } label: {
                    Image(systemName: "ellipsis.circle").imageScale(.large)
                }
                .accessibilityLabel("More timer controls")
            }
            .buttonStyle(.bordered)
        }
        .numberPrompt(item: $asking) { ask in
            switch ask {
            case .minutes: NumberPrompt(title: "Change the time", message: "Minutes to add (negative takes time off).", placeholder: "10") { value in
                Task { await store.addTime(timer, seconds: value * 60) }
            }
            case .multiplier: NumberPrompt(title: "Multiplier", message: "How much each event adds, e.g. 1.5.", placeholder: "1.5") { value in
                if value > 0 { Task { await store.setMultiplier(timer, value) } }
            }
            }
        }
    }

    static func multiplierText(_ value: Double) -> String { value.formatted(.number.precision(.fractionLength(0...2))) + "×" }
}

extension SubathonTimer.State {
    var label: String {
        switch self {
        case .running: "Running"
        case .paused: "Paused"
        case .stopped: "Stopped"
        case .unknown: "Unknown"
        }
    }

    var color: Color {
        switch self {
        case .running: .cuebotGood
        case .paused: .cuebotWarn
        default: .cuebotMuted
        }
    }
}

// MARK: Goals

struct GoalCard: View {
    @Environment(DashboardStore.self) private var store
    let goal: PointsGoal
    @State private var asking: Ask?
    @State private var confirmReset = false

    enum Ask: Identifiable { case add, set, multiplier; var id: Self { self } }

    var body: some View {
        Card(title: goal.name.isEmpty ? "Goal" : goal.name, systemImage: "flag.checkered") {
            HStack(alignment: .firstTextBaseline) {
                Text(goal.points.formatted()).font(.system(size: 34, weight: .bold, design: .rounded).monospacedDigit())
                Text("points").foregroundStyle(Color.cuebotMuted)
                Spacer()
                if goal.multiplier != 1 { Text(SubathonCard.multiplierText(goal.multiplier)).font(.caption.weight(.bold)).foregroundStyle(Color.cuebotMuted) }
            }
            ProgressView(value: goal.progressToNext)
                .accessibilityLabel(goal.next.map { "Progress to \($0.name)" } ?? "All steps reached")
            if let next = goal.next {
                Text("Next: \(next.name) at \(next.points.formatted())").font(.subheadline)
            } else if !goal.steps.isEmpty {
                Text("Every step reached").font(.subheadline).foregroundStyle(Color.cuebotGood)
            }
            if !goal.steps.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(Array(goal.steps.enumerated()), id: \.offset) { _, step in
                        Label {
                            Text("\(step.name) · \(step.points.formatted())")
                        } icon: {
                            Image(systemName: step.reached || goal.points >= step.points ? "checkmark.circle.fill" : "circle")
                                .foregroundStyle(step.reached || goal.points >= step.points ? Color.cuebotGood : Color.cuebotMuted)
                        }
                        .font(.footnote)
                    }
                }
            }
            HStack(spacing: 8) {
                Button("+1") { Task { await store.addPoints(goal, 1) } }
                Button("+5") { Task { await store.addPoints(goal, 5) } }
                Button("+10") { Task { await store.addPoints(goal, 10) } }
                Spacer()
                Menu {
                    Button("Add points…") { asking = .add }
                    Button("Set points…") { asking = .set }
                    Button("Multiplier…") { asking = .multiplier }
                    Divider()
                    Button("Reset", role: .destructive) { confirmReset = true }
                } label: {
                    Image(systemName: "ellipsis.circle").imageScale(.large)
                }
                .accessibilityLabel("More goal controls")
            }
            .buttonStyle(.bordered)
        }
        .numberPrompt(item: $asking) { ask in
            switch ask {
            case .add: NumberPrompt(title: "Add points", message: "Negative takes points off.", placeholder: "25") { value in Task { await store.addPoints(goal, value) } }
            case .set: NumberPrompt(title: "Set points", message: "The goal's new total.", placeholder: "100") { value in Task { await store.setPoints(goal, value) } }
            case .multiplier: NumberPrompt(title: "Multiplier", message: "How much each event counts, e.g. 2.", placeholder: "2") { value in
                if value > 0 { Task { await store.setMultiplier(goal, value) } }
            }
            }
        }
        .confirmationDialog("Reset \(goal.name)?", isPresented: $confirmReset, titleVisibility: .visible) {
            Button("Reset to 0", role: .destructive) { Task { await store.reset(goal) } }
        } message: {
            Text("The points go back to 0 and the steps start over.")
        }
    }
}

// MARK: Top spenders

struct TopSpendersCard: View {
    let spenders: TopSpenders
    @State private var period = "stream"
    @State private var platform = "all"

    var body: some View {
        let periods = spenders.periodKeys
        let current = periods.contains(period) ? period : periods.first ?? "stream"
        let platforms = spenders.platformKeys(period: current)
        let shownPlatform = platforms.contains(platform) ? platform : "all"
        let board = spenders.board(period: current, platform: shownPlatform)
        Card(title: "Top spenders", systemImage: "crown") {
            Picker("Period", selection: $period) {
                ForEach(periods, id: \.self) { Text(Self.periodName($0)).tag($0) }
            }
            .pickerStyle(.segmented)
            if platforms.count > 1 {
                Picker("Platform", selection: $platform) {
                    ForEach(platforms, id: \.self) { Text($0 == "all" ? "All platforms" : $0.capitalized).tag($0) }
                }
            }
            if board.spenders.isEmpty {
                Text("Nobody yet").foregroundStyle(Color.cuebotMuted)
            } else {
                ForEach(board.spenders.prefix(5)) { spender in
                    HStack {
                        Text("\(spender.rank)").font(.headline.monospacedDigit()).frame(width: 24)
                        PlatformIcon(platform: spender.platform)
                        Text(spender.name).lineLimit(1)
                        Spacer()
                        Text(spender.formatted).font(.body.monospacedDigit().weight(.semibold))
                    }
                    .accessibilityElement(children: .combine)
                }
            }
            if !board.formattedTotal.isEmpty {
                Text("Total \(board.formattedTotal)").font(.caption).foregroundStyle(Color.cuebotMuted)
            }
        }
    }

    static func periodName(_ key: String) -> String {
        switch key {
        case "stream": "Stream"
        case "day": "Day"
        case "week": "Week"
        case "month": "Month"
        case "year": "Year"
        case "alltime", "all": "All time"
        default: key.capitalized
        }
    }
}

// MARK: Totals

struct TotalsCard: View {
    let totals: [String: JSONValue]

    var body: some View {
        let rows = StreamTotals.rows(totals)
        Card(title: "This stream", systemImage: "chart.bar") {
            if rows.isEmpty {
                Text("Nothing counted yet this stream").foregroundStyle(Color.cuebotMuted)
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), alignment: .leading)], alignment: .leading, spacing: 12) {
                    ForEach(rows) { row in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(row.value).font(.title2.weight(.bold).monospacedDigit())
                            Text(row.label).font(.caption.weight(.medium))
                            if !row.detail.isEmpty { Text(row.detail).font(.caption2).foregroundStyle(Color.cuebotMuted) }
                        }
                        .accessibilityElement(children: .combine)
                    }
                }
            }
        }
    }
}

// MARK: Feed

struct FeedCard: View {
    let feed: [FeedItem]

    var body: some View {
        Card(title: "Activity", systemImage: "list.bullet.rectangle") {
            if feed.isEmpty {
                Text("No events yet").foregroundStyle(Color.cuebotMuted)
            } else {
                ForEach(Array(feed.prefix(50).enumerated()), id: \.offset) { _, item in
                    HStack(alignment: .top, spacing: 10) {
                        PlatformIcon(platform: item.platform)
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) {
                                Text(item.title.isEmpty ? "\(item.user) · \(item.kind)" : item.title).font(.subheadline.weight(.semibold))
                                if item.isTest {
                                    Text("TEST").font(.caption2.weight(.bold)).padding(.horizontal, 5).padding(.vertical, 1)
                                        .background(Color.cuebotWarn.opacity(0.22), in: Capsule()).foregroundStyle(Color.cuebotWarn)
                                }
                            }
                            if !item.detail.isEmpty { Text(item.detail).font(.footnote).foregroundStyle(Color.cuebotMuted).lineLimit(3) }
                        }
                        Spacer(minLength: 4)
                        VStack(alignment: .trailing, spacing: 2) {
                            if !item.amount.isEmpty { Text(item.amount).font(.footnote.weight(.semibold)) }
                            if let date = Self.date(item.time) { Text(date, style: .relative).font(.caption2).foregroundStyle(Color.cuebotMuted) }
                        }
                    }
                    .accessibilityElement(children: .combine)
                    Divider()
                }
            }
        }
    }

    private static func date(_ text: String) -> Date? {
        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = iso.date(from: text) { return date }
        iso.formatOptions = [.withInternetDateTime]
        return iso.date(from: text)
    }
}

struct PlatformIcon: View {
    let platform: String

    var body: some View {
        let symbol = switch platform.lowercased() {
        case "twitch": "tv.fill"
        case "youtube": "play.rectangle.fill"
        case "kick": "k.circle.fill"
        case "rumble": "r.circle.fill"
        case "": "circle.fill"
        default: "dollarsign.circle.fill"
        }
        let color = platform.isEmpty ? Color.cuebotMuted : ["twitch", "youtube", "kick", "rumble"].contains(platform.lowercased()) ? Color.platform(platform) : .accentColor
        Image(systemName: symbol).foregroundStyle(color).frame(width: 22).accessibilityLabel(platform)
    }
}

// MARK: Quick actions

struct QuickActionsCard: View {
    @Environment(QuickActionsStore.self) private var actions
    @Binding var editing: Bool
    @State private var confirming: QuickActionsStore.Pinned?
    @State private var result: (id: String, result: QuickActionsStore.RunResult)?
    @State private var tick = 0

    var body: some View {
        Card(title: "Quick actions", systemImage: "bolt.fill") {
            if actions.pinned.isEmpty {
                Button("Pin actions to run from here…") { editing = true }
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 130), spacing: 8)], spacing: 8) {
                    ForEach(actions.pinned) { pinned in
                        Button {
                            if pinned.askFirst { confirming = pinned } else { run(pinned) }
                        } label: {
                            Text(pinned.name)
                                .font(.subheadline.weight(.semibold))
                                .lineLimit(2)
                                .frame(maxWidth: .infinity, minHeight: 44)
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(tint(for: pinned))
                    }
                }
            }
            if case .failed(let message)? = result?.result { Text(message).font(.footnote).foregroundStyle(Color.cuebotBad) }
        }
        .sensoryFeedback(trigger: tick) { _, _ in
            if case .failed? = result?.result { return .error }
            return .success
        }
        .confirmationDialog("Run \(confirming?.name ?? "")?", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }),
                            titleVisibility: .visible) {
            Button("Run") { if let confirming { run(confirming) } }
        }
    }

    private func tint(for pinned: QuickActionsStore.Pinned) -> Color {
        guard result?.id == pinned.id else { return .accentColor }
        if case .failed = result?.result { return .cuebotBad }
        return .cuebotGood
    }

    private func run(_ pinned: QuickActionsStore.Pinned) {
        Task {
            let outcome = await actions.run(pinned)
            result = (pinned.id, outcome)
            tick += 1
            try? await Task.sleep(for: .seconds(1.5))
            if result?.id == pinned.id, outcome == .started { result = nil }
        }
    }
}

struct QuickActionsEditor: View {
    @Environment(QuickActionsStore.self) private var actions
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                Group {
                if !actions.pinned.isEmpty {
                    Section {
                        ForEach(actions.pinned) { pinned in
                            Toggle(isOn: Binding(get: { pinned.askFirst }, set: { actions.setAskFirst(pinned.id, $0) })) {
                                VStack(alignment: .leading) {
                                    Text(pinned.name)
                                    Text("Ask before running").font(.caption).foregroundStyle(Color.cuebotMuted)
                                }
                            }
                        }
                        .onMove { actions.move(from: $0, to: $1) }
                    } header: {
                        Text("Pinned")
                    } footer: {
                        Text("Turn on \"Ask before running\" for actions that can't be undone.")
                    }
                }
                Section("All actions") {
                    if let error = actions.loadError { Text(error).foregroundStyle(Color.cuebotBad) }
                    ForEach(actions.available.filter(\.enabled)) { action in
                        Button { actions.toggle(action) } label: {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text(action.name).foregroundStyle(.primary)
                                    if !action.group.isEmpty { Text(action.group).font(.caption).foregroundStyle(Color.cuebotMuted) }
                                }
                                Spacer()
                                Image(systemName: actions.isPinned(action.id) ? "pin.fill" : "pin").foregroundStyle(.tint)
                            }
                        }
                    }
                }
                }
                .listRowBackground(Color.cuebotCard)
            }
            .scrollContentBackground(.hidden)
            .background(Color.cuebotGround)
            .navigationTitle("Quick actions")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { EditButton() }
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
            .task { await actions.fetchActions() }
        }
    }
}

// MARK: Number prompt

/// An alert asking for one number.
struct NumberPrompt {
    var title: String
    var message: String
    var placeholder: String
    var done: (Double) -> Void
}

private struct NumberPromptModifier<Item: Identifiable>: ViewModifier {
    @Binding var item: Item?
    let prompt: (Item) -> NumberPrompt
    @State private var text = ""

    func body(content: Content) -> some View {
        let current = item.map(prompt)
        content.alert(current?.title ?? "", isPresented: Binding(get: { item != nil }, set: { if !$0 { item = nil; text = "" } })) {
            TextField(current?.placeholder ?? "", text: $text).keyboardType(.numbersAndPunctuation)
            Button("OK") {
                if let value = Double(text.replacingOccurrences(of: ",", with: ".")) { current?.done(value) }
                text = ""
            }
            Button("Cancel", role: .cancel) { text = "" }
        } message: {
            Text(current?.message ?? "")
        }
    }
}

extension View {
    func numberPrompt<Item: Identifiable>(item: Binding<Item?>, prompt: @escaping (Item) -> NumberPrompt) -> some View {
        modifier(NumberPromptModifier(item: item, prompt: prompt))
    }
}
