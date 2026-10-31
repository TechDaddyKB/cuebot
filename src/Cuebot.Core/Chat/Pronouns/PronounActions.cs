using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;

namespace Cuebot.Core.Chat.Pronouns;

/// <summary>
/// "Add pronouns for user": looks the user up on alejo.io and adds pronoun arguments (pronoun, pronounSubject,
/// pronounObject, pronounAltSubject, pronounAltObject, pronounSingular, hasPronouns), optionally with a prefix.
/// </summary>
public sealed class AddPronounsSubAction(PronounService pronouns) : ISubActionHandler
{
    public string TypeId => "pronouns.addForUser";
    public string Category => "Pronouns";
    public string DisplayName => "Add pronouns for user";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("user", "User login") { DefaultValue = "%userName%", Help = "Twitch login; defaults to the trigger's user" },
        new("prefix", "Variable name start") { Help = "e.g. \"target\" gives targetPronoun, targetPronounSubject..." },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var login = p["user"];
        if (login.Length == 0 || login.Contains('%')) login = context.Args.GetValueOrDefault("user") as string ?? "";
        var found = await pronouns.GetAsync(login, ct).ConfigureAwait(false);
        Apply(context.Args, p["prefix"], found);
        return SubActionResult.Next;
    }

    /// <summary>Writes the pronoun arguments (empty strings when the user has none).</summary>
    public static void Apply(IDictionary<string, object?> args, string prefix, UserPronouns? found)
    {
        string Key(string name) => prefix.Length == 0 ? name : prefix + char.ToUpperInvariant(name[0]) + name[1..];
        args[Key("hasPronouns")] = found is not null;
        args[Key("pronoun")] = found?.Display ?? "";
        args[Key("pronounSubject")] = found?.Primary.Subject ?? "";
        args[Key("pronounObject")] = found?.Primary.Object ?? "";
        args[Key("pronounSingular")] = found?.Primary.Singular ?? false;
        args[Key("pronounAltSubject")] = found?.Alt?.Subject ?? "";
        args[Key("pronounAltObject")] = found?.Alt?.Object ?? "";
    }
}

/// <summary>Script function pronouns.get(user) → {display, subject, object, altSubject, altObject, singular} or null.</summary>
public sealed class PronounScriptFunctions(PronounService pronouns) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("pronouns.get", "A Twitch user's pronouns from alejo.io, or null when none are set", ["user"], async (ctx, a, ct) =>
        {
            var login = a.GetString("user", ctx.Args.GetValueOrDefault("userName") as string ?? "");
            var found = await pronouns.GetAsync(login, ct).ConfigureAwait(false);
            return found is null ? null : new Dictionary<string, object?>
            {
                ["display"] = found.Display,
                ["subject"] = found.Primary.Subject,
                ["object"] = found.Primary.Object,
                ["singular"] = found.Primary.Singular,
                ["altSubject"] = found.Alt?.Subject,
                ["altObject"] = found.Alt?.Object,
            };
        }),
    ];
}
