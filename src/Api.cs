using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Shoko.AniListScrobble;

public sealed record CapabilityDto(string Name, bool Enabled, string? Detail = null);
public sealed record StatusDto(
    string ApiVersion,
    string PluginVersion,
    string MinimumShokoAbstractionsVersion,
    bool Enabled,
    bool Connected,
    string? AnilistUsername,
    int? ShokoUserId,
    string? ShokoUsername,
    int ScrobbledCount,
    int Scrobbled,
    int Completed,
    int Skipped,
    int Failed,
    LastScrobble? LastScrobble,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    IReadOnlyList<CapabilityDto> Capabilities);
public sealed record AuthorizeUrlResponseDto(int ApiVersion, string Url, string PinRedirect);
public sealed record TokenConnectRequestDto(int ApiVersion = 1, string? AccessToken = null);
public sealed record TokenConnectResponseDto(int ApiVersion, string? Username, int? UserId);

[ApiController]
[ApiVersion(3.0)]
[Authorize(Roles = "admin")]
[Route("/api/v{version:apiVersion}/Plugin/AniListScrobble")]
public sealed class AniListScrobbleController : ControllerBase
{
    private readonly IAniListScrobbleService _service;

    public AniListScrobbleController(IAniListScrobbleService service) => _service = service;

    [AllowAnonymous]
    [HttpGet("ui")]
    [Produces("text/html")]
    public ActionResult GetUiPage() => GetUiResource("Shoko.AniListScrobble.Ui.anilist-scrobble.html", "text/html; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("ui/style.css")]
    [Produces("text/css")]
    public ActionResult GetUiStyles() => GetUiResource("Shoko.AniListScrobble.Ui.anilist-scrobble.css", "text/css; charset=utf-8");

    [AllowAnonymous]
    [HttpGet("ui/script.js")]
    [Produces("text/javascript")]
    public ActionResult GetUiScript() => GetUiResource("Shoko.AniListScrobble.Ui.anilist-scrobble.js", "text/javascript; charset=utf-8");

    [HttpGet("status")]
    public ActionResult<StatusDto> GetStatus() => Ok(ToStatus(_service.GetStatus()));

    [HttpGet("auth/authorize-url")]
    public ActionResult<AuthorizeUrlResponseDto> GetAuthorizeUrl()
    {
        try
        {
            return Ok(new AuthorizeUrlResponseDto(1, _service.GetAuthorizeUrl(), AniListClient.PinRedirect));
        }
        catch (InvalidOperationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unable to build authorize URL", detail: exception.Message);
        }
    }

    [HttpPost("auth/token")]
    public async Task<ActionResult<TokenConnectResponseDto>> ConnectToken([FromBody] TokenConnectRequestDto request, CancellationToken cancellationToken)
    {
        if (request.ApiVersion != 1)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported API version", detail: "Use apiVersion 1.");
        try
        {
            var result = await _service.ConnectTokenAsync(request.AccessToken ?? "", cancellationToken).ConfigureAwait(false);
            return Ok(new TokenConnectResponseDto(1, result.Username, result.UserId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: StatusCodes.Status499ClientClosedRequest, title: "Request cancelled");
        }
        catch (ArgumentException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid token", detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unable to connect", detail: exception.Message);
        }
        catch (AniListRequestException exception)
        {
            return Problem(statusCode: exception.StatusCode is >= 400 and < 600 ? exception.StatusCode : StatusCodes.Status502BadGateway, title: "AniList token check failed", detail: exception.Message);
        }
    }

    [HttpPost("auth/disconnect")]
    public async Task<ActionResult<StatusDto>> Disconnect(CancellationToken cancellationToken)
    {
        await _service.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return Ok(ToStatus(_service.GetStatus()));
    }

    private ActionResult GetUiResource(string resourceName, string contentType)
    {
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; base-uri 'none'; connect-src 'self'; frame-ancestors 'self'; object-src 'none'; script-src 'self'; style-src 'self'; form-action 'none'";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";
        using var stream = typeof(AniListScrobblePlugin).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return NotFound("The AniList Scrobble UI resource was not found.");
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), contentType);
    }

    private static StatusDto ToStatus(StatusSnapshot status)
        => new(
            status.ApiVersion,
            status.PluginVersion,
            "6.0.0-alpha.77",
            status.Enabled,
            status.Connected,
            status.AnilistUsername,
            status.ShokoUserId,
            status.ShokoUsername,
            status.ScrobbledCount,
            status.Counters.Scrobbled,
            status.Counters.Completed,
            status.Counters.Skipped,
            status.Counters.Failed,
            status.LastScrobble,
            status.LastError,
            status.LastErrorAt,
            status.Capabilities);
}
