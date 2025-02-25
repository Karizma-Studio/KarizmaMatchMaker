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

    private readonly SemaphoreSlim _semaphore = new(1, 1);

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

    public bool RemovePlayer(string playerId)
        => _players.TryRemove(playerId, out _);

    public TPlayer GetPlayer(string playerId) => _players[playerId];
    
    public bool PlayerExists(string playerId) => _players.ContainsKey(playerId);

    public void UpdateMatchLabel(TLabel newLabel)
    {
        MatchLabel = newLabel;
    }

    public async Task LockAsync()
    {
        await _semaphore.WaitAsync();
    }

    public void Unlock()
    {
        _semaphore.Release();
    }

    public RoomInfoDto<TPlayer, TLabel> GetDto()
    {
        return new RoomInfoDto<TPlayer, TLabel>
        {
            RoomCode = RoomCode,
            HostPlayer = HostPlayer,
            MatchLabel = MatchLabel
        };
    }
}