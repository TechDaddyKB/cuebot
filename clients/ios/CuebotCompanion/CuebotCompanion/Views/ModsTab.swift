import AuthenticationServices
import CuebotKit
import PhotosUI
import SwiftUI

/// Mods: the streamer approves mods' changes here (on the paired Cuebot), and a mod opens the portals of the streamers
/// they help (docs/MOD_PORTAL_PLAN.md, sections 5 and 6). Like the Android app's Mods tab.
struct ModsTab: View {
    @Environment(ModStore.self) private var store

    var body: some View {
        NavigationStack {
            Group {
                switch store.state {
                case .home: ModsHome()
                case .portal(let portal): PortalScreen(portal: portal)
                default: ModFlowView()
                }
            }
            .background(Color.cuebotGround.ignoresSafeArea())
        }
    }
}

private struct ModsHome: View {
    @Environment(ModStore.self) private var store
    @Environment(ConnectionStore.self) private var connection
    @Environment(ApprovalsStore.self) private var approvals
    @Environment(\.scenePhase) private var scenePhase
    @State private var joining = false
    @State private var renaming: Membership?
    @State private var newName = ""

    var body: some View {
        @Bindable var store = store
        List {
            Group {
                if let notice = store.notice {
                    Section {
                        HStack(alignment: .top) {
                            Text(notice).font(.callout)
                            Spacer()
                            Button { store.notice = nil } label: { Image(systemName: "xmark") }.accessibilityLabel("Dismiss")
                        }
                    }
                }
                if connection.client != nil && !approvals.pending.isEmpty {
                    Section {
                        if let message = approvals.message {
                            Text(message).font(.footnote).foregroundStyle(Color.cuebotMuted).accessibilityIdentifier("approvals-message")
                        }
                        ForEach(approvals.pending) { change in ChangeRow(change: change) }
                    } header: {
                        Text("Waiting for your OK")
                    } footer: {
                        Text("Your mods' changes that need your OK, on the Cuebot this phone is paired with. Unanswered changes expire after 7 days.")
                    }
                } else if let message = approvals.message {
                    Section { Text(message).font(.footnote).foregroundStyle(Color.cuebotMuted).accessibilityIdentifier("approvals-message") }
                }
                Section {
                    ForEach(store.memberships.all) { m in
                        Button { store.resume(m) } label: {
                            VStack(alignment: .leading) {
                                Text(m.name).foregroundStyle(.primary)
                                Text(m.signedInAs).font(.footnote).foregroundStyle(Color.cuebotMuted)
                            }
                        }
                        .swipeActions {
                            Button("Forget", role: .destructive) { store.forget(m) }
                            Button("Rename") { newName = m.name; renaming = m }
                        }
                        .contextMenu {
                            Button("Rename") { newName = m.name; renaming = m }
                            Button("Forget on this phone", role: .destructive) { store.forget(m) }
                        }
                        .accessibilityIdentifier("membership-\(m.id)")
                    }
                    Button { joining = true } label: { Label("Join with an invite", systemImage: "person.badge.plus") }
                        .accessibilityIdentifier("join-invite")
                } header: {
                    Text("Streamers you mod for")
                } footer: {
                    Text(store.memberships.all.isEmpty
                         ? "When a streamer invites you to help with their stream, scan the invite's QR code or paste its link here. You sign in with Google, Twitch or Discord so their Cuebot knows it's you."
                         : "Tap one to open that streamer's mod portal. Swipe to rename or forget one on this phone.")
                }
            }
            .listRowBackground(Color.cuebotCard)
        }
        .scrollContentBackground(.hidden)
        .navigationTitle("Mods")
        .toolbarBackground(Color.cuebotGround, for: .navigationBar)
        .sheet(isPresented: $joining) { JoinSheet() }
        .alert("Rename", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $newName)
            Button("Save") { if let renaming { store.rename(renaming, to: newName) } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("What to call this streamer's portal on this phone, for example their channel name.")
        }
        // Checked every 5 seconds only while this screen is on screen and the app is in front.
        .task(id: TaskKey(client: connection.client.map(ObjectIdentifier.init), active: scenePhase == .active)) {
            guard scenePhase == .active else { return }
            await approvals.poll(connection)
        }
    }

    private struct TaskKey: Equatable { let client: ObjectIdentifier?, active: Bool }
}

private struct ChangeRow: View {
    @Environment(ApprovalsStore.self) private var approvals
    @Environment(ConnectionStore.self) private var connection
    let change: PendingChange

    var body: some View {
        let conflict = approvals.conflicts.contains(change.id)
        VStack(alignment: .leading, spacing: 6) {
            Text(change.summary).font(.body.weight(.medium))
            Text([change.member, change.area].filter { !$0.isEmpty }.joined(separator: " · ")).font(.footnote).foregroundStyle(Color.cuebotMuted)
            if conflict {
                Text("'\(change.target)' changed since \(change.member) asked. Approve again to use their version anyway.")
                    .font(.footnote).foregroundStyle(Color.cuebotWarn)
            }
            HStack {
                Button(conflict ? "Approve anyway" : "Approve") { Task { await approvals.decide(change, approve: true, connection) } }
                    .buttonStyle(.borderedProminent)
                    .accessibilityIdentifier("approve-\(change.id)")
                Button("Reject") { Task { await approvals.decide(change, approve: false, connection) } }
                    .buttonStyle(.bordered)
                    .accessibilityIdentifier("reject-\(change.id)")
            }
        }
        .padding(.vertical, 4)
    }
}

/// Join by the invite's QR code (camera or a picture of it) or by pasting its link.
private struct JoinSheet: View {
    @Environment(ModStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var error: String?
    @State private var photo: PhotosPickerItem?

    var body: some View {
        NavigationStack {
            Form {
                Group {
                    if QRScannerView.isAvailable {
                        Section {
                            QRScannerView { open($0) }.frame(height: 280).listRowInsets(EdgeInsets())
                        } footer: {
                            Text("Point the camera at the invite's QR code.")
                        }
                    }
                    Section {
                        // PasteButton: iOS hands it the clipboard without asking "Allow Paste?" first.
                        PasteButton(payloadType: String.self) { texts in
                            Task { @MainActor in paste(texts) }
                        }
                        .labelStyle(.titleAndIcon)
                        .buttonBorderShape(.capsule)
                        .accessibilityIdentifier("join-paste")
                        PhotosPicker(selection: $photo, matching: .images) {
                            Label("Choose a picture of the QR code", systemImage: "photo")
                        }
                    } footer: {
                        Text("Copy the invite link the streamer sent, then paste it here. The streamer makes invites on Cuebot's Mod portal page; an invite works once, for one account.")
                    }
                    if let error { Section { Text(error).foregroundStyle(Color.cuebotBad).accessibilityIdentifier("join-error") } }
                }
                .listRowBackground(Color.cuebotCard)
            }
            .scrollContentBackground(.hidden)
            .background(Color.cuebotGround)
            .navigationTitle("Join with an invite")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } } }
            .onChange(of: photo) { _, item in
                guard let item else { return }
                Task {
                    defer { photo = nil }
                    guard let data = try? await item.loadTransferable(type: Data.self) else { error = "That image could not be opened."; return }
                    let codes = QRImageReader.codes(in: data)
                    guard let code = codes.first(where: { PortalTarget.parse($0) != nil }) ?? codes.first else {
                        error = "No QR code was found in that image."
                        return
                    }
                    open(code)
                }
            }
        }
    }

    private func open(_ link: String) {
        error = store.openInvite(link)
        if error == nil { dismiss() }
    }

    private func paste(_ texts: [String]) {
        guard let text = texts.first?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else {
            error = "There is no link on the clipboard. Copy the invite link the streamer sent first."
            return
        }
        open(text)
    }
}

/// Joining, signing in, and what went wrong.
private struct ModFlowView: View {
    @Environment(ModStore.self) private var store
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                switch store.state {
                case .busy(_, let text):
                    HStack(spacing: 12) { ProgressView(); Text(text) }
                case .providers(let target, let providers):
                    Text(target.invite != nil
                         ? "The streamer invited you to help with their stream. Sign in so their Cuebot knows it's you. Your account is only used to recognise you; Cuebot does not post or read anything with it."
                         : "This phone's sign-in has expired. Sign in with the same account as before.")
                    ForEach(providers) { p in
                        Button { store.signIn(target, p, session: webAuthenticationSession) } label: {
                            Text("Sign in with \(p.name)").frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.large)
                        .accessibilityIdentifier("provider-\(p.id)")
                    }
                case .inBrowser(let provider):
                    Text("Finish signing in with \(provider) in the browser. You come back here when it's done.")
                    Button("Cancel") { store.home() }
                case .problem(let text, let retry):
                    Text(text).foregroundStyle(Color.cuebotBad).accessibilityIdentifier("mod-problem")
                    if let retry { Button("Try again", action: retry).buttonStyle(.borderedProminent) }
                default:
                    EmptyView()
                }
            }
            .padding(24)
            .frame(maxWidth: 560, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .navigationBarBackButtonHidden()
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button { store.home() } label: { Label("Back", systemImage: "chevron.backward") }
            }
        }
    }

    private var title: String {
        switch store.state {
        case .busy(let title, _): title
        case .providers(let target, _): target.invite != nil ? "Join as a mod" : "Sign in again"
        case .inBrowser: "Signing in"
        default: "Something went wrong"
        }
    }
}

/// The portal itself, full screen under a bar with the streamer's name.
private struct PortalScreen: View {
    @Environment(ModStore.self) private var store
    let portal: ModStore.OpenPortal
    @State private var failed: String?

    var body: some View {
        Group {
            if portal.js == nil || portal.css == nil {
                message("The streamer's Cuebot needs an update for the full mod portal. This phone stays signed in until then.")
            } else if let failed {
                message("The mod portal did not start: \(failed)")
            } else {
                PortalWebView(portal: portal, onSignOut: { store.signOut() }, onFailed: { failed = $0 })
                    .ignoresSafeArea(edges: .bottom)
                    .accessibilityIdentifier("portal-web")
            }
        }
        .navigationTitle(portal.membership.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(Color.cuebotGround, for: .navigationBar)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button { store.home() } label: { Label("Leave the portal", systemImage: "chevron.backward") }
            }
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Button("Sign out on this phone", role: .destructive) { store.signOut() }
                } label: { Label("More", systemImage: "ellipsis.circle") }
            }
        }
    }

    private func message(_ text: String) -> some View {
        Text(text).padding(24).frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}
