import SwiftUI

/// The side menu that replaced the tab bar (issue #45): the hamburger in each section's navigation bar opens it, and
/// choosing a section closes it. "Keep menu open" (Settings) pins it beside the page on iPad.
@MainActor @Observable
final class SideMenu {
    nonisolated static let pinnedKey = "menu.pinned"

    var isOpen = false
    /// Stream mode hides the menu (and the pinned sidebar) with everything else.
    var hidden = false

    func open() { withAnimation(.easeOut(duration: 0.2)) { isOpen = true } }
    func close() { withAnimation(.easeOut(duration: 0.2)) { isOpen = false } }
}

private struct SideMenuPinnedKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    /// True where the menu is pinned open beside the page, so the sections show no hamburger.
    var sideMenuPinned: Bool {
        get { self[SideMenuPinnedKey.self] }
        set { self[SideMenuPinnedKey.self] = newValue }
    }
}

/// Adds the hamburger to a section's navigation bar (nothing while the menu is pinned).
private struct SideMenuButton: ViewModifier {
    @Environment(SideMenu.self) private var menu
    @Environment(\.sideMenuPinned) private var pinned

    func body(content: Content) -> some View {
        content.toolbar {
            if !pinned {
                ToolbarItem(placement: .topBarLeading) {
                    Button { menu.open() } label: { Label("Menu", systemImage: "line.3.horizontal") }
                        .accessibilityIdentifier("menu-button")
                }
            }
        }
    }
}

extension View {
    func sideMenuButton() -> some View { modifier(SideMenuButton()) }
}

/// The sections. Each button's identifier is the section's name ("Deck", "Dashboard"...), as the tab bar's were.
struct SideMenuList: View {
    @Binding var selection: RootView.Tab
    @Environment(SideMenu.self) private var menu
    @Environment(ApprovalsStore.self) private var approvals

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Cuebot Companion").font(.headline).padding(.horizontal, 12).padding(.vertical, 16)
            ForEach(RootView.Tab.allCases, id: \.self) { tab in
                Button {
                    selection = tab
                    menu.close()
                } label: {
                    HStack {
                        Label(tab.title, systemImage: tab.symbol)
                        Spacer()
                        if tab == .mods && !approvals.pending.isEmpty {
                            Text("\(approvals.pending.count)").font(.caption.bold()).padding(.horizontal, 7).padding(.vertical, 2)
                                .background(Color.cuebotBad, in: Capsule()).foregroundStyle(.white)
                        }
                    }
                    .padding(.horizontal, 12)
                    .padding(.vertical, 10)
                    .background(selection == tab ? Color.accentColor.opacity(0.18) : .clear, in: RoundedRectangle(cornerRadius: 10))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityIdentifier(tab.title)
                .accessibilityAddTraits(selection == tab ? .isSelected : [])
            }
            Spacer()
        }
        .padding(.horizontal, 8)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Color.cuebotCard.ignoresSafeArea())
    }
}
