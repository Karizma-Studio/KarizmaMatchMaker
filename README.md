# KarizmaPlatform.MatchMaker

A flexible, **thread-safe**, background **matchmaking service** for .NET, built with **dependency injection** and **hosted services**. Easily configure wait times, bot usage, and shuffling to create custom matchmaking solutions for your multi-player games or applications.

## Features

1. **Generic Matchmaking**
    - Accepts any **player** type (`TPlayer`) that implements `IMatchMakingPlayer`
    - Accepts any **label** type (`TLabel`) that implements `IMatchMakingLabel`

2. **Configurable Matchmaking Options**
    - Minimum and maximum wait times
    - Shuffle or not
    - Enable or disable **bot** matchmaking when players exceed maximum wait time

3. **Create and Manage Rooms**
    - Generate a unique **6-digit** room code for join
    - Mark a **host** for the room
    - Kick players, start the room, and more

4. **Matchmaking Events**
    - `JoinedMatchmaking`
    - `MatchFound`
    - `MatchNotFound` (timed out and no bots)
    - `KickedFromRoom`
    - `JoinedRoom`
    - `etc...`

5. **Thread Safe**
    - Uses concurrent collections (`ConcurrentQueue`, `ConcurrentDictionary`)
    - Hosted service architecture ensures centralized update loop

## Getting Started

### 1. Installation

install via:

```bash
dotnet add package KarizmaMatchMaker --version 1.2.0
```


### 2. Implement the Required Classes

Your **player** and **label** classes must be implemented:

#### Example:

```csharp
public class MyPlayer(string playerId) : IMatchMakingPlayer
{
    public string GetPlayerId()
    {
        return playerId;
    }
}
public class MyLabel(string playerId) : IMatchMakingLabel
{
    public string GetIdentifier()
    {
        return "Classic,BronzeLeague";
    }

    public int GetMatchPlayersSize()
    {
        return 2;
    }
}
```

### 3. Register in DI

In your ASP.NET Core or .NET 8 minimal API **Program.cs** (or **Startup.cs** for older patterns), register the matchmaking services with:

```csharp
using KarizmaPlatform.MatchMaker.Extensions;
using KarizmaPlatform.MatchMaker.Configurations;

var builder = WebApplication.CreateBuilder(args);

// Option A: Use default values
builder.Services.AddMatchMaker<MyPlayer, MyLabel>();

// Option B: Provide your own configuration
builder.Services.AddMatchMaker<MyPlayer, MyLabel>(options =>
{
    options.MinimumWaitTime = TimeSpan.FromSeconds(10);
    options.MaximumWaitTime = TimeSpan.FromSeconds(60);
    options.EnableBotMatchmaking = false;
    options.ShufflePlayers = true;
});

var app = builder.Build();
app.Run();
```

### 4. How to Use

Once registered, the **KarizmaMatchMakerService** is available via DI. For example, you can **inject** it into controllers or other services:

```csharp
using KarizmaPlatform.MatchMaker.Services;

public class MyMatchController : ControllerBase
{
    private readonly KarizmaMatchMakerService<MyPlayer, MyLabel> _matchmaker;

    public MyMatchController(KarizmaMatchMakerService<MyPlayer, MyLabel> matchmaker)
    {
        _matchmaker = matchmaker;
    }

    public async Task JoinMatchmaking(MyPlayer player, MyLabel label, int desiredCount)
    {
        await _matchmaker.JoinMatchmaking(player, label, desiredCount);
    }

    public async Task<string> CreateRoom(MyPlayer player)
    {
        return await _matchmaker.CreateRoom(player);
    }

    public async Task KickPlayer(MyPlayer hostPlayer, MyPlayer targetPlayer, string roomCode)
    {
        await _matchmaker.KickFromRoom(hostPlayer, targetPlayer, roomCode);
    }

    public async Task StartRoom(MyPlayer hostPlayer, string roomCode)
    {
        await _matchmaker.StartRoom(hostPlayer, roomCode);
    }
}
```

### 5. Listening to Events

If you need to **subscribe** to matchmaking events (`JoinedMatchmaking`, `MatchFound`, etc.), you can **inject** the `MatchmakerEvents<MyPlayer, MyLabel>` singleton:

```csharp
using KarizmaPlatform.MatchMaker.Events;

public class MyMatchEventListener
{
    public MyMatchEventListener(MatchmakerEvents<MyPlayer, MyLabel> events)
    {
        events.JoinedMatchmaking += (player, label) =>
        {
            Console.WriteLine($"{player.Name} joined with label {label.LabelName}");
        };

        events.MatchFound += (players, label) =>
        {
            Console.WriteLine($"Match found for label {label.LabelName} with {players.Count} players");
        };

        events.MatchNotFound += (player, label) =>
        {
            Console.WriteLine($"No match found for player {player.Name} with label {label.LabelName}");
        };

        events.KickedFromRoom += (player, roomCode) =>
        {
            Console.WriteLine($"Player {player.Name} got kicked from room {roomCode}");
        };

        events.JoinedRoom += (player, roomCode) =>
        {
            Console.WriteLine($"{player.Name} joined room {roomCode}");
        };
    }
}
```


### 6. Contributing

1. Fork or clone the repository.
2. Make your changes.
3. Submit a pull request.

### 7. Contact / Support

For bugs, questions, or feature requests, open an **issue** in the repository or contact the maintainers directly.
