using System.Text.Json.Nodes;
using Cuebot.Server.Mcp;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    private IMediaEditor MediaEditor => services.MediaEditor ?? throw ApiException.Unavailable("Editing pictures and videos only works in the Cuebot app.");

    public Task<JsonObject> MediaInfoAsync(string source, CancellationToken ct) => Guard(() => MediaEditor.InfoAsync(source, ct));

    /// <summary>The file an edit would write, or a bad-request error (for the overwrite confirmation).</summary>
    public string MediaOutput(string source, string? output, string format, bool video) =>
        GuardSync(() => MediaEditor.OutputFor(source, output, format, video));

    public Task<JsonObject> EditImageAsync(string source, string? output, string format, JsonArray steps, CancellationToken ct) =>
        Guard(() => MediaEditor.EditImageAsync(source, output, format, steps, overwrite: true, ct));

    public Task<JsonObject> EditVideoAsync(string source, string? output, string format, JsonObject options, CancellationToken ct) =>
        Guard(() => MediaEditor.EditVideoAsync(source, output, format, options, overwrite: true, ct));

    /// <summary>The editor's "this cannot be done" errors (bad paths, formats, steps) become readable tool errors.</summary>
    private static T GuardSync<T>(Func<T> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException
                                       or UnauthorizedAccessException or NotSupportedException or HttpRequestException or IOException)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }
}
