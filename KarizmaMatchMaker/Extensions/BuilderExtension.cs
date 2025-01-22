using KarizmaPlatform.MatchMaker.Configurations;
using KarizmaPlatform.MatchMaker.Events;
using KarizmaPlatform.MatchMaker.Interfaces;
using KarizmaPlatform.MatchMaker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KarizmaPlatform.MatchMaker.Extensions;

public static class BuilderExtension
{
    /// <summary>
    /// Extension method to register all the services needed for matchmaking in DI.
    /// </summary>
    /// <typeparam name="TPlayer">Type that implements IMatchMakingPlayer interface.</typeparam>
    /// <typeparam name="TLabel">Type that implements IMatchMakingLabel interface.</typeparam>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <param name="configureOptions">Optional action to configure the MatchmakerOptions.</param>
    /// <returns>The original IServiceCollection for chaining.</returns>
    public static IServiceCollection AddMatchMaker<TPlayer, TLabel>(
        this IServiceCollection services,
        Action<MatchmakerOptions>? configureOptions = null
    )
        where TPlayer : IMatchMakingPlayer
        where TLabel : IMatchMakingLabel
    {
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.Configure<MatchmakerOptions>(opts =>
            {
                opts.MinimumWaitTime = TimeSpan.FromSeconds(5);
                opts.MaximumWaitTime = TimeSpan.FromSeconds(30);
                opts.EnableBotMatchmaking = true;
                opts.ShufflePlayers = true;
            });
        }

        // Add the event aggregator
        services.AddSingleton<MatchmakerEvents<TPlayer, TLabel>>();

        // Add the hosted matchmaking service
        services.AddSingleton<KarizmaMatchMakerService<TPlayer, TLabel>>();
        services.AddHostedService(provider => provider.GetRequiredService<KarizmaMatchMakerService<TPlayer, TLabel>>());

        return services;
    }
}