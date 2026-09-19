using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PCAnalyse.Core;
using DrawingImage = System.Drawing.Image;
using FormsClipboard = System.Windows.Forms.Clipboard;

namespace PCAnalyse.InputShare;

public sealed class DeskShareService : IDisposable
{
    private const byte MsgClipboardText = 1;
    private const byte MsgClipboardImage = 2;
    private const byte MsgFileBegin = 3;
    private const byte MsgFileChunk = 4;
    private const byte MsgScreenshotRequest = 5;
    private const byte MsgScreenshot = 6;
    private const byte MsgClipboardRequest = 7;
    private const int ChunkSize = 64 * 1024;
    private const int MaxClipboardBytes = 25 * 1024 * 1024;

    private readonly object _sendLock = new();
    private readonly object _sessionLock = new();
    private readonly ConcurrentQueue<Action> _staWork = new();
    private readonly UTF8Encoding _utf8 = new(false);
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _staThread;
    private uint _staThreadId;
    private volatile bool _connected;
    private uint _ignoreClipboardUntil;
    private string? _incomingName;
    private FileStream? _incomingFile;
    private long _incomingLeft;

    public string Status { get; private set; } = "Dateien: getrennt";
    public event Action<string>? StatusChanged;

    public static string DropFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Von anderem PC");

    public void Start(IPAddress? peerAddress, string? peerId, string localId)
    {
        if (peerAddress is null)
        {
            SetStatus("Dateien: keine Peer-IP – erst verbinden");
            return;
        }

        if (_cts is { IsCancellationRequested: false })
            return;

        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var weConnect = string.IsNullOrEmpty(peerId) || string.CompareOrdinal(localId, peerId) > 0;
        StartStaThread();
        _ = Task.Run(() => ConnectAsync(peerAddress, weConnect, token), token);
        SetStatus("Dateien: verbinde mit " + peerAddress + " …");
    }

    public void RequestScreenshot()
    {
        if (!EnsureConnected())
            return;
        Send(MsgScreenshotRequest, Array.Empty<byte>());
        SetStatus("Screenshot vom anderen PC …");
    }

    public void PullClipboard()
    {
        if (!EnsureConnected())
            return;
        Send(MsgClipboardRequest, Array.Empty<byte>());
        SetStatus("Zwischenablage vom anderen PC …");
    }

    public void SendDropped(IEnumerable<string> paths)
    {
        if (!EnsureConnected())
            return;
        var files = EnumerateFiles(paths).ToList();
        if (files.Count == 0)
        {
            SetStatus("Nichts zu senden.");
            return;
        }

        _ = Task.Run(() => SendFilesCore(files));
    }

