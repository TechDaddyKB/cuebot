import Foundation

// Decoding is tolerant everywhere (see KeyedDecodingContainer.value): a missing or changed field falls back to a
// default instead of failing the whole response.

public enum ConnectionState: String, Codable, Hashable, Sendable {
    case disconnected = "Disconnected", connecting = "Connecting", connected = "Connected", error = "Error", unknown

    public init(from decoder: Decoder) throws {
        self = ConnectionState(rawValue: (try? decoder.singleValueContainer().decode(String.self)) ?? "") ?? .unknown
    }
}

public struct ConnectionInfo: Codable, Hashable, Sendable, Identifiable {
    public var name: String
    public var state: ConnectionState
    public var status: String
    public var id: String { name }

    public init(name: String, state: ConnectionState, status: String = "") {
        self.name = name
        self.state = state
        self.status = status
    }

    private enum CodingKeys: String, CodingKey { case name, state, status }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = c.value(.name, "")
        state = c.value(.state, .unknown)
        status = c.value(.status, "")
    }
}

public struct AppInfo: Codable, Hashable, Sendable {
    public var name = "", version = "", runtime = "", os = "", instanceId = "", apiVersion = ""

    private enum CodingKeys: String, CodingKey { case name, version, runtime, os, instanceId, apiVersion }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = c.value(.name, "")
        version = c.value(.version, "")
        runtime = c.value(.runtime, "")
        os = c.value(.os, "")
        instanceId = c.value(.instanceId, "")
        apiVersion = c.value(.apiVersion, "")
    }
}

/// `GET /api/v1/status`.
public struct StatusResponse: Codable, Hashable, Sendable {
    public var app = AppInfo()
    public var connections: [ConnectionInfo] = []

    private enum CodingKeys: String, CodingKey { case app, connections }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        app = c.value(.app, AppInfo())
        connections = c.value(.connections, [])
    }
}

/// One entry of `GET /api/v1/actions`.
public struct ActionSummary: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var group: String
    public var enabled: Bool

    public init(id: String, name: String, group: String = "", enabled: Bool = true) {
        self.id = id
        self.name = name
        self.group = group
        self.enabled = enabled
    }

    private enum CodingKeys: String, CodingKey { case id, name, group, enabled }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        group = c.value(.group, "")
        enabled = c.value(.enabled, true)
    }
}

/// One entry of `GET /api/v1/decks`.
public struct DeckSummary: Codable, Hashable, Sendable, Identifiable {
    public var id: String
    public var name: String
    public var rows: Int
    public var columns: Int
    public var pageCount: Int
    /// Only enabled decks answer on /deck/{id}.
    public var enabled: Bool

    private enum CodingKeys: String, CodingKey { case id, name, rows, columns, pageCount, enabled }

    public init(id: String, name: String, rows: Int = 3, columns: Int = 5, pageCount: Int = 1, enabled: Bool = true) {
        self.id = id
        self.name = name
        self.enabled = enabled
        self.rows = rows
        self.columns = columns
        self.pageCount = pageCount
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        rows = c.value(.rows, 3)
        columns = c.value(.columns, 5)
        pageCount = c.value(.pageCount, 1)
        enabled = c.value(.enabled, true)
    }
}

/// One button of a deck view.
public struct DeckItem: Codable, Hashable, Sendable, Identifiable {
    public var id = "", name = "", title = "", titleColor = "", background = "", backgroundImage = ""
    public var icon = "", iconColor = "", iconUrl = "", value = ""
    public var row = 0, column = 0, state = 0

    private enum CodingKeys: String, CodingKey {
        case id, name, title, titleColor, background, backgroundImage, icon, iconColor, iconUrl, value, row, column, state
    }

    public init(id: String, row: Int, column: Int, title: String = "") {
        self.id = id
        self.row = row
        self.column = column
        self.title = title
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        title = c.value(.title, "")
        titleColor = c.value(.titleColor, "")
        background = c.value(.background, "")
        backgroundImage = c.value(.backgroundImage, "")
        icon = c.value(.icon, "")
        iconColor = c.value(.iconColor, "")
        iconUrl = c.value(.iconUrl, "")
        value = c.value(.value, "")
        row = c.value(.row, 0)
        column = c.value(.column, 0)
        state = c.value(.state, 0)
    }

    /// What VoiceOver reads: the title, else the name.
    public var accessibilityName: String { title.isEmpty ? (name.isEmpty ? "Button" : name) : title }
}

/// `GET /deck/{id}/state` and each `state` event of `/deck/{id}/events`.
public struct DeckView: Codable, Hashable, Sendable, Identifiable {
    public var id = "", name = "", background = "", pageName = ""
    public var rows = 3, columns = 5, gap = 8, page = 0, pageCount = 1
    public var items: [DeckItem] = []

    private enum CodingKeys: String, CodingKey { case id, name, background, pageName, rows, columns, gap, page, pageCount, items }

    public init(id: String, rows: Int, columns: Int, items: [DeckItem] = []) {
        self.id = id
        self.rows = rows
        self.columns = columns
        self.items = items
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        background = c.value(.background, "")
        pageName = c.value(.pageName, "")
        rows = max(1, c.value(.rows, 3))
        columns = max(1, c.value(.columns, 5))
        gap = max(0, c.value(.gap, 8))
        page = c.value(.page, 0)
        pageCount = max(1, c.value(.pageCount, 1))
        items = c.value(.items, [])
    }

    /// The button at a grid cell, if any.
    public func item(row: Int, column: Int) -> DeckItem? { items.first { $0.row == row && $0.column == column } }
}

/// A `notify` event of a deck (the "Deck notify" sub-action).
public struct DeckNotification: Codable, Hashable, Sendable {
    public var title = ""
    public var description = ""

    private enum CodingKeys: String, CodingKey { case title, description }

    public init(title: String, description: String = "") {
        self.title = title
        self.description = description
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = c.value(.title, "")
        description = c.value(.description, "")
    }
}
