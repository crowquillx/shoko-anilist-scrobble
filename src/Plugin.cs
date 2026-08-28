using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using Shoko.Abstractions.User.Events;
using Shoko.Abstractions.User.Services;

namespace Shoko.AniListScrobble;

public sealed class AniListScrobblePlugin : IPlugin, IPluginServiceRegistration, IPluginApplicationRegistration
{
    public Guid ID => Guid.Parse("a3e8c1f0-4b7d-4e9a-9c2f-6d1b8a5e3f70");
    public string Name => "Shoko AniList Scrobble";
    public string? Description => "Scrobble Jellyfin-originated episode watches from Shoko to AniList. Not a history sync.";

    public IReadOnlyList<PluginPage> GetPages() =>
    [
        new PluginPage
        {
            Name = "AniList Scrobble",
            Url = "/api/v3/Plugin/AniListScrobble/ui",
            CanEmbed = true,
        },
    ];

    public static void RegisterServices(IServiceCollection services, IApplicationPaths applicationPaths)
    {
        services.AddOptions<AniListScrobbleOptions>().ValidateDataAnnotations();
        services.AddSingleton(applicationPaths);
        services.AddSingleton<IPluginStateStore, AtomicPluginStateStore>();
        services.AddSingleton<IArmCatalog, ArmCatalog>();
        services.AddSingleton<IAniListClientFactory, AniListClientFactory>();
        services.AddSingleton<IAniListScrobbleService, AniListScrobbleService>();
        services.AddHttpClient("anilist-scrobble", client =>
        {
            client.BaseAddress = new Uri(AniListClient.GraphQlEndpoint);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient("anilist-scrobble-arm", client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("shoko-anilist-scrobble");
        });
        services.AddHostedService<WatchHostedService>();
    }

    public static void RegisterServices(IApplicationBuilder application, IApplicationPaths applicationPaths)
    {
        var configurationService = application.ApplicationServices.GetRequiredService<IConfigurationService>();
        configurationService.AddParts([typeof(AniListScrobbleOptions)]);
        var provider = configurationService.CreateProvider<AniListScrobbleOptions>();
        var options = provider.Load();
        application.ApplicationServices.GetRequiredService<IOptions<AniListScrobbleOptions>>().Value.CopyFrom(options);
    }
}

public sealed class WatchHostedService : BackgroundService
{
    private readonly IAniListScrobbleService _scrobble;
    private readonly IUserDataService _userData;
    private readonly IArmCatalog _arm;
    private readonly IOptions<AniListScrobbleOptions> _options;
    private readonly ILogger<WatchHostedService> _logger;
    private readonly Channel<EpisodeUserDataSavedEventArgs> _queue = Channel.CreateUnbounded<EpisodeUserDataSavedEventArgs>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public WatchHostedService(
        IAniListScrobbleService scrobble,
        IUserDataService userData,
        IArmCatalog arm,
        IOptions<AniListScrobbleOptions> options,
        ILogger<WatchHostedService> logger)
    {
        _scrobble = scrobble;
        _userData = userData;
        _arm = arm;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
            return;

        try
        {
            await _arm.EnsureLoadedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "ARM catalog could not be loaded at startup.");
        }

        _userData.EpisodeUserDataSaved += OnEpisodeUserDataSaved;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _scrobble.HandleEventAsync(item, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Queued AniList scrobble failed.");
                }
            }
        }
        finally
        {
            _userData.EpisodeUserDataSaved -= OnEpisodeUserDataSaved;
        }
    }

    private void OnEpisodeUserDataSaved(object? sender, EpisodeUserDataSavedEventArgs args)
    {
        try
        {
            _queue.Writer.TryWrite(args);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to queue an AniList scrobble event.");
        }
    }
}
