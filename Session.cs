using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace BToverlay;

public sealed record Invite(string Address, int Port, int Slot, string Secret, string CertificateHash);
public sealed record Wire(string Type, int Slot = -1, int Count = -1, int Turn = -1, int Start = -1,
    int[]? Counts = null, string[]? Names = null, bool[]? Included = null, int[]? Order = null,
    string? Name = null, string? GuestId = null, string? Proof = null, bool OffTurn = false, long Version = 0);

public sealed class Session : IAsyncDisposable
{
    sealed class Peer(TcpClient tcp, SslStream stream, int slot, Guid id, string name)
    {
        public TcpClient Tcp = tcp;
        public SslStream Stream = stream;
        public int Slot = slot;
        public Guid Id = id;
        public string Name = name;
        public SemaphoreSlim SendLock = new(1, 1);
    }
    readonly Board _board;
    readonly UserSettings _settings;
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<int, byte[]> _invites = new();
    readonly ConcurrentDictionary<int, Peer> _peers = new();
    readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new();
    readonly SemaphoreSlim _handshakes = new(8, 8);
    TcpListener? _listener;
    TcpClient? _guestTcp;
    Peer? _hostPeer;
    X509Certificate2? _certificate;
    string? _certificateHash;
    bool _disposed;
    long _lastStateVersion = -1;
    public bool IsHost { get; private init; }
    public bool Connected => IsHost ? _listener is not null : _hostPeer is not null;
    public Func<int, string, Task<bool>>? ApproveJoin { get; set; }
    public event Action<string>? Notice;
    public event Action? Changed;
    Session(Board board, UserSettings settings, bool isHost) { _board = board; _settings = settings; IsHost = isHost; }

    public static Task<Session> HostAsync(Board board, UserSettings settings)
    {
        var s = new Session(board, settings, true);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=BToverlay session", key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        s._certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        s._certificateHash = Convert.ToHexString(SHA256.HashData(s._certificate.RawData));
        s._listener = new TcpListener(IPAddress.Any, settings.Port);
        s._listener.Start(16);
        board.MySlot = 0;
        board.Names[0] = settings.Name;
        board.Counts[0] = settings.MaxItems;
        _ = s.AcceptLoop();
        return Task.FromResult(s);
    }

