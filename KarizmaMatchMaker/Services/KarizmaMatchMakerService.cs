using System.Collections.Concurrent;
using KarizmaPlatform.MatchMaker.Configurations;
using KarizmaPlatform.MatchMaker.Events;
using KarizmaPlatform.MatchMaker.Interfaces;
using KarizmaPlatform.MatchMaker.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace KarizmaPlatform.MatchMaker.Services;

public class KarizmaMatchMakerService<TPlayer, TLabel>(
    IOptions<MatchmakerOptions> options,
    MatchmakerEvents<TPlayer, TLabel> events)
    : BackgroundService
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    private readonly ConcurrentQueue<PlayerQueueInfo<TPlayer, TLabel>> _queue = new();
    private readonly ConcurrentDictionary<string, RoomInfo<TPlayer, TLabel>> _rooms = new();
    private readonly ConcurrentDictionary<string, string> _playerRooms = new();
    private readonly MatchmakerOptions _options = options.Value;

    // A SemaphoreSlim for async locking around queue operations.
    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);

    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(1);
    private readonly Random _random = new();

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

        events.OnJoinedMatchmaking(player, label);
    }

    /// <summary>
    /// Remove a player from the queue (if present).
    /// </summary>
    public async Task<TPlayer?> RemoveFromQueueAsync(string playerId)
    {
        await _queueSemaphore.WaitAsync();
        try
        {
            return RemoveFromQueueInternal(playerId);
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    /// <summary>
    /// Leave matchmaking entirely.
    /// </summary>
    public async Task<TPlayer?> LeaveMatchmakingAsync(string playerId, TLabel label)
    {
        await _queueSemaphore.WaitAsync();
        TPlayer? player;
        try
        {
            player = RemoveFromQueueInternal(playerId);
        }
        finally
        {
            _queueSemaphore.Release();
        }

        if(player != null)
        {
            events.OnPlayerLeftMatchmaking(player, label);
        }
        
        return player;
    }

    /// <summary>
    /// Create a room with a random code and optional TLabel for the match.
    /// Rooms are not processed in the main queue, so they're only join-able by code.
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

        events.OnJoinedRoom(hostPlayer, roomCode);
        return roomCode;
    }

    /// <summary>
    /// Retrieves all available rooms and returns their DTO representations.
    /// </summary>
    public IEnumerable<RoomInfoDto<TPlayer, TLabel>> GetAllAvailableRooms()
    {
        return _rooms.Values.Select(room => room.GetDto());
    }

    /// <summary>
    /// Join a room by code.
    /// </summary>
    public async Task<bool> JoinRoomAsync(TPlayer player, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return false;

        await room.LockAsync();

        try
        {
            if (!room.AddPlayer(player)) return false;

            events.OnJoinedRoom(player, roomCode);
            _playerRooms[player.GetPlayerId()] = roomCode;
            return true;
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Kick a player from a room (only if the caller is host).
    /// </summary>
    public async Task KickFromRoomAsync(string hostPlayerId, string targetPlayerId, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayerId))
            throw new InvalidOperationException("Only the host can kick players.");

        await room.LockAsync();

        try
        {
            if (room.RemovePlayer(targetPlayerId))
            {
                var targetPlayer = room.GetPlayer(targetPlayerId);
                events.OnKickedFromRoom(targetPlayer, roomCode);
                _playerRooms.TryRemove(targetPlayer.GetPlayerId(), out _);
            }
        }
        finally
        {
            room.Unlock();
        }
    }

    /// <summary>
    /// Get a room data by room's id.
    /// </summary>
    public RoomInfoDto<TPlayer, TLabel>? GetRoomById(string roomId)
    {
        return _rooms.TryGetValue(roomId, out var room) ? room.GetDto() : null;
    }


    /// <summary>
    /// Leave a room (remove player from the room).
    /// </summary>
    public async Task LeaveRoomAsync(string playerId, string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");
        
        await room.LockAsync();
        try
        {
            if(room.HostPlayer.GetPlayerId() == playerId)
            {
                try
                {
                    var playersInRoom = room.GetPlayers().ToList();
                    foreach (var p in playersInRoom)
                    {
                        _playerRooms.TryRemove(p.GetPlayerId(), out _);
                    }

                    _rooms.TryRemove(roomCode, out _);
                    events.OnRoomDestroyed(roomCode);
                }
                finally
                {
                    room.Unlock();
                }

                return;
            }
            var player = room.GetPlayer(playerId);
            if (room.RemovePlayer(playerId))
            {
                events.OnPlayerLeftRoom(player, roomCode);
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
    public async Task StartRoomAsync(string hostPlayerId, string roomCode, bool force = false)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        await room.LockAsync();

        try
        {
            if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayerId))
                throw new InvalidOperationException("Only the host can start the room.");

            var playersInRoom = room.GetPlayers().ToList();
            if (room.MatchLabel != null && !force && playersInRoom.Count != room.MatchLabel.GetMatchPlayersSize())
            {
                throw new InvalidOperationException("Not enough players to start the match.");
            }

            events.OnMatchFound(playersInRoom, room.MatchLabel);

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
    public async Task UpdateRoomLabelAsync(string hostPlayerId, string roomCode, TLabel newLabel)
    {
        if (!_rooms.TryGetValue(roomCode, out var room))
            throw new InvalidOperationException("Room not found.");

        if (!Equals(room.HostPlayer.GetPlayerId(), hostPlayerId))
            throw new InvalidOperationException("Only the host can update the room label.");

        await room.LockAsync();
        try
        {
            room.UpdateMatchLabel(newLabel);

            events.OnLabelUpdated(roomCode, newLabel);
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
    public string? GetRoomCodeByPlayerId(string playerId)
    {
        if (_playerRooms.TryGetValue(playerId, out var roomCode) && _rooms.TryGetValue(roomCode, out var room))
        {
            return room.RoomCode;
        }

        return null;
    }

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
                                    RemoveFromQueueInternal(m.Player.GetPlayerId());
                                }

                                readyPlayers.Clear();

                                events.OnMatchFound(
                                    matched.Select(m => m.Player).ToList(),
                                    firstPlayerQueueInfo.Label
                                );
                            }
                            else
                            {
                                RemoveFromQueueInternal(
                                    firstPlayerQueueInfo.Player.GetPlayerId()
                                );
                                readyPlayers.RemoveAt(0);

                                events.OnMatchNotFound(
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
                            RemoveFromQueueInternal(m.Player.GetPlayerId());
                        }

                        var labelForEvent = matched[0].Label;
                        events.OnMatchFound(
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
    /// Remove a player from the queue by player ID.
    /// caller must hold the semaphore.
    /// </summary>
    private TPlayer? RemoveFromQueueInternal(string playerId)
    {
        var tempQueue = new ConcurrentQueue<PlayerQueueInfo<TPlayer, TLabel>>();
        TPlayer? removedPlayer = default;
    
        while (_queue.TryDequeue(out var current))
        {
            if (current.Player.GetPlayerId() == playerId)
            {
                removedPlayer = current.Player;
            }
            else
            {
                tempQueue.Enqueue(current);
            }
        }
    
        while (tempQueue.TryDequeue(out var item))
        {
            _queue.Enqueue(item);
        }
    
        return removedPlayer;
    }

    #endregion
}