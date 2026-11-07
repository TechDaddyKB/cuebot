import CuebotKit
import SwiftUI

/// The open streamer profile at the top of the Dashboard; tapping it lists the profiles to switch to.
struct ProfileRow: View {
    @Environment(ProfileStore.self) private var store
    @State private var choosing = false

    var body: some View {
        if let current = store.state?.current {
            Button { choosing = true } label: {
                HStack(spacing: 10) {
                    ProfileDot(color: current.color)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Profile").font(.caption).foregroundStyle(.secondary)
                        Text(current.name).font(.body.weight(.semibold))
                    }
                    Spacer()
                    Image(systemName: "chevron.up.chevron.down").font(.footnote).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(Color.cuebotCard, in: RoundedRectangle(cornerRadius: 14))
                .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Color.cuebotLine, lineWidth: 0.5))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(store.switchingTo != nil)
            .accessibilityIdentifier("profile-row")
            .accessibilityLabel("Profile, \(current.name)")
            .accessibilityHint("Shows your profiles to switch to")
            .sheet(isPresented: $choosing) { ProfilePicker() }
        }
        if let message = store.message {
            Label(message, systemImage: "exclamationmark.triangle").font(.footnote).foregroundStyle(Color.cuebotBad)
        }
    }
}

/// "Switching to Night Owl…" while Cuebot restarts on the other profile.
struct ProfileSwitchingBar: View {
    let profile: StreamerProfile

    var body: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            ProfileDot(color: profile.color, size: 9)
            Text("Switching to \(profile.name)…").font(.footnote.weight(.medium))
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
        .background(.bar)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("profile-switching")
    }
}

struct ProfileDot: View {
    let color: String
    var size: CGFloat = 12

    var body: some View {
        Circle()
            .fill(HexColor(color).map { Color(red: $0.red, green: $0.green, blue: $0.blue) } ?? Color.cuebotMuted)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// The list of profiles, the open one ticked. Choosing another asks first, and asks again while a stream is live.
private struct ProfilePicker: View {
    @Environment(ProfileStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    @State private var confirming: StreamerProfile?
    @State private var forcing: (profile: StreamerProfile, question: String)?
    @State private var working = false

    var body: some View {
        NavigationStack {
            List(store.state?.profiles ?? []) { profile in
                Button {
                    if profile.open { dismiss() } else { confirming = profile }
                } label: {
                    HStack(spacing: 12) {
                        ProfileDot(color: profile.color)
                        Text(profile.name).foregroundStyle(Color.primary)
                        Spacer()
                        if profile.open { Image(systemName: "checkmark").foregroundStyle(.tint).fontWeight(.semibold) }
                    }
                    .contentShape(Rectangle())
                }
                .disabled(working)
                .accessibilityIdentifier("profile-\(profile.id)")
                .accessibilityAddTraits(profile.open ? .isSelected : [])
            }
            .navigationTitle("Profiles")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Close") { dismiss() } }
            }
            .overlay { if working { ProgressView() } }
        }
        .presentationDetents([.medium, .large])
        .alert("Switch to \(confirming?.name ?? "")?", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } }),
               presenting: confirming) { profile in
            Button("Switch") { start(profile, force: false) }.accessibilityIdentifier("profile-switch-confirm")
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("Cuebot restarts on that profile; it takes a few seconds.")
        }
        .alert(forcing?.question ?? "", isPresented: Binding(get: { forcing != nil }, set: { if !$0 { forcing = nil } }),
               presenting: forcing) { ask in
            Button("Switch anyway", role: .destructive) { start(ask.profile, force: true) }.accessibilityIdentifier("profile-switch-force")
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("The stream keeps going, but Cuebot stops for a few seconds and comes back on the other profile.")
        }
    }

    private func start(_ profile: StreamerProfile, force: Bool) {
        working = true
        Task {
            let outcome = await store.activate(profile, force: force)
            working = false
            switch outcome {
            case .askForce(let question): forcing = (profile, question)
            case .started, .alreadyOpen, .failed: dismiss()
            }
        }
    }
}
