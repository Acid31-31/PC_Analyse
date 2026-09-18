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
    private const int EdgePixels = 8;

    private readonly object _sendLock = new();
    private readonly object _sessionLock = new();
    private readonly Native.HookProc _mouseProc;
    private readonly Native.HookProc _keyProc;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _hookThread;
    private uint _hookThreadId;
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
    private bool _leftDown;
    private bool _rightDown;
    private bool _midDown;

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
        if (peerAddress is null)
        {
            SetStatus("Maus: keine Peer-IP – erst verbinden");
            return;
        }

        if (_cts is { IsCancellationRequested: false })
        {
            PeerSide = peerSide;
            return;
        }

        Stop();
        PeerSide = peerSide;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var weConnect = string.IsNullOrEmpty(peerId)
                        || string.CompareOrdinal(localId, peerId) > 0;
        StartHookThread();
        _ = Task.Run(() => ConnectAsync(peerAddress, weConnect, token), token);
        SetStatus("Maus: verbinde mit " + peerAddress + " …");
    }

    public void SetPeerSide(PeerSide side) => PeerSide = side;

    public void SwitchNow()
    {
        if (!_connected)
        {
            SetStatus("Maus: noch nicht verbunden – kurz warten");
            return;
        }

        if (!_cursorHere)
            return;

        var screen = Native.VirtualScreen();
        Native.GetCursorPos(out var pt);
        var yNorm = screen.Height <= 1 ? 0.5f : (pt.Y - screen.Y) / (float)Math.Max(1, screen.Height - 1);
        SwitchToPeer(Math.Clamp(yNorm, 0f, 1f));
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_hookThreadId != 0)
            Native.PostThreadMessage(_hookThreadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero);
        try { _hookThread?.Join(1000); } catch { /* ignore */ }
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
        _hookThreadId = 0;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private void StartHookThread()
    {
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "PCAnalyse-Maus"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void HookThreadMain()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        TryInstallHooks();
        Native.Msg msg;
        while (true)
        {
            while (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                if (msg.Message == Native.WmQuit)
                {
                    RemoveHooks();
                    return;
                }

                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            PollCursor();
            PollButtons();
            Thread.Sleep(8);
        }
    }

    private void TryInstallHooks()
    {
        var modules = new List<IntPtr> { Native.LoadLibrary("user32.dll"), IntPtr.Zero };
        try
        {
            var name = System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName;
            modules.Insert(1, Native.GetModuleHandle(name));
        }
        catch { /* self-contained ohne MainModule */ }

        foreach (var module in modules)
        {
            _mouseHook = Native.SetWindowsHookEx(Native.WhMouseLl, _mouseProc, module, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _keyProc, module, 0);
            if (_mouseHook != IntPtr.Zero)
                return;
        }
    }

    private void PollCursor()
    {
        if (!_connected || _mouseHook != IntPtr.Zero)
            return;
        Native.GetCursorPos(out var pt);
        if (_cursorHere)
        {
            if (DateTime.UtcNow >= _ignoreEdgeUntil && HitPeerEdge(pt.X, pt.Y, out var yNorm))
                SwitchToPeer(yNorm);
            return;
        }

        var dx = pt.X - _centerX;
        var dy = pt.Y - _centerY;
        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
            return;
        Native.SetCursorPos(_centerX, _centerY);
        _remoteX = Math.Clamp(_remoteX + dx, 0, _remoteW - 1);
        _remoteY = Math.Clamp(_remoteY + dy, 0, _remoteH - 1);
        if (HitReturnEdge())
        {
            SendLeave();
            ReturnCursorHere();
            return;
        }

        Send(writer =>
        {
            writer.Write(MsgMove);
            writer.Write(_remoteX);
            writer.Write(_remoteY);
        });
    }

    private void PollButtons()
    {
        if (!_connected || _cursorHere || _mouseHook != IntPtr.Zero)
            return;
        PollButton(Native.VkLButton, 0, ref _leftDown);
        PollButton(Native.VkRButton, 1, ref _rightDown);
        PollButton(Native.VkMButton, 2, ref _midDown);
    }

    private void PollButton(int vk, byte button, ref bool wasDown)
    {
        var down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        if (down == wasDown)
            return;
        wasDown = down;
        Send(writer =>
        {
            writer.Write(MsgButton);
            writer.Write(button);
            writer.Write((byte)(down ? 1 : 0));
        });
    }

    private async Task ConnectAsync(IPAddress peer, bool weConnect, CancellationToken token)
    {
        var listening = false;
        _listener = new TcpListener(IPAddress.Any, LinkPorts.InputTcp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _listener.Start();
            listening = true;
        }
        catch (Exception ex)
        {
            SetStatus("Maus-Port 49583 belegt: " + ex.Message);
        }

        if (listening)
            _ = AcceptLoopAsync(token);

        try
        {
            if (weConnect || !listening)
                await ConnectLoopAsync(peer, token);
            else
                await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            /* Stop */
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch { continue; }
            client.NoDelay = true;
            _ = RunSessionAsync(client, token);
        }
    }

    private async Task ConnectLoopAsync(IPAddress peer, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_connected)
            {
                await Task.Delay(1000, token);
                continue;
            }

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
                return;
            }
            catch
            {
                client.Dispose();
                await Task.Delay(400, token);
            }
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken token)
    {
        lock (_sessionLock)
        {
            if (_connected)
            {
                client.Dispose();
                return;
            }

            _client = client;
            _stream = client.GetStream();
            _connected = true;
            _cursorHere = true;
        }

        SendHello();
        SetStatus(PeerSide == PeerSide.Right
            ? "Maus verbunden – rechten Rand oder Strg+Alt+Pfeil rechts"
            : "Maus verbunden – linken Rand oder Strg+Alt+Pfeil links");
        try
        {
            await ReceiveLoopAsync(token);
        }
        finally
        {
            lock (_sessionLock)
            {
                if (ReferenceEquals(_client, client))
                {
                    _connected = false;
                    _cursorHere = true;
                    _stream = null;
                    _client = null;
                }
            }

            try { client.Dispose(); } catch { /* ignore */ }
            Native.ClipCursor(IntPtr.Zero);
            if (!token.IsCancellationRequested)
                SetStatus("Maus: Verbindung weg, suche neu …");
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
        if (nCode < 0 || !_connected)
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<Native.Kbdllhookstruct>(lParam);
        if ((data.flags & Native.LlkhfInjected) != 0)
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var down = wParam.ToInt32() is Native.WmKeyDown or Native.WmSysKeyDown;
        if (_cursorHere)
        {
            if (down && IsSwitchHotkey((int)data.vkCode))
            {
                SwitchNow();
                return 1;
            }

            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        Send(writer =>
        {
            writer.Write(MsgKey);
            writer.Write((ushort)data.vkCode);
            writer.Write((byte)(down ? 1 : 0));
        });
        return 1;
    }

    private bool IsSwitchHotkey(int vk)
    {
        var ctrl = (Native.GetAsyncKeyState(Native.VkControl) & 0x8000) != 0;
        var alt = (Native.GetAsyncKeyState(Native.VkMenu) & 0x8000) != 0;
        if (!ctrl || !alt)
            return false;
        if (vk == Native.VkR)
            return true;
        return PeerSide == PeerSide.Right ? vk == Native.VkRight : vk == Native.VkLeft;
    }

    private bool HitPeerEdge(int x, int y, out float yNorm)
    {
        var screen = Native.VirtualScreen();
        yNorm = screen.Height <= 1 ? 0.5f : (y - screen.Y) / (float)(screen.Height - 1);
        yNorm = Math.Clamp(yNorm, 0f, 1f);
        return PeerSide == PeerSide.Right
            ? x >= screen.Right - EdgePixels
            : x <= screen.X + EdgePixels;
    }

    private bool HitReturnEdge()
    {
        return PeerSide == PeerSide.Right ? _remoteX <= EdgePixels : _remoteX >= _remoteW - 1 - EdgePixels;
    }

    private void SwitchToPeer(float yNorm)
    {
        _cursorHere = false;
        _leftDown = _rightDown = _midDown = false;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(500);
        var screen = Native.VirtualScreen();
        _centerX = screen.X + screen.Width / 2;
        _centerY = screen.Y + screen.Height / 2;
        Native.SetCursorPos(_centerX, _centerY);
        _remoteX = PeerSide == PeerSide.Right ? EdgePixels + 2 : _remoteW - EdgePixels - 3;
        _remoteY = Math.Clamp((int)(yNorm * (_remoteH - 1)), 0, _remoteH - 1);
        var enterEdge = (byte)(PeerSide == PeerSide.Right ? 0 : 1);
        Send(writer =>
        {
            writer.Write(MsgEnter);
            writer.Write(enterEdge);
            writer.Write(yNorm);
            writer.Write(PeerSide == PeerSide.Right ? 0f : 1f);
        });
        SetStatus("Maus auf dem anderen PC – Rand zurück oder Strg+Alt+Pfeil");
    }

    private void TakeCursorFromNetwork(byte edge, float yNorm, float xNorm)
    {
        _cursorHere = true;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(500);
        Native.ClipCursor(IntPtr.Zero);
        var screen = Native.VirtualScreen();
        var x = edge == 0 ? screen.X + EdgePixels : screen.Right - EdgePixels;
        if (xNorm > 0.5f)
            x = screen.Right - EdgePixels;
        var y = screen.Y + (int)(Math.Clamp(yNorm, 0f, 1f) * (screen.Height - 1));
        Native.SetCursorPos(x, y);
        SetStatus("Maus auf diesem PC");
    }

    private void ReturnCursorHere()
    {
        _cursorHere = true;
        _ignoreEdgeUntil = DateTime.UtcNow.AddMilliseconds(500);
        Native.ClipCursor(IntPtr.Zero);
        var screen = Native.VirtualScreen();
        var x = PeerSide == PeerSide.Right ? screen.Right - EdgePixels - 4 : screen.X + EdgePixels + 4;
        Native.SetCursorPos(x, _centerY == 0 ? screen.Y + screen.Height / 2 : _centerY);
        SetStatus(PeerSide == PeerSide.Right
            ? "Maus auf diesem PC – rechts rüber oder Strg+Alt+→"
            : "Maus auf diesem PC – links rüber oder Strg+Alt+←");
    }

    private void SendLeave() => Send(writer => writer.Write(MsgLeave));

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
        var absX = (int)Math.Round((screen.X + x) * 65535.0 / Math.Max(1, screen.Width - 1));
        var absY = (int)Math.Round((screen.Y + y) * 65535.0 / Math.Max(1, screen.Height - 1));
        absX = (int)Math.Round(x * 65535.0 / Math.Max(1, screen.Width - 1));
        absY = (int)Math.Round(y * 65535.0 / Math.Max(1, screen.Height - 1));
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
