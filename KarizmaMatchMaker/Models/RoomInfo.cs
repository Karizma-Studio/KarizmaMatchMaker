using System.Collections.Concurrent;
using KarizmaPlatform.MatchMaker.Interfaces;

namespace KarizmaPlatform.MatchMaker.Models;

internal class RoomInfo<TPlayer, TLabel>
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    public string RoomCode { get; }
    public TPlayer HostPlayer { get; }
    public TLabel? MatchLabel { get; private set; }

    private readonly ConcurrentDictionary<string, TPlayer> _players;

    public RoomInfo(string roomCode, TPlayer hostPlayer, TLabel? label)
    {
        RoomCode = roomCode;
        HostPlayer = hostPlayer;
        MatchLabel = label;
        _players = new ConcurrentDictionary<string, TPlayer>();
        _players.TryAdd(hostPlayer.GetPlayerId(), hostPlayer);
    }

    public IEnumerable<TPlayer> GetPlayers() => _players.Values;

    public bool AddPlayer(TPlayer player)
        => _players.TryAdd(player.GetPlayerId(), player);

    public bool RemovePlayer(TPlayer player)
        => _players.TryRemove(player.GetPlayerId(), out _);

    public void UpdateMatchLabel(TLabel newLabel)
    {
        MatchLabel = newLabel;
    }
}