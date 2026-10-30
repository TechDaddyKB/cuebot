using System.Text;

namespace Cuebot.Core.Overlays.Widgets;

/// <summary>
/// Custom widget code, one folder per library element (<c>overlays/widgets/{elementId}/</c> with <c>widget.html</c>,
/// <c>widget.css</c>, <c>widget.js</c>, <c>fields.json</c>, <c>data.json</c>), so large purchased scripts stay out of
/// elements.json. With no <c>paths</c> it uses a temporary folder deleted on dispose (tests).
/// </summary>
public sealed class WidgetCodeStore : IDisposable
{
    private static readonly (string File, Func<WidgetCode, string> Get)[] Files =
    [
        ("widget.html", c => c.Html), ("widget.css", c => c.Css), ("widget.js", c => c.Js),
        ("fields.json", c => c.Fields), ("data.json", c => c.Data),
    ];

    private readonly bool _temporary;

    public WidgetCodeStore(AppPaths? paths)
    {
        if (paths is not null)
        {
            Folder = paths.GetDirectory(Path.Combine("overlays", "widgets"));
            return;
        }
        Folder = Path.Combine(Path.GetTempPath(), "cuebot-widgets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        _temporary = true;
    }

    public string Folder { get; }

    /// <summary>Raised after an element's code was saved or deleted.</summary>
    public event Action<Guid>? Changed;

    /// <summary>The element's code, or <see cref="WidgetCode.Empty"/> when it has none.</summary>
    public WidgetCode Read(Guid elementId)
    {
        var dir = Dir(elementId);
        if (!Directory.Exists(dir)) return WidgetCode.Empty;
        string Get(string file, string fallback = "") =>
            File.Exists(Path.Combine(dir, file)) ? File.ReadAllText(Path.Combine(dir, file), Encoding.UTF8) : fallback;
        return new WidgetCode(Get("widget.html"), Get("widget.css"), Get("widget.js"), Get("fields.json", "{}"), Get("data.json", "{}"));
    }

    public bool Exists(Guid elementId) => Directory.Exists(Dir(elementId));

    /// <summary>Saves every pane (each file written to a temporary name, then moved over the old one).</summary>
    public void Write(Guid elementId, WidgetCode code)
    {
        var dir = Dir(elementId);
        Directory.CreateDirectory(dir);
        foreach (var (file, get) in Files)
        {
            var path = Path.Combine(dir, file);
            File.WriteAllText(path + ".tmp", get(code), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        Changed?.Invoke(elementId);
    }

    public void Delete(Guid elementId)
    {
        var dir = Dir(elementId);
        if (!Directory.Exists(dir)) return;
        Directory.Delete(dir, recursive: true);
        Changed?.Invoke(elementId);
    }

    private string Dir(Guid elementId) => Path.Combine(Folder, elementId.ToString("N"));

    public void Dispose()
    {
        if (!_temporary) return;
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
