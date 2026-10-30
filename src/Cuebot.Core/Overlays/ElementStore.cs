using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Overlays;

/// <summary>
/// The element library (elements.json; in memory when <c>paths</c> is null): widgets and alert designs that layers on
/// any overlay can use. Reads return copies; <see cref="Changed"/> carries the element's id.
/// </summary>
public sealed class ElementStore : IDisposable
{
    private readonly object _gate = new();
    private readonly JsonFile<ElementDocument>? _file;
    private readonly ElementDocument _memory = new();

    public ElementStore(AppPaths? paths, AppLog log)
    {
        if (paths is not null) _file = new JsonFile<ElementDocument>(paths.File("elements.json"), log);
    }

    /// <summary>Raised (on the caller's thread) after an element is added, changed or deleted.</summary>
    public event Action<Guid>? Changed;

    private ElementDocument Document => _file?.Value ?? _memory;

    public IReadOnlyList<OverlayElement> All()
    {
        lock (_gate) return Document.Elements.Select(Json.Clone).ToArray();
    }

    public OverlayElement? Get(Guid id)
    {
        lock (_gate) return Find(id) is { } element ? Json.Clone(element) : null;
    }

    /// <summary>Adds a copy of <paramref name="element"/> (a new id when it is empty or taken) and returns it.</summary>
    public OverlayElement Add(OverlayElement element)
    {
        var copy = Json.Clone(element);
        copy.Name = CleanName(copy.Name);
        copy.Created = copy.Modified = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (copy.Id == Guid.Empty || Find(copy.Id) is not null) copy.Id = Guid.NewGuid();
            Document.Elements.Add(copy);
            _file?.ScheduleSave();
        }
        Changed?.Invoke(copy.Id);
        return Json.Clone(copy);
    }

    /// <summary>Edits an element in place; returns false when it does not exist. The id cannot be changed.</summary>
    public bool Update(Guid id, Action<OverlayElement> edit)
    {
        lock (_gate)
        {
            if (Find(id) is not { } element) return false;
            edit(element);
            element.Id = id;
            element.Name = CleanName(element.Name);
            element.Modified = DateTimeOffset.UtcNow;
            _file?.ScheduleSave();
        }
        Changed?.Invoke(id);
        return true;
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (Document.Elements.RemoveAll(e => e.Id == id) == 0) return false;
            _file?.ScheduleSave();
        }
        Changed?.Invoke(id);
        return true;
    }

    private OverlayElement? Find(Guid id) => Document.Elements.FirstOrDefault(e => e.Id == id);

    private static string CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? "Element" : name.Trim();

    public void Dispose() => _file?.Dispose();
}
