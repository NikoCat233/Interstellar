namespace Interstellar.Server.VoiceChat;

internal static class RoomManager
{
    private static readonly Dictionary<string, VCRoom> Rooms = new();
    private static readonly object Sync = new();

    public static VCRoom GetRoom(string region, string roomId)
    {
        string key = region + "." + roomId;
        lock (Sync)
        {
            if (Rooms.TryGetValue(key, out var room))
            {
                return room;
            }

            room = new VCRoom(key);
            Rooms[key] = room;
            return room;
        }
    }

    public static void RemoveRoom(string key)
    {
        lock (Sync)
        {
            Rooms.Remove(key);
        }
    }

    public static AdminSnapshot GetSnapshot(TimeSpan uptime)
    {
        RoomSnapshot[] rooms;
        lock (Sync)
        {
            rooms = Rooms.Values.Select(r => r.ToSnapshot()).OrderBy(r => r.Key).ToArray();
        }

        return new AdminSnapshot(
            RoomCount: rooms.Length,
            ClientCount: rooms.Sum(r => r.ClientCount),
            UptimeSeconds: (long)uptime.TotalSeconds,
            Rooms: rooms);
    }

    public static int TotalClientCount
    {
        get
        {
            lock (Sync)
            {
                return Rooms.Values.Sum(room => room.Clients.Count());
            }
        }
    }

    public static bool TryDisconnectClient(string roomKey, byte clientId)
    {
        VCRoom? room;
        lock (Sync)
        {
            Rooms.TryGetValue(roomKey, out room);
        }

        return room != null && room.DisconnectClient(clientId, $"Disconnected by admin from room {roomKey}.");
    }

    public static int DisconnectRoom(string roomKey)
    {
        VCRoom? room;
        lock (Sync)
        {
            Rooms.TryGetValue(roomKey, out room);
        }

        return room?.DisconnectAllClients($"Room {roomKey} closed by admin.") ?? 0;
    }
}

internal sealed record AdminSnapshot(int RoomCount, int ClientCount, long UptimeSeconds, IReadOnlyList<RoomSnapshot> Rooms);
