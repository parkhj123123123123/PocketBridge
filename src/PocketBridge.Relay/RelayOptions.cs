namespace PocketBridge.Relay;

public sealed class RelayOptions
{
    public int MaxRooms { get; set; } = 200;
    public int WaitingMinutes { get; set; } = 10;
    public int ActiveHours { get; set; } = 12;
}

public sealed record CreatedRoom(string RoomId, string ReceiverToken, string SenderToken, DateTimeOffset ExpiresAt);
