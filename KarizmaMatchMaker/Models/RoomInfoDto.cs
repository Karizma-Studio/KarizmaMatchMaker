using KarizmaPlatform.MatchMaker.Interfaces;

namespace KarizmaPlatform.MatchMaker.Models;

public class RoomInfoDto<TPlayer, TLabel>
    where TPlayer : IMatchMakingPlayer
    where TLabel : IMatchMakingLabel
{
    public string RoomCode { get; init; }
    public TPlayer HostPlayer { get; init; }
    public TLabel? MatchLabel { get; init; }
}