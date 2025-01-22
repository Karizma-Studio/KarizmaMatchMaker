using System.Collections.Concurrent;
using KarizmaPlatform.MatchMaker.Configurations;
using KarizmaPlatform.MatchMaker.Events;
using KarizmaPlatform.MatchMaker.Interfaces;
using KarizmaPlatform.MatchMaker.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KarizmaPlatform.MatchMaker.Services;

public class KarizmaMatchMakerService<TPlayer, TLabel> : BackgroundService
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    private readonly ConcurrentQueue<PlayerQueueInfo<TPlayer, TLabel>> _queue = new();
    private readonly ConcurrentDictionary<string, RoomInfo<TPlayer, TLabel>> _rooms = new();
    private readonly MatchmakerOptions _options;
    private readonly MatchmakerEvents<TPlayer, TLabel> _events;

    // A SemaphoreSlim for async locking around queue operations.
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);

    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(1);
    private readonly Random _random = new();

    public KarizmaMatchMakerService(
        IOptions<MatchmakerOptions> options,
        MatchmakerEvents<TPlayer, TLabel> events)
    {
        _options = options.Value;
        _events = events;
    }

    #region Public API

    /// <summary>
    /// Async method to join matchmaking queue.
    /// </summary>
    public async Task JoinMatchmakingAsync(TPlayer player, TLabel label)
    {
        await _queueSemaphore.WaitAsync();
        try
        {
            var info = new PlayerQueueInfo<TPlayer, TLabel>(player, label);
            _queue.Enqueue(info);
        }
        finally
        {
            _queueSemaphore.Release();
        }

        _events.OnJoinedMatchmaking(player, label);
    }

    /// <summary>
    /// Remove a player from the queue (if present).
    /// </summary>
    public async Task RemoveFromQueueAsync(TPlayer player, TLabel label)
    {
        await _queueSemaphore.WaitAsync();
        try
        {
            RemoveFromQueueInternal(player, label);
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    /// <summary>
    /// Leave matchmaking entirely.
    /// </summary>
    public async Task LeaveMatchmakingAsync(TPlayer player, TLabel label)
    {
        await _queueSemaphore.WaitAsync();
        try
        {
            // Quick check; if not present, do nothing
            if (!_queue.Any(q => Equals(q.Player.GetPlayerId(), player.GetPlayerId())))
                return;

            RemoveFromQueueInternal(player, label);
        }
        finally
        {
            _queueSemaphore.Release();
        }

        _events.OnPlayerLeftMatchmaking(player, label);
    }

    /// <summary>
    /// Create a room with a random code and optional TLabel for the match.
    /// Rooms are not processed in the main queue, so they're only joinable by code.
    /// </summary>
    public string CreateRoomAsync(TPlayer hostPlayer, TLabel? matchLabel = default)
    {
        // Generate unique code
        string roomCode;
        do
        {
            roomCode = _random.Next(0, 999999).ToString("D6");
        }
        while (_rooms.ContainsKey(roomCode));

        var roomInfo = new RoomInfo<TPlayer, TLabel>(roomCode, hostPlayer, matchLabel);
        _rooms[roomCode] = roomInfo;

        _events.OnJoinedRoom(hostPlayer, roomCode);
        return roomCode;
    }

    /// <summary>
    /// Kick a player from a room (only if the caller is host).
    /// </summary>
    public Task KickFromRoomAsync(TPlayer hostPlayer, TPlayer targetPlayer, string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            if (Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
            {
                if (room.RemovePlayer(targetPlayer))
                {
                    _events.OnKickedFromRoom(targetPlayer, roomCode);
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Leave a room (remove player from the room).
    /// </summary>
    public Task LeaveRoomAsync(TPlayer player, string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            if (room.RemovePlayer(player))
            {
                _events.OnPlayerLeftRoom(player, roomCode);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start the match in the given room (only by host).
    /// Removes the room afterward.
    /// </summary>
    public Task StartRoomAsync(TPlayer hostPlayer, string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            if (Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
            {
                var playersInRoom = room.GetPlayers().ToList();
                // Use the room's label if it exists
                var label = room.MatchLabel;

                _events.OnMatchFound(playersInRoom, label!);

                _rooms.TryRemove(roomCode, out _);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Update the room's match label (only the host can do this).
    /// </summary>
    public Task UpdateRoomLabelAsync(TPlayer hostPlayer, string roomCode, TLabel newLabel)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            if (Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
            {
                room.UpdateMatchLabel(newLabel);
                
                _events.OnLabelUpdated(roomCode, newLabel);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get all users currently in the matchmaking queue (optionally filtered by label).
    /// </summary>
    public async Task<IEnumerable<TPlayer>> GetAllQueuedPlayersAsync(TLabel? label = default)
    {
        await _queueSemaphore.WaitAsync();
        try
        {
            if (Equals(label, default(TLabel)) || label == null)
                return _queue.Select(q => q.Player).ToList();

            var labelId = label.GetIdentifier();
            return _queue
                .Where(q => q.Label.GetIdentifier() == labelId)
                .Select(q => q.Player)
                .ToList();
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    /// <summary>
    /// Get all players in a specific room by code.
    /// </summary>
    public Task<IEnumerable<TPlayer>> GetRoomPlayersAsync(string roomCode)
    {
        if (_rooms.TryGetValue(roomCode, out var room))
        {
            return Task.FromResult(room.GetPlayers());
        }
        return Task.FromResult(Enumerable.Empty<TPlayer>());
    }

    #endregion

    #region BackgroundService Loop

    /// <summary>
    /// Main loop that periodically checks the matchmaking queue in an async manner.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessMatchmakingQueueAsync();
            }
            catch (Exception)
            {
                // TODO: handle or log
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task ProcessMatchmakingQueueAsync()
    {
        // We only do matching for the queue (NOT the manually created rooms).
        await _queueSemaphore.WaitAsync();
        try
        {
            if (_queue.IsEmpty) return;

            var items = _queue.ToList(); // snapshot
            if (items.Count == 0) return;

            // Group by label's identifier
            var groupedByLabelId = items.GroupBy(i => i.Label.GetIdentifier());

            foreach (var group in groupedByLabelId)
            {
                var groupItems = group.ToList();
                var labelForEvent = groupItems.First().Label;

                if (_options.ShufflePlayers)
                {
                    // Shuffle
                    groupItems = groupItems.OrderBy(_ => _random.Next()).ToList();
                }

                foreach (var item in groupItems)
                {
                    var waitTime = DateTime.UtcNow - item.EnqueuedTime;
                    if (waitTime <= _options.MinimumWaitTime) 
                        continue;

                    // Attempt to gather 'matchSize'
                    var matchSize = item.Label.GetMatchPlayersSize();
                    var matched = groupItems.Take(matchSize).ToList();

                    if (matched.Count == matchSize)
                    {
                        // Found enough players
                        foreach (var m in matched)
                            RemoveFromQueueInternal(m.Player, m.Label);

                        _events.OnMatchFound(
                            matched.Select(m => m.Player).ToList(),
                            labelForEvent
                        );
                    }
                    else
                    {
                        // Not enough players to fill a match
                        if (waitTime <= _options.MaximumWaitTime)
                            continue;

                        // Check if we allow bots
                        if (_options.EnableBotMatchmaking)
                        {
                            // Fill with bots or placeholders
                            foreach (var m in matched)
                                RemoveFromQueueInternal(m.Player, m.Label);

                            _events.OnMatchFound(
                                matched.Select(m => m.Player).ToList(),
                                labelForEvent
                            );
                        }
                        else
                        {
                            // No bots => remove from queue, raise "not found"
                            RemoveFromQueueInternal(item.Player, item.Label);
                            _events.OnMatchNotFound(item.Player, labelForEvent);
                        }
                    }
                }
            }
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Internal method to remove (player, label) from queue; 
    /// caller must hold the semaphore.
    /// </summary>
    private void RemoveFromQueueInternal(TPlayer player, TLabel label)
    {
        var tempList = new List<PlayerQueueInfo<TPlayer, TLabel>>();

        while (_queue.TryDequeue(out var current))
        {
            if (!Equals(current.Player, player) || !Equals(current.Label, label))
            {
                tempList.Add(current);
            }
        }

        foreach (var item in tempList)
        {
            _queue.Enqueue(item);
        }
    }

    #endregion
}