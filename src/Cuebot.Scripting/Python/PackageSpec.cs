using System.Text.RegularExpressions;

namespace Cuebot.Scripting;

/// <summary>
/// Validates what users type as pip packages. Only plain requirement specifiers are accepted (a name, optional
/// extras and version clauses, e.g. <c>requests[socks]&gt;=2.31,&lt;3</c>), so input can never become a pip option.
/// </summary>
internal static partial class PackageSpec
{
    private const int MaxLength = 200;

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?\z")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?(\[[A-Za-z0-9._-]+(,[A-Za-z0-9._-]+)*\])?((===|==|!=|~=|<=|>=|<|>)[A-Za-z0-9.*+!_-]+(,(===|==|!=|~=|<=|>=|<|>)[A-Za-z0-9.*+!_-]+)*)?\z")]
    private static partial Regex Requirement();

    /// <summary>Returns the trimmed requirement, or throws <see cref="ArgumentException"/> when it is not acceptable.</summary>
    public static string ValidateRequirement(string spec)
    {
        var trimmed = spec.Trim();
        if (trimmed.Length > MaxLength || !Requirement().IsMatch(trimmed))
            throw new ArgumentException(
                $"'{spec}' is not a valid package. Use a name with an optional version and no spaces, e.g. requests or requests==2.32.3",
                nameof(spec));
        return trimmed;
    }

    /// <summary>Returns the trimmed package name, or throws <see cref="ArgumentException"/>.</summary>
    public static string ValidateName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length > MaxLength || !Name().IsMatch(trimmed))
            throw new ArgumentException($"'{name}' is not a valid package name", nameof(name));
        return trimmed;
    }
}
