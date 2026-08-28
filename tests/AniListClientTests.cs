using System.Net;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class AniListClientTests
{
    [Fact]
    public async Task ViewerQuerySendsBearerToken()
    {
        var handler = new RecordingHandler();
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"data":{"Viewer":{"id":9,"name":"jane"}}}"""));
        var client = Create(handler, "secret-token");

        var viewer = await client.GetViewerAsync(CancellationToken.None);

        Assert.Equal(9, viewer.Id);
        Assert.Equal("jane", viewer.Name);
        Assert.Equal("Bearer secret-token", handler.Requests[0].Authorization);
        Assert.Contains("Viewer", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProgressMutationUsesIndexAndStatus()
    {
        var handler = new RecordingHandler();
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"data":{"SaveMediaListEntry":{"id":4,"status":"COMPLETED","progress":12}}}"""));
        var client = Create(handler, "tok");

        var saved = await client.SaveProgressAsync(20958, 12, "COMPLETED", CancellationToken.None);

        Assert.Equal(12, saved.Progress);
        Assert.Equal("COMPLETED", saved.Status);
        Assert.Contains("\"mediaId\":20958", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"progress\":12", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"COMPLETED\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("score", handler.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", handler.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMediaReadsExistingProgress()
    {
        var handler = new RecordingHandler();
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"data":{"Media":{"id":1,"episodes":12,"format":"TV","mediaListEntry":{"id":8,"status":"CURRENT","progress":3}}}}"""));
        var client = Create(handler, "tok");

        var media = await client.GetMediaAsync(1, CancellationToken.None);

        Assert.Equal(12, media.Episodes);
        Assert.Equal(3, media.MediaListEntry!.Progress);
        Assert.Equal("CURRENT", media.MediaListEntry.Status);
    }

    [Fact]
    public async Task GraphQlErrorsBecomeRequestExceptions()
    {
        var handler = new RecordingHandler();
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"Invalid token","status":401}]}"""));
        var client = Create(handler, "bad");

        var exception = await Assert.ThrowsAsync<AniListRequestException>(() => client.GetViewerAsync(CancellationToken.None));
        Assert.Equal(401, exception.StatusCode);
        Assert.True(exception.IsUnauthorized);
    }

    [Fact]
    public async Task RateLimitUsesRetryAfter()
    {
        var handler = new RecordingHandler();
        handler.Responses.Enqueue(RecordingHandler.JsonResponse(HttpStatusCode.TooManyRequests, """{"errors":[{"message":"Too Many Requests.","status":429}]}""", new Dictionary<string, string> { ["Retry-After"] = "30" }));
        var client = Create(handler, "tok");

        var exception = await Assert.ThrowsAsync<AniListRequestException>(() => client.GetMediaAsync(1, CancellationToken.None));
        Assert.Equal(429, exception.StatusCode);
        Assert.True(exception.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
    }

    [Fact]
    public async Task MissingTokenFailsWithoutSending()
    {
        var handler = new RecordingHandler();
        var client = Create(handler, accessToken: null);

        var exception = await Assert.ThrowsAsync<AniListRequestException>(() => client.GetViewerAsync(CancellationToken.None));
        Assert.Equal(401, exception.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AuthorizeUrlUsesImplicitGrant()
    {
        var url = AniListClient.AuthorizeUrl("abc123");
        Assert.StartsWith("https://anilist.co/api/v2/oauth/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=abc123", url, StringComparison.Ordinal);
        Assert.Contains("response_type=token", url, StringComparison.Ordinal);
    }

    private static AniListClient Create(RecordingHandler handler, string? accessToken)
        => new(new HttpClient(handler) { BaseAddress = new Uri(AniListClient.GraphQlEndpoint) }, new AniListClientOptions
        {
            AccessToken = accessToken,
            RequestTimeout = TimeSpan.FromSeconds(5),
        });
}
