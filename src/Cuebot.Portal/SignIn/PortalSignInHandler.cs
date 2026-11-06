using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Logging;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;

namespace Cuebot.Portal.SignIn;

/// <summary>
/// A mod device's messages over its encrypted link (docs/MOD_PORTAL_PLAN.md, section 4). JSON requests
/// <c>{"op": "...", "req": n, ...}</c>; every answer echoes <c>req</c>. Before sign-in a device may only ask for
/// <c>ping</c>, <c>providers</c>, <c>signin</c> and <c>resume</c>; afterwards everything goes to the portal API as the
/// signed-in member.
/// <list type="bullet">
/// <item><c>signin</c>: <c>provider</c>, <c>code</c>, <c>verifier</c>, optional <c>invite</c>, and <c>device</c>
/// {<c>key</c>, <c>name</c>, <c>proof</c>}. Cuebot exchanges the code, then redeems the invite (or signs a member
/// in) and records the device key.</item>
/// <item><c>resume</c>: <c>device</c> {<c>key</c>, <c>proof</c>}: a known device reconnects without the provider.</item>
/// </list>
/// The proof is the device key's ECDSA signature over <see cref="PortalHandshake.DeviceProof"/> of this connection's
/// binding, so it cannot be replayed on another connection.
/// </summary>
public sealed class PortalSignInHandler(PortalDirectory directory, IPortalIdentityExchange exchange,
    Func<Guid, JsonObject, Task<JsonObject>>? api = null, AppLog? log = null) : IPortalLinkHandler
{
    public const int MaxSignInAttempts = 5;
    private const string LogSource = "Mod portal";

    public async Task<byte[]?> HandleAsync(PortalLinkContext link, byte[] message, CancellationToken ct)
    {
        JsonObject request;
        try { request = JsonNode.Parse(message) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return Encode(Error("That message was not understood."), null); }
        var req = request["req"]?.DeepClone();
        request.Remove("req");
        var op = request["op"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
        JsonObject answer;
        try
        {
            answer = op switch
            {
                "ping" => new JsonObject { ["ok"] = true, ["op"] = "pong", ["version"] = PortalRelayClient.ProtocolVersion },
                "providers" => new JsonObject { ["ok"] = true, ["providers"] = exchange.Describe(), ["redirectUri"] = PortalIdentityExchange.RedirectUri },
                "signin" => await SignInAsync(link, request, ct).ConfigureAwait(false),
                "resume" => await ResumeAsync(link, request).ConfigureAwait(false),
                "signout" => SignOut(link),
                _ when link.MemberId is null => SignInRequired("Sign in first."),
                _ when directory.Member(link.MemberId.Value) is null or { Suspended: true } => SignedOut(link, "Your access was removed or paused by the streamer."),
                _ => api is null ? Error($"Unknown request '{op}'.") : await api(link.MemberId.Value, request).ConfigureAwait(false),
            };
        }
        catch (PortalAccessException e)
        {
            answer = Error(e.Message);
        }
        return Encode(answer, req);
    }

    private async Task<JsonObject> SignInAsync(PortalLinkContext link, JsonObject request, CancellationToken ct)
    {
        if (++link.SignInAttempts > MaxSignInAttempts) throw new PortalAccessException("Too many sign-in attempts. Reload the page and try again.");
        var provider = PortalIdentityExchange.Parse(Str(request, "provider")) ?? throw new PortalAccessException("Pick Google, Twitch or Discord to sign in.");
        var (key, name) = ProvenDevice(link, request);
        var identity = await exchange.ExchangeAsync(provider, Str(request, "code"), Str(request, "verifier"), ct).ConfigureAwait(false);

        var invite = Str(request, "invite");
        var (member, device) = invite.Length > 0 && !directory.IsMember(identity)
            ? directory.RedeemInvite(invite, identity, key, name)
            : directory.SignIn(identity, key, name);
        log?.Info(LogSource, $"{member.Name} signed in with {provider} on \"{device.Name}\".");
        return await WelcomeAsync(link, member, device).ConfigureAwait(false);
    }

    private async Task<JsonObject> ResumeAsync(PortalLinkContext link, JsonObject request)
    {
        var (key, _) = ProvenDevice(link, request);
        if (directory.Resume(key) is not { } found) return SignInRequired("Sign in again to continue.");
        return await WelcomeAsync(link, found.Member, found.Device).ConfigureAwait(false);
    }

    private JsonObject SignOut(PortalLinkContext link)
    {
        if (link.MemberId is { } m && link.DeviceId is { } d) directory.RemoveDevice(m, d);
        link.MemberId = null;
        link.DeviceId = null;
        return new JsonObject { ["ok"] = true };
    }

    private JsonObject SignedOut(PortalLinkContext link, string message)
    {
        link.MemberId = null;
        link.DeviceId = null;
        return SignInRequired(message);
    }

    private async Task<JsonObject> WelcomeAsync(PortalLinkContext link, PortalMember member, PortalDevice device)
    {
        link.MemberId = member.Id;
        link.DeviceId = device.Id;
        var me = api is null ? new JsonObject { ["ok"] = true, ["name"] = member.Name, ["role"] = directory.RoleOf(member)?.Name ?? "" }
            : await api(member.Id, new JsonObject { ["op"] = "me" }).ConfigureAwait(false);
        me["op"] = "welcome";
        me["device"] = device.Id.ToString();
        return me;
    }

    /// <summary>The device's public key, after checking its proof for this connection.</summary>
    private static (byte[] Key, string Name) ProvenDevice(PortalLinkContext link, JsonObject request)
    {
        if (request["device"] is not JsonObject device) throw new PortalAccessException("The device key is missing.");
        byte[] key, proof;
        try
        {
            key = PortalKeys.FromBase64Url(Str(device, "key"));
            proof = PortalKeys.FromBase64Url(Str(device, "proof"));
        }
        catch (FormatException)
        {
            throw new PortalAccessException("The device key is not valid.");
        }
        if (!PortalKeys.Verify(key, PortalHandshake.DeviceProof(link.Binding), proof))
            throw new PortalAccessException("This device could not prove its key. Reload the page.");
        return (key, Str(device, "name"));
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject SignInRequired(string message) => new() { ["ok"] = false, ["error"] = message, ["signIn"] = true };

    private static byte[] Encode(JsonObject answer, JsonNode? req)
    {
        if (req is not null) answer["req"] = req;
        return JsonSerializer.SerializeToUtf8Bytes(answer);
    }

    private static string Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
}
