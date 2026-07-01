using Interstellar.Messages;
using Interstellar.Messages.Messages;
using Interstellar.Messages.Variation;
using Interstellar.Server.VoiceChat;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Interstellar.Server.Services;

internal sealed class VCClientSession : IMessageProcessor
{
    private readonly string id = Guid.NewGuid().ToString("N")[..8];
    private readonly WebSocket socket;
    private readonly string remoteSocketIp;
    private readonly int? remoteSocketPort;
    private readonly RTCPeerConnection connection;
    private readonly Dictionary<int, MediaStreamTrack> streamTracks = new(32);
    private readonly Dictionary<int, AudioStream> audioStreams = new(32);
    private readonly ConcurrentQueue<IceCandMessage> pendingIceCandidates = new();
    private readonly ConcurrentQueue<IceCandMessage> pendingRemoteIceCandidates = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>();
    private readonly ILogger<VCClientSession> logger;
    private readonly string voiceServerUrl;

    private VCClient? client;
    private bool closed;
    private bool supportsExtendedProtocol;
    private bool remoteDescriptionSet;
    private string disconnectReason = "Client left the game.";
    private string? joinedRegion;
    private string? joinedRoomCode;

    public string? RemoteIpAddress { get; private set; }
    public int? RemotePort { get; private set; }
    public int? LocalUdpPort { get; private set; }

    public VCClientSession(
        WebSocket socket,
        PortRange? udpPortRange,
        ILogger<VCClientSession> logger,
        string remoteSocketIp,
        int? remoteSocketPort,
        bool supportsExtendedProtocol,
        string voiceServerUrl)
    {
        this.socket = socket;
        this.logger = logger;
        this.remoteSocketIp = remoteSocketIp;
        this.remoteSocketPort = remoteSocketPort;
        this.supportsExtendedProtocol = supportsExtendedProtocol;
        this.voiceServerUrl = voiceServerUrl;

        connection = udpPortRange == null
            ? new RTCPeerConnection(WebSocketHelpers.GetRTCConfiguration())
            : new RTCPeerConnection(WebSocketHelpers.GetRTCConfiguration(), 0, udpPortRange, false);
        connection.OnAudioFrameReceived += frame =>
        {
            var durationRtpUnits = RtpTimestampExtensions.ToRtpUnits(frame.DurationMilliSeconds, AudioHelpers.ClockRate);
            client?.BroadcastAudio(durationRtpUnits, frame.EncodedAudio);
        };

        connection.onicecandidate += candidate =>
        {
            var msg = new IceCandMessage(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex, candidate.usernameFragment);
            if (socket.State == WebSocketState.Open)
            {
                SendMessage(msg);
            }
            else
            {
                pendingIceCandidates.Enqueue(msg);
            }
        };

        connection.oniceconnectionstatechange += _ => UpdateEndpointInfo();
    }

