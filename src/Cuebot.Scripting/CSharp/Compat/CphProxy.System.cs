using Cuebot.Core.Desktop.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Scripting;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Streamer.bot.Plugin.Interface.Enums;

namespace Cuebot.Scripting.Compat;

public sealed partial class CphProxy
{
    // ---- Chat actions, custom events, other code, keyboard --------------------------------------------------------

    /// <summary>
    /// Sends a /me line to Twitch chat (shown in italics). Uses the Twitch module's action announcer when it has one;
    /// otherwise the text is sent as an ordinary chat message. <paramref name="bot"/> and <paramref name="fallback"/>
    /// follow Cuebot's chat sender setting like <see cref="SendMessage"/>.
    /// </summary>
    public void SendAction(string action, bool bot = true, bool fallback = true)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        if (_services.ChatModeration?.AnnouncersFor("Twitch", ChatAnnouncementKind.Action) is { Count: > 0 } announcers)
        {
            Sync(announcers[0].AnnounceAsync(action, ChatAnnouncementKind.Action, _ct));
            return;
        }
        SendChat("Twitch", "twitch.sendMessage", action);
    }

    /// <summary>
    /// Fires the custom event <paramref name="eventName"/>: actions with a "Custom code event" trigger for that name
    /// run, receiving this run's arguments when <paramref name="useArgs"/>. Same as <see cref="TriggerCodeEvent(string, bool)"/>.
    /// </summary>
    public void TriggerEvent(string eventName, bool useArgs = true) =>
        Need(_services.Bus).Publish(CodeTriggerRegistry.CreateEvent(eventName, useArgs ? _context?.Args : null));

    /// <summary>
    /// Presses keys, in SendKeys syntax (<c>{W}</c>, <c>{DOWN}</c>, <c>+{W}</c> Shift, <c>^{W}</c> Ctrl, <c>%{W}</c> Alt).
    /// Needs a keyboard output service (<see cref="IKeyboardOutput"/>).
    /// </summary>
    public void KeyboardPress(string keyPress)
    {
        var keyboard = _services.Keyboard ?? throw Unsupported("no keyboard output service is loaded");
        if (string.IsNullOrEmpty(keyPress)) return;
        Sync(keyboard.PressAsync(keyPress, _ct));   // accepts Streamer.bot's SendKeys syntax
    }

    /// <summary>
    /// Runs the public parameterless method <paramref name="methodName"/> of another "Execute C# code" sub-action's
    /// CPHInline class, on that code's long-lived instance, with this run's arguments. The code is found by its
    /// "Code name" setting, or else as the first CPHInline code in the action named <paramref name="codeName"/>.
    /// Returns the method's bool result (true for void methods); false when the code or method is not found.
    /// </summary>
    public bool ExecuteMethod(string codeName, string methodName)
    {
        var host = _inline ?? throw Unsupported("no C# code host is available");
        var context = RequireContext();
        if (string.IsNullOrWhiteSpace(codeName) || string.IsNullOrWhiteSpace(methodName)) return false;
        var step = InlineCodeFinder.Find(Profile(nameof(ExecuteMethod)).Snapshot.Actions, codeName);
        if (step is null)
        {
            LogWarn($"ExecuteMethod: no C# code (CPHInline class) named '{codeName}'");
            return false;
        }
        var result = host.InvokeMethod(step.Parameters["code"], methodName.Trim(), this, context.Args, CSharpCodeSubAction.References(step));
        if (result is null) LogWarn($"ExecuteMethod: '{codeName}' has no public method {methodName}() without parameters");
        return result ?? false;
    }

    // ---- Older and wiki-era forms ---------------------------------------------------------------------------------

    /// <summary>Fires the custom event with the properties of the JSON object <paramref name="json"/> as its arguments.</summary>
    public void TriggerCodeEvent(string eventName, string json)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(json))
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject()) args[property.Name] = ScriptArgs.ToClr(property.Value);
            }
        }
        TriggerCodeEvent(eventName, args);
    }

    /// <summary>Percent-encodes text for a URL (query values, path segments).</summary>
    public string UrlEncode(string text) => Uri.EscapeDataString(text ?? "");

    /// <summary>Escapes text for use inside a JSON string (quotes, backslashes, control characters).</summary>
    public string EscapeString(string text) => JsonSerializer.Serialize(text ?? "", EscapeOptions)[1..^1];

    private static readonly JsonSerializerOptions EscapeOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Sends <paramref name="data"/> (text as is, anything else as JSON) as one UDP datagram to <paramref name="port"/>
    /// on this computer (127.0.0.1). Returns the number of bytes sent.
    /// </summary>
    public int BroadcastUdp(int port, object? data)
    {
        var bytes = Encoding.UTF8.GetBytes(data as string ?? JsonSerializer.Serialize(data));
        using var udp = new UdpClient();
        return udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    // Older user-group forms with numeric Twitch user ids.
    public bool AddUserToGroup(int userId, string groupName) => AddUserIdToGroup(Num(userId), Platform.Twitch, groupName);
    public bool RemoveUserFromGroup(int userId, string groupName) => RemoveUserIdFromGroup(Num(userId), Platform.Twitch, groupName);
    public bool UserInGroup(int userId, string groupName) => UserIdInGroup(Num(userId), Platform.Twitch, groupName);

    /// <summary>Older form without an avatar; returns the message id like the current one.</summary>
    public string DiscordPostTextToWebhook(string webhookUrl, string content, string? username, bool textToSpeech) =>
        DiscordPostTextToWebhook(webhookUrl, content, username, null, textToSpeech);

    /// <summary>Sends bytes to a WebSocket client connection as UTF-8 text (Cuebot's clients send text messages).</summary>
    public void WebsocketSend(byte[] data, int connection = 0) => WebsocketSend(Encoding.UTF8.GetString(data ?? []), connection);
}
