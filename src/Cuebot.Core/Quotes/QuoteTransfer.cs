using System.Globalization;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Quotes;

/// <summary>
/// Quote import/export. JSON is an array of quote objects (or a whole quotes.json document);
/// CSV has the header <c>id,text,user,userId,platform,game,createdAt</c> (RFC 4180 quoting; createdAt in ISO 8601).
/// Unknown CSV columns are ignored and only <c>text</c> is required, so simple spreadsheets import too.
/// </summary>
public static class QuoteTransfer
{
    public static readonly string[] CsvColumns = ["id", "text", "user", "userId", "platform", "game", "createdAt"];

    public static string ToJson(IEnumerable<Quote> quotes) => JsonSerializer.Serialize(quotes.ToList(), Json.Options);

    /// <exception cref="FormatException">The text is not a quote array or quotes document.</exception>
    public static List<Quote> FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.Deserialize<List<Quote>>(Json.Options) ?? [],
                JsonValueKind.Object => doc.RootElement.Deserialize<QuotesFile>(Json.Options)?.Quotes ?? [],
                _ => throw new FormatException("Expected a JSON array of quotes"),
            };
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not valid quotes JSON: " + ex.Message, ex);
        }
    }

    public static string ToCsv(IEnumerable<Quote> quotes)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', CsvColumns)).Append("\r\n");
        foreach (var q in quotes)
        {
            string[] cells =
            [
                q.Id.ToString(CultureInfo.InvariantCulture), q.Text, q.User, q.UserId, q.Platform, q.Game,
                q.CreatedAt == default ? "" : q.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
            ];
            sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <exception cref="FormatException">No header with a "text" column.</exception>
    public static List<Quote> FromCsv(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0) return [];
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
        int Col(string name) => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        var text = Col("text") is var t and >= 0 ? t : Col("quote");
        if (text < 0) throw new FormatException("The CSV needs a header row with a 'text' column");
        int id = Col("id"), user = Col("user"), userId = Col("userId"), platform = Col("platform"), game = Col("game"),
            created = Col("createdAt") is var c and >= 0 ? c : Col("date");

        string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";
        var quotes = new List<Quote>();
        foreach (var row in rows.Skip(1))
        {
            var quoteText = Cell(row, text).Trim();
            if (quoteText.Length == 0) continue;
            quotes.Add(new Quote
            {
                Id = int.TryParse(Cell(row, id), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0,
                Text = quoteText,
                User = Cell(row, user).Trim(),
                UserId = Cell(row, userId).Trim(),
                Platform = Cell(row, platform).Trim(),
                Game = Cell(row, game).Trim(),
                CreatedAt = DateTimeOffset.TryParse(Cell(row, created), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at) ? at : default,
            });
        }
        return quotes;
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>RFC 4180 parser: quoted fields may contain commas, quotes ("") and line breaks.</summary>
    internal static List<string[]> ParseCsv(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' when cell.Length == 0:
                    quoted = true;
                    any = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    any = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    cell.Append(ch);
                    any = true;
                    break;
            }
        }
        EndRow();
        return rows;

        void EndRow()
        {
            if (any || cell.Length > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row.ToArray());
            }
            row.Clear();
            cell.Clear();
            any = false;
        }
    }
}
