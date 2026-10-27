using System.Globalization;
using System.Reflection;

namespace Cuebot.App.Updates;

/// <summary>
/// A semantic version (major.minor.patch[-prerelease][+build]), compared by SemVer 2.0 rules: build metadata is
/// ignored, a pre-release sorts before its release, and pre-release identifiers compare numerically when numeric.
/// Accepts a leading "v" (release tags) and a missing patch number.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Empty for a release.</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a version: '{text}'");

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var prerelease = "";
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0 || prerelease.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>This build's version, from the informational version (set by <c>-p:Version=</c> in packaging).</summary>
    public static SemVersion Current { get; } = FromAssembly(typeof(SemVersion).Assembly);

    public static SemVersion FromAssembly(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParse(info, out var v)) return v;
        var n = assembly.GetName().Version;
        return n is null ? new SemVersion(0, 0, 0, "") : new SemVersion(n.Major, n.Minor, Math.Max(n.Build, 0), "");
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;
        if (!IsPrerelease) return 0;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,   // numeric identifiers sort before alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(SemVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}
