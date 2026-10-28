namespace Cuebot.Core.Decks;

/// <summary>
/// decks.json: Cuebot's version of Streamer.bot Decks, web pages of buttons served by Cuebot's HTTP server at
/// <c>/deck/&lt;id&gt;</c> that any browser (phone, tablet, OBS dock) can open. Pressing a button runs its action.
/// </summary>
public sealed class DeckSettings
{
    public int Version { get; set; } = 1;
    public List<DeckDefinition> Decks { get; set; } = [];
}

public sealed class DeckDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New deck";

    /// <summary>When false the deck's page answers 404 and its buttons cannot be pressed.</summary>
    public bool Enabled { get; set; } = true;

    public int Rows { get; set; } = 3;
    public int Columns { get; set; } = 5;

    /// <summary>Page background colour (CSS colour).</summary>
    public string Background { get; set; } = "#16161c";

    /// <summary>Space between buttons, in pixels.</summary>
    public int Gap { get; set; } = 8;

    /// <summary>Arguments every button of this deck passes to its action, one name=value per line.</summary>
    public string Arguments { get; set; } = "";

    public List<DeckPage> Pages { get; set; } = [new()];
}

public sealed class DeckPage
{
    public string Name { get; set; } = "Page 1";
    public List<DeckItem> Items { get; set; } = [];
}

/// <summary>One button. It shows its active state (0 = default); states beyond the list fall back to state 0.</summary>
public sealed class DeckItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name sub-actions use to find the item (unique within a deck).</summary>
    public string Name { get; set; } = "Button";

    /// <summary>Grid position, from 0.</summary>
    public int Row { get; set; }
    public int Column { get; set; }

    /// <summary>The action pressing the button runs (by name); empty = only the "Deck item pressed" trigger fires.</summary>
    public string Action { get; set; } = "";

    /// <summary>Extra arguments for the action, one name=value per line.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Pressing the button also toggles its state between 0 and 1.</summary>
    public bool ToggleOnPress { get; set; }

    public List<DeckItemState> States { get; set; } = [new()];
}

/// <summary>The look of an item in one state. Empty fields fall back to state 0's.</summary>
public sealed class DeckItemState
{
    public string Title { get; set; } = "";
    public string TitleColor { get; set; } = "";
    public string Background { get; set; } = "";

    /// <summary>Background image URL.</summary>
    public string BackgroundImage { get; set; } = "";

    /// <summary>An Iconify icon name such as "mdi:home" (rendered from the Iconify CDN by the browser).</summary>
    public string Icon { get; set; } = "";
    public string IconColor { get; set; } = "";

    /// <summary>Icon image URL (wins over <see cref="Icon"/>).</summary>
    public string IconUrl { get; set; } = "";

    /// <summary>Status value shown in the button's corner (Streamer.bot "Item Set Value"); empty = none.</summary>
    public string Value { get; set; } = "";
}
