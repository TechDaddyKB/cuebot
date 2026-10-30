using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.YouTube.Api.Grpc;
using Cuebot.YouTube.Auth;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Cuebot.YouTube.Api;

/// <summary>A server-streamed live chat: pages arrive as YouTube pushes them, until the server closes the stream.</summary>
internal interface IChatStream
{
    /// <summary>
    /// Opens liveChatMessages.streamList and yields each response as a <see cref="ChatPage"/> in the same JSON shape as
    /// liveChatMessages.list. Ends when the server closes the stream; errors are <see cref="YouTubeApiException"/>s
    /// with the REST reasons the chat reader already understands.
    /// </summary>
    IAsyncEnumerable<ChatPage> ReadAsync(AccountTokens account, string liveChatId, string? pageToken, CancellationToken ct);
}

/// <summary>
/// liveChatMessages.streamList over gRPC (Google offers it only there). One call costs
/// <see cref="QuotaCosts.LiveChatMessagesStream"/> units however long it stays open, instead of that much per poll.
/// </summary>
internal sealed class GrpcChatStream : IChatStream, IDisposable
{
    public static readonly Uri ProductionAddress = new("https://youtube.googleapis.com");

    private static readonly string[] Parts = ["id", "snippet", "authorDetails"];

    private readonly GrpcChannel _channel;
    private readonly V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient _client;
    private readonly QuotaTracker _quota;

    public GrpcChatStream(QuotaTracker quota, Uri? address = null, HttpMessageHandler? handler = null)
    {
        _quota = quota;
        _channel = GrpcChannel.ForAddress(address ?? ProductionAddress, new GrpcChannelOptions
        {
            HttpHandler = handler ?? new SocketsHttpHandler
            {
                // A chat can be quiet for minutes; pings keep NAT and proxies from dropping the idle connection.
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                EnableMultipleHttp2Connections = true,
            },
            DisposeHttpClient = handler is null,
        });
        _client = new V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient(_channel);
    }

    public async IAsyncEnumerable<ChatPage> ReadAsync(AccountTokens account, string liveChatId, string? pageToken,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new LiveChatMessageListRequest { LiveChatId = liveChatId };
        request.Part.AddRange(Parts);
        if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;

        var token = await account.GetAccessTokenAsync(ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var call = _client.StreamList(request, new Metadata { { "authorization", "Bearer " + token } }, cancellationToken: ct);
            var stream = call.ResponseStream;
            var opened = false;
            while (true)
            {
                bool more;
                try
                {
                    more = await stream.MoveNext(ct).ConfigureAwait(false);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated && attempt == 0 && !opened)
                {
                    await account.RefreshAsync(token, ct).ConfigureAwait(false);
                    token = await account.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    break;
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (RpcException ex)
                {
                    if (!opened) _quota.Spend(QuotaCosts.LiveChatMessagesStream);
                    var error = ToApiException(ex);
                    if (error.IsQuotaExceeded) _quota.MarkExhausted();
                    throw error;
                }
                if (!opened)
                {
                    opened = true;
                    _quota.Spend(QuotaCosts.LiveChatMessagesStream);
                }
                if (!more) yield break;
                yield return ChatStreamPages.ToPage(stream.Current);
            }
        }
    }

    /// <summary>
    /// gRPC status to the REST reasons of liveChatMessages.list. Google's gRPC errors cannot tell a disabled chat from
    /// an ended one, so both become "liveChatDisabled": the chat stops, and the broadcast poll notices when the
    /// broadcast itself has ended.
    /// </summary>
    internal static YouTubeApiException ToApiException(RpcException ex)
    {
        var detail = ex.Status.Detail;
        var (status, reason) = ex.StatusCode switch
        {
            StatusCode.NotFound => (404, "liveChatNotFound"),
            StatusCode.FailedPrecondition => (403, "liveChatDisabled"),
            StatusCode.ResourceExhausted when detail.Contains("quota", StringComparison.OrdinalIgnoreCase) => (403, "quotaExceeded"),
            StatusCode.ResourceExhausted => (403, "rateLimitExceeded"),
            StatusCode.PermissionDenied => (403, "forbidden"),
            StatusCode.Unauthenticated => (401, "unauthorized"),
            StatusCode.InvalidArgument => (400, "invalidArgument"),
            _ => (0, ""),
        };
        var body = JsonElementExtensions.Parse(JsonSerializer.Serialize(new
        {
            error = new { message = detail.Length > 0 ? detail : ex.StatusCode.ToString(), errors = new[] { new { reason } } },
        }));
        return YouTubeApiClient.ParseError(body, status);
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>Turns streamList's protobuf responses into the JSON that <see cref="Chat.ChatItemMapper"/> reads.</summary>
internal static class ChatStreamPages
{
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default);

    public static ChatPage ToPage(LiveChatMessageListResponse response) =>
        new(response.Items.Select(ToJson).ToArray(), response.NextPageToken,
            TimeSpan.Zero, DateTimeOffset.TryParse(response.OfflineAt, out var offline) ? offline : null);

    /// <summary>
    /// Protobuf JSON names fields like the REST API but writes enums as their proto names (TEXT_MESSAGE_EVENT); those
    /// become the REST spellings (textMessageEvent). A poll's status also moves to where the REST API keeps it.
    /// </summary>
    internal static JsonElement ToJson(LiveChatMessage message)
    {
        var item = JsonNode.Parse(Formatter.Format(message))!.AsObject();
        if (item["snippet"] is JsonObject snippet)
        {
            Rename(snippet, "type");
            if (snippet["userBannedDetails"] is JsonObject banned) Rename(banned, "banType");
            if (snippet["pollDetails"] is JsonObject poll)
            {
                Rename(poll, "status");
                if (poll["status"] is { } status && poll["metadata"] is JsonObject metadata) metadata["status"] = status.DeepClone();
            }
        }
        return JsonElementExtensions.Parse(item.ToJsonString());
    }

    private static void Rename(JsonObject obj, string field)
    {
        if (obj[field] is JsonValue v && v.TryGetValue<string>(out var name)) obj[field] = Camel(name);
    }

    /// <summary>TEXT_MESSAGE_EVENT → textMessageEvent; TOMBSTONE → tombstone.</summary>
    internal static string Camel(string protoName)
    {
        var words = protoName.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select((w, i) => i == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
