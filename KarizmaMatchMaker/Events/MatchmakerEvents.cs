using KarizmaPlatform.MatchMaker.Interfaces;

namespace KarizmaPlatform.MatchMaker.Events;

public class MatchmakerEvents<TPlayer, TLabel>
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    /// <summary>
    /// Raised when a player joins matchmaking (player, label).
    /// </summary>
    public event Action<TPlayer, TLabel>? JoinedMatchmaking;

    /// <summary>
    /// Raised when a set of players is successfully matched (players, label).
    /// </summary>
    public event Action<List<TPlayer>, TLabel>? MatchFound;

    /// <summary>
    /// Raised when a player times out (exceeds maximum wait time) and no bots are enabled.
    /// </summary>
    public event Action<TPlayer, TLabel>? MatchNotFound;

    /// <summary>
    /// Raised when a player is kicked from a room (player, roomCode).
    /// </summary>
    public event Action<TPlayer, string>? KickedFromRoom;

    /// <summary>
    /// Raised when a player leaves a room (player, roomCode).
    /// </summary>
    public event Action<TPlayer, string>? LeftFromRoom;

    /// <summary>
    /// Raised when a player joins or creates a room (player, roomCode).
    /// </summary>
    public event Action<TPlayer, string>? JoinedRoom;

    /// <summary>
    /// Raised when a player leaves matchmaking (player, label).
    /// </summary>
    public event Action<TPlayer, TLabel>? LeftMatchmaking;
    
    /// <summary>
    /// Raised when a label is updated (code, label).
    /// </summary>
    public event Action<string, TLabel>? LabelUpdated;
    
    public void OnJoinedMatchmaking(TPlayer player, TLabel label)
        => JoinedMatchmaking?.Invoke(player, label);

    public void OnMatchFound(List<TPlayer> players, TLabel label)
        => MatchFound?.Invoke(players, label);

    public void OnMatchNotFound(TPlayer player, TLabel label)
        => MatchNotFound?.Invoke(player, label);

    public void OnKickedFromRoom(TPlayer player, string roomCode)
        => KickedFromRoom?.Invoke(player, roomCode);

    public void OnJoinedRoom(TPlayer player, string roomCode)
        => JoinedRoom?.Invoke(player, roomCode);

    public void OnPlayerLeftRoom(TPlayer player, string roomCode)
        => LeftFromRoom?.Invoke(player, roomCode);

    public void OnPlayerLeftMatchmaking(TPlayer player, TLabel label)
        => LeftMatchmaking?.Invoke(player, label);
    
    public void OnLabelUpdated(string code, TLabel label)
        => LabelUpdated?.Invoke(code, label);
}