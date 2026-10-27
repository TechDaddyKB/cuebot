using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Transfer;

namespace Cuebot.Tests.Transfer;

/// <summary>An in-memory profile, command store and globals with a <see cref="TransferService"/> over them.</summary>
internal sealed class TransferTestEnv : IDisposable
{
    public TransferTestEnv()
    {
        Log = new AppLog();
        Profile = new ProfileService(null, Log);
        Commands = new CommandStore(null, Log);
        Globals = new GlobalVariables(null, Log);
        Service = new TransferService(Profile, Commands, Globals, log: Log);
    }

    public AppLog Log { get; }
    public ProfileService Profile { get; }
    public CommandStore Commands { get; }
    public GlobalVariables Globals { get; }
    public TransferService Service { get; }

    public void Edit(Action<BotProfile> edit)
    {
        edit(Profile.Profile);
        Profile.NotifyChanged();
    }

    public static string Canon<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    /// <summary>base64("SBAE" + gzip(json)) — the publicly described Streamer.bot string shape.</summary>
    public static string SbString(string json)
    {
        using var buffer = new MemoryStream();
        buffer.Write("SBAE"u8);
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(buffer.ToArray());
    }

    public static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        Profile.Dispose();
        Commands.Dispose();
        Globals.Dispose();
        Log.Dispose();
    }
}
