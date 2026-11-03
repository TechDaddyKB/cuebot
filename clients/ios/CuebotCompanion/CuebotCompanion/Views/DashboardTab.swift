import CuebotKit
import SwiftUI

/// Milestone I3 fills this with the live dashboard; for now it shows the connections from /api/v1/status.
struct DashboardTab: View {
    @Environment(ConnectionStore.self) private var connection

    var body: some View {
        NavigationStack {
            ConnectionGate {
                List {
                    Section("Connections") {
                        if let status = connection.status {
                            if status.connections.isEmpty { Text("Nothing connected yet").foregroundStyle(.secondary) }
                            ForEach(status.connections) { item in
                                HStack {
                                    Circle().fill(item.state.color).frame(width: 10, height: 10)
                                    Text(item.name)
                                    Spacer()
                                    Text(item.status.isEmpty ? item.state.rawValue : item.status)
                                        .font(.footnote).foregroundStyle(.secondary).lineLimit(1)
                                }
                                .accessibilityElement(children: .combine)
                            }
                        } else {
                            ProgressView()
                        }
                    }
                    Section {
                        Text("Subathon timers, goals, top spenders and the activity feed arrive with the next update of the app and of Cuebot.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }
                }
                .refreshable { await connection.check() }
            }
            .navigationTitle("Dashboard")
        }
    }
}

extension ConnectionState {
    var color: Color {
        switch self {
        case .connected: .green
        case .connecting: .orange
        case .error: .red
        case .disconnected, .unknown: .gray
        }
    }
}
