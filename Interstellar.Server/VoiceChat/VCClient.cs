using Interstellar.Messages;
using Interstellar.Messages.Variation;
using Interstellar.Server.Services;
using System.Diagnostics.CodeAnalysis;

namespace Interstellar.Server.VoiceChat;

internal class VCClient
{
    internal record Profile(string PlayerName, byte PlayerId);

    private readonly VCClientSession service;
    private readonly VCRoom myRoom;
    private Profile? profile;

    public bool IsClosed => service.IsClosed;

    public bool IsMute { get; private set; }
    public bool IsImpostorRadio { get; private set; }

    public VCRoom Room => myRoom;

    public byte ClientId { get; }

    public VCClient(VCClientSession service, byte clientId, VCRoom room)
    {
        this.service = service;
        ClientId = clientId;
        myRoom = room;
    }

    public void UpdateMuteStatus(bool isMute, bool isImpostorRadio = false)
    {
        if (IsMute == isMute && IsImpostorRadio == isImpostorRadio)
        {
            return;
        }

        IsMute = isMute;
        IsImpostorRadio = isImpostorRadio;
        myRoom.Broadcast(ClientId, new ShareMuteStatusMessage(ClientId, isMute, isImpostorRadio));
    }

    public void BroadcastHostSettings(HostSettingsMessage message)
    {
        myRoom.LastHostSettings = message;
        myRoom.BroadcastExtended(ClientId, message);
    }

    public void OnJoinOrLeaveAnyone(long currentMask)
    {
        service.SendTracksMask(currentMask);
    }

    public void NoticeLeaveClient(byte clientId)
    {
        service.SendClientLeft(clientId);
    }

    public void BroadcastAudio(uint durationRtpUnits, byte[] encodedAudio)
    {
        myRoom.Broadcast(ClientId, durationRtpUnits, encodedAudio);
    }

    public void BroadcastRawMessage(ReadOnlySpan<byte> message)
    {
        myRoom.BroadcastRawMessage(ClientId, message.ToArray());
    }

    public void SendAudio(int id, uint durationRtpUnits, byte[] encodedAudio)
    {
        service.SendAudio(id, durationRtpUnits, encodedAudio);
    }

    public void Send(byte[] rawMessage)
    {
        service.SendRawMessage(rawMessage);
    }

    public void Send(IMessage message)
    {
        service.SendMessage(message);
    }

    public void SendExtended(IMessage message)
    {
        service.SendExtendedMessage(message);
    }

    public void UpdateProfile(string playerName, byte playerId)
    {
        profile = new Profile(playerName, playerId);
        myRoom.Broadcast(ClientId, new ShareProfileMessage(ClientId, playerName, playerId));
    }

    public bool TryGetProfile([MaybeNullWhen(false)] out string playerName, out byte playerId)
    {
        if (profile != null)
        {
            playerName = profile.PlayerName;
            playerId = profile.PlayerId;
            return true;
        }

        playerName = null;
        playerId = 0;
        return false;
    }

    public void Close()
    {
        myRoom.Leave(this);
    }

    public void ForceDisconnect(string reason)
    {
        service.ForceDisconnect(reason);
    }

    public ClientSnapshot ToSnapshot()
    {
        string? playerName = null;
        byte? playerId = null;
        if (TryGetProfile(out var n, out var id))
        {
            playerName = n;
            playerId = id;
        }

        return new ClientSnapshot(
            ClientId: ClientId,
            IsMute: IsMute,
            IsImpostorRadio: IsImpostorRadio,
            IsClosed: IsClosed,
            PlayerName: playerName,
            PlayerId: playerId,
            RemoteIpAddress: service.RemoteIpAddress,
            RemotePort: service.RemotePort,
            LocalUdpPort: service.LocalUdpPort);
    }

    internal IEnumerable<ShareProfileMessage> ShareExistingProfiles()
    {
        foreach (var c in myRoom.Clients)
        {
            if (c.ClientId != ClientId && c.TryGetProfile(out var name, out var pid))
            {
                yield return new ShareProfileMessage(c.ClientId, name, pid);
            }
        }
    }
}

internal sealed record ClientSnapshot(
    byte ClientId,
    bool IsMute,
    bool IsImpostorRadio,
    bool IsClosed,
    string? PlayerName,
    byte? PlayerId,
    string? RemoteIpAddress,
    int? RemotePort,
    int? LocalUdpPort);
