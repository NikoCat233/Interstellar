using Interstellar.Server.Services;
using Interstellar.Server.VoiceChat;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using SIPSorcery.Sys;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

namespace Interstellar.Server;

internal static class Program
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var adminAuth = builder.Configuration.GetSection("AdminAuth").Get<AdminAuthOptions>() ?? new AdminAuthOptions();
        var serverOptions = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions();
        var mediaOptions = builder.Configuration.GetSection("Media").Get<MediaOptions>() ?? new MediaOptions();

        if (adminAuth.JwtKey.Length < 32)
        {
            throw new InvalidOperationException("AdminAuth:JwtKey must be at least 32 characters.");
        }
        if (string.IsNullOrWhiteSpace(adminAuth.PasswordBcryptHash))
        {
            throw new InvalidOperationException("AdminAuth:PasswordBcryptHash is required.");
        }

        bool secureFromArg = args.Any(a => a.Equals("-secure", StringComparison.OrdinalIgnoreCase));
        bool enableSsl = serverOptions.EnableSsl || secureFromArg;
        string urlPrefix = enableSsl ? "https://" : "http://";
        string hostPort = args.FirstOrDefault(a => !a.StartsWith('-')) ?? $"{serverOptions.Host}:{serverOptions.Port}";
        string listenUrl = urlPrefix + hostPort;
        builder.WebHost.UseUrls(listenUrl);
        if (enableSsl)
        {
            if (!string.IsNullOrWhiteSpace(serverOptions.CertificatePath))
            {
                var certificate = new X509Certificate2(serverOptions.CertificatePath, serverOptions.CertificatePassword);
                builder.WebHost.ConfigureKestrel(kestrel =>
                {
                    kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate);
                });
            }
        }

        PortRange? udpPortRange = null;
        if (mediaOptions.UdpPortRangeStart > 0 && mediaOptions.UdpPortRangeEnd > 0)
        {
            if (mediaOptions.UdpPortRangeStart >= mediaOptions.UdpPortRangeEnd)
            {
                throw new InvalidOperationException("Media:UdpPortRangeStart must be less than UdpPortRangeEnd.");
            }
            if ((mediaOptions.UdpPortRangeStart & 1) != 0)
            {
                throw new InvalidOperationException("Media:UdpPortRangeStart must be an even number.");
            }

            udpPortRange = new PortRange(mediaOptions.UdpPortRangeStart, mediaOptions.UdpPortRangeEnd);
        }

        builder.Services.AddSingleton(adminAuth);
        builder.Services.AddSingleton(new RuntimeOptions(udpPortRange));
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = adminAuth.JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = adminAuth.JwtAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(adminAuth.JwtKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        builder.Services.AddAuthorization();

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                string host = context.Request.Host.Host;
                if (!IPAddress.TryParse(host, out _))
                {
                    return RateLimitPartition.GetNoLimiter("non-direct-host");
                }

                string remoteKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"direct-ip:{remoteKey}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromSeconds(10),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(DashboardHtml);
        });

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            service = "Interstellar.Server",
            version = "v1"
        }));

        app.MapGet("/api/v1/health", () => ApiResponse.Ok(new
        {
            status = "ok",
            startedAt = StartedAt,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds
        }));

        app.MapGet("/api/v1/config", (HttpContext context) =>
        {
            string scheme = context.Request.IsHttps ? "wss" : "ws";
            string wsEndpoint = $"{scheme}://{context.Request.Host}/vc";
            return ApiResponse.Ok(new
            {
                wsEndpoint,
                signalProtocol = "interstellar-binary-v1",
                transport = "webrtc-udp",
                sslEnabled = context.Request.IsHttps,
                udpPortRange = mediaOptions.UdpPortRangeStart > 0 && mediaOptions.UdpPortRangeEnd > 0
                    ? new { start = mediaOptions.UdpPortRangeStart, end = mediaOptions.UdpPortRangeEnd }
                    : null
            });
        });

        app.MapPost("/api/v1/admin/login", (AdminLoginRequest request, AdminAuthOptions options) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password) || !BCrypt.Net.BCrypt.Verify(request.Password, options.PasswordBcryptHash))
            {
                return ApiResponse.Error("unauthorized", "Invalid admin password.", StatusCodes.Status401Unauthorized);
            }

            var now = DateTimeOffset.UtcNow;
            var expires = now.AddMinutes(Math.Max(1, options.JwtExpiresMinutes));
            var claims = new[]
            {
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("scope", "interstellar.admin")
            };
            var creds = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtKey)),
                SecurityAlgorithms.HmacSha256);

            var jwt = new JwtSecurityToken(
                issuer: options.JwtIssuer,
                audience: options.JwtAudience,
                claims: claims,
                notBefore: now.UtcDateTime,
                expires: expires.UtcDateTime,
                signingCredentials: creds);

            return ApiResponse.Ok(new
            {
                accessToken = new JwtSecurityTokenHandler().WriteToken(jwt),
                tokenType = "Bearer",
                expiresAt = expires
            });
        });

        var adminApi = app.MapGroup("/api/v1/admin").RequireAuthorization();
        adminApi.MapGet("/stats", () => ApiResponse.Ok(RoomManager.GetSnapshot(DateTimeOffset.UtcNow - StartedAt)));
        adminApi.MapDelete("/rooms/{roomKey}", (string roomKey) =>
        {
            int disconnected = RoomManager.DisconnectRoom(roomKey);
            if (disconnected == 0)
            {
                return ApiResponse.Error("room_not_found", $"Room '{roomKey}' was not found.", StatusCodes.Status404NotFound);
            }

            return ApiResponse.Ok(new { roomKey, disconnectedClients = disconnected });
        });
        adminApi.MapDelete("/rooms/{roomKey}/clients/{clientId:int}", (string roomKey, int clientId) =>
        {
            if (clientId < byte.MinValue || clientId > byte.MaxValue)
            {
                return ApiResponse.Error("invalid_client_id", "Client ID must be between 0 and 255.", StatusCodes.Status400BadRequest);
            }

            bool disconnected = RoomManager.TryDisconnectClient(roomKey, (byte)clientId);
            if (!disconnected)
            {
                return ApiResponse.Error("client_not_found", $"Client '{clientId}' was not found in room '{roomKey}'.", StatusCodes.Status404NotFound);
            }

            return ApiResponse.Ok(new { roomKey, clientId });
        });

        app.Map("/vc", async (HttpContext context, RuntimeOptions runtime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                context.Response.Headers.Upgrade = "websocket";
                await context.Response.WriteAsync("This endpoint is WebSocket only. Use ws(s)://.../vc");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = new VCClientSession(socket, runtime.UdpPortRange);
            await session.RunAsync(context.RequestAborted);
        });

        app.MapFallback(() => ApiResponse.Error("not_found", "Resource was not found.", StatusCodes.Status404NotFound));
        app.Run();
    }

    private static class ApiResponse
    {
        public static IResult Ok(object data) => Results.Json(new
        {
            success = true,
            timestamp = DateTimeOffset.UtcNow,
            data
        });

        public static IResult Error(string code, string message, int status) => Results.Json(new
        {
            success = false,
            timestamp = DateTimeOffset.UtcNow,
            error = new { code, message }
        }, statusCode: status);
    }

    private sealed class AdminAuthOptions
    {
        public string PasswordBcryptHash { get; set; } = "";
        public string JwtKey { get; set; } = "ChangeThisJwtSigningKeyToAtLeast32Chars!";
        public string JwtIssuer { get; set; } = "Interstellar.Server";
        public string JwtAudience { get; set; } = "Interstellar.Admin";
        public int JwtExpiresMinutes { get; set; } = 120;
    }

    private sealed class ServerOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 8000;
        public bool EnableSsl { get; set; }
        public string CertificatePath { get; set; } = "";
        public string CertificatePassword { get; set; } = "";
    }

    private sealed class MediaOptions
    {
        public int UdpPortRangeStart { get; set; }
        public int UdpPortRangeEnd { get; set; }
    }

    private sealed class RuntimeOptions
    {
        public RuntimeOptions(PortRange? udpPortRange)
        {
            UdpPortRange = udpPortRange;
        }

        public PortRange? UdpPortRange { get; }
    }

    private sealed record AdminLoginRequest(string Password);

    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Interstellar Server Panel</title>
  <style>
    :root { --bg:#0d1117; --card:#161b22; --line:#2b3240; --text:#e6edf3; --muted:#9fb0c3; --good:#3fb950; --accent:#58a6ff; }
    * { box-sizing:border-box; }
    body { margin:0; font-family:"Segoe UI","Noto Sans",sans-serif; background:radial-gradient(circle at top, #152238, var(--bg)); color:var(--text); min-height:100vh; padding:20px; }
    .wrap { max-width:1100px; margin:0 auto; }
    .title { font-size:28px; font-weight:700; margin:4px 0 16px; }
    .grid { display:grid; gap:16px; grid-template-columns:repeat(auto-fit, minmax(220px,1fr)); }
    .card { background:linear-gradient(180deg, #1b2330, var(--card)); border:1px solid var(--line); border-radius:14px; padding:16px; }
    .k { color:var(--muted); font-size:13px; }
    .v { font-size:26px; font-weight:700; margin-top:4px; }
    .ok { color:var(--good); }
    table { width:100%; border-collapse:collapse; margin-top:12px; }
    th,td { text-align:left; padding:10px 8px; border-bottom:1px solid var(--line); font-size:13px; vertical-align:top; }
    th { color:var(--muted); font-weight:600; }
    .badge { color:var(--accent); font-weight:600; }
    .footer { margin-top:12px; color:var(--muted); font-size:12px; }
    .btn { cursor:pointer; border:1px solid var(--line); border-radius:8px; background:#202938; color:var(--text); padding:6px 10px; font-size:12px; margin-right:6px; }
    .btn.danger { border-color:#7f1d1d; background:#3f1b1b; }
    .clients-table { width:100%; margin:0; }
    .clients-table td,.clients-table th { font-size:12px; padding:6px 4px; }
    .muted { color:var(--muted); }
    .hidden { display:none; }
    .login { max-width:360px; margin:40px auto; }
    input[type=password] { width:100%; border:1px solid var(--line); border-radius:10px; background:#0f1622; color:var(--text); padding:10px; margin:10px 0; }
    .error { color:#f87171; min-height:20px; font-size:13px; }
    .top-actions { margin-bottom:10px; }
  </style>
</head>
<body>
  <div class="wrap">
    <div id="loginView" class="card login">
      <div class="title" style="font-size:22px; margin-bottom:8px;">Admin Login</div>
      <div class="k">Enter panel password from server config.</div>
      <input id="password" type="password" placeholder="Admin password" />
      <button class="btn" onclick="login()">Login</button>
      <div id="loginError" class="error"></div>
    </div>

    <div id="panelView" class="hidden">
      <div class="title">Interstellar Server Panel</div>
      <div class="top-actions"><button class="btn" onclick="logout()">Logout</button></div>
      <div class="grid">
        <div class="card"><div class="k">Status</div><div class="v ok" id="status">ONLINE</div></div>
        <div class="card"><div class="k">Active Rooms</div><div class="v" id="roomCount">0</div></div>
        <div class="card"><div class="k">Connected Clients</div><div class="v" id="clientCount">0</div></div>
        <div class="card"><div class="k">Uptime (seconds)</div><div class="v" id="uptime">0</div></div>
      </div>

      <div class="card" style="margin-top:16px;">
        <div class="k">Room Details</div>
        <table>
          <thead><tr><th>Room</th><th>Clients</th><th>VoiceMask</th><th>Actions</th></tr></thead>
          <tbody id="rooms"></tbody>
        </table>
        <div class="footer">Refresh interval: 2 seconds</div>
      </div>
    </div>
  </div>

  <script>
    const TOKEN_KEY = 'interstellar_admin_token';

    function getToken() { return localStorage.getItem(TOKEN_KEY) || ''; }
    function setToken(t) { localStorage.setItem(TOKEN_KEY, t); }
    function clearToken() { localStorage.removeItem(TOKEN_KEY); }

    function esc(v) {
      return String(v ?? '').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
    }

    function splitKey(key) {
      const i = key.indexOf('.');
      if (i < 0) return { region: '-', roomCode: key };
      return { region: key.slice(0, i), roomCode: key.slice(i + 1) };
    }

    async function authFetch(url, options = {}) {
      const headers = { ...(options.headers || {}) };
      const token = getToken();
      if (token) headers.Authorization = `Bearer ${token}`;
      const res = await fetch(url, { ...options, headers });
      if (res.status === 401) {
        switchToLogin('Session expired, login again.');
        throw new Error('unauthorized');
      }
      return res;
    }

    function switchToPanel() {
      document.getElementById('loginView').classList.add('hidden');
      document.getElementById('panelView').classList.remove('hidden');
      document.getElementById('loginError').textContent = '';
    }

    function switchToLogin(message = '') {
      document.getElementById('panelView').classList.add('hidden');
      document.getElementById('loginView').classList.remove('hidden');
      document.getElementById('loginError').textContent = message;
      clearToken();
    }

    async function login() {
      const password = document.getElementById('password').value;
      const res = await fetch('/api/v1/admin/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password })
      });
      const payload = await res.json();
      if (!res.ok || !payload.success) {
        document.getElementById('loginError').textContent = payload?.error?.message || 'Login failed';
        return;
      }

      setToken(payload.data.accessToken);
      switchToPanel();
      await refresh();
    }

    function logout() {
      switchToLogin();
    }

    async function disconnectRoom(roomKey) {
      await authFetch(`/api/v1/admin/rooms/${encodeURIComponent(roomKey)}`, { method: 'DELETE' });
      await refresh();
    }

    async function disconnectClient(roomKey, clientId) {
      await authFetch(`/api/v1/admin/rooms/${encodeURIComponent(roomKey)}/clients/${clientId}`, { method: 'DELETE' });
      await refresh();
    }

    function renderClients(room) {
      if (!room.clients || room.clients.length === 0) return '<span class="muted">No clients</span>';

      const rows = room.clients.map(c => {
        const profile = c.playerName ? `${esc(c.playerName)} (${c.playerId ?? '-'})` : '<span class="muted">no profile</span>';
        const flags = `${c.isMute ? 'mute' : 'live'} / ${c.isClosed ? 'closed' : 'open'}`;
        return `<tr>
          <td>#${c.clientId}</td>
          <td>${profile}</td>
          <td>${flags}</td>
          <td><button class="btn danger" onclick="disconnectClient('${encodeURIComponent(room.key)}', ${c.clientId})">Disconnect</button></td>
        </tr>`;
      }).join('');

      return `<table class="clients-table"><thead><tr><th>ID</th><th>Profile</th><th>Status</th><th>Action</th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function refresh() {
      const res = await authFetch('/api/v1/admin/stats', { cache: 'no-store' });
      const payload = await res.json();
      const stats = payload.data;

      document.getElementById('roomCount').textContent = stats.roomCount;
      document.getElementById('clientCount').textContent = stats.clientCount;
      document.getElementById('uptime').textContent = stats.uptimeSeconds;

      const tbody = document.getElementById('rooms');
      tbody.innerHTML = '';
      for (const room of stats.rooms) {
        const key = splitKey(room.key);
        const tr = document.createElement('tr');
        tr.innerHTML = `
          <td><span class="badge">${esc(room.key)}</span><div class="muted">${esc(key.region)} / ${esc(key.roomCode)}</div></td>
          <td>${renderClients(room)}</td>
          <td>${room.voiceMask}</td>
          <td><button class="btn danger" onclick="disconnectRoom('${encodeURIComponent(room.key)}')">Disconnect Room</button></td>`;
        tbody.appendChild(tr);
      }
    }

    (async () => {
      if (!getToken()) return;
      try {
        await refresh();
        switchToPanel();
      } catch {
        switchToLogin('Please login.');
      }
    })();

    setInterval(async () => {
      if (document.getElementById('panelView').classList.contains('hidden')) return;
      try { await refresh(); } catch {}
    }, 2000);
  </script>
</body>
</html>
""";
}
