import CuebotKit
import SwiftUI

struct SettingsTab: View {
    @Environment(PairedCuebots.self) private var paired
    @Environment(ConnectionStore.self) private var connection
    @State private var pairing = false
    @State private var renaming: PairedCuebot?
    @State private var newName = ""

    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach(paired.all) { entry in
                        Button { paired.activate(entry.id) } label: {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text(entry.name).foregroundStyle(.primary)
                                    Text(verbatim: "\(entry.host):\(entry.apiPort)").font(.footnote.monospaced()).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if entry.id == paired.activeId { Image(systemName: "checkmark").foregroundStyle(.tint) }
                            }
                        }
                        .swipeActions {
                            Button("Remove", role: .destructive) { paired.remove(entry.id) }
                            Button("Rename") {
                                newName = entry.name
                                renaming = entry
                            }
                        }
                    }
                    Button { pairing = true } label: { Label("Pair a Cuebot", systemImage: "qrcode.viewfinder") }
                        .accessibilityIdentifier("pair-button")
                } header: {
                    Text("Paired Cuebots")
                } footer: {
                    Text("Swipe a Cuebot to rename or remove it. Tap one to use it.")
                }
                Section("About") {
                    LabeledContent("App", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")
                    if let status = connection.status {
                        LabeledContent("Cuebot", value: status.app.version)
                        LabeledContent("API", value: status.app.apiVersion)
                    }
                    Link("Troubleshooting", destination: URL(string: "https://techdaddykb.github.io/cuebot/guides/troubleshooting/")!)
                }
            }
            .navigationTitle("Settings")
            .sheet(isPresented: $pairing) { PairingView() }
            .alert("Rename", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
                TextField("Name", text: $newName)
                Button("Save") { if let renaming { paired.rename(renaming.id, to: newName) } }
                Button("Cancel", role: .cancel) {}
            }
        }
    }
}

/// Pairing: scan Cuebot's QR code, or type the details.
struct PairingView: View {
    @Environment(PairedCuebots.self) private var paired
    @Environment(\.dismiss) private var dismiss
    @State private var host = ""
    @State private var apiPort = "\(CuebotEndpoint.defaultApiPort)"
    @State private var httpPort = "\(CuebotEndpoint.defaultHttpPort)"
    @State private var token = ""
    @State private var deckToken = ""
    @State private var name = ""
    @State private var error: String?
    @State private var scanning = QRScannerView.isAvailable

    var body: some View {
        NavigationStack {
            Form {
                if scanning {
                    Section {
                        QRScannerView { code in
                            do {
                                paired.pair(try PairingLink.parse(code))
                                dismiss()
                            } catch {
                                self.error = Self.describe(error)
                            }
                        }
                        .frame(height: 300)
                        .listRowInsets(EdgeInsets())
                        Button("Enter the details instead") { scanning = false }
                    } footer: {
                        Text("In Cuebot on your computer: Settings → API → Pair a phone.")
                    }
                } else {
                    Section {
                        TextField("Computer address (e.g. 192.168.1.20)", text: $host)
                            .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                            .accessibilityIdentifier("pair-host")
                        TextField("API port", text: $apiPort).keyboardType(.numberPad)
                        TextField("Deck port", text: $httpPort).keyboardType(.numberPad)
                        SecureField("API token", text: $token).accessibilityIdentifier("pair-token")
                        SecureField("Deck token (if Cuebot shows one)", text: $deckToken)
                        TextField("Name (optional)", text: $name)
                    } footer: {
                        Text("Cuebot shows these under Settings → API. You can also paste a cuebot://pair link into the address field.")
                    }
                    if QRScannerView.isAvailable { Button("Scan the QR code instead") { scanning = true } }
                }
                if let error { Section { Text(error).foregroundStyle(.red) } }
            }
            .navigationTitle("Pair a Cuebot")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                if !scanning {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Pair") { pairManually() }
                            .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty)
                            .accessibilityIdentifier("pair-confirm")
                    }
                }
            }
        }
    }

    private func pairManually() {
        let text = host.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.lowercased().hasPrefix("cuebot://") {
            do {
                paired.pair(try PairingLink.parse(text))
                dismiss()
            } catch {
                self.error = Self.describe(error)
            }
            return
        }
        guard let api = Int(apiPort), let http = Int(httpPort), (1...65535).contains(api), (1...65535).contains(http) else {
            error = "The ports must be numbers between 1 and 65535."
            return
        }
        guard !token.trimmingCharacters(in: .whitespaces).isEmpty else {
            error = "Enter the API token Cuebot shows."
            return
        }
        paired.pair(CuebotEndpoint(name: name.isEmpty ? "Cuebot" : name, host: text, apiPort: api, httpPort: http,
                                   token: token.trimmingCharacters(in: .whitespaces),
                                   deckToken: deckToken.isEmpty ? nil : deckToken))
        dismiss()
    }

    static func describe(_ error: Error) -> String {
        switch error as? PairingLink.Failure {
        case .notAPairingLink: "That is not a Cuebot pairing code."
        case .unsupportedVersion: "This pairing code is from a newer Cuebot. Update Cuebot Companion."
        case .missing(let field): "The pairing code has no \(field)."
        case .badPort(let field): "The pairing code's \(field) port is not valid."
        case nil: error.localizedDescription
        }
    }
}
