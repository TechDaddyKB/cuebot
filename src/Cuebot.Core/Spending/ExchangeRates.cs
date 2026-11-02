using System.Text.Json;

namespace Cuebot.Core.Spending;

/// <summary>Exchange rates for one base currency: 1 base = rate × other currency.</summary>
public sealed record ExchangeRateTable(string Base, string Date, string Source, IReadOnlyDictionary<string, double> Rates);

/// <summary>Where exchange rates come from (a free web API, or a fake in tests).</summary>
public interface IExchangeRateSource
{
    /// <summary>Today's rates for <paramref name="baseCurrency"/>, or null when no provider could be reached.</summary>
    Task<ExchangeRateTable?> FetchAsync(string baseCurrency, CancellationToken ct);
}

/// <summary>
/// Free exchange rate APIs that need no key or account: Frankfurter (the European Central Bank's daily reference rates,
/// about 30 currencies) first, then fawazahmed0's currency-api (200+ currencies, on jsDelivr with a Cloudflare mirror)
/// for every currency Frankfurter does not have, or for everything when Frankfurter cannot be reached.
/// </summary>
public sealed class WebExchangeRateSource(HttpClient http) : IExchangeRateSource
{
    public const string FrankfurterName = "Frankfurter (European Central Bank)";
    public const string CurrencyApiName = "currency-api (fawazahmed0)";

    public async Task<ExchangeRateTable?> FetchAsync(string baseCurrency, CancellationToken ct)
    {
        var code = baseCurrency.Trim().ToUpperInvariant();
        var ecb = await TryAsync(() => FrankfurterAsync(code, ct));
        var wide = await TryAsync(() => CurrencyApiAsync(code, ct));
        if (ecb is null && wide is null) return null;
        if (ecb is null) return wide;
        if (wide is null) return ecb;
        var rates = new Dictionary<string, double>(wide.Rates, StringComparer.OrdinalIgnoreCase);
        foreach (var (currency, rate) in ecb.Rates) rates[currency] = rate;   // the central bank's rate wins where it has one
        return new ExchangeRateTable(code, ecb.Date, $"{FrankfurterName} + {CurrencyApiName}", rates);
    }

    private async Task<ExchangeRateTable?> FrankfurterAsync(string code, CancellationToken ct)
    {
        using var doc = await GetAsync($"https://api.frankfurter.dev/v1/latest?base={Uri.EscapeDataString(code)}", ct);
        if (doc is null || !doc.RootElement.TryGetProperty("rates", out var list) || list.ValueKind != JsonValueKind.Object) return null;
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [code] = 1 };
        foreach (var p in list.EnumerateObject())
            if (p.Value.TryGetDouble(out var rate) && rate > 0) rates[p.Name] = rate;
        var date = doc.RootElement.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        return new ExchangeRateTable(code, date, FrankfurterName, rates);
    }

    private async Task<ExchangeRateTable?> CurrencyApiAsync(string code, CancellationToken ct)
    {
        var lower = code.ToLowerInvariant();
        foreach (var url in (string[])[
                     $"https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/{lower}.json",
                     $"https://latest.currency-api.pages.dev/v1/currencies/{lower}.json"])
        {
            using var doc = await GetAsync(url, ct);
            if (doc is null || !doc.RootElement.TryGetProperty(lower, out var list) || list.ValueKind != JsonValueKind.Object) continue;
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [code] = 1 };
            foreach (var p in list.EnumerateObject())
                if (p.Name.Length == 3 && p.Value.TryGetDouble(out var rate) && rate > 0) rates[p.Name.ToUpperInvariant()] = rate;
            var date = doc.RootElement.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
            return new ExchangeRateTable(code, date, CurrencyApiName, rates);
        }
        return null;
    }

    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task<ExchangeRateTable?> TryAsync(Func<Task<ExchangeRateTable?>> fetch)
    {
        try
        {
            return await fetch();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
