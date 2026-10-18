using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PearlPull
{
    public sealed class PairingOffer
    {
        public string Id { get; internal set; }
        public string Address { get; internal set; }
        public string Code { get; internal set; }
    }

    // Approval binds the displayed code to a fresh public key. The existing
    // 256-bit control key is released only to that key, encrypted with RSA OAEP.
    public sealed class PairingServer
    {
        private sealed class Session
        {
            public PairingOffer Offer;
            public string Request, Peer, Modulus, Status = "pending", EncryptedKey;
            public long Started;
        }
        public const int LifetimeMs = 60000;
        private readonly object gate = new object();
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly byte[] key;
        private readonly Action<PairingOffer> requested;
        private readonly Func<long> elapsed;
        private long nextRequest;
        private bool closed;

        public PairingServer(string controlKey, Action<PairingOffer> requested)
            : this(controlKey, requested, Stopwatch.StartNew()) { }
        private PairingServer(string controlKey, Action<PairingOffer> requested, Stopwatch clock)
            : this(controlKey, requested, delegate { return clock.ElapsedMilliseconds; }) { }
        public PairingServer(string controlKey, Action<PairingOffer> requested, Func<long> elapsed)
        {
            key = Protocol.ParseKey(controlKey);
            this.requested = requested;
            this.elapsed = elapsed;
        }
        public static bool IsRequest(byte[] packet)
        {
            return packet.Length >= 9 && Encoding.ASCII.GetString(packet, 0, 9) == "PP2-PAIR|";
        }
        internal static string CodeFor(string request)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] digest;
            using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(Encoding.ASCII.GetBytes(request));
            // Eight base-32 characters (40 bits), grouped for a visual comparison.
            ulong bits = 0;
            for (int i = 0; i < 5; i++) bits = (bits << 8) | digest[i];
            char[] code = new char[9];
            for (int i = 7; i >= 0; i--) { code[i < 4 ? i : i + 1] = alphabet[(int)(bits & 31)]; bits >>= 5; }
            code[4] = '-';
            return new string(code);
        }
        private static byte[] Reply(string id, string status, string payload)
        {
            return Encoding.ASCII.GetBytes("PP2-PAIR-ACK|" + id + "|" + status + (payload == null ? "" : "|" + payload));
        }
        private void Expire()
        {
            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, Session> entry in sessions)
            {
                if (elapsed() - entry.Value.Started >= LifetimeMs && entry.Value.Status == "pending")
                {
                    entry.Value.Status = "expired";
                    TraceLog.Write("INFO", "pairing.expired", "request_id=" + entry.Key);
                }
                if (elapsed() - entry.Value.Started > LifetimeMs + 30000) remove.Add(entry.Key);
            }
            foreach (string id in remove) sessions.Remove(id);
        }
        public PairingOffer Pending
        {
            get
            {
                lock (gate)
                {
                    Expire();
                    foreach (Session session in sessions.Values) if (session.Status == "pending") return session.Offer;
                    return null;
                }
            }
        }
        public byte[] Handle(byte[] packet, IPEndPoint peer, long nowUtcMs)
        {
            if (packet.Length > 512) return null;
            string wire = Encoding.ASCII.GetString(packet);
            string[] fields = wire.Split('|');
            Guid id;
            long issued;
            if (fields.Length != 4 || fields[0] != "PP2-PAIR" || fields[1].Length != 32 ||
                !Guid.TryParseExact(fields[1], "N", out id) || !long.TryParse(fields[2], out issued)) return null;
            if (issued > nowUtcMs + Protocol.MaxAgeMs || issued < nowUtcMs - LifetimeMs)
                return Reply(fields[1], "expired", null);
            PairingOffer offer;
            lock (gate)
            {
                if (closed) return Reply(fields[1], "unavailable", null);
                Expire();
                Session existing;
                if (sessions.TryGetValue(fields[1], out existing))
                {
                    if (existing.Request != wire || existing.Peer != peer.ToString()) return null;
                    return Reply(fields[1], existing.Status, existing.EncryptedKey);
                }
                if (elapsed() < nextRequest || sessions.Count >= 10 || Pending != null) return Reply(fields[1], "busy", null);
                byte[] modulus;
                try { modulus = Convert.FromBase64String(fields[3]); }
                catch (FormatException) { return null; }
                if (modulus.Length != 256 || modulus[0] < 128 || (modulus[255] & 1) == 0) return null;
                offer = new PairingOffer { Id = fields[1], Address = peer.Address.ToString(), Code = CodeFor(wire) };
                sessions.Add(offer.Id, new Session { Offer = offer, Request = wire, Peer = peer.ToString(), Modulus = fields[3], Started = elapsed() });
                nextRequest = elapsed() + 10000;
            }
            TraceLog.Write("INFO", "pairing.requested", "request_id=" + offer.Id + " peer=" + peer);
            try { requested(offer); }
            catch (Exception ex) { Decide(offer.Id, false); TraceLog.Failure("pairing.prompt_failed", ex, "request_id=" + offer.Id); }
            return Reply(offer.Id, "pending", null);
        }
        public bool Decide(string id, bool accept)
        {
            lock (gate)
            {
                Expire();
                Session session;
                if (closed || !sessions.TryGetValue(id, out session) || session.Status != "pending") return false;
                if (accept)
                {
                    using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
                    {
                        rsa.PersistKeyInCsp = false;
                        rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(session.Modulus), Exponent = new byte[] { 1, 0, 1 } });
                        session.EncryptedKey = Convert.ToBase64String(rsa.Encrypt(key, true));
                    }
                }
                session.Status = accept ? "accepted" : "declined";
                TraceLog.Write("INFO", "pairing." + session.Status, "request_id=" + id + " peer=" + session.Offer.Address);
                return true;
            }
        }
        public void Close() { lock (gate) { closed = true; sessions.Clear(); } }
    }

    public sealed class PairingClient : IDisposable
    {
        private readonly RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);
        private readonly byte[] request;
        public string Id { get; private set; }
        public string Code { get; private set; }
        public PairingClient()
        {
            rsa.PersistKeyInCsp = false;
            Id = Guid.NewGuid().ToString("N");
            string wire = "PP2-PAIR|" + Id + "|" + Protocol.NowMs() + "|" + Convert.ToBase64String(rsa.ExportParameters(false).Modulus);
            request = Encoding.ASCII.GetBytes(wire);
            Code = PairingServer.CodeFor(wire);
        }
        public string Pair(string address, int port, CancellationToken cancellation)
        {
            TraceLog.Write("INFO", "pairing.started", "request_id=" + Id + " destination=" + address + ":" + port);
            Stopwatch clock = Stopwatch.StartNew();
            bool reachedReceiver = false;
            using (UdpClient socket = new UdpClient(AddressFamily.InterNetwork))
            {
                socket.Connect(IPAddress.Parse(address), port);
                socket.Client.ReceiveTimeout = 250;
                while (clock.ElapsedMilliseconds < PairingServer.LifetimeMs)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        socket.Send(request, request.Length);
                        IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                        byte[] packet = socket.Receive(ref peer);
                        if (packet.Length > 512) continue;
                        string[] fields = Encoding.ASCII.GetString(packet).Split('|');
                        if (fields.Length < 3 || fields[0] != "PP2-PAIR-ACK" || fields[1] != Id) continue;
                        string status = fields[2];
                        if (status == "accepted" && fields.Length == 4)
                        {
                            byte[] secret;
                            try { secret = rsa.Decrypt(Convert.FromBase64String(fields[3]), true); }
                            catch (CryptographicException) { continue; }
                            catch (FormatException) { continue; }
                            if (secret.Length != 32) continue;
                            string key = BitConverter.ToString(secret).Replace("-", "").ToLowerInvariant();
                            cancellation.ThrowIfCancellationRequested();
                            if (Transport.Send(address, port, key, "ping") != "ready") throw new InvalidOperationException("PC 2 could not confirm the pairing. Try again.");
                            TraceLog.Write("INFO", "pairing.completed", "request_id=" + Id + " receiver_verified=true");
                            return key;
                        }
                        if (status == "declined") throw new InvalidOperationException("Pairing was declined on PC 2. Click Pair with PC 2 to try again.");
                        if (status == "expired") throw new TimeoutException("Pairing expired. Try again and accept on PC 2 within one minute. Keep both Windows clocks synchronized.");
                        if (status == "busy") throw new InvalidOperationException("PC 2 is already handling a pairing request. Wait a moment and try again.");
                        if (status == "unavailable") throw new InvalidOperationException("PC 2 is restarting its receiver. Try again in a moment.");
                        if (status == "pending" && !reachedReceiver)
                        {
                            reachedReceiver = true;
                            TraceLog.Write("INFO", "pairing.awaiting_approval", "request_id=" + Id);
                        }
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock &&
                            ex.SocketErrorCode != SocketError.ConnectionReset && ex.SocketErrorCode != SocketError.ConnectionRefused) throw;
                    }
                    if (!reachedReceiver && clock.ElapsedMilliseconds >= 5000)
                        throw new TimeoutException("No pairing reply from PC 2. Update both apps, check PC 2's address, and use Allow home network on its Receiver.");
                    cancellation.WaitHandle.WaitOne(250);
                }
            }
            throw new TimeoutException("Pairing expired. Click Pair with PC 2 and accept on PC 2 within one minute.");
        }
        public void Dispose() { rsa.Dispose(); }
    }
}
