using KarizmaPlatform.MatchMaker.Interfaces;

namespace KarizmaPlatform.MatchMaker.Models;

internal class PlayerQueueInfo<TPlayer, TLabel>(TPlayer player, TLabel label)
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    public TPlayer Player { get; } = player;
    public TLabel Label { get; } = label;
    public DateTime EnqueuedTime { get; } = DateTime.UtcNow;
}