import CuebotKit
import PhotosUI
import SwiftUI

struct SettingsTab: View {
    @Environment(PairedCuebots.self) private var paired
    @Environment(ConnectionStore.self) private var connection
    @State private var pairing = false
    @State private var renaming: PairedCuebot?
    @State private var newName = ""
    @AppStorage(AppAppearance.storageKey) private var appearance = AppAppearance.system.rawValue

    var body: some View {
        NavigationStack {
            List {
                Group {
                Section {
                    ForEach(paired.all) { entry in
                        Button { paired.activate(entry.id) } label: {
                            HStack {
                                VStack(alignment: .leading) {
                                    Text(entry.name).foregroundStyle(.primary)
                                    Text(verbatim: "\(entry.host):\(entry.apiPort)").font(.footnote.monospaced()).foregroundStyle(Color.cuebotMuted)
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
                Section("Appearance") {
                    Picker("Appearance", selection: $appearance) {
                        ForEach(AppAppearance.allCases) { Text($0.label).tag($0.rawValue) }
                    }
                    .pickerStyle(.segmented)
                    .accessibilityIdentifier("appearance-picker")
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
                .listRowBackground(Color.cuebotCard)
            }
            .scrollContentBackground(.hidden)
            .background(Color.cuebotGround)
            .toolbarBackground(Color.cuebotGround, for: .navigationBar)
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
    @State private var photo: PhotosPickerItem?

    var body: some View {
        NavigationStack {
            Form {
                Group {
                if scanning {
                    Section {
                        QRScannerView { code in pair(code) }
                        .frame(height: 300)
                        .listRowInsets(EdgeInsets())
                        Button("Enter the details instead") { scanning = false }
                    } footer: {
                        Text("In Cuebot on your computer: Servers page → REST API → Pair a phone.")
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
                        Text("Cuebot shows these on its Servers page, in the REST API card under \"Pair a phone\".")
                    }
                    if QRScannerView.isAvailable { Button("Scan the QR code instead") { scanning = true } }
                }
                Section {
                    PhotosPicker(selection: $photo, matching: .images) {
                        Label("Choose a photo of the code", systemImage: "photo")
                    }
                    .accessibilityIdentifier("pair-photo")
                    Button { pasteLink() } label: { Label("Paste a pairing link", systemImage: "doc.on.clipboard") }
                        .accessibilityIdentifier("pair-paste")
                } footer: {
                    Text("A screenshot of the code works too, for example one sent from the computer.")
                }
                if let error { Section { Text(error).foregroundStyle(Color.cuebotBad).accessibilityIdentifier("pair-error") } }
                }
                .listRowBackground(Color.cuebotCard)
            }
            .onChange(of: photo) { _, item in
                guard let item else { return }
                Task {
                    defer { photo = nil }
                    guard let data = try? await item.loadTransferable(type: Data.self) else {
                        error = "That photo could not be opened."
                        return
                    }
                    guard let code = QRImageReader.codes(in: data).first else {
                        error = "No QR code was found in that photo."
                        return
                    }
                    pair(code)
                }
            }
            .scrollContentBackground(.hidden)
            .background(Color.cuebotGround)
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

    private func pair(_ link: String) {
        do {
            paired.pair(try PairingLink.parse(link))
            dismiss()
        } catch {
            self.error = Self.describe(error)
        }
    }

    private func pasteLink() {
        let board = UIPasteboard.general
        guard let text = board.url?.absoluteString ?? board.string, !text.isEmpty else {
            error = "There is no link on the clipboard. Copy the cuebot://pair link from Cuebot first."
            return
        }
        pair(text.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private func pairManually() {
        let text = host.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.lowercased().hasPrefix("cuebot://") {
            pair(text)
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