    public bool IsClosed => closed || socket.State is WebSocketState.Closed or WebSocketState.Aborted or WebSocketState.CloseSent;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Client connected. SessionId={SessionId}, SocketIp={SocketIp}, SocketPort={SocketPort}.",
            id,
            remoteSocketIp,
            remoteSocketPort);
        var senderTask = RunSenderAsync(cancellationToken);

        while (pendingIceCandidates.TryDequeue(out var msg))
        {
            SendMessage(msg);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var packet = await ReceiveBinaryMessageAsync(cancellationToken);
                if (packet == null)
                {
                    break;
                }

                try
                {
                    MessagePacker.UnpackMessages(packet, this);
                }
                catch (InvalidDataException ex)
                {
                    logger.LogWarning(ex, "Error processing message from client {ClientId}.", id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            disconnectReason = "Request aborted.";
        }
        catch (WebSocketException ex)
        {
            disconnectReason = "WebSocket receive failed: " + ex.Message;
            logger.LogWarning(ex, "WebSocket receive failed for client {ClientId}.", id);
        }
        catch (Exception ex)
        {
            disconnectReason = "Session failed: " + ex.Message;
            logger.LogError(ex, "Unhandled session error for client {ClientId}.", id);
        }
        finally
        {
            ForceDisconnect(disconnectReason);
            await senderTask;
            logger.LogInformation(
                "Client disconnected. SessionId={SessionId}, ClientId={ClientId}, Region={Region}, RoomCode={RoomCode}, SocketIp={SocketIp}, SocketPort={SocketPort}, RtpRemoteIp={RtpRemoteIp}, RtpRemotePort={RtpRemotePort}, Reason={Reason}.",
                id,
                client?.ClientId,
                joinedRegion,
                joinedRoomCode,
                remoteSocketIp,
                remoteSocketPort,
                RemoteIpAddress,
                RemotePort,
                disconnectReason);
        }
    }

    private async Task<byte[]?> ReceiveBinaryMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", cancellationToken);
                }
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                if (result.EndOfMessage) return null;
                continue;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return ms.ToArray();
            }
        }
    }

    private async Task RunSenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            disconnectReason = "WebSocket send failed: " + ex.Message;
            logger.LogWarning(ex, "WebSocket send failed for client {ClientId}.", id);
        }
        catch (Exception ex)
        {
            disconnectReason = "Sender failed: " + ex.Message;
            logger.LogError(ex, "Unhandled sender error for client {ClientId}.", id);
        }
    }

    private bool IsJoined => client != null;

    int IMessageProcessor.Process(MessageTag tag, ReadOnlySpan<byte> bytes)
    {
        int read = -1;
        switch (tag)
        {
            case MessageTag.Join:
                JoinRoom(JoinMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.SdpAnswer:
                AcceptSdpAnswer(SdpAnswerMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.AddIceCand:
                AddIceCandidate(IceCandMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.Profile:
                if (client == null)
                {
                    break;
                }
                var profile = ProfileMessage.DeserializeWithoutTag(bytes, out read);
                client.UpdateProfile(profile.PlayerName, profile.PlayerId);
                break;
            case MessageTag.Custom:
                CustomMessage.DeserializeForServerWithoutTag(bytes, out read);
                client?.BroadcastRawMessage(bytes);
                break;
            case MessageTag.RequestReload:
                read = 0;
                ResendConnectionInformation();
                break;
            case MessageTag.UpdateMuteStatus:
                var muteStatus = UpdateMuteStatusMessage.DeserializeWithoutTag(bytes, out read);
                if (read > 1)
                {
                    supportsExtendedProtocol = true;
                }
                client?.UpdateMuteStatus(muteStatus.Mute, muteStatus.IsImpostorRadio);
                break;
            case MessageTag.HostSettings:
                supportsExtendedProtocol = true;
                var hostSettings = HostSettingsMessage.DeserializeWithoutTag(bytes, out read);
                client?.BroadcastHostSettings(hostSettings);
                break;
            case MessageTag.ServerInfo:
                read = 0;
                break;
        }

        return read;
    }

    private void JoinRoom(JoinMessage message)
    {
        if (IsJoined)
        {
            return;
        }

        joinedRegion = message.Region;
        joinedRoomCode = message.RoomCode;
        VCRoom room = RoomManager.GetRoom(message.Region, message.RoomCode);
        client = room.Join(this);
        logger.LogInformation(
            "Client joined room. SessionId={SessionId}, ClientId={ClientId}, Region={Region}, RoomCode={RoomCode}, SocketIp={SocketIp}, SocketPort={SocketPort}.",
            id,
            client.ClientId,
            message.Region,
            message.RoomCode,
            remoteSocketIp,
            remoteSocketPort);

        var format = AudioHelpers.GetOpusFormat(client.ClientId);
        var stream = new MediaStreamTrack(format, MediaStreamStatusEnum.RecvOnly);
        connection.addTrack(stream);

        List<IMessage> joinMessages = [new ShareIdMessage(client.ClientId), UpdateTracks(room.CurrentVoiceMask), .. client.ShareExistingProfiles()];
        if (supportsExtendedProtocol && room.LastHostSettings != null)
        {
            joinMessages.Add(room.LastHostSettings);
        }
        SendMessages(joinMessages);
        SendServerInfo();
    }

    private void AcceptSdpAnswer(SdpAnswerMessage message)
    {
        connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = message.Sdp });
        remoteDescriptionSet = true;
        while (pendingRemoteIceCandidates.TryDequeue(out var candidate))
        {
            AddIceCandidateNow(candidate);
        }
        UpdateEndpointInfo();
    }

    private void UpdateEndpointInfo()
    {
        try
        {
            var remoteEndpoint = connection.AudioDestinationEndPoint;
            if (remoteEndpoint != null)
            {
                RemoteIpAddress = remoteEndpoint.Address.ToString();
                RemotePort = remoteEndpoint.Port;
            }

            var localEndpoint = connection.GetRtpChannel()?.RTPLocalEndPoint;
            if (localEndpoint != null)
            {
                LocalUdpPort = localEndpoint.Port;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get endpoint info for client {ClientId}.", id);
        }
    }

    private void AddIceCandidate(IceCandMessage message)
    {
        if (!remoteDescriptionSet)
        {
            pendingRemoteIceCandidates.Enqueue(message);
            return;
        }

        AddIceCandidateNow(message);
    }

    private void AddIceCandidateNow(IceCandMessage message)
    {
        try
        {
            connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = message.Candidate,
                sdpMid = message.SdpMid,
                sdpMLineIndex = (ushort)message.SdpMLineIndex,
                usernameFragment = message.UsernameFragment
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring invalid remote ICE for client {ClientId}.", id);
        }
    }

    private void ResendConnectionInformation()
    {
        if (client == null)
        {
            return;
        }

        List<IMessage> reloadMessages = [UpdateTracks(client.Room.CurrentVoiceMask), .. client.ShareExistingProfiles()];
        if (supportsExtendedProtocol && client.Room.LastHostSettings != null)
        {
            reloadMessages.Add(client.Room.LastHostSettings);
        }
        SendMessages(reloadMessages);
    }

    private SdpOfferMessage UpdateTracks(long mask)
    {
        int myId = client!.ClientId;
        for (int i = 0; i < AudioHelpers.MaxTracks; i++)
        {
            if (i == myId)
            {
                continue;
            }

            bool shouldHave = (mask & (1L << i)) != 0;
            bool have = streamTracks.TryGetValue(i, out var existed);

            if (shouldHave && !have)
            {
                var format = AudioHelpers.GetOpusFormat(i);
                var stream = new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly);
                streamTracks.Add(i, stream);
                connection.addTrack(stream);
            }
            else if (!shouldHave && have)
            {
                connection.removeTrack(existed!);
                streamTracks.Remove(i);
            }
        }

        audioStreams.Clear();
        foreach (var audioStream in connection.AudioStreamList)
        {
            audioStreams[audioStream.GetSendingFormat().ID] = audioStream;
        }

        var offer = connection.createOffer(null);
        connection.setLocalDescription(offer).Wait();
        return new SdpOfferMessage(offer.sdp, mask);
    }

    public void SendTracksMask(long mask)
    {
        if (IsJoined)
        {
            SendMessage(UpdateTracks(mask));
        }
    }

    public void SendClientLeft(int clientId)
    {
        if (IsJoined)
        {
            SendMessage(new NoticeDisconnectMessage(clientId));
        }
    }

    public void SendAudio(int id, uint durationRtpUnits, byte[] encodedAudio)
    {
        if (audioStreams.TryGetValue(id, out var stream))
        {
            stream.SendAudio(durationRtpUnits, encodedAudio);
        }
    }

    public void SendMessage(IMessage message)
    {
        outgoing.Writer.TryWrite(MessagePacker.PackMessage(message).ToArray());
    }

    public void SendMessages(IEnumerable<IMessage> messages)
    {
        outgoing.Writer.TryWrite(MessagePacker.PackMessages(messages).ToArray());
    }

    public void SendRawMessage(byte[] message)
    {
        outgoing.Writer.TryWrite(message.ToArray());
    }

    public void SendExtendedMessage(IMessage message)
    {
        if (supportsExtendedProtocol)
        {
            SendMessage(message);
        }
    }

    public void SendServerInfo()
    {
        if (!supportsExtendedProtocol)
        {
            return;
        }

        SendMessage(new ServerInfoMessage(0, RoomManager.TotalClientCount, voiceServerUrl));
    }

    public void ForceDisconnect(string reason)
    {
        if (closed)
        {
            return;
        }

        closed = true;
        disconnectReason = reason;
        client?.Close();
        connection.Close(reason);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
                }
                catch
                {
                    socket.Abort();
                }
            });
        }

        outgoing.Writer.TryComplete();
    }
}
