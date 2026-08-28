using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoko.AniListScrobble;

public sealed class AniListClientOptions
{
    public string? AccessToken { get; init; }
    public string AppName { get; init; } = "shoko-anilist-scrobble";
    public string AppVersion { get; init; } = "1.0.0";
    public int MaxJsonResponseBytes { get; init; } = 1_048_576;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public interface IAniListClient
{
    Task<AniListViewer> GetViewerAsync(CancellationToken cancellationToken);
    Task<AniListMedia> GetMediaAsync(int mediaId, CancellationToken cancellationToken);
    Task<AniListListEntry> SaveProgressAsync(int mediaId, int progress, string status, CancellationToken cancellationToken);
}

public sealed class AniListClient : IAniListClient
{
    public const string GraphQlEndpoint = "https://graphql.anilist.co/";
    public const string AuthorizeEndpoint = "https://anilist.co/api/v2/oauth/authorize";
    public const string PinRedirect = "https://anilist.co/api/v2/oauth/pin";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private const string ViewerQuery = """
        query {
          Viewer { id name }
        }
        """;

    private const string MediaQuery = """
        query ($id: Int) {
          Media(id: $id, type: ANIME) {
            id
            episodes
            status
            format
            mediaListEntry {
              id
              status
              progress
            }
          }
        }
        """;

    private const string SaveMutation = """
        mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) {
          SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status) {
            id
            status
            progress
            media { id episodes format }
          }
        }
        """;

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly AniListClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public AniListClient(HttpClient http, AniListClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
        _http.BaseAddress ??= new Uri(GraphQlEndpoint);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{options.AppName}/{options.AppVersion}");
    }

    public static string AuthorizeUrl(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Configure an AniList client ID first.", nameof(clientId));
        return $"{AuthorizeEndpoint}?client_id={Uri.EscapeDataString(clientId.Trim())}&response_type=token";
    }

    public async Task<AniListViewer> GetViewerAsync(CancellationToken cancellationToken)
    {
        var payload = await SendAsync<ViewerData>(ViewerQuery, null, requireToken: true, cancellationToken).ConfigureAwait(false);
        return payload.Viewer ?? throw new AniListRequestException("AniList did not return the authenticated user.", 502, retryable: false);
    }

    public async Task<AniListMedia> GetMediaAsync(int mediaId, CancellationToken cancellationToken)
    {
        if (mediaId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mediaId));
        var payload = await SendAsync<MediaData>(MediaQuery, new { id = mediaId }, requireToken: true, cancellationToken).ConfigureAwait(false);
        return payload.Media ?? throw new AniListRequestException($"AniList media {mediaId} was not found.", 404, retryable: false);
    }

    public async Task<AniListListEntry> SaveProgressAsync(int mediaId, int progress, string status, CancellationToken cancellationToken)
    {
        if (mediaId <= 0)
            throw new ArgumentOutOfRangeException(nameof(mediaId));
        if (progress < 0)
            throw new ArgumentOutOfRangeException(nameof(progress));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("A MediaListStatus is required.", nameof(status));
        var payload = await SendAsync<SaveData>(SaveMutation, new { mediaId, progress, status }, requireToken: true, cancellationToken).ConfigureAwait(false);
        return payload.SaveMediaListEntry ?? throw new AniListRequestException("AniList did not return the saved list entry.", 502, retryable: false);
    }

    private async Task<T> SendAsync<T>(string query, object? variables, bool requireToken, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        await WaitTurnAsync(timeout.Token).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (requireToken)
        {
            if (string.IsNullOrWhiteSpace(_options.AccessToken))
                throw new AniListRequestException("AniList access token is missing.", 401, retryable: false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        }

        var body = JsonSerializer.Serialize(new { query, variables }, JsonOptions);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        ApplyRateLimit(response);
        var raw = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
        if ((int)response.StatusCode == 429)
            throw MapError(response, raw);
        if (!response.IsSuccessStatusCode)
            throw MapError(response, raw);

        GraphQlEnvelope<T> envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<GraphQlEnvelope<T>>(raw, JsonOptions)
                ?? throw new AniListRequestException("AniList returned empty JSON.", (int)response.StatusCode, retryable: false);
        }
        catch (JsonException exception)
        {
            throw new AniListRequestException("AniList returned invalid JSON.", (int)response.StatusCode, retryable: false)
            {
                Data = { ["cause"] = exception.Message },
            };
        }

        if (envelope.Errors is { Count: > 0 } errors)
        {
            var first = errors[0];
            var status = first.Status is >= 400 and < 600 ? first.Status : (int)response.StatusCode;
            throw new AniListRequestException(first.Message ?? "AniList GraphQL error.", status, retryable: status is 429 or >= 500);
        }

        if (envelope.Data is null)
            throw new AniListRequestException("AniList returned no data.", (int)response.StatusCode, retryable: false);
        return envelope.Data;
    }

