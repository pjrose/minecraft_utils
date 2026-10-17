using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PearlPull
{
    public static class TraceLog
    {
        public const string Build = "0.5.0";
        public static event Action<string, string, string> Entry;
        private static readonly object Gate = new object();
        private static readonly Regex Secret = new Regex("[0-9a-fA-F]{64}");
        private static StreamWriter writer;
        private static long bytesWritten;
        private static string role = "core";
        public static string FilePath { get; private set; }

        public static void Initialize(string component, string directory)
        {
            lock (Gate)
            {
                Close();
                if (!Regex.IsMatch(component, "^[a-z-]+$")) throw new ArgumentException("Invalid log role.");
                role = component;
                FilePath = null;
                try
                {
                    Directory.CreateDirectory(directory);
                    // Retain at most five inactive sessions per role. Active files on Windows
                    // cannot be deleted because the writer does not grant FileShare.Delete.
                    string[] old = Directory.GetFiles(directory, role + "-*.log");
                    Array.Sort(old, StringComparer.Ordinal);
                    for (int i = 0; i < old.Length - 4; i++)
                    {
                        try { File.Delete(old[i]); File.Delete(old[i] + ".previous"); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    FilePath = Path.Combine(directory, role + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                    OpenFile();
                }
                catch (Exception ex)
                {
                    FilePath = null;
                    ConsoleLine("WARN", "log.file_unavailable", "exception=" + ex.GetType().Name + " console_logging_continues=true");
                }
                Write("INFO", "app.started", "build=" + Build + " pid=" + Process.GetCurrentProcess().Id + " process_bits=" + (IntPtr.Size * 8));
                if (FilePath != null) Write("INFO", "log.file", "path=" + FilePath);
            }
        }
        private static void OpenFile()
        {
            writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            writer.AutoFlush = true;
            bytesWritten = 0;
        }
        private static string Clean(string value)
        {
            return Secret.Replace(value ?? "", "[REDACTED]").Replace("\r", " ").Replace("\n", " ");
        }
        private static string Line(string level, string name, string details)
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + " [" + role.ToUpperInvariant() + "] " + level + " " + name + " " + Clean(details);
        }
        private static void ConsoleLine(string level, string name, string details)
        {
            try { Console.WriteLine(Line(level, name, details)); }
            catch (IOException) { }
        }
        public static void Write(string level, string name, string details)
        {
            lock (Gate)
            {
                string line = Line(level, name, details);
                try { Console.WriteLine(line); } catch (IOException) { }
                Action<string, string, string> handler = Entry;
                if (handler != null) { try { handler(level, name, Clean(details)); } catch (Exception) { } }
                if (writer == null) return;
                try
                {
                    if (bytesWritten > 1024 * 1024)
                    {
                        writer.Dispose(); writer = null;
                        File.Delete(FilePath + ".previous");
                        File.Move(FilePath, FilePath + ".previous");
                        OpenFile();
                    }
                    writer.WriteLine(line);
                    bytesWritten += Encoding.UTF8.GetByteCount(line) + 2;
                }
                catch (Exception ex)
                {
                    Close();
                    ConsoleLine("WARN", "log.write_failed", "exception=" + ex.GetType().Name + " console_logging_continues=true");
                }
            }
        }
        public static void Failure(string name, Exception exception, string details)
        {
            Exception cause = exception.GetBaseException();
            Win32Exception native = cause as Win32Exception;
            SocketException socket = cause as SocketException;
            Write("ERROR", name, details + " exception=" + cause.GetType().Name + " hresult=" + cause.HResult +
                (native == null ? "" : " win32_error=" + native.NativeErrorCode) +
                (socket == null ? "" : " socket_error=" + socket.SocketErrorCode) + " message=" + cause.Message);
        }
        public static void Close()
        {
            lock (Gate)
            {
                if (writer != null) { try { writer.Dispose(); } catch (IOException) { } writer = null; }
            }
        }
    }

    public static class Protocol
    {
        public const int DefaultPort = 48123;
        public const long MaxAgeMs = 5000;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static byte[] ParseKey(string value)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("Pairing code must contain 64 hexadecimal characters.");
            byte[] bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        public static string NewKey()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
            return Hex(bytes);
        }
        public static string Fingerprint(string pairingCode)
        {
            using (SHA256 hash = SHA256.Create()) { return Hex(hash.ComputeHash(ParseKey(pairingCode))).Substring(0, 12).ToUpperInvariant(); }
        }
        private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
        private static string Sign(string value, byte[] key)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key)) { return Hex(hmac.ComputeHash(Encoding.ASCII.GetBytes(value))); }
        }
        private static bool Verify(string value, string signature, byte[] key)
        {
            string expected = Sign(value, key);
            if (signature.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ signature[i];
            return difference == 0;
        }
        public static byte[] Request(string id, long now, string command, byte[] key)
        {
            string body = "PP1|" + id + "|" + now.ToString(CultureInfo.InvariantCulture) + "|" + command;
            return Encoding.ASCII.GetBytes(body + "|" + Sign(body, key));
        }
        public static string[] ReadRequest(byte[] packet, byte[] key, long now)
        {
            string reason;
            return ReadRequest(packet, key, now, out reason);
        }
        public static string[] ReadRequest(byte[] packet, byte[] key, long now, out string reason)
        {
            reason = "malformed_request";
            if (packet == null || packet.Length > 256) return null;
            string[] parts = Encoding.ASCII.GetString(packet).Split('|');
            Guid id;
            long issued;
            if (parts.Length != 5 || parts[0] != "PP1" || parts[1].Length != 32 ||
                !Guid.TryParseExact(parts[1], "N", out id) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out issued) ||
                (parts[3] != "click" && parts[3] != "ping")) return null;
            string body = string.Join("|", parts, 0, 4);
            if (!Verify(body, parts[4], key)) { reason = "authentication_failed pairing_settings_may_differ=true"; return null; }
            if (issued < now - MaxAgeMs || issued > now + MaxAgeMs)
            {
                reason = "clock_skew request_id=" + parts[1] + " age_ms=" + (now - issued) + " allowed_skew_ms=" + MaxAgeMs;
                return null;
            }
            reason = "accepted";
            return parts;
        }
        public static byte[] Reply(string id, string status, byte[] key)
        {
            string body = "PP1-ACK|" + id + "|" + status;
            return Encoding.ASCII.GetBytes(body + "|" + Sign(body, key));
        }
        public static string ReadReply(byte[] packet, string id, byte[] key)
        {
            string reason;
            return ReadReply(packet, id, key, out reason);
        }
        public static string ReadReply(byte[] packet, string id, byte[] key, out string reason)
        {
            reason = "malformed_reply";
            if (packet == null || packet.Length > 256) return null;
            string[] parts = Encoding.ASCII.GetString(packet).Split('|');
            if (parts.Length != 4 || parts[0] != "PP1-ACK") return null;
            if (parts[1] != id) { reason = "request_id_mismatch"; return null; }
            if (!Verify(string.Join("|", parts, 0, 3), parts[3], key)) { reason = "authentication_failed"; return null; }
            reason = "accepted";
            return parts[2];
        }
    }

    // Shared by the live receiver and tests, which supply a fake desktop.
    public sealed class RequestHandler
    {
        private sealed class CachedReply { public byte[] Packet; public long Expires; }
        private readonly byte[] key;
        private readonly bool dryRun;
        private readonly Func<bool> focused;
        private readonly Action click;
        private readonly Dictionary<string, CachedReply> replies = new Dictionary<string, CachedReply>();
        private long lastClick = long.MinValue;

        public RequestHandler(byte[] key, bool dryRun, Func<bool> focused, Action click)
        {
            this.key = key; this.dryRun = dryRun; this.focused = focused; this.click = click;
        }
        public byte[] Handle(byte[] packet, long now, long uptimeMs)
        {
            string rejection;
            string[] request = Protocol.ReadRequest(packet, key, now, out rejection);
            if (request == null) { TraceLog.Write("WARN", "request.rejected", "reason=" + rejection); return null; }
            string context = "request_id=" + request[1] + " command=" + request[3];
            TraceLog.Write("INFO", "request.accepted", context);
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, CachedReply> entry in replies)
                if (entry.Value.Expires < uptimeMs) expired.Add(entry.Key);
            foreach (string id in expired) replies.Remove(id);
            CachedReply cached;
            if (replies.TryGetValue(request[1], out cached))
            {
                TraceLog.Write("INFO", "request.duplicate", context + " cached_reply=true second_click=false");
                return cached.Packet;
            }

            string status;
            if (request[3] == "ping") status = "ready";
            else if (lastClick != long.MinValue && uptimeMs - lastClick < 500) status = "cooldown";
            else if (dryRun) { status = "dry-run"; lastClick = uptimeMs; }
            else
            {
                bool isFocused = focused();
                TraceLog.Write("INFO", "focus.checked", context + " minecraft_focused=" + isFocused);
                if (!isFocused) status = "unfocused";
                else
                {
                    lastClick = uptimeMs;
                    TraceLog.Write("INFO", "click.started", context);
                    try { click(); status = "clicked"; }
                    catch (Exception ex) { TraceLog.Failure("click.failed", ex, context); status = "input-error"; }
                }
            }
            TraceLog.Write(status == "input-error" ? "ERROR" : "INFO", "request.result", context + " status=" + status);
            byte[] reply = Protocol.Reply(request[1], status, key);
            replies[request[1]] = new CachedReply { Packet = reply, Expires = uptimeMs + 15000 };
            return reply;
        }
    }

    public static class Transport
    {
        public static string Send(string address, int port, string pairingCode, string command)
        {
            return Send(address, port, pairingCode, command, Guid.NewGuid().ToString("N"));
        }
        public static string Send(string address, int port, string pairingCode, string command, string id)
        {
            Guid requestId;
            if (!Guid.TryParseExact(id, "N", out requestId)) throw new ArgumentException("Invalid request ID.");
            byte[] key = Protocol.ParseKey(pairingCode);
            byte[] request = Protocol.Request(id, Protocol.NowMs(), command, key);
            string context = "request_id=" + id + " command=" + command + " destination=" + address + ":" + port;
            Stopwatch total = Stopwatch.StartNew();
            TraceLog.Write("INFO", "request.started", context);
            using (UdpClient socket = new UdpClient(AddressFamily.InterNetwork))
            {
                try
                {
                    socket.Connect(IPAddress.Parse(address), port);
                    socket.Client.ReceiveTimeout = 200;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        socket.Send(request, request.Length);
                        TraceLog.Write("TRACE", "packet.sent", context + " attempt=" + (attempt + 1) + "/3 local=" + socket.Client.LocalEndPoint + " bytes=" + request.Length);
                        Stopwatch wait = Stopwatch.StartNew();
                        while (wait.ElapsedMilliseconds < 200)
                        {
                            try
                            {
                                IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                                byte[] packet = socket.Receive(ref peer);
                                string reason;
                                string result = Protocol.ReadReply(packet, id, key, out reason);
                                if (result != null)
                                {
                                    TraceLog.Write("INFO", "reply.accepted", context + " status=" + result + " elapsed_ms=" + total.ElapsedMilliseconds);
                                    return result;
                                }
                                TraceLog.Write("WARN", "reply.rejected", context + " reason=" + reason + " bytes=" + packet.Length);
                            }
                            catch (SocketException ex)
                            {
                                if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock) break;
                                throw;
                            }
                        }
                        TraceLog.Write("WARN", "reply.wait_expired", context + " attempt=" + (attempt + 1) + "/3 elapsed_ms=" + total.ElapsedMilliseconds);
                    }
                }
                catch (SocketException ex) { TraceLog.Failure("network.failed", ex, context); throw; }
            }
            TraceLog.Write("ERROR", "request.timed_out", context + " elapsed_ms=" + total.ElapsedMilliseconds);
            string timeoutContext = command == "ping"
                ? " This was a connectivity check; no click was requested."
                : " The click may have happened if replies were lost; check before retrying.";
            throw new TimeoutException("No reply from PC 2. Check its receiver, IP address, pairing code, firewall, and Windows clock synchronization." + timeoutContext);
        }

        public static void Listen(string address, int port, string pairingCode, bool dryRun)
        {
            Listen(address, port, pairingCode, dryRun, CancellationToken.None);
        }
        public static void Listen(string address, int port, string pairingCode, bool dryRun, CancellationToken cancellation)
        {
            byte[] key = Protocol.ParseKey(pairingCode);
            RequestHandler handler = new RequestHandler(key, dryRun, DesktopInput.IsMinecraftFocused, DesktopInput.RightClick);
            using (UdpClient socket = new UdpClient(new IPEndPoint(IPAddress.Parse(address), port)))
            {
                Stopwatch uptime = Stopwatch.StartNew();
                long lastHeartbeat = 0;
                long received = 0;
                socket.Client.ReceiveTimeout = 1000;
                TraceLog.Write("INFO", "receiver.listening", "bind=" + address + ":" + port + " dry_run=" + dryRun + " hotkey_listener=false");
                Console.WriteLine("Receiver listening on UDP port {0}. Mode: {1}.", port, dryRun ? "DRY RUN (no input)" : "right-click");
                while (!cancellation.IsCancellationRequested)
                {
                    if (uptime.ElapsedMilliseconds - lastHeartbeat >= 30000)
                    {
                        TraceLog.Write("INFO", "receiver.waiting", "packets_received=" + received + " uptime_seconds=" + (uptime.ElapsedMilliseconds / 1000));
                        lastHeartbeat = uptime.ElapsedMilliseconds;
                    }
                    IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet;
                    try { packet = socket.Receive(ref peer); }
                    catch (SocketException ex)
                    {
                        // Windows reports an ICMP reply from a departed sender on the next receive.
                        if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock) continue;
                        if (ex.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            TraceLog.Write("WARN", "receiver.peer_disconnected", "continuing=true");
                            continue;
                        }
                        TraceLog.Failure("receiver.receive_failed", ex, "bind=" + address + ":" + port);
                        throw;
                    }
                    if (cancellation.IsCancellationRequested) break;
                    received++;
                    TraceLog.Write("TRACE", "packet.received", "peer=" + peer + " bytes=" + packet.Length + " packet_number=" + received);
                    byte[] reply = handler.Handle(packet, Protocol.NowMs(), uptime.ElapsedMilliseconds);
                    if (reply == null) continue;
                    try { socket.Send(reply, reply.Length, peer); }
                    catch (SocketException ex) { TraceLog.Failure("receiver.reply_failed", ex, "peer=" + peer); throw; }
                    string[] fields = Encoding.ASCII.GetString(reply).Split('|');
                    TraceLog.Write("INFO", "reply.sent", "request_id=" + fields[1] + " peer=" + peer + " status=" + fields[2]);
                }
                TraceLog.Write("INFO", "receiver.stopped", "listener_closed=true");
            }
        }
    }

    // Stream Deck's built-in Website action can send a background GET directly
    // to this loopback-only endpoint, without synthetic keyboard input.
    public sealed class StreamDeckTrigger : IDisposable
    {
        private readonly TcpListener listener;
        private readonly string address, pairingCode, path;
        private readonly int receiverPort;
        private Thread thread;
        private volatile bool running;
        private int sending;
        private readonly List<TcpClient> clients = new List<TcpClient>();
        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public StreamDeckTrigger(string address, int receiverPort, string pairingCode, string token, int localPort)
        {
            Protocol.ParseKey(pairingCode);
            Protocol.ParseKey(token);
            this.address = address; this.receiverPort = receiverPort; this.pairingCode = pairingCode;
            path = "/pull/" + token;
            listener = new TcpListener(IPAddress.Loopback, localPort);
        }
        public void Start()
        {
            listener.Start();
            running = true;
            TraceLog.Write("INFO", "streamdeck.listening", "bind=127.0.0.1:" + Port + " token_required=true keyboard_input_required=false");
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }
        public void Wait() { thread.Join(); }
        private static bool Matches(string actual, string expected)
        {
            if (actual.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ expected[i];
            return difference == 0;
        }
        private static void Reply(NetworkStream stream, int status, string body)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(body + "\n");
            string header = "HTTP/1.1 " + status + " Result\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n";
            byte[] headers = Encoding.ASCII.GetBytes(header);
            stream.Write(headers, 0, headers.Length);
            stream.Write(bytes, 0, bytes.Length);
        }
        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 1000;
            client.SendTimeout = 1000;
            using (NetworkStream stream = client.GetStream())
            {
                StringBuilder header = new StringBuilder();
                Stopwatch reading = Stopwatch.StartNew();
                bool complete = false;
                while (header.Length < 8192 && reading.ElapsedMilliseconds < 2000)
                {
                    int value = stream.ReadByte();
                    if (value < 0) return;
                    if (value > 127 || value == 0) break;
                    header.Append((char)value);
                    int n = header.Length;
                    if (n >= 4 && header[n-4] == '\r' && header[n-3] == '\n' && header[n-2] == '\r' && header[n-1] == '\n') { complete = true; break; }
                }
                if (!complete) { TraceLog.Write("WARN", "streamdeck.rejected", "reason=invalid_headers"); Reply(stream, 400, "Invalid request"); return; }
                string[] lines = header.ToString().Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] request = lines[0].Split(' ');
                string host = null;
                foreach (string line in lines)
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (host != null) { Reply(stream, 400, "Duplicate Host"); return; }
                        host = line.Substring(5).Trim();
                    }
                }
                if (request.Length != 3 || request[0] != "GET")
                {
                    TraceLog.Write("WARN", "streamdeck.rejected", "reason=expected_GET"); Reply(stream, 405, "Use GET"); return;
                }
                if (host != "127.0.0.1:" + Port)
                {
                    TraceLog.Write("WARN", "streamdeck.rejected", "reason=unexpected_host"); Reply(stream, 403, "Invalid host"); return;
                }
                if (request[1] == "/health") { Reply(stream, 200, "Local sender is listening; this does not test PC 2"); return; }
                if (!Matches(request[1], path))
                {
                    // Never log URLs, paths, raw headers, or tokens.
                    TraceLog.Write("WARN", "streamdeck.rejected", "reason=wrong_URL_or_token"); Reply(stream, 403, "Use the URL from Setup-StreamDeck.cmd"); return;
                }
                if (!running || reading.ElapsedMilliseconds > 200)
                {
                    TraceLog.Write("WARN", "streamdeck.rejected", "reason=stopping_or_stale_request no_click_requested=true"); Reply(stream, 503, "Request expired; check the sender"); return;
                }
                if (Interlocked.CompareExchange(ref sending, 1, 0) != 0)
                {
                    TraceLog.Write("WARN", "streamdeck.rejected", "reason=busy no_click_requested=true"); Reply(stream, 409, "A request is already in progress; this press was discarded"); return;
                }
                try
                {
                    string id = Guid.NewGuid().ToString("N");
                    TraceLog.Write("INFO", "streamdeck.triggered", "request_id=" + id + " input=local_HTTP no_hotkey_required=true");
                    string result;
                    try { result = Transport.Send(address, receiverPort, pairingCode, "click", id); }
                    catch (Exception ex)
                    {
                        TraceLog.Failure("streamdeck.request_failed", ex, "request_id=" + id);
                        Reply(stream, 504, "No successful receiver reply; check logs before retrying"); return;
                    }
                    TraceLog.Write("INFO", "streamdeck.completed", "request_id=" + id + " receiver_status=" + result);
                    Reply(stream, result == "clicked" || result == "dry-run" ? 200 : 409, result);
                }
                finally { Interlocked.Exchange(ref sending, 0); }
            }
        }
        private void Run()
        {
            Stopwatch uptime = Stopwatch.StartNew();
            long heartbeat = 0;
            while (running)
            {
                try
                {
                    if (uptime.ElapsedMilliseconds - heartbeat >= 30000)
                    {
                        TraceLog.Write("INFO", "streamdeck.waiting", "bind=127.0.0.1:" + Port);
                        heartbeat = uptime.ElapsedMilliseconds;
                    }
                    if (!listener.Server.Poll(1000000, SelectMode.SelectRead)) continue;
                    TcpClient client = listener.AcceptTcpClient();
                    lock (clients)
                    {
                        if (!running || clients.Count >= 8) { client.Close(); continue; }
                        clients.Add(client);
                    }
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { using (client) { Handle(client); } }
                        catch (Exception ex) { if (running) TraceLog.Failure("streamdeck.connection_failed", ex, "continuing=true"); }
                        finally { lock (clients) { clients.Remove(client); Monitor.PulseAll(clients); } }
                    });
                }
                catch (Exception ex) { if (running) TraceLog.Failure("streamdeck.connection_failed", ex, "continuing=true"); }
            }
        }
        public void Dispose()
        {
            running = false;
            listener.Stop();
            if (thread != null) thread.Join(2500);
            lock (clients)
            {
                foreach (TcpClient client in clients) client.Close();
                Stopwatch wait = Stopwatch.StartNew();
                while (clients.Count > 0 && wait.ElapsedMilliseconds < 2500) Monitor.Wait(clients, 100);
            }
            TraceLog.Write("INFO", "streamdeck.stopped", "listener_closed=true");
        }
    }

    public static class DesktopInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Message
        {
            public IntPtr Window;
            public uint Id;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Position;
            public uint Private;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, IntPtr callback);
        [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, UIntPtr id);

        private static string ProcessName(IntPtr window)
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return "";
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                    return process.ProcessName;
            }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (Win32Exception) { return ""; }
        }
        public static bool IsMinecraftFocused()
        {
            IntPtr window = GetForegroundWindow();
            string name = ProcessName(window);
            uint foregroundPid;
            GetWindowThreadProcessId(window, out foregroundPid);
            TraceLog.Write("TRACE", "foreground.inspected", "process=" + (name.Length == 0 ? "unavailable" : name) + " pid=" + foregroundPid + " hwnd=" + window.ToInt64());
            if (string.Equals(name, "Minecraft.Windows", StringComparison.OrdinalIgnoreCase)) return true;
            // Older Bedrock installations can use a UWP frame belonging to this Windows host.
            if (!string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return false;
            bool found = false;
            EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
            {
                found = IsWindowVisible(child) && string.Equals(ProcessName(child), "Minecraft.Windows", StringComparison.OrdinalIgnoreCase);
                return !found;
            }, IntPtr.Zero);
            TraceLog.Write("TRACE", "foreground.uwp_checked", "minecraft_child_found=" + found);
            return found;
        }
        public static void RightClick()
        {
            TraceLog.Write("TRACE", "input.injecting", "button=right hold_ms=50");
            // Check again immediately before sending input. Never activate a window or move the aim.
            if (!IsMinecraftFocused()) throw new InvalidOperationException("Minecraft lost focus.");
            Input down = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0008 } };
            Input up = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0010 } };
            int size = Marshal.SizeOf(typeof(Input));
            if (SendInput(1, new Input[] { down }, size) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the mouse press.");
            try { System.Threading.Thread.Sleep(50); }
            finally
            {
                if (SendInput(1, new Input[] { up }, size) != 1)
                {
                    int error = Marshal.GetLastWin32Error();
                    SendInput(1, new Input[] { up }, size);
                    throw new Win32Exception(error, "Windows did not accept the mouse release.");
                }
                TraceLog.Write("TRACE", "input.injected", "right_down_accepted=true right_up_accepted=true");
            }
        }
        public static void RunHotkey(string address, int port, string pairingCode, int functionKey)
        {
            RunHotkey(address, port, pairingCode, functionKey, false);
        }
        public static void RunHotkey(string address, int port, string pairingCode, int functionKey, bool testOnly)
        {
            if (functionKey < 1 || functionKey > 24) throw new ArgumentOutOfRangeException("functionKey");
            const int id = 1;
            // MOD_NOREPEAT: holding the key generates one request until released.
            uint virtualKey = (uint)(0x70 + functionKey - 1);
            TraceLog.Write("INFO", "hotkey.registering", "key=F" + functionKey + " virtual_key=" + virtualKey + " test_only=" + testOnly);
            if (!RegisterHotKey(IntPtr.Zero, id, 0x4000, virtualKey))
            {
                Win32Exception error = new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the hotkey. Close another sender/hotkey tester or try another function key.");
                TraceLog.Failure("hotkey.registration_failed", error, "key=F" + functionKey);
                throw error;
            }
            TraceLog.Write("INFO", "hotkey.registered", "key=F" + functionKey + " modifiers=none no_repeat=true listener_pc=PC1");
            UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 30000, IntPtr.Zero);
            if (timer == UIntPtr.Zero) TraceLog.Write("WARN", "hotkey.heartbeat_unavailable", "win32_error=" + Marshal.GetLastWin32Error());
            long detected = 0;
            try
            {
                Console.WriteLine(testOnly ? "HOTKEY TEST: press F{0} on the keyboard, then the Neo. No network requests or clicks will be made." : "Press F{0} to request one right-click on PC 2. Close this window to stop.", functionKey);
                TraceLog.Write("INFO", "hotkey.waiting", "key=F" + functionKey + " detected=0 test_only=" + testOnly);
                Message message;
                int result;
                while ((result = GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.Id == 0x0113 && timer != UIntPtr.Zero && message.WParam == timer)
                    {
                        TraceLog.Write("INFO", "hotkey.waiting", "key=F" + functionKey + " detected=" + detected + " test_only=" + testOnly);
                        continue;
                    }
                    if (message.Id != 0x0312 || message.WParam.ToUInt64() != (ulong)id) continue;
                    detected++;
                    string requestId = Guid.NewGuid().ToString("N");
                    uint age = unchecked((uint)Environment.TickCount - message.Time);
                    string context = "request_id=" + requestId + " key=F" + functionKey + " detected=" + detected + " queue_age_ms=" + age;
                    TraceLog.Write("INFO", "hotkey.detected", context + " source=Windows_WM_HOTKEY device=not_reported");
                    // Do not execute keypresses that backed up while an earlier request timed out.
                    if (age > 200)
                    {
                        TraceLog.Write("WARN", "hotkey.discarded", context + " reason=stale_queued_event max_age_ms=200");
                        continue;
                    }
                    if (testOnly) { TraceLog.Write("INFO", "hotkey.test_passed", context + " network_request=false mouse_input=false"); continue; }
                    try
                    {
                        string status = Transport.Send(address, port, pairingCode, "click", requestId);
                        TraceLog.Write("INFO", "hotkey.completed", context + " receiver_status=" + status);
                    }
                    catch (Exception ex) { TraceLog.Failure("hotkey.request_failed", ex, context); }
                }
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (timer != UIntPtr.Zero) KillTimer(IntPtr.Zero, timer);
                UnregisterHotKey(IntPtr.Zero, id);
                TraceLog.Write("INFO", "hotkey.unregistered", "key=F" + functionKey + " detected=" + detected);
            }
        }
    }
}
