using SIPSorcery.Net;

namespace Interstellar.Messages;

public static class WebSocketHelpers
{
    public static RTCConfiguration GetRTCConfiguration()
    {
        return new RTCConfiguration
        {
            iceServers =
            [
                new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
            ]
        };
    }
}
