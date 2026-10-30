using Cuebot.Core.Storage;
using Tmds.DBus.Protocol;

namespace Cuebot.App.Platform;

/// <summary>
/// Keeps the secret-store key in the desktop keyring through the freedesktop Secret Service API (GNOME Keyring,
/// KWallet, KeePassXC, ...), one item per data folder. Where there is no keyring (headless servers, a Flatpak without
/// the permission, a refused unlock) it falls back to <see cref="FileKeyProtector"/>; a key file left by such a
/// fallback moves into the keyring as soon as one is available.
/// </summary>
internal sealed class SecretServiceKeyProtector(Action<string> warn) : ISecretKeyProtector
{
    private const string Bus = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string Service = "org.freedesktop.Secret.Service";
    private const string Collection = "org.freedesktop.Secret.Collection";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);   // room for an unlock prompt

    private bool _usingFile;

    public string Name => _usingFile ? "key file (no desktop keyring)" : "desktop keyring";

    public byte[]? Load(string folder)
    {
        byte[]? key;
        try
        {
            key = Run(c => LookupAsync(c, folder));
        }
        catch (Exception ex)
        {
            FallBack("read", ex);
            return FileKeyProtector.Instance.Load(folder);
        }
        if (key is not null) return key;

        // A key file from an earlier fallback: move it into the keyring.
        var fileKey = FileKeyProtector.Instance.Load(folder);
        if (fileKey is not null) TryStore(folder, fileKey);
        return fileKey;
    }

    public void Save(string folder, byte[] key)
    {
        if (!TryStore(folder, key)) FileKeyProtector.Instance.Save(folder, key);
    }

    private bool TryStore(string folder, byte[] key)
    {
        try
        {
            Run(async c => { await StoreAsync(c, folder, key); return true; });
            FileKeyProtector.Delete(folder);
            _usingFile = false;
            return true;
        }
        catch (Exception ex)
        {
            FallBack("store", ex);
            return false;
        }
    }

    private void FallBack(string what, Exception ex)
    {
        if (!_usingFile) warn($"The desktop keyring could not {what} the key for stored sign-ins ({ex.Message}); using a key file in the data folder instead.");
        _usingFile = true;
    }

    private static T Run<T>(Func<DBusConnection, Task<T>> operation)
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) throw new InvalidOperationException("no session bus");
        return Task.Run(async () =>
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            return await operation(connection);
        }).WaitAsync(Timeout).GetAwaiter().GetResult();
    }

    private static Dictionary<string, string> Attributes(string folder) => new()
    {
        ["application"] = "Cuebot",
        ["cuebot-data-folder"] = folder,
    };

    private static async Task<byte[]?> LookupAsync(DBusConnection c, string folder)
    {
        var (unlocked, locked) = await SearchAsync(c, Attributes(folder));
        var items = unlocked.ToList();
        if (items.Count == 0 && locked.Length > 0) items.AddRange(await UnlockAsync(c, locked));
        if (items.Count == 0) return null;
        var session = await OpenSessionAsync(c);
        return await GetSecretAsync(c, items[0], session);
    }

    private static async Task StoreAsync(DBusConnection c, string folder, byte[] key)
    {
        var collection = await ReadAliasAsync(c, "default");
        if (collection == "/") throw new InvalidOperationException("the keyring has no default collection");
        await UnlockAsync(c, [collection]);
        var session = await OpenSessionAsync(c);
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, collection, Collection, "CreateItem", "a{sv}(oayays)b");
            var props = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("org.freedesktop.Secret.Item.Label");
            w.WriteVariantString($"Cuebot stored sign-ins ({folder})");
            w.WriteDictionaryEntryStart();
            w.WriteString("org.freedesktop.Secret.Item.Attributes");
            w.WriteVariant(new Dict<string, string>(Attributes(folder)).AsVariantValue());
            w.WriteDictionaryEnd(props);
            w.WriteStructureStart();
            w.WriteObjectPath(session);
            w.WriteArray(Array.Empty<byte>());
            w.WriteArray(key);
            w.WriteString("application/octet-stream");
            w.WriteBool(true);   // replace an existing item with the same attributes
            message = w.CreateMessage();
        }
        var prompt = await c.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            r.ReadObjectPath();
            return r.ReadObjectPathAsString();
        }, null);
        if (prompt != "/") await PromptAsync(c, prompt);
    }

    private static async Task<(string[] Unlocked, string[] Locked)> SearchAsync(DBusConnection c, Dictionary<string, string> attributes)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, ServicePath, Service, "SearchItems", "a{ss}");
            // Written inline: MessageWriter is a struct, so a helper taking it by value would write into a copy.
            var start = w.WriteDictionaryStart();
            foreach (var (k, v) in attributes)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString(k);
                w.WriteString(v);
            }
            w.WriteDictionaryEnd(start);
            message = w.CreateMessage();
        }
        return await c.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            return (ReadPaths(ref r), ReadPaths(ref r));
        }, null);
    }

    private static string[] ReadPaths(ref Reader r)
    {
        var paths = new List<string>();
        var end = r.ReadArrayStart(DBusType.ObjectPath);
        while (r.HasNext(end)) paths.Add(r.ReadObjectPathAsString());
        return paths.ToArray();
    }

    private static async Task<string[]> UnlockAsync(DBusConnection c, string[] objects)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, ServicePath, Service, "Unlock", "ao");
            w.WriteArray(objects.Select(o => new ObjectPath(o)));
            message = w.CreateMessage();
        }
        var (unlocked, prompt) = await c.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            return (ReadPaths(ref r), r.ReadObjectPathAsString());
        }, null);
        if (prompt == "/") return unlocked;
        await PromptAsync(c, prompt);
        return objects;
    }

    private static async Task PromptAsync(DBusConnection c, string prompt)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await c.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = prompt, Interface = PromptInterface, Member = "Completed" },
            static (Message m, object? _) => m.GetBodyReader().ReadBool(),
            (Notification<bool> n) =>
            {
                if (n.Exception is not null) completed.TrySetException(n.Exception);
                else if (n.HasValue) completed.TrySetResult(n.Value);
            },
            false, ObserverFlags.None, null);
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, prompt, PromptInterface, "Prompt", "s");
            w.WriteString("");
            message = w.CreateMessage();
        }
        await c.CallMethodAsync(message);
        if (await completed.Task) throw new OperationCanceledException("the keyring unlock was dismissed");
    }

    private static async Task<string> OpenSessionAsync(DBusConnection c)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, ServicePath, Service, "OpenSession", "sv");
            w.WriteString("plain");   // the session bus is private to this user; the value is not sent anywhere else
            w.WriteVariantString("");
            message = w.CreateMessage();
        }
        return await c.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            r.ReadVariantValue();
            return r.ReadObjectPathAsString();
        }, null);
    }

    private static async Task<string> ReadAliasAsync(DBusConnection c, string alias)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, ServicePath, Service, "ReadAlias", "s");
            w.WriteString(alias);
            message = w.CreateMessage();
        }
        return await c.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null);
    }

    private static async Task<byte[]?> GetSecretAsync(DBusConnection c, string item, string session)
    {
        MessageBuffer message;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bus, ServicePath, Service, "GetSecrets", "aoo");
            w.WriteArray(new[] { new ObjectPath(item) });
            w.WriteObjectPath(session);
            message = w.CreateMessage();
        }
        return await c.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            var end = r.ReadDictionaryStart();
            if (r.HasNext(end))
            {
                r.AlignStruct();
                r.ReadObjectPath();
                r.AlignStruct();
                r.ReadObjectPath();
                r.ReadArrayOfByte();
                var value = r.ReadArrayOfByte();
                r.ReadString();
                return value;
            }
            return null;
        }, null);
    }
}
