using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PearlPull;

internal static class PairingChecks
{
    private static int checks;
    private static readonly string Key = new string('a', 64);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
    private static byte[] Request(RSACryptoServiceProvider rsa, string id, long now)
    {
        return Encoding.ASCII.GetBytes("PP2-PAIR|" + id + "|" + now + "|" + Convert.ToBase64String(rsa.ExportParameters(false).Modulus));
    }
    private static string[] Fields(byte[] packet) { return Encoding.ASCII.GetString(packet).Split('|'); }
    public static int ProtocolChecks()
    {
        checks = 0;
        long elapsed = 0, now = Protocol.NowMs();
        int prompts = 0;
        PairingOffer offer = null;
        PairingServer server = new PairingServer(Key, delegate(PairingOffer value) { prompts++; offer = value; }, delegate { return elapsed; });
        IPEndPoint peer = new IPEndPoint(IPAddress.Loopback, 43123);
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
        using (RSACryptoServiceProvider other = new RSACryptoServiceProvider(2048))
        {
            rsa.PersistKeyInCsp = false; other.PersistKeyInCsp = false;
            string id = Guid.NewGuid().ToString("N");
            byte[] packet = Request(rsa, id, now);
            string[] pending = Fields(server.Handle(packet, peer, now));
            Check(pending.Length == 3 && pending[2] == "pending", "No key may be sent before approval");
            Check(prompts == 1 && server.Pending == offer, "Request must create one pending offer");
            Check(offer.Code.Length == 9 && offer.Code[4] == '-', "Code must have two groups of four characters");
            Check(offer.Code == PairingServer.CodeFor(Encoding.ASCII.GetString(packet)), "Code must match request");
            byte[] replaced = Request(other, id, now);
            Check(offer.Code != PairingServer.CodeFor(Encoding.ASCII.GetString(replaced)), "Substituting public key must change comparison code");
            Check(server.Handle(replaced, peer, now) == null, "Same ID cannot replace public key");
            Check(server.Handle(packet, new IPEndPoint(IPAddress.Loopback, 43124), now) == null, "Another endpoint cannot take over request");
            Check(Fields(server.Handle(packet, peer, now))[2] == "pending" && prompts == 1, "UDP retries must not repeat prompts");
            Check(Fields(server.Handle(Request(other, Guid.NewGuid().ToString("N"), now), peer, now))[2] == "busy", "Concurrent requests must not replace visible offer");
            Check(!server.Decide(Guid.NewGuid().ToString("N"), true), "Unknown ID cannot be approved");
            Check(server.Decide(id, true), "Explicit approval must succeed");
            string[] accepted = Fields(server.Handle(packet, peer, now));
            Check(accepted.Length == 4 && accepted[2] == "accepted", "Approved request must return encrypted key");
            byte[] ciphertext = Convert.FromBase64String(accepted[3]);
            string decrypted = BitConverter.ToString(rsa.Decrypt(ciphertext, true)).Replace("-", "").ToLowerInvariant();
            Check(decrypted == Key && !Encoding.ASCII.GetString(server.Handle(packet, peer, now)).Contains(Key), "Only encrypted key may be sent");
            bool wrongKeyRejected = false;
            try { other.Decrypt(ciphertext, true); } catch (CryptographicException) { wrongKeyRejected = true; }
            Check(wrongKeyRejected, "Another private key cannot decrypt approval");
            Check(Fields(server.Handle(packet, peer, now))[3] == accepted[3] && prompts == 1, "Lost approval reply must be retried without a new prompt");
            Check(!server.Decide(id, true), "Approval cannot be applied twice");
            Check(Fields(server.Handle(Request(other, Guid.NewGuid().ToString("N"), now), peer, now))[2] == "busy", "New prompts must be rate limited");
            elapsed = 10001;
            string declineId = Guid.NewGuid().ToString("N");
            byte[] decline = Request(rsa, declineId, now);
            server.Handle(decline, peer, now);
            Check(server.Decide(declineId, false), "Decline must succeed");
            string[] declined = Fields(server.Handle(decline, peer, now));
            Check(declined.Length == 3 && declined[2] == "declined", "Declined request cannot receive a key");
            Check(!server.Decide(declineId, true), "Declined request cannot later be approved");
            elapsed = 20002;
            string expiredId = Guid.NewGuid().ToString("N");
            byte[] expired = Request(rsa, expiredId, now);
            server.Handle(expired, peer, now);
            elapsed += PairingServer.LifetimeMs;
            Check(server.Pending == null && !server.Decide(expiredId, true), "Expired request cannot be approved");
            Check(Fields(server.Handle(expired, peer, now))[2] == "expired", "Expired retries cannot create another prompt");
            Check(Fields(server.Handle(packet, peer, now + PairingServer.LifetimeMs + 1))[2] == "expired", "Old network request cannot be replayed");
            Check(Fields(server.Handle(Request(rsa, Guid.NewGuid().ToString("N"), now + 60000), peer, now))[2] == "expired", "Future timestamps must be rejected");
            int beforeMalformed = prompts;
            Check(server.Handle(new byte[513], peer, now) == null, "Oversize requests must be dropped");
            Check(server.Handle(Encoding.ASCII.GetBytes("PP2-PAIR|bad|0|bad"), peer, now) == null, "Malformed ID must be dropped");
            Check(server.Handle(Encoding.ASCII.GetBytes("PP2-PAIR|" + Guid.NewGuid().ToString("N") + "|" + now + "|not-base64"), peer, now) == null, "Malformed public key must be dropped");
            Check(server.Handle(Encoding.ASCII.GetBytes("PP2-PAIR|" + Guid.NewGuid().ToString("N") + "|" + now + "|" + Convert.ToBase64String(new byte[256])), peer, now) == null, "Weak public key must be dropped");
            Check(prompts == beforeMalformed, "Malformed requests must not prompt");
            server.Close();
            Check(Fields(server.Handle(Request(rsa, Guid.NewGuid().ToString("N"), now), peer, now))[2] == "unavailable", "Closed server cannot grant pairing");
        }
        return checks;
    }
    public static void Live(string decision, int port)
    {
        using (CancellationTokenSource stop = new CancellationTokenSource())
        using (CancellationTokenSource cancelPair = new CancellationTokenSource())
        using (ManualResetEvent ready = new ManualResetEvent(false))
        using (ManualResetEvent requested = new ManualResetEvent(false))
        using (PairingClient client = new PairingClient())
        {
            PairingOffer offer = null;
            PairingServer pairing = new PairingServer(Key, delegate(PairingOffer value) { offer = value; requested.Set(); });
            Action<string, string, string> onTrace = delegate(string level, string name, string details) { if (name == "receiver.listening") ready.Set(); };
            TraceLog.Entry += onTrace;
            Task receiver = Task.Factory.StartNew(delegate { Transport.Listen("127.0.0.1", port, Key, true, stop.Token, pairing); }, TaskCreationOptions.LongRunning);
            Task<string> sender = null;
            try
            {
                if (!ready.WaitOne(5000)) throw new Exception("Pairing receiver failed to listen");
                sender = Task.Run(delegate { return client.Pair("127.0.0.1", port, cancelPair.Token); });
                if (!requested.WaitOne(5000)) throw new Exception("Pairing request never arrived");
                Check(offer.Code == client.Code, "Both screens must show the same code");
                Check(!sender.IsCompleted, "Client cannot finish before approval");
                Check(Transport.Send("127.0.0.1", port, Key, "ping") == "ready", "Existing pairing must keep working while approval is pending");
                if (decision == "cancel") cancelPair.Cancel();
                else Check(pairing.Decide(offer.Id, decision == "accept"), "Receiver decision failed");
                Exception failure = null;
                try { if (!sender.Wait(5000)) throw new Exception("Pairing decision did not reach client"); }
                catch (AggregateException ex) { failure = ex.GetBaseException(); }
                if (decision == "accept") Check(failure == null && sender.Result == Key, "Approved pairing failed");
                else if (decision == "decline") Check(failure is InvalidOperationException && failure.Message.Contains("declined"), "Client did not report declined request");
                else Check(failure is OperationCanceledException, "Canceled request must not save a key");
                Console.WriteLine("Live pairing " + decision + " passed");
            }
            finally
            {
                cancelPair.Cancel();
                if (sender != null) { try { sender.Wait(3000); } catch (AggregateException) { } }
                stop.Cancel(); pairing.Close(); receiver.Wait(3000); TraceLog.Entry -= onTrace;
            }
        }
    }
    public static void LostAndForgedReplies(int port)
    {
        using (UdpClient socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
        using (PairingClient client = new PairingClient())
        {
            socket.Client.ReceiveTimeout = 5000;
            PairingOffer offer = null;
            PairingServer pairing = new PairingServer(Key, delegate(PairingOffer value) { offer = value; });
            Task server = Task.Run(delegate
            {
                string first = null;
                int requests = 0;
                while (true)
                {
                    IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet = socket.Receive(ref peer);
                    if (!PairingServer.IsRequest(packet))
                    {
                        string[] ping = Protocol.ReadRequest(packet, Protocol.ParseKey(Key), Protocol.NowMs());
                        Check(ping != null && ping[3] == "ping", "Client must verify a ping after approval");
                        byte[] pong = Protocol.Reply(ping[1], "ready", Protocol.ParseKey(Key));
                        socket.Send(pong, pong.Length, peer);
                        Check(requests >= 4, "Client must retry dropped/invalid replies");
                        return;
                    }
                    requests++;
                    string wire = Encoding.ASCII.GetString(packet);
                    if (first == null) first = wire;
                    Check(wire == first, "Retries must use the same request and public key");
                    byte[] reply = pairing.Handle(packet, peer, Protocol.NowMs());
                    if (requests == 1) reply = Encoding.ASCII.GetBytes("PP2-PAIR-ACK|" + Guid.NewGuid().ToString("N") + "|accepted|AAAA");
                    else if (requests == 2) reply = Encoding.ASCII.GetBytes("PP2-PAIR-ACK|" + client.Id + "|accepted|AAAA");
                    else if (requests == 3) { Check(pairing.Decide(offer.Id, true), "Approval must succeed"); continue; }
                    socket.Send(reply, reply.Length, peer);
                }
            });
            try
            {
                Check(client.Pair("127.0.0.1", port, CancellationToken.None) == Key, "Client must recover from invalid and lost replies");
                server.Wait(6000);
                Console.WriteLine("Pairing retry and forged-reply checks passed");
            }
            finally { socket.Close(); try { server.Wait(6000); } catch (AggregateException) { if (server.IsFaulted) throw; } }
        }
    }
    public static void Offline(int port)
    {
        using (PairingClient client = new PairingClient())
        {
            Stopwatch clock = Stopwatch.StartNew();
            bool timedOut = false;
            try { client.Pair("127.0.0.1", port, CancellationToken.None); }
            catch (TimeoutException ex) { timedOut = ex.Message.Contains("Update both apps"); }
            Check(timedOut && clock.ElapsedMilliseconds < 12000, "Offline pairing must stop promptly with setup guidance");
            Console.WriteLine("Offline pairing timeout passed");
        }
    }
}
