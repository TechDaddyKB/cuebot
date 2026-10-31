using System.Globalization;

namespace Cuebot.Donations;

/// <summary>Display amounts ("$5.00", "€12.50", "1,000 JPY") for donations that arrive without one.</summary>
internal static class Money
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$", ["CAD"] = "CA$", ["AUD"] = "A$", ["NZD"] = "NZ$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥",
        ["CNY"] = "CN¥", ["INR"] = "₹", ["KRW"] = "₩", ["BRL"] = "R$", ["MXN"] = "MX$", ["RUB"] = "₽", ["TRY"] = "₺",
        ["PLN"] = "zł", ["CHF"] = "CHF ", ["SEK"] = "kr ", ["NOK"] = "kr ", ["DKK"] = "kr ", ["ILS"] = "₪", ["PHP"] = "₱",
    };

    private static readonly HashSet<string> NoDecimals = new(StringComparer.OrdinalIgnoreCase) { "JPY", "KRW" };

    public static string Format(double amount, string currency)
    {
        var code = currency.Trim().ToUpperInvariant();
        var number = amount.ToString(NoDecimals.Contains(code) ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        if (code.Length == 0) return number;
        return Symbols.TryGetValue(code, out var symbol) ? symbol + number : $"{number} {code}";
    }

    /// <summary>
    /// True when <paramref name="currency"/> is in the comma/space separated <paramref name="allowed"/> list (empty allows
    /// all). An event without a currency (Patreon never names one) passes: the filter cannot tell, and blocking every
    /// such event would silently break the trigger.
    /// </summary>
    public static bool CurrencyAllowed(string allowed, string currency)
    {
        var list = allowed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length == 0 || currency.Trim().Length == 0 || list.Any(c => string.Equals(c, currency, StringComparison.OrdinalIgnoreCase));
    }
}
