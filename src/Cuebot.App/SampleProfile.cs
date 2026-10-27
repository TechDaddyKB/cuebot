using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

namespace Cuebot.App;

/// <summary>Starter actions created on first launch so the editor isn't empty.</summary>
public static class SampleProfile
{
    public static void Seed(BotProfile profile)
    {
        profile.Actions.Add(HelloCommand());
        profile.Actions.Add(FollowThanks());
        profile.Actions.Add(CheerReaction());
    }

    private static ActionDefinition HelloCommand() => new()
    {
        Name = "!hello",
        Group = "Examples",
        Triggers = [new TriggerDefinition { Type = "twitch.command", Settings = { ["command"] = "!hello" } }],
        SubActions =
        [
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Hello %user%, welcome in!" } },
        ],
    };

    private static ActionDefinition FollowThanks() => new()
    {
        Name = "Thank new followers",
        Group = "Examples",
        Triggers = [new TriggerDefinition { Type = "twitch.follow" }],
        SubActions =
        [
            new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Thanks for the follow, %user%!" } },
            new SubActionDefinition { Type = "speech.speak", Parameters = { ["text"] = "%user% just followed" } },
        ],
    };

    private static ActionDefinition CheerReaction()
    {
        var big = new SubActionDefinition
        {
            Type = "core.if",
            Parameters = { ["left"] = "%bits%", ["operator"] = "greater than", ["right"] = "499" },
        };
        big.Branch(IfSubAction.True).Add(new SubActionDefinition
        {
            Type = "obs.setScene", Parameters = { ["scene"] = "Celebration" },
        });
        big.Branch(IfSubAction.True).Add(new SubActionDefinition
        {
            Type = "core.delay", Parameters = { ["milliseconds"] = "5000" },
        });
        big.Branch(IfSubAction.False).Add(new SubActionDefinition
        {
            Type = "speech.speak", Parameters = { ["text"] = "%user% cheered %bits% bits" },
        });
        return new ActionDefinition
        {
            Name = "Cheer reaction",
            Group = "Examples",
            Enabled = false,
            Triggers = [new TriggerDefinition { Type = "twitch.cheer" }],
            SubActions =
            [
                big,
                new SubActionDefinition { Type = "twitch.sendMessage", Parameters = { ["message"] = "Thank you for the %bits% bits, %user%!" } },
            ],
        };
    }
}
