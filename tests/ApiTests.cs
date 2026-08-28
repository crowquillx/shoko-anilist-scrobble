using System.Reflection;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class ApiTests
{
    [Fact]
    public void ControllerHasPublicVersionedAdminOnlyMetadata()
    {
        var controllerType = typeof(AniListScrobbleController);
        var route = Assert.Single(controllerType.GetCustomAttributes<RouteAttribute>());
        var apiVersion = Assert.Single(controllerType.GetCustomAttributes<ApiVersionAttribute>());
        var authorization = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal("/api/v{version:apiVersion}/Plugin/AniListScrobble", route.Template);
        Assert.Equal("3.0", apiVersion.Versions.Single().ToString());
        Assert.Equal("admin", authorization.Roles);
        Assert.DoesNotContain(controllerType.Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "Shoko.Server");
    }

    [Fact]
    public void UiPageIsEmbeddedAndAnonymousWithoutSecrets()
    {
        var plugin = new AniListScrobblePlugin();
        var page = Assert.Single(plugin.GetPages());
        var resourceNames = typeof(AniListScrobblePlugin).Assembly.GetManifestResourceNames();

        Assert.Equal("AniList Scrobble", page.Name);
        Assert.Equal("/api/v3/Plugin/AniListScrobble/ui", page.Url);
        Assert.True(page.CanEmbed);
        Assert.Contains("Shoko.AniListScrobble.Ui.anilist-scrobble.html", resourceNames);
        Assert.Contains("Shoko.AniListScrobble.Ui.anilist-scrobble.css", resourceNames);
        Assert.Contains("Shoko.AniListScrobble.Ui.anilist-scrobble.js", resourceNames);
    }

    [Fact]
    public void StaticUiResourcesAreAnonymousAndDataApisAreAdminOnly()
    {
        var controllerType = typeof(AniListScrobbleController);
        foreach (var methodName in new[] { nameof(AniListScrobbleController.GetUiPage), nameof(AniListScrobbleController.GetUiStyles), nameof(AniListScrobbleController.GetUiScript) })
        {
            var method = controllerType.GetMethod(methodName);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        }

        foreach (var methodName in new[] { nameof(AniListScrobbleController.GetStatus), nameof(AniListScrobbleController.GetAuthorizeUrl), nameof(AniListScrobbleController.ConnectToken), nameof(AniListScrobbleController.Disconnect) })
        {
            var method = controllerType.GetMethod(methodName);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public void UiResourceUsesSafeDomAndARestrictiveCsp()
    {
        const string scriptResource = "Shoko.AniListScrobble.Ui.anilist-scrobble.js";
        using var stream = typeof(AniListScrobblePlugin).Assembly.GetManifestResourceStream(scriptResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.Contains("sessionStorage", script, StringComparison.Ordinal);
        Assert.Contains("localStorage", script, StringComparison.Ordinal);
        Assert.Contains("headers.set('apikey'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("field.value = state.secrets", script, StringComparison.Ordinal);

        var controller = CreateController(new RecordingScrobbleService());
        var result = Assert.IsType<ContentResult>(controller.GetUiPage());
        Assert.Contains("frame-ancestors 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", result.Content!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-token", result.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("export", result.Content!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThereIsNoExportEndpoint()
    {
        var methods = typeof(AniListScrobbleController).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name.Contains("Export", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, method => method.GetCustomAttribute<HttpPostAttribute>()?.Template?.Contains("export", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ConnectTokenRejectsUnsupportedApiVersion()
    {
        var controller = CreateController(new RecordingScrobbleService());
        var result = await controller.ConnectToken(new TokenConnectRequestDto(ApiVersion: 2, AccessToken: "x"), CancellationToken.None);
        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public void StatusDtoDoesNotDeclareSecrets()
    {
        var names = typeof(StatusDto).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("AccessToken", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClientId", names, StringComparer.OrdinalIgnoreCase);
    }

    private static AniListScrobbleController CreateController(IAniListScrobbleService service)
        => new(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private sealed class RecordingScrobbleService : IAniListScrobbleService
    {
        public StatusSnapshot GetStatus()
            => new("1", "1.0.0", true, false, null, 1, "admin", new ScrobbleCounters(), 0, null, null, null, []);

        public string GetAuthorizeUrl() => "https://anilist.co/api/v2/oauth/authorize?client_id=abc&response_type=token";

        public Task<TokenConnectResult> ConnectTokenAsync(string accessToken, CancellationToken cancellationToken)
            => Task.FromResult(new TokenConnectResult("jane", 9));

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandleEventAsync(Shoko.Abstractions.User.Events.EpisodeUserDataSavedEventArgs args, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
