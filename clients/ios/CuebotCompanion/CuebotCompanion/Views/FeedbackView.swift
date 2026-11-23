import CuebotKit
import SwiftUI
import UIKit

/// Send feedback: what the user writes and the listed app facts become a public GitHub issue, after they tap Send.
struct FeedbackView: View {
    private static let installIdKey = "feedback.installId"

    @Environment(PairedCuebots.self) private var paired
    @Environment(ConnectionStore.self) private var connection
    @Environment(ModStore.self) private var mods
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL
    @State private var kind = FeedbackKind.problem
    @State private var title = ""
    @State private var details = ""
    @State private var sending = false
    @State private var error: String?
    @State private var sent: (number: Int, url: URL)?

    var body: some View {
        NavigationStack {
            Form {
                Group {
                    if let sent {
                        Section {
                            Text(verbatim: "It was posted as issue #\(sent.number).").accessibilityIdentifier("feedback-sent")
                            Button("View issue") { openURL(sent.url) }
                        } header: {
                            Text("Thanks for the feedback")
                        }
                    } else {
                        form
                    }
                }
                .listRowBackground(Color.cuebotCard)
            }
            .scrollContentBackground(.hidden)
            .background(Color.cuebotGround)
            .navigationTitle("Send feedback")
            .navigationBarTitleDisplayMode(.inline)
            .interactiveDismissDisabled(sending)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(sent == nil ? "Cancel" : "Done") { dismiss() }.disabled(sending)
                }
                if sent == nil {
                    ToolbarItem(placement: .confirmationAction) {
                        if sending {
                            ProgressView()
                        } else {
                            Button("Send") { Task { await send() } }
                                .disabled(title.trimmingCharacters(in: .whitespacesAndNewlines).count < 3
                                          || details.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                                .accessibilityIdentifier("feedback-send")
                        }
                    }
                }
            }
        }
    }

    @ViewBuilder private var form: some View {
        Section {
            Picker("Type", selection: $kind) {
                ForEach(FeedbackKind.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            TextField("Short summary", text: $title)
                .accessibilityIdentifier("feedback-title")
                .onChange(of: title) { _, new in if new.count > FeedbackText.maxTitle - 20 { title = String(new.prefix(FeedbackText.maxTitle - 20)) } }
            TextField(kind.prompt, text: $details, axis: .vertical)
                .lineLimit(4...8)
                .accessibilityIdentifier("feedback-details")
                .onChange(of: details) { _, new in if new.count > FeedbackText.maxDetails { details = String(new.prefix(FeedbackText.maxDetails)) } }
        } footer: {
            Text("This is posted publicly on Cuebot's GitHub issues. Links, email addresses, IP addresses and tokens we recognize are removed, but check your message and don't include personal or private details.")
        }
        Section("Also included") {
            ForEach(context.rows, id: \.name) { row in
                LabeledContent(row.name, value: row.value).font(.footnote)
            }
        }
        if let error {
            Section {
                Text(error).foregroundStyle(Color.cuebotBad).accessibilityIdentifier("feedback-error")
            }
        }
    }

    private var context: FeedbackContext {
        let info = Bundle.main.infoDictionary
        let state: String = switch connection.state {
        case .unpaired: "Not paired"
        case .checking: "Checking"
        case .online: "Online"
        case .unreachable: "Can't reach Cuebot"
        case .unauthorized: "Cuebot refused the pairing"
        }
        return FeedbackContext(
            appVersion: info?["CFBundleShortVersionString"] as? String ?? "0.0.0",
            build: info?["CFBundleVersion"] as? String ?? "",
            system: UIDevice.current.systemVersion,
            device: Self.deviceModel,
            formFactor: UIDevice.current.userInterfaceIdiom == .pad ? "Tablet" : "Phone",
            language: Locale.current.identifier(.bcp47),
            appearance: colorScheme == .dark ? "Dark" : "Light",
            connection: state,
            pairedCuebots: paired.all.count,
            modMemberships: mods.memberships.all.count,
            cuebotVersion: connection.status?.app.version,
            apiVersion: connection.status?.app.apiVersion
        )
    }

    /// The hardware model such as iPhone18,1: it names no person or device owner.
    private static var deviceModel: String {
        if let simulated = ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] { return simulated }
        var info = utsname()
        uname(&info)
        return withUnsafeBytes(of: &info.machine) { String(decoding: $0.prefix(while: { $0 != 0 }), as: UTF8.self) }
    }

    private func installId() -> String {
        let defaults = UserDefaults.standard
        if let saved = defaults.string(forKey: Self.installIdKey), FeedbackReporter.isInstallId(saved) { return saved }
        let id = FeedbackReporter.newInstallId()
        defaults.set(id, forKey: Self.installIdKey)
        return id
    }

    private func send() async {
        sending = true
        error = nil
        let result = await FeedbackReporter().send(kind: kind, title: title, details: details, context: context, installId: installId())
        sending = false
        switch result {
        case .sent(let number, let url): sent = (number, url)
        case .failed(let message): error = message
        }
    }
}
