import CuebotKit
import SwiftUI

/// The Chat page's events card settings (per device, in UserDefaults).
enum EventsCardSettings {
    static let visibleKey = "eventsCard.visible"
    static let groupsKey = "eventsCard.groups"
    static let expandedKey = "eventsCard.expanded"
    static let defaultGroups = FeedGroup.encode(FeedGroup.defaults)
    /// From this width the card sits beside chat instead of above it (iPad, large phones in landscape).
    static let wideWidth: CGFloat = 600
}

/// The events card beside chat (wide screens): every recent event, newest first.
struct EventsPanel: View {
    let events: [FeedItem]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Events", systemImage: "party.popper").font(.headline)
                Spacer()
                if !events.isEmpty { Text("\(events.count)").font(.footnote).foregroundStyle(Color.cuebotMuted) }
            }
            EventList(events: events)
            Spacer(minLength: 0)
        }
        .padding(12)
        .frame(width: 320)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Color.cuebotCard, in: RoundedRectangle(cornerRadius: 16))
        .padding(8)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("events-card")
    }
}

/// The events card above chat (narrow screens): one line with the newest event, opened to a short list with a tap.
struct EventsStrip: View {
    let events: [FeedItem]
    @Binding var expanded: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button { withAnimation { expanded.toggle() } } label: {
                HStack(spacing: 8) {
                    Image(systemName: "party.popper").foregroundStyle(.tint)
                    Text("Events").font(.subheadline.weight(.semibold))
                    Text(expanded ? "" : events.first.map(EventRow.headline) ?? "None yet")
                        .font(.footnote).foregroundStyle(Color.cuebotMuted).lineLimit(1)
                    Spacer(minLength: 4)
                    Image(systemName: expanded ? "chevron.up" : "chevron.down").foregroundStyle(Color.cuebotMuted)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("events-toggle")
            .accessibilityValue(expanded ? "Expanded" : "Collapsed")
            if expanded {
                EventList(events: events)
                    .frame(maxHeight: 220)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 8)
            }
        }
        .background(Color.cuebotCard, in: RoundedRectangle(cornerRadius: 12))
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("events-card")
    }
}

private struct EventList: View {
    let events: [FeedItem]

    var body: some View {
        if events.isEmpty {
            Text("No events yet. Follows, subs, tips and raids from every platform show up here.")
                .font(.footnote).foregroundStyle(Color.cuebotMuted)
                .accessibilityIdentifier("events-empty")
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(events.enumerated()), id: \.offset) { index, item in
                        EventRow(item: item).accessibilityIdentifier("event-\(index)")
                        Divider()
                    }
                }
            }
        }
    }
}

/// One event: platform, what happened (with the name), its type, the amount and when.
private struct EventRow: View {
    let item: FeedItem

    /// What happened: Cuebot's title ("ann cheered 500 bits"), else who and what.
    static func headline(_ item: FeedItem) -> String {
        if !item.title.isEmpty { return item.title }
        return [item.user, FeedGroup.kindLabel(item.kind).lowercased()].filter { !$0.isEmpty }.joined(separator: " ")
    }

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            PlatformIcon(platform: item.platform)
            VStack(alignment: .leading, spacing: 2) {
                Text(Self.headline(item)).font(.subheadline.weight(.semibold)).lineLimit(2)
                let about = [item.platform, FeedGroup.kindLabel(item.kind), item.isTest ? "test" : ""].filter { !$0.isEmpty }.joined(separator: " · ")
                if !about.isEmpty { Text(about).font(.caption2).foregroundStyle(Color.cuebotMuted) }
            }
            Spacer(minLength: 4)
            VStack(alignment: .trailing, spacing: 2) {
                if !item.amount.isEmpty { Text(item.amount).font(.footnote.weight(.semibold)) }
                if let date = FeedCard.date(item.time) { Text(date, style: .relative).font(.caption2).foregroundStyle(Color.cuebotMuted) }
            }
        }
        .padding(.vertical, 6)
        .accessibilityElement(children: .combine)
    }
}