    public static void OpenDropFolder()
    {
        Directory.CreateDirectory(DropFolder);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DropFolder,
                UseShellExecute = true
            });
        }
        catch
        {
            // Explorer nicht zwingend
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_staThreadId != 0)
            Native.PostThreadMessage(_staThreadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero);
        try { _staThread?.Join(1000); } catch { /* ignore */ }
        CloseIncoming();
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
        _listener = null;
        _connected = false;
        _staThreadId = 0;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private bool EnsureConnected()
    {
        if (_connected)
            return true;
        SetStatus("Erst verbinden, dann Dateien oder Screenshot.");
        return false;
    }

    private void StartStaThread()
    {
        _staThread = new Thread(StaThreadMain)
        {
            IsBackground = true,
            Name = "PCAnalyse-Desk"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void StaThreadMain()
    {
        _staThreadId = Native.GetCurrentThreadId();
        var lastSeq = Native.GetClipboardSequenceNumber();
        Native.Msg msg;
        while (true)
        {
            while (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                if (msg.Message == Native.WmQuit)
                    return;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            while (_staWork.TryDequeue(out var work))
            {
                try { work(); } catch { /* Zwischenablage belegt */ }
            }

            if (_connected)
            {
                var seq = Native.GetClipboardSequenceNumber();
                if (seq != lastSeq)
                {
                    lastSeq = seq;
                    if (seq > _ignoreClipboardUntil)
                        PushLocalClipboard();
                }
            }

            Thread.Sleep(80);
        }
    }

    private void RunOnSta(Action work) => _staWork.Enqueue(work);

    private async Task ConnectAsync(IPAddress peer, bool weConnect, CancellationToken token)
    {
        var listening = false;
        _listener = new TcpListener(IPAddress.Any, LinkPorts.DeskTcp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            _listener.Start();
            listening = true;
        }
        catch (Exception ex)
        {
            SetStatus("Datei-Port 49584 belegt: " + ex.Message);
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
                await client.ConnectAsync(peer, LinkPorts.DeskTcp, timeout.Token);
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
        }

        SetStatus("Dateien, Screenshot und Zwischenablage bereit – Dateien hierher ziehen.");
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
                    _stream = null;
                    _client = null;
                }
            }

            CloseIncoming();
            try { client.Dispose(); } catch { /* ignore */ }
            if (!token.IsCancellationRequested)
                SetStatus("Dateien: Verbindung weg, suche neu …");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_stream is null)
            return;
        var typeBuf = new byte[1];
        while (!token.IsCancellationRequested)
        {
            if (await ReadExactAsync(_stream, typeBuf, 1, token) == 0)
                return;
            var payload = await ReadPayloadAsync(_stream, token);
            if (payload is null)
                return;
            HandleMessage(typeBuf[0], payload);
        }
    }

    private void HandleMessage(byte type, byte[] payload)
    {
        switch (type)
        {
            case MsgClipboardText:
                RunOnSta(() => SetClipboardText(_utf8.GetString(payload)));
                SetStatus("Text vom anderen PC ist in der Zwischenablage – hier einfügen.");
                break;
            case MsgClipboardImage:
                RunOnSta(() => SetClipboardImage(payload));
                SaveScreenshotFile(payload, "Clipboard");
                SetStatus("Bild vom anderen PC ist in der Zwischenablage – hier einfügen.");
                break;
            case MsgFileBegin:
                BeginIncoming(payload);
                break;
            case MsgFileChunk:
                WriteIncoming(payload);
                break;
            case MsgScreenshotRequest:
                _ = Task.Run(SendLocalScreenshot);
                break;
            case MsgScreenshot:
                RunOnSta(() => SetClipboardImage(payload));
                var path = SaveScreenshotFile(payload, "Screenshot");
                SetStatus(string.IsNullOrEmpty(path)
                    ? "Screenshot in der Zwischenablage – hier einfügen."
                    : "Screenshot in der Zwischenablage und auf dem Desktop.");
                break;
            case MsgClipboardRequest:
                RunOnSta(PushLocalClipboard);
                break;
        }
    }

    private void BeginIncoming(byte[] payload)
    {
        CloseIncoming();
        if (payload.Length < 12)
            return;
        var nameLen = BitConverter.ToInt32(payload, 0);
        if (nameLen < 1 || nameLen > 400 || payload.Length < 12 + nameLen)
            return;
        var name = SafeRelativePath(_utf8.GetString(payload, 4, nameLen));
        var size = BitConverter.ToInt64(payload, 4 + nameLen);
        if (string.IsNullOrWhiteSpace(name) || size < 0 || size > 4L * 1024 * 1024 * 1024)
            return;

        Directory.CreateDirectory(DropFolder);
        var full = UniquePath(Path.Combine(DropFolder, name));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _incomingFile = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _incomingName = Path.GetFileName(full);
        _incomingLeft = size;
        SetStatus("Empfange " + _incomingName + " …");
        if (size == 0)
            FinishIncoming();
    }

    private void WriteIncoming(byte[] payload)
    {
        if (_incomingFile is null)
            return;
        var take = (int)Math.Min(payload.Length, _incomingLeft);
        if (take > 0)
        {
            _incomingFile.Write(payload, 0, take);
            _incomingLeft -= take;
        }

        if (_incomingLeft <= 0)
            FinishIncoming();
    }

    private void FinishIncoming()
    {
        var name = _incomingName;
        CloseIncoming();
        SetStatus("Datei liegt auf dem Desktop in «Von anderem PC»: " + name);
    }

    private void CloseIncoming()
    {
        try { _incomingFile?.Dispose(); } catch { /* ignore */ }
        _incomingFile = null;
        _incomingName = null;
        _incomingLeft = 0;
    }

    private void SendFilesCore(List<(string Full, string Relative)> files)
    {
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file.Full);
                if (!info.Exists)
                    continue;
                var nameBytes = _utf8.GetBytes(file.Relative);
                var header = new byte[12 + nameBytes.Length];
                BitConverter.TryWriteBytes(header.AsSpan(0, 4), nameBytes.Length);
                Buffer.BlockCopy(nameBytes, 0, header, 4, nameBytes.Length);
                BitConverter.TryWriteBytes(header.AsSpan(4 + nameBytes.Length, 8), info.Length);
                Send(MsgFileBegin, header);
                using var stream = info.OpenRead();
                var buffer = new byte[ChunkSize];
                int read;
                long sent = 0;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (read == buffer.Length)
                        Send(MsgFileChunk, buffer);
                    else
                    {
                        var slice = new byte[read];
                        Buffer.BlockCopy(buffer, 0, slice, 0, read);
                        Send(MsgFileChunk, slice);
                    }

                    sent += read;
                    if (info.Length > ChunkSize * 8 && sent % (ChunkSize * 16) == 0)
                        SetStatus($"Sende {file.Relative} ({sent * 100 / Math.Max(1, info.Length)} %)");
                }

                SetStatus("Gesendet: " + file.Relative);
            }
            catch (Exception ex)
            {
                SetStatus("Senden fehlgeschlagen: " + ex.Message);
            }
        }
    }

    private void SendLocalScreenshot()
    {
        try
        {
            var jpeg = CaptureScreenJpeg();
            if (jpeg.Length == 0)
            {
                SetStatus("Screenshot leer.");
                return;
            }

            Send(MsgScreenshot, jpeg);
            SetStatus("Screenshot an den anderen PC gesendet.");
        }
        catch (Exception ex)
        {
            SetStatus("Screenshot fehlgeschlagen: " + ex.Message);
        }
    }

    private void PushLocalClipboard()
    {
        if (!_connected)
            return;
        try
        {
            if (FormsClipboard.ContainsImage())
            {
                using var image = FormsClipboard.GetImage();
                if (image is not null)
                {
                    using var ms = new MemoryStream();
                    image.Save(ms, ImageFormat.Png);
                    if (ms.Length > 0 && ms.Length <= MaxClipboardBytes)
                    {
                        Send(MsgClipboardImage, ms.ToArray());
                        SetStatus("Bild-Zwischenablage an den anderen PC.");
                        return;
                    }
                }
            }

            if (FormsClipboard.ContainsText())
            {
                var text = FormsClipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text.Length <= 1_000_000)
                {
                    Send(MsgClipboardText, _utf8.GetBytes(text));
                    SetStatus("Text-Zwischenablage an den anderen PC.");
                }
            }
        }
        catch
        {
            // Zwischenablage gerade belegt
        }
    }

    private void SetClipboardText(string text)
    {
        try
        {
            FormsClipboard.SetText(text);
            _ignoreClipboardUntil = Native.GetClipboardSequenceNumber();
        }
        catch
        {
            // belegt
        }
    }

    private void SetClipboardImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var image = DrawingImage.FromStream(ms);
            using var copy = new Bitmap(image);
            FormsClipboard.SetImage(copy);
            _ignoreClipboardUntil = Native.GetClipboardSequenceNumber();
        }
        catch
        {
            // kein gültiges Bild
        }
    }

    private static string SaveScreenshotFile(byte[] bytes, string prefix)
    {
        try
        {
            Directory.CreateDirectory(DropFolder);
            var ext = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg" : ".png";
            var path = Path.Combine(DropFolder, $"{prefix} {DateTime.Now:yyyy-MM-dd HHmmss}{ext}");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return "";
        }
    }

    private static byte[] CaptureScreenJpeg()
    {
        var screen = Native.VirtualScreen();
        using var bmp = new Bitmap(screen.Width, screen.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screen.X, screen.Y, 0, 0, new Size(screen.Width, screen.Height));
        }

        using var ms = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
        bmp.Save(ms, codec, p);
        return ms.ToArray();
    }

    private void Send(byte type, byte[] payload)
    {
        var stream = _stream;
        if (stream is null || !_connected)
            return;
        lock (_sendLock)
        {
            try
            {
                var header = new byte[5];
                header[0] = type;
                BitConverter.TryWriteBytes(header.AsSpan(1, 4), payload.Length);
                stream.Write(header, 0, header.Length);
                if (payload.Length > 0)
                    stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch
            {
                _connected = false;
            }
        }
    }

    private static async Task<byte[]?> ReadPayloadAsync(Stream stream, CancellationToken token)
    {
        var lenBuf = new byte[4];
        if (await ReadExactAsync(stream, lenBuf, 4, token) == 0)
            return null;
        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len < 0 || len > MaxClipboardBytes)
            return null;
        if (len == 0)
            return Array.Empty<byte>();
        var data = new byte[len];
        return await ReadExactAsync(stream, data, len, token) == 0 ? null : data;
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

    private static IEnumerable<(string Full, string Relative)> EnumerateFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
                yield return (path, Path.GetFileName(path));
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(path)!, file);
                    yield return (file, SafeRelativePath(rel));
                }
            }
        }
    }

    private static string SafeRelativePath(string name)
    {
        var parts = name.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not "." and not "..")
            .Select(p => string.Concat(p.Split(Path.GetInvalidFileNameChars())));
        return Path.Combine(parts.ToArray());
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{stem} {Guid.NewGuid():N}{ext}");
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }
}
