namespace KarizmaPlatform.MatchMaker.Configurations;

public class MatchmakerOptions
{
    /// <summary>
    /// Minimum length a player should wait.
    /// </summary>
    public TimeSpan MinimumWaitTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum length a player should wait.
    /// </summary>
    public TimeSpan MaximumWaitTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shuffle players to prevent match abuse.
    /// </summary>
    public bool ShufflePlayers { get; set; } = true;

    /// <summary>
    /// If no match found and maximum wait is exceeded, can we add bots or not?
    /// </summary>
    public bool EnableBotMatchmaking { get; set; } = true;
}