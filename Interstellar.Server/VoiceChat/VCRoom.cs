using Interstellar.Messages;
using Interstellar.Server.Services;

namespace Interstellar.Server.VoiceChat;

internal sealed class VCRoom
{
    private readonly string myKey;
    private readonly Dictionary<byte, VCClient> fastClients = new();
    private readonly object sync = new();

    public VCRoom(string key)
    {
        myKey = key;
    }

    private void CheckAliveLocked()
    {
        foreach (var closed in fastClients.Where(c => c.Value.IsClosed).ToArray())
        {
            fastClients.Remove(closed.Key);
        }
    }

    private byte AvailableIdLocked()
    {
        byte id = 0;
        while (fastClients.ContainsKey(id))
        {
            id++;
        }

        return id;
    }

    public VCClient Join(VCClientSession service)
    {
        lock (sync)
        {
            CheckAliveLocked();
            var client = new VCClient(service, AvailableIdLocked(), this);
            fastClients.Add(client.ClientId, client);

            long currentMask = CurrentVoiceMaskLocked();
            foreach (var c in fastClients.Values)
            {
                if (c.ClientId != client.ClientId)
                {
                    c.OnJoinOrLeaveAnyone(currentMask);
                }
            }

            return client;
        }
    }

    public void Leave(VCClient client)
    {
        lock (sync)
        {
            if (fastClients.Remove(client.ClientId))
            {
                long currentMask = CurrentVoiceMaskLocked();
                foreach (var c in fastClients.Values)
                {
                    if (c.IsClosed)
                    {
                        continue;
                    }

                    c.OnJoinOrLeaveAnyone(currentMask);
                    c.NoticeLeaveClient(client.ClientId);
                }
            }

            if (fastClients.Count == 0)
            {
                RoomManager.RemoveRoom(myKey);
            }
        }
    }

    public long CurrentVoiceMask
    {
        get
        {
            lock (sync)
            {
                return CurrentVoiceMaskLocked();
            }
        }
    }

    private long CurrentVoiceMaskLocked()
    {
        long mask = 0;
        foreach (var client in fastClients.Values)
        {
            mask |= (1L << client.ClientId);
        }

        return mask;
    }

    public void Broadcast(byte id, uint durationRtpUnits, byte[] encodedAudio)
    {
        lock (sync)
        {
            foreach (var client in fastClients.Values)
            {
                if (client.ClientId != id)
                {
                    client.SendAudio(id, durationRtpUnits, encodedAudio);
                }
            }
        }
    }

    public void BroadcastRawMessage(byte id, byte[] rawMessage)
    {
        lock (sync)
        {
            foreach (var client in fastClients.Values)
            {
                if (client.ClientId != id)
                {
                    client.Send(rawMessage);
                }
            }
        }
    }

    public void Broadcast(byte sender, IMessage message)
    {
        lock (sync)
        {
            foreach (var client in fastClients.Values)
            {
                if (client.ClientId != sender)
                {
                    client.Send(message);
                }
            }
        }
    }

    public IEnumerable<VCClient> Clients
    {
        get
        {
            lock (sync)
            {
                return fastClients.Values.ToArray();
            }
        }
    }

    public RoomSnapshot ToSnapshot()
    {
        lock (sync)
        {
            return new RoomSnapshot(
                Key: myKey,
                ClientCount: fastClients.Count,
                VoiceMask: CurrentVoiceMaskLocked(),
                Clients: fastClients.Values.OrderBy(c => c.ClientId).Select(c => c.ToSnapshot()).ToArray());
        }
    }

    public bool DisconnectClient(byte clientId, string reason)
    {
        VCClient? target;
        lock (sync)
        {
            fastClients.TryGetValue(clientId, out target);
        }

        if (target == null)
        {
            return false;
        }

        target.ForceDisconnect(reason);
        return true;
    }

    public int DisconnectAllClients(string reason)
    {
        VCClient[] targets;
        lock (sync)
        {
            targets = fastClients.Values.ToArray();
        }

        foreach (var client in targets)
        {
            client.ForceDisconnect(reason);
        }

        return targets.Length;
    }
}

internal sealed record RoomSnapshot(string Key, int ClientCount, long VoiceMask, IReadOnlyList<ClientSnapshot> Clients);
