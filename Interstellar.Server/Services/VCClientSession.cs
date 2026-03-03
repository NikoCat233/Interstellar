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
    private readonly RTCPeerConnection connection;
    private readonly Dictionary<int, MediaStreamTrack> streamTracks = new(32);
    private readonly Dictionary<int, AudioStream> audioStreams = new(32);
    private readonly ConcurrentQueue<IceCandMessage> pendingIceCandidates = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>();
    private readonly ILogger<VCClientSession> logger;

    private VCClient? client;
    private bool closed;

    public string? RemoteIpAddress { get; private set; }
    public int? RemotePort { get; private set; }
    public int? LocalUdpPort { get; private set; }

    public VCClientSession(WebSocket socket, PortRange? udpPortRange, ILogger<VCClientSession> logger)
    {
        this.socket = socket;
        this.logger = logger;

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
        logger.LogInformation("Client {ClientId} connected.", id);
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
        finally
        {
            ForceDisconnect("Client left the game.");
            await senderTask;
            logger.LogInformation("Client {ClientId} disconnected.", id);
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
        await foreach (var message in outgoing.Reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open) break;
            await socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken);
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
                client?.UpdateMuteStatus(muteStatus.Mute);
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

        VCRoom room = RoomManager.GetRoom(message.Region, message.RoomCode);
        client = room.Join(this);

        var format = AudioHelpers.GetOpusFormat(client.ClientId);
        var stream = new MediaStreamTrack(format, MediaStreamStatusEnum.RecvOnly);
        connection.addTrack(stream);

        SendMessages([new ShareIdMessage(client.ClientId), UpdateTracks(room.CurrentVoiceMask), .. client.ShareExistingProfiles()]);
    }

    private void AcceptSdpAnswer(SdpAnswerMessage message)
    {
        connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = message.Sdp });
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
        connection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)message.SdpMLineIndex,
            usernameFragment = message.UsernameFragment
        });
    }

    private void ResendConnectionInformation()
    {
        if (client == null)
        {
            return;
        }

        SendMessages([UpdateTracks(client.Room.CurrentVoiceMask), .. client.ShareExistingProfiles()]);
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

    public void ForceDisconnect(string reason)
    {
        if (closed)
        {
            return;
        }

        closed = true;
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
