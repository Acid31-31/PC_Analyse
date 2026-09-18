using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PCAnalyse.Core;

namespace PCAnalyse.InputShare;

public enum PeerSide
{
    Right,
    Left
}

public sealed class MouseShareService : IDisposable
{
    private const byte MsgHello = 1;
    private const byte MsgEnter = 2;
    private const byte MsgMove = 3;
    private const byte MsgButton = 4;
    private const byte MsgWheel = 5;
    private const byte MsgKey = 6;
    private const byte MsgLeave = 7;

    private readonly object _sendLock = new();
    private readonly Native.HookProc _mouseProc;
    private readonly Native.HookProc _keyProc;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private volatile bool _cursorHere = true;
    private volatile bool _connected;
    private DateTime _ignoreEdgeUntil = DateTime.MinValue;
    private int _remoteX;
    private int _remoteY;
    private int _remoteW = 1920;
    private int _remoteH = 1080;
    private int _centerX;
    private int _centerY;

    public PeerSide PeerSide { get; private set; } = PeerSide.Right;
    public string Status { get; private set; } = "Maus: getrennt";
    public event Action<string>? StatusChanged;

    public MouseShareService()
    {
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
    }

    public void Start(IPAddress? peerAddress, string? peerId, string localId, PeerSide peerSide)
    {
        Stop();
        if (peerAddress is null)
        {
            SetStatus("Maus: keine Peer-IP");
            return;
        }

        PeerSide = peerSide;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ConnectLoopAsync(peerAddress, peerId ?? "", localId, token), token);
        InstallHooks();
        SetStatus(peerSide == PeerSide.Right
            ? "Maus bereit – rechten Rand für den anderen PC"
            : "Maus bereit – linken Rand für den anderen PC");
    }

    public void Stop()
    {
        _cts?.Cancel();
        RemoveHooks();
        Native.ClipCursor(IntPtr.Zero);
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
        _listener = null;
        _connected = false;
        _cursorHere = true;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task ConnectLoopAsync(IPAddress peer, string peerId, string localId, CancellationToken token)
    {
        var weConnect = string.IsNullOrEmpty(peerId) || string.CompareOrdinal(localId, peerId) < 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (weConnect)
                    await ConnectClientAsync(peer, token);
                else
                    await AcceptServerAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(800, token);
            }
        }
    }

    private async Task ConnectClientAsync(IPAddress peer, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(peer, LinkPorts.InputTcp, timeout.Token);
                await RunSessionAsync(client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch
            {
                client.Dispose();
                await Task.Delay(500, token);
            }
        }
    }

    private async Task AcceptServerAsync(CancellationToken token)
    {
        _listener = new TcpListener(IPAddress.Any, LinkPorts.InputTcp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        while (!token.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(token);
            client.NoDelay = true;
            try { await RunSessionAsync(client, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* nächste Verbindung */ }
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken token)
    {
        _client?.Dispose();
        _client = client;
        _stream = client.GetStream();
        _connected = true;
        _cursorHere = true;
        SendHello();
        SetStatus(PeerSide == PeerSide.Right
            ? "Eine Maus für beide PCs – rechts rüber"
            : "Eine Maus für beide PCs – links rüber");
        try
        {
            await ReceiveLoopAsync(token);
        }
        finally
        {
            _connected = false;
            _cursorHere = true;
            Native.ClipCursor(IntPtr.Zero);
            SetStatus("Maus: Verbindung unterbrochen, suche neu …");
        }
    }

    private void SendHello()
    {
        var screen = Native.VirtualScreen();
        Send(writer =>
        {
            writer.Write(MsgHello);
            writer.Write(screen.Width);
            writer.Write(screen.Height);
        });
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_stream is null)
            return;
        var buffer = new byte[32];
        while (!token.IsCancellationRequested)
        {
            var type = await ReadExactAsync(_stream, buffer, 1, token);
            if (type == 0)
                break;
            switch (buffer[0])
            {
                case MsgHello:
                    if (await ReadExactAsync(_stream, buffer, 8, token) == 0) return;
                    _remoteW = Math.Max(1, BitConverter.ToInt32(buffer, 0));
                    _remoteH = Math.Max(1, BitConverter.ToInt32(buffer, 4));
                    break;
                case MsgEnter:
                    if (await ReadExactAsync(_stream, buffer, 9, token) == 0) return;
                    TakeCursorFromNetwork(buffer[0], BitConverter.ToSingle(buffer, 1), BitConverter.ToSingle(buffer, 5));
                    break;
                case MsgMove:
                    if (await ReadExactAsync(_stream, buffer, 8, token) == 0) return;
                    if (_cursorHere)
                        InjectMove(BitConverter.ToInt32(buffer, 0), BitConverter.ToInt32(buffer, 4));
                    break;
                case MsgButton:
                    if (await ReadExactAsync(_stream, buffer, 2, token) == 0) return;
                    if (_cursorHere)
                        InjectButton(buffer[0], buffer[1] != 0);
                    break;
                case MsgWheel:
                    if (await ReadExactAsync(_stream, buffer, 2, token) == 0) return;
                    if (_cursorHere)
                        InjectWheel(BitConverter.ToInt16(buffer, 0));
                    break;
                case MsgKey:
                    if (await ReadExactAsync(_stream, buffer, 3, token) == 0) return;
                    if (_cursorHere)
                        InjectKey(BitConverter.ToUInt16(buffer, 0), buffer[2] != 0);
                    break;
                case MsgLeave:
                    ReturnCursorHere();
                    break;
            }
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), token);
            if (n == 0)
                return 0;
            read += n;
        }
        return read;
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_connected)
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.Msllhookstruct>(lParam);
        if ((data.flags & Native.LlmhfInjected) != 0)
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        if (!_cursorHere)
            return HandleRemoteControl(msg, data);

        if (msg == Native.WmMouseMove && DateTime.UtcNow >= _ignoreEdgeUntil && HitPeerEdge(data.pt.X, data.pt.Y, out var yNorm))
        {
            SwitchToPeer(yNorm);
            return 1;
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr HandleRemoteControl(int msg, Native.Msllhookstruct data)
    {
        if (msg == Native.WmMouseMove)
        {
            var dx = data.pt.X - _centerX;
            var dy = data.pt.Y - _centerY;
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2)
                return 1;
            Native.SetCursorPos(_centerX, _centerY);
            _remoteX = Math.Clamp(_remoteX + dx, 0, _remoteW - 1);
            _remoteY = Math.Clamp(_remoteY + dy, 0, _remoteH - 1);
            if (HitReturnEdge())
            {
                SendLeave();
                ReturnCursorHere();
                return 1;
            }
            Send(writer =>
            {
                writer.Write(MsgMove);
                writer.Write(_remoteX);
                writer.Write(_remoteY);
            });
            return 1;
        }

        if (msg is Native.WmLButtonDown or Native.WmLButtonUp or Native.WmRButtonDown or Native.WmRButtonUp
            or Native.WmMButtonDown or Native.WmMButtonUp)
        {
            var (button, down) = msg switch
            {
                Native.WmLButtonDown => ((byte)0, true),
                Native.WmLButtonUp => ((byte)0, false),
                Native.WmRButtonDown => ((byte)1, true),
                Native.WmRButtonUp => ((byte)1, false),
                Native.WmMButtonDown => ((byte)2, true),
                Native.WmMButtonUp => ((byte)2, false),
                _ => ((byte)0, false)
            };
            Send(writer =>
            {
                writer.Write(MsgButton);
                writer.Write(button);
                writer.Write((byte)(down ? 1 : 0));
            });
            return 1;
        }

        if (msg == Native.WmMouseWheel)
        {
            var delta = (short)((data.mouseData >> 16) & 0xFFFF);
            Send(writer =>
            {
                writer.Write(MsgWheel);
                writer.Write(delta);
            });
            return 1;
        }

        return 1;
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_connected || _cursorHere)
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.Kbdllhookstruct>(lParam);
        if ((data.flags & Native.LlkhfInjected) != 0)
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var down = wParam.ToInt32() is Native.WmKeyDown or Native.WmSysKeyDown;
        Send(writer =>
        {
            writer.Write(MsgKey);
            writer.Write((ushort)data.vkCode);
            writer.Write((byte)(down ? 1 : 0));
        });
        return 1;
    }

    private bool HitPeerEdge(int x, int y, out float yNorm)
    {
        var screen = Native.VirtualScreen();
        yNorm = screen.Height <= 1 ? 0.5f : (y - screen.Y) / (float)(screen.Height - 1);
        yNorm = Math.Clamp(yNorm, 0f, 1f);
        return PeerSide == PeerSide.Right
            ? x >= screen.Right
            : x <= screen.X;
    }

    private bool HitReturnEdge()
    {
        return PeerSide == PeerSide.Right ? _remoteX <= 0 : _remoteX >= _remoteW - 1;
    }

    private void SwitchToPeer(float yNorm)
    {
        _cursorHere = false;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(400);
        var screen = Native.VirtualScreen();
        _centerX = screen.X + screen.Width / 2;
        _centerY = screen.Y + screen.Height / 2;
        Native.SetCursorPos(_centerX, _centerY);
        _remoteX = PeerSide == PeerSide.Right ? 2 : _remoteW - 3;
        _remoteY = Math.Clamp((int)(yNorm * (_remoteH - 1)), 0, _remoteH - 1);
        var enterEdge = (byte)(PeerSide == PeerSide.Right ? 0 : 1);
        Send(writer =>
        {
            writer.Write(MsgEnter);
            writer.Write(enterEdge);
            writer.Write(yNorm);
            writer.Write(PeerSide == PeerSide.Right ? 0f : 1f);
        });
        SetStatus("Maus auf dem anderen PC – Rand zurück");
    }

    private void TakeCursorFromNetwork(byte edge, float yNorm, float xNorm)
    {
        _cursorHere = true;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(400);
        Native.ClipCursor(IntPtr.Zero);
        var screen = Native.VirtualScreen();
        var x = edge == 0 ? screen.X + 4 : screen.Right - 4;
        if (xNorm > 0.5f)
            x = screen.Right - 4;
        var y = screen.Y + (int)(Math.Clamp(yNorm, 0f, 1f) * (screen.Height - 1));
        Native.SetCursorPos(x, y);
        SetStatus("Maus auf diesem PC");
    }

    private void ReturnCursorHere()
    {
        _cursorHere = true;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(400);
        Native.ClipCursor(IntPtr.Zero);
        var screen = Native.VirtualScreen();
        var x = PeerSide == PeerSide.Right ? screen.Right - 8 : screen.X + 8;
        Native.SetCursorPos(x, _centerY);
        SetStatus(PeerSide == PeerSide.Right
            ? "Maus auf diesem PC – rechts rüber"
            : "Maus auf diesem PC – links rüber");
    }

    private void SendLeave()
    {
        Send(writer => writer.Write(MsgLeave));
    }

    private void Send(Action<BinaryWriter> write)
    {
        var stream = _stream;
        if (stream is null || !_connected)
            return;
        lock (_sendLock)
        {
            try
            {
                using var buffer = new MemoryStream(16);
                using var writer = new BinaryWriter(buffer);
                write(writer);
                writer.Flush();
                var bytes = buffer.ToArray();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
                _connected = false;
            }
        }
    }

    private static void InjectMove(int x, int y)
    {
        var screen = Native.VirtualScreen();
        var absX = (int)Math.Round(x * 65535.0 / Math.Max(1, screen.Width - 1));
        var absY = (int)Math.Round(y * 65535.0 / Math.Max(1, screen.Height - 1));
        SendMouse(absX, absY, Native.MouseeventfMove | Native.MouseeventfAbsolute | Native.MouseeventfVirtualdesk, 0);
    }

    private static void InjectButton(byte button, bool down)
    {
        var flags = (button, down) switch
        {
            (0, true) => Native.MouseeventfLeftdown,
            (0, false) => Native.MouseeventfLeftup,
            (1, true) => Native.MouseeventfRightdown,
            (1, false) => Native.MouseeventfRightup,
            (2, true) => Native.MouseeventfMiddledown,
            _ => Native.MouseeventfMiddleup
        };
        SendMouse(0, 0, flags, 0);
    }

    private static void InjectWheel(short delta) =>
        SendMouse(0, 0, Native.MouseeventfWheel, (uint)(ushort)delta);

    private static void InjectKey(ushort vk, bool down)
    {
        var input = new Native.Input
        {
            Type = Native.InputKeyboard,
            Data = new Native.InputUnion
            {
                Keyboard = new Native.Keybdinput
                {
                    Vk = vk,
                    Flags = down ? 0 : Native.KeyeventfKeyup
                }
            }
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.Input>());
    }

    private static void SendMouse(int dx, int dy, uint flags, uint data)
    {
        var input = new Native.Input
        {
            Type = Native.InputMouse,
            Data = new Native.InputUnion
            {
                Mouse = new Native.Mouseinput
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    Flags = flags
                }
            }
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.Input>());
    }

    private void InstallHooks()
    {
        RemoveHooks();
        var moduleName = System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName;
        var module = Native.GetModuleHandle(moduleName);
        _mouseHook = Native.SetWindowsHookEx(Native.WhMouseLl, _mouseProc, module, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _keyProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }
}