    private async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextAllowed - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds)
            && seconds > 0)
        {
            var retry = DateTimeOffset.UtcNow.AddSeconds(seconds);
            if (retry > _nextAllowed)
                _nextAllowed = retry;
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var remaining = _options.MaxJsonResponseBytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return memory.ToArray();
            memory.Write(buffer, 0, read);
            remaining -= read;
        }

        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0)
            throw new AniListRequestException("AniList response exceeded the configured size limit.", 502, retryable: false);
        return memory.ToArray();
    }

    private static AniListRequestException MapError(HttpResponseMessage response, byte[] raw)
    {
        var status = (int)response.StatusCode;
        var retryable = status is 429 or (int)HttpStatusCode.RequestTimeout or >= 500;
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = delta;
        else if (response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out var seconds))
            retryAfter = TimeSpan.FromSeconds(seconds);

        var message = status switch
        {
            401 => "AniList rejected the access token.",
            429 => "AniList rate-limited the request.",
            _ => $"AniList returned HTTP {status}.",
        };
        return new AniListRequestException(message, status, retryable) { RetryAfter = retryAfter, Body = Encoding.UTF8.GetString(raw) };
    }

    private sealed class GraphQlEnvelope<T>
    {
        public T? Data { get; set; }
        public List<GraphQlError>? Errors { get; set; }
    }

    private sealed class GraphQlError
    {
        public string? Message { get; set; }
        public int Status { get; set; }
    }

    private sealed class ViewerData
    {
        public AniListViewer? Viewer { get; set; }
    }

    private sealed class MediaData
    {
        public AniListMedia? Media { get; set; }
    }

    private sealed class SaveData
    {
        public AniListListEntry? SaveMediaListEntry { get; set; }
    }
}

public sealed class AniListViewer
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public sealed class AniListMedia
{
    public int Id { get; set; }
    public int? Episodes { get; set; }
    public string? Status { get; set; }
    public string? Format { get; set; }
    public AniListListEntry? MediaListEntry { get; set; }
}

public sealed class AniListListEntry
{
    public int Id { get; set; }
    public string? Status { get; set; }
    public int Progress { get; set; }
    public AniListMedia? Media { get; set; }
}

public sealed class AniListRequestException : Exception
{
    public AniListRequestException(string message, int statusCode, bool retryable) : base(message)
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }

    public int StatusCode { get; }
    public bool Retryable { get; }
    public bool IsUnauthorized => StatusCode is 401 or 403;
    public TimeSpan? RetryAfter { get; init; }
    public string? Body { get; init; }
}

public static class ProgressPlanner
{
    public const string Current = "CURRENT";
    public const string Completed = "COMPLETED";
    public const string Repeating = "REPEATING";

    public static ScrobblePlan Plan(MappedWatch watch, AniListMedia media)
    {
        var current = media.MediaListEntry?.Progress ?? 0;
        var currentStatus = media.MediaListEntry?.Status;
        if (current >= watch.ProgressIndex)
            return new ScrobblePlan(SkipReason.AlreadyAhead, current, currentStatus, Write: false);

        var progress = watch.ProgressIndex;
        var complete = IsComplete(watch, media, progress);
        var status = complete
            ? Completed
            : string.Equals(currentStatus, Repeating, StringComparison.OrdinalIgnoreCase) ? Repeating : Current;
        return new ScrobblePlan(SkipReason.None, progress, status, Write: true);
    }

    public static bool IsComplete(MappedWatch watch, AniListMedia media, int progress)
    {
        if (watch.IsMovie || string.Equals(media.Format, "MOVIE", StringComparison.OrdinalIgnoreCase))
            return progress >= 1;
        return media.Episodes is int episodes && episodes > 0 && progress >= episodes;
    }
}

public sealed record ScrobblePlan(SkipReason Skip, int Progress, string? Status, bool Write);
