using System.Globalization;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Variables;

/// <summary>Modes of the Set sub-actions.</summary>
public static class SetModes
{
    public const string Value = "Value";
    public const string Increment = "Increment";
    public const string Decrement = "Decrement";

    public static IReadOnlyList<string> All { get; } = [Value, Increment, Decrement];

    /// <summary>The step for Increment/Decrement: empty means 1.</summary>
    public static decimal Amount(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 1;
        return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw new InvalidOperationException($"Amount '{text}' is not a number");
    }

    public static bool IsIncrement(string mode, out int sign)
    {
        sign = 0;
        if (string.Equals(mode, Increment, StringComparison.OrdinalIgnoreCase)) sign = 1;
        else if (string.Equals(mode, Decrement, StringComparison.OrdinalIgnoreCase)) sign = -1;
        else if (mode.Trim().Length > 0 && !string.Equals(mode, Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown mode '{mode}' (expected Value, Increment or Decrement)");
        return sign != 0;
    }
}

public static class VariableNames
{
    /// <summary>Usable names: not empty and without whitespace (names usable in %..% / ~..~ use letters, digits, _ and .).</summary>
    public static bool IsValid(string name) => name.Length > 0 && !name.Any(char.IsWhiteSpace);

    internal static string Require(string name, string what = "Variable name")
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new InvalidOperationException($"{what} is empty");
        if (!IsValid(trimmed)) throw new InvalidOperationException($"{what} '{trimmed}' contains spaces");
        return trimmed;
    }
}

/// <summary>Which viewer a user-variable sub-action or script call targets.</summary>
public readonly record struct UserTarget(string Platform, string UserId, string? UserName)
{
    /// <summary>
    /// Explicit values win; empty ones fall back to the triggering user (arguments platform/eventType, userId,
    /// userName/user). A user given only by name is looked up among known users. Unresolved "%arg%" text counts
    /// as empty. Null when no user can be determined.
    /// </summary>
    public static UserTarget? Find(UserVariables users, IReadOnlyDictionary<string, object?> args,
        string? platform, string? userId, string? userName)
    {
        var p = Clean(platform);
        var id = Clean(userId);
        var name = Clean(userName);
        var resolvedPlatform = p.Length > 0 ? UserVariables.NormalizePlatform(p) : UserVariables.PlatformFromArgs(args);
        if (resolvedPlatform is null) return null;
        if (id.Length == 0 && name.Length == 0)
        {
            id = Clean(Arg(args, "userId"));
            name = Clean(Arg(args, "userName"));
            if (name.Length == 0) name = Clean(Arg(args, "user"));
        }
        if (id.Length == 0 && name.Length > 0) id = users.FindUserId(resolvedPlatform, name) ?? "";
        if (id.Length == 0) return null;
        return new UserTarget(resolvedPlatform, id, name.Length > 0 ? name : null);
    }

    internal static UserTarget Require(UserVariables users, IReadOnlyDictionary<string, object?> args,
        string? platform, string? userId, string? userName) =>
        Find(users, args, platform, userId, userName)
        ?? throw new InvalidOperationException(
            "No user to use: set Platform and User id/name, or run from a trigger that provides platform and userId");

    private static string Arg(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? VariableResolver.Format(v) : "";

    private static string Clean(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Length > 2 && t[0] == '%' && t[^1] == '%' && t.AsSpan(1, t.Length - 2).IndexOf('%') < 0 ? "" : t;
    }
}