    public string CreateInvite(int slot)
    {
        if (!IsHost || slot is < 1 or > 7 || _certificateHash is null) throw new InvalidOperationException("참가자 2~8번 중 하나를 선택하세요.");
        if (_peers.ContainsKey(slot)) throw new InvalidOperationException("이 자리는 이미 사용 중입니다.");
        if (_invites.TryRemove(slot, out var old)) CryptographicOperations.ZeroMemory(old);
        var secret = RandomNumberGenerator.GetBytes(32);
        _invites[slot] = secret;
        var data = new Invite(_settings.HostAddress.Trim(), _settings.Port, slot, Convert.ToBase64String(secret), _certificateHash);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));
    }

    public static async Task<Session> JoinAsync(Board board, UserSettings settings, string token)
    {
        Invite invite;
        byte[] secret;
        try
        {
            invite = JsonSerializer.Deserialize<Invite>(Encoding.UTF8.GetString(Convert.FromBase64String(token.Trim())))!;
            if (invite is null || invite.Slot is < 1 or > 7 || invite.Port is < 1 or > 65535 || invite.Address.Length > 255) throw new FormatException();
            secret = Convert.FromBase64String(invite.Secret);
            if (secret.Length != 32 || invite.CertificateHash.Length != 64 || !invite.CertificateHash.All(Uri.IsHexDigit)) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException or ArgumentException)
        { throw new InvalidOperationException("올바르지 않은 초대 토큰입니다.", ex); }
        var s = new Session(board, settings, false);
        try
        {
            s._guestTcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await s._guestTcp.ConnectAsync(invite.Address, invite.Port, timeout.Token);
            var stream = new SslStream(s._guestTcp.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.Export(X509ContentType.Cert)), Convert.FromHexString(invite.CertificateHash)));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "BToverlay session", RemoteCertificateValidationCallback = null }, timeout.Token);
            var challenge = await ReadAsync(stream, timeout.Token);
            if (challenge?.Type != "challenge" || challenge.Proof is null) throw new IOException("호스트 확인 요청을 받지 못했습니다.");
            byte[] nonce = Convert.FromBase64String(challenge.Proof);
            if (nonce.Length != 32) throw new IOException("호스트 확인 요청이 올바르지 않습니다.");
            byte[] proof = HMACSHA256.HashData(secret, nonce);
            await WriteAsync(stream, new Wire("join", invite.Slot, Name: settings.Name[..Math.Min(24, settings.Name.Length)],
                GuestId: settings.GuestId.ToString("N"), Proof: Convert.ToBase64String(proof)), timeout.Token);
            using var approvalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var answer = await ReadAsync(stream, approvalTimeout.Token);
            if (answer?.Type != "welcome" || answer.Slot != invite.Slot) throw new IOException(answer?.Name ?? "호스트가 참가를 거절했습니다.");
            s._hostPeer = new Peer(s._guestTcp, stream, invite.Slot, settings.GuestId, settings.Name);
            board.MySlot = invite.Slot;
            board.Counts[invite.Slot] = settings.MaxItems;
            _ = s.GuestReadLoop(s._hostPeer);
            await s.SendToHostAsync(new Wire("count", invite.Slot, settings.MaxItems));
            return s;
        }
        catch { await s.DisposeAsync(); throw; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            var ip = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString();
            var now = DateTimeOffset.UtcNow;
            if (_lastAttempt.Count > 4096)
                foreach (var stale in _lastAttempt.Where(x => now - x.Value > TimeSpan.FromMinutes(1)).Take(2048))
                    _lastAttempt.TryRemove(stale.Key, out _);
            if (_lastAttempt.TryGetValue(ip, out var last) && now - last < TimeSpan.FromSeconds(3)) { tcp.Dispose(); continue; }
            _lastAttempt[ip] = now;
            if (!_handshakes.Wait(0)) { tcp.Dispose(); continue; }
            _ = HandleJoin(tcp);
        }
    }

    async Task HandleJoin(TcpClient tcp)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            var stream = new SslStream(tcp.GetStream(), false);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, timeout.Token);
            var nonce = RandomNumberGenerator.GetBytes(32);
            await WriteAsync(stream, new Wire("challenge", Proof: Convert.ToBase64String(nonce)), timeout.Token);
            var join = await ReadAsync(stream, timeout.Token);
            if (join?.Type != "join" || join.Proof is null || join.Name is null || join.GuestId is null ||
                join.Slot is < 1 or > 7 || join.Name.Length is < 1 or > 24 || !_invites.TryGetValue(join.Slot, out var secret) ||
                !Guid.TryParseExact(join.GuestId, "N", out var id)) return;
            byte[] supplied;
            try { supplied = Convert.FromBase64String(join.Proof); } catch { return; }
            if (supplied.Length != 32 || !CryptographicOperations.FixedTimeEquals(supplied, HMACSHA256.HashData(secret, nonce))) return;
            bool approved = false;
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (_peers.ContainsKey(join.Slot) || !_invites.ContainsKey(join.Slot)) return;
                approved = await (ApproveJoin?.Invoke(join.Slot, join.Name) ?? Task.FromResult(false));
            }).Task.Unwrap();
            if (!approved)
            {
                if (_invites.TryRemove(join.Slot, out var declinedSecret)) CryptographicOperations.ZeroMemory(declinedSecret);
                await WriteAsync(stream, new Wire("declined", Name: "호스트가 참가를 거절했습니다. 이 초대 토큰은 더 이상 사용할 수 없습니다."), timeout.Token);
                return;
            }
            var peer = new Peer(tcp, stream, join.Slot, id, join.Name);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_peers.ContainsKey(join.Slot) || !_invites.TryRemove(join.Slot, out var used)) return;
                CryptographicOperations.ZeroMemory(used);
                _peers[join.Slot] = peer;
                _board.Names[join.Slot] = join.Name;
                _board.Refresh(); Changed?.Invoke();
            });
            if (!_peers.TryGetValue(join.Slot, out var accepted) || accepted != peer) return;
            await SendAsync(peer, new Wire("welcome", join.Slot));
            await BroadcastAsync();
            await HostReadLoop(peer);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or JsonException or SocketException)
        { }
        finally { tcp.Dispose(); _handshakes.Release(); }
    }

    async Task HostReadLoop(Peer peer)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var msg = await ReadAsync(peer.Stream, _stop.Token);
                if (msg is null) break;
                if (msg.Type is not ("count" or "resetCount") || msg.Slot != peer.Slot || msg.Count is < 0 or > 10) continue;
                bool accepted = false, offTurn = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_peers.GetValueOrDefault(peer.Slot) != peer) return;
                    accepted = true;
                    offTurn = msg.Type == "count" && msg.Count < _board.Counts[peer.Slot] &&
                        (peer.Slot != _board.Turn || msg.OffTurn);
                    _board.SetCount(peer.Slot, msg.Count, msg.Type != "resetCount");
                    if (offTurn) _board.MarkOffTurn(peer.Slot);
                });
                if (!accepted) continue;
                await BroadcastAsync();
                if (offTurn) await BroadcastOffTurnAsync(peer.Slot);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_peers.GetValueOrDefault(peer.Slot) != peer) return;
                _peers.TryRemove(peer.Slot, out _);
                Notice?.Invoke($"{peer.Name}님이 참가자 {peer.Slot + 1}번 자리에서 연결을 끊었습니다.");
                Changed?.Invoke();
            });
        }
    }

    async Task GuestReadLoop(Peer peer)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var msg = await ReadAsync(peer.Stream, _stop.Token);
                if (msg is null) break;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (msg.Type == "state" && msg.Version > _lastStateVersion &&
                        msg.Counts?.Length == 8 && msg.Included?.Length == 8 && msg.Names?.Length == 8 &&
                        msg.Order?.Length == 8 && msg.Order.Order().SequenceEqual(Enumerable.Range(0, 8)) &&
                        msg.Counts.All(x => x is >= 0 and <= 10) && msg.Turn is >= 0 and < 8 && msg.Start is >= 0 and < 8)
                    {
                        Array.Copy(msg.Counts, _board.Counts, 8);
                        Array.Copy(msg.Included, _board.Included, 8);
                        Array.Copy(msg.Names, _board.Names, 8);
                        Array.Copy(msg.Order, _board.Order, 8);
                        _board.Turn = msg.Turn;
                        _board.StartSlot = msg.Start;
                        _lastStateVersion = msg.Version;
                        _board.Refresh();
                    }
                    else if (msg.Type == "resetCounts")
                    {
                        _board.ClearOffTurnMarkers();
                        _board.Counts[_board.MySlot] = _settings.MaxItems;
                        _board.Refresh();
                        _ = SendToHostAsync(new Wire("resetCount", _board.MySlot, _settings.MaxItems));
                    }
                    else if (msg.Type == "offTurn" && msg.Slot is >= 0 and < 8)
                        _board.MarkOffTurn(msg.Slot);
                    else if (msg.Type == "kicked") { Notice?.Invoke("호스트가 세션에서 내보냈습니다."); _ = DisposeAsync(); }
                });
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            _hostPeer = null;
            _guestTcp?.Dispose();
            if (!_disposed) Notice?.Invoke("호스트와 연결이 끊어졌습니다.");
            Changed?.Invoke();
        }
    }

    public async Task UpdateMyCountAsync(int count)
    {
        if (count is < 0 or > 10) return;
        var slot = _board.MySlot;
        var offTurn = count < _board.Counts[slot] && slot != _board.Turn;
        _board.SetCount(slot, count);
        if (offTurn) _board.MarkOffTurn(slot);
        if (IsHost)
        {
            await BroadcastAsync();
            if (offTurn) await BroadcastOffTurnAsync(slot);
        }
        else await SendToHostAsync(new Wire("count", slot, count, OffTurn: offTurn));
    }
    public async Task ResetCountsAsync()
    {
        if (!IsHost) return;
        _board.ClearOffTurnMarkers();
        _board.SetCount(0, _settings.MaxItems, false);
        foreach (var peer in _peers.Values.ToArray()) { try { await SendAsync(peer, new Wire("resetCounts")); } catch (IOException) { } }
    }
    public async Task ResetTurnAsync() { if (!IsHost) return; _board.ResetTurn(); await BroadcastAsync(); }
    public async Task SetStartAsync(int slot)
    {
        if (!IsHost || slot is < 0 or > 7 || !_board.Included[slot]) return;
        _board.StartSlot = slot;
        _board.Refresh();
        await BroadcastAsync();
    }
    public async Task SetIncludedAsync(int slot, bool included)
    {
        if (!IsHost || slot is < 0 or > 7) return;
        _board.Included[slot] = included;
        if (!_board.Included[_board.Turn]) _board.ResetTurn();
        _board.Refresh(); await BroadcastAsync();
    }
    public async Task MoveAsync(int slot, int direction)
    {
        if (!IsHost || slot is < 0 or > 7) return;
        var from = Array.IndexOf(_board.Order, slot);
        var to = from + direction;
        if (to is < 0 or > 7) return;
        (_board.Order[from], _board.Order[to]) = (_board.Order[to], _board.Order[from]);
        _board.Refresh(); await BroadcastAsync();
    }
    public async Task KickAsync(int slot)
    {
        if (!IsHost || !_peers.TryRemove(slot, out var peer)) return;
        await SendAsync(peer, new Wire("kicked"));
        peer.Tcp.Dispose();
        _board.Names[slot] = $"참가자 {slot + 1}";
        _board.Refresh(); Changed?.Invoke();
        await BroadcastAsync();
    }
    async Task BroadcastAsync()
    {
        if (!IsHost) return;
        var msg = await Application.Current.Dispatcher.InvokeAsync(() =>
            new Wire("state", Turn: _board.Turn, Start: _board.StartSlot, Version: _board.Version,
                Counts: [.. _board.Counts], Names: [.. _board.Names], Included: [.. _board.Included], Order: [.. _board.Order]));
        foreach (var peer in _peers.Values.ToArray()) { try { await SendAsync(peer, msg); } catch (IOException) { } }
    }
    async Task BroadcastOffTurnAsync(int slot)
    {
        foreach (var peer in _peers.Values.ToArray())
        { try { await SendAsync(peer, new Wire("offTurn", slot)); } catch (IOException) { } }
    }
    async Task SendToHostAsync(Wire msg) { if (_hostPeer is { } peer) await SendAsync(peer, msg); }
    static async Task SendAsync(Peer peer, Wire msg)
    {
        await peer.SendLock.WaitAsync();
        try { await WriteAsync(peer.Stream, msg, CancellationToken.None); }
        finally { peer.SendLock.Release(); }
    }
    static async Task WriteAsync(Stream stream, Wire msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg) + "\n");
        if (bytes.Length > 2048) throw new IOException("메시지가 너무 큽니다.");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
    static async Task<Wire?> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var data = new MemoryStream();
        var b = new byte[1];
        while (data.Length < 2048)
        {
            if (await stream.ReadAsync(b, ct) == 0) return null;
            if (b[0] == 10) return JsonSerializer.Deserialize<Wire>(data.ToArray());
            data.WriteByte(b[0]);
        }
        throw new IOException("메시지가 너무 큽니다.");
    }
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _stop.Cancel(); _listener?.Stop(); _guestTcp?.Dispose();
        foreach (var p in _peers.Values) p.Tcp.Dispose();
        _peers.Clear();
        foreach (var key in _invites.Values) CryptographicOperations.ZeroMemory(key);
        _invites.Clear(); _certificate?.Dispose();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
