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
    private readonly ConcurrentDictionary<string, string> _playerRooms = new();
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
    public string CreateRoom(TPlayer hostPlayer, TLabel? matchLabel = default)
    {
        // Generate unique code
        string roomCode;
        do
        {
            roomCode = _random.Next(0, 999999).ToString("D6");
        } while (_rooms.ContainsKey(roomCode));

        var roomInfo = new RoomInfo<TPlayer, TLabel>(roomCode, hostPlayer, matchLabel);
        _rooms[roomCode] = roomInfo;
        _playerRooms[hostPlayer.GetPlayerId()] = roomCode;
        
        _events.OnJoinedRoom(hostPlayer, roomCode);
        return roomCode;
    }

    /// <summary>
    /// Join a room by code.
    /// </summary>
    public async Task JoinRoomAsync(TPlayer player, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return;

        await room.LockAsync();

        try
        {
            if (room.AddPlayer(player))
            {
                _events.OnJoinedRoom(player, roomCode);
                _playerRooms[player.GetPlayerId()] = roomCode;
            }
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Kick a player from a room (only if the caller is host).
    /// </summary>
    public async Task KickFromRoomAsync(TPlayer hostPlayer, TPlayer targetPlayer, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
            throw new InvalidOperationException("Only the host can kick players.");

        await room.LockAsync();

        try
        {
            if (room.RemovePlayer(targetPlayer))
            {
                _events.OnKickedFromRoom(targetPlayer, roomCode);
                _playerRooms.TryRemove(targetPlayer.GetPlayerId(), out _);
            }
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Leave a room (remove player from the room).
    /// </summary>
    public async Task LeaveRoomAsync(TPlayer player, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        await room.LockAsync();

        try
        {
            if (room.RemovePlayer(player))
            {
                _events.OnPlayerLeftRoom(player, roomCode);
                _playerRooms.TryRemove(player.GetPlayerId(), out _);
            }
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Start the match in the given room (only by host).
    /// Removes the room afterward.
    /// </summary>
    public async Task StartRoomAsync(TPlayer hostPlayer, string roomCode, bool force = false)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        await room.LockAsync();

        try
        {
            if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
                throw new InvalidOperationException("Only the host can start the room.");

            var playersInRoom = room.GetPlayers().ToList();
            if (room.MatchLabel != null && !force && playersInRoom.Count != room.MatchLabel.GetMatchPlayersSize())
            {
                throw new InvalidOperationException("Not enough players to start the match.");
            }

            _events.OnMatchFound(playersInRoom, room.MatchLabel);

            foreach (var player in playersInRoom)
            {
                _playerRooms.TryRemove(player.GetPlayerId(), out _);
            }
            
            _rooms.TryRemove(roomCode, out _);
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Update the room's match label (only the host can do this).
    /// </summary>
    public async Task UpdateRoomLabelAsync(TPlayer hostPlayer, string roomCode, TLabel newLabel)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayer.GetPlayerId()))
            throw new InvalidOperationException("Only the host can update the room label.");

        await room.LockAsync();
        try
        {
            room.UpdateMatchLabel(newLabel);

            _events.OnLabelUpdated(roomCode, newLabel);
        }
        finally
        {
            room.Unlock();
        }
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
    /// Get user's current room by player ID.
    /// </summary>

    /// <summary>
    /// Get all players in a specific room by code.
    /// </summary>
    public IEnumerable<TPlayer> GetRoomPlayersAsync(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        return room.GetPlayers();
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
        await _queueSemaphore.WaitAsync();
        try
        {
            if (_queue.IsEmpty) return;

            var items = _queue.ToList(); // Snapshot of the queue
            if (items.Count == 0) return;

            // Group by label's identifier
            var groupedByLabelId = items.GroupBy(i => i.Label.GetIdentifier());

            foreach (var group in groupedByLabelId)
            {
                var groupItems = group.ToList();

                if (_options.ShufflePlayers)
                {
                    groupItems = groupItems.OrderBy(_ => _random.Next()).ToList();
                }

                var readyPlayers = groupItems
                    .Where(i => (DateTime.UtcNow - i.EnqueuedTime) >= _options.MinimumWaitTime)
                    .ToList();

                while (readyPlayers.Count > 0)
                {
                    var matchSize = readyPlayers[0].Label.GetMatchPlayersSize();
                    var firstPlayerQueueInfo = readyPlayers[0];
                    var waitTime = DateTime.UtcNow - firstPlayerQueueInfo.EnqueuedTime;

                    if (readyPlayers.Count < matchSize)
                    {
                        // Not enough players to fill a match
                        // Check if we've exceeded maximum wait time
                        if (waitTime >= _options.MaximumWaitTime)
                        {
                            if (_options.EnableBotMatchmaking)
                            {
                                var matched = readyPlayers.ToList();
                                foreach (var m in matched)
                                {
                                    RemoveFromQueueInternal(m.Player, m.Label);
                                }

                                readyPlayers.Clear();

                                _events.OnMatchFound(
                                    matched.Select(m => m.Player).ToList(),
                                    firstPlayerQueueInfo.Label
                                );
                            }
                            else
                            {
                                RemoveFromQueueInternal(
                                    firstPlayerQueueInfo.Player,
                                    firstPlayerQueueInfo.Label
                                );
                                readyPlayers.RemoveAt(0);

                                _events.OnMatchNotFound(
                                    firstPlayerQueueInfo.Player,
                                    firstPlayerQueueInfo.Label
                                );
                            }
                        }

                        break;
                    }
                    else
                    {
                        var matched = readyPlayers.Take(matchSize).ToList();
                        readyPlayers.RemoveRange(0, matchSize);

                        // Remove them from the actual queue
                        foreach (var m in matched)
                        {
                            RemoveFromQueueInternal(m.Player, m.Label);
                        }

                        var labelForEvent = matched[0].Label;
                        _events.OnMatchFound(
                            matched.Select(m => m.Player).ToList(),
                            labelForEvent
                        );
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