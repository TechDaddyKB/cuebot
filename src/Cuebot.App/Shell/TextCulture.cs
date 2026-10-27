using System.Globalization;
using Cuebot.Core.Variables;

namespace Cuebot.App.Shell;

/// <summary>Maps app.json's text culture setting onto the shared <see cref="TextEnvironment"/>.</summary>
public static class TextCulture
{
    // ICU accepts made-up names ("not-a-culture"); only cultures the system lists are used.
    private static readonly Lazy<HashSet<string>> Known = new(() =>
        CultureInfo.GetCultures(CultureTypes.AllCultures).Select(c => c.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The culture for a setting value: empty or unknown names give the default (neutral, "$" currency).</summary>
    public static CultureInfo Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Known.Value.Contains(name.Trim())) return TextEnvironment.DefaultCulture;
        try
        {
            return CultureInfo.ReadOnly(CultureInfo.GetCultureInfo(name.Trim()));
        }
        catch (CultureNotFoundException)
        {
            return TextEnvironment.DefaultCulture;
        }
    }

    public static void Apply(TextEnvironment text, string? name) => text.Culture = Resolve(name);
}
