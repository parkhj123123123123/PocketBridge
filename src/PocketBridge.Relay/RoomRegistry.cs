using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PocketBridge.Relay;

public sealed class RoomRegistry(IOptions<RelayOptions> options) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, RelayRoom> rooms = new(StringComparer.Ordinal);
    private readonly RelayOptions settings = options.Value;

    public CreatedRoom? Create()
    {
        lock (gate)
        {
            RemoveExpired();
            if (rooms.Count >= settings.MaxRooms) return null;
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var receiver = CreateToken();
            var sender = CreateToken();
            var expiration = DateTimeOffset.UtcNow.AddMinutes(settings.WaitingMinutes);
            rooms.Add(id, new RelayRoom(id, Hash(receiver), Hash(sender), expiration, TimeSpan.FromHours(settings.ActiveHours)));
            return new CreatedRoom(id, receiver, sender, expiration);
        }
    }

    public RoomReservation? Reserve(string id, string role, string token, out int status)
    {
        status = StatusCodes.Status404NotFound;
        if (id.Length != 32 || token.Length != 43 || role is not ("sender" or "receiver")) return null;
        lock (gate)
        {
            if (!rooms.TryGetValue(id, out var room)) return null;
            if (room.IsExpired)
            {
                rooms.Remove(id);
                room.Stop();
                return null;
            }
            return room.Reserve(role, Hash(token), out status);
        }
    }

    public void Remove(RelayRoom room)
    {
        lock (gate)
        {
            if (rooms.TryGetValue(room.Id, out var existing) && ReferenceEquals(existing, room))
                rooms.Remove(room.Id);
            room.Stop();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                lock (gate) RemoveExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                foreach (var room in rooms.Values) room.Stop();
                rooms.Clear();
            }
        }
    }

    private void RemoveExpired()
    {
        foreach (var room in rooms.Values.Where(r => r.IsExpired).ToArray())
        {
            rooms.Remove(room.Id);
            room.Stop();
        }
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));
}
