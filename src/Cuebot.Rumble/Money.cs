using System.Globalization;

namespace Cuebot.Rumble;

/// <summary>Rumble amounts (rants, subscriptions) arrive in US cents.</summary>
internal static class Money
{
    public const string Currency = "USD";

    public static double Dollars(long cents) => cents / 100.0;

    /// <summary>Display text such as "$5.00".</summary>
    public static string Format(long cents) => Dollars(cents).ToString("$0.00", CultureInfo.InvariantCulture);
}
