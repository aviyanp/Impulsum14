using System.Net;
using System.Net.Sockets;
using Blaze.Core;
using Blaze3SDK.Blaze.Util;
using EATDF;
using EATDF.Serialization;
using EATDF.Types;
using Microsoft.Extensions.Logging.Abstractions;
using ProtoFire;
using ProtoFire.Frames;
using ProtoFire.Tls;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;

internal static class TransportTests
{
    internal static void Register(Action<string, Func<Task>> test)
    {
        test("Portable PFX loading preserves the TLS identity and rejects invalid key stores", PortableCertificate);
        test("Fragmented Fire/Fire2 frames preserve jumbo contexts and metadata", FragmentedFrames);
        test("Cancelled packet reads terminate the underlying read", CancelledRead);
        test("Heat2 rejects corrupt integers, maps, variables and excessive nesting", HostileHeat2);
        test("Pipelined ProtoFire requests keep matching replies", PipelinedRequests);
        test("ProtoFire timeouts clean pending requests and disconnect fails promptly", RequestCleanup);
        test("Slow-peer writer queue rejects overload and completes queued sends", OutboundBackpressure);
        test("Server shutdown awaits disconnect callbacks", ShutdownCleanup);
        test("Blaze queues snapshot notifications after their reply on Fire and Fire2", ReplyOrdering);
        test("Malformed Fire2 metadata produces a terminal error reply", InvalidMetadata);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static Task PortableCertificate()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "certs/server.pfx");
        using var input = File.OpenRead(path);
        var certificate = ProtoSSLCertificate.FromPkcs12(input);
        Check(input.CanRead && certificate.PrivateKey.IsPrivate, "Loader closed the caller's stream or lost its private key.");
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.Certificate.GetCertificateAt(0).GetEncoded()));
        Check(fingerprint == "E40AB538E8D742D0F51D67AADC21A7ED61F52ADBCDDE772BFEB9D3376F3EB73D", "Legacy TLS leaf identity changed.");
        void Reject(Action load)
        {
            bool failed = false;
            try { load(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or Org.BouncyCastle.Security.GeneralSecurityException) { failed = true; }
            Check(failed, "Invalid PFX was accepted.");
        }
        Reject(() => ProtoSSLCertificate.FromPkcs12File(path, "not-the-password"));
        Reject(() => ProtoSSLCertificate.FromPkcs12(new MemoryStream(new byte[] { 1, 2, 3 })));

        var original = new Pkcs12StoreBuilder().Build();
        input.Position = 0; original.Load(input, []);
        string alias = original.Aliases.Single(original.IsKeyEntry);
        var noKey = new Pkcs12StoreBuilder().Build();
        noKey.SetCertificateEntry("public-only", original.GetCertificate(alias));
        var ambiguous = new Pkcs12StoreBuilder().Build();
        ambiguous.SetKeyEntry("one", original.GetKey(alias), original.GetCertificateChain(alias));
        ambiguous.SetKeyEntry("two", original.GetKey(alias), original.GetCertificateChain(alias));
        foreach (var invalid in new[] { noKey, ambiguous })
        {
            using var encoded = new MemoryStream();
            invalid.Save(encoded, [], new SecureRandom()); encoded.Position = 0;
            Reject(() => ProtoSSLCertificate.FromPkcs12(encoded));
        }
        return Task.CompletedTask;
    }

    private static async Task FragmentedFrames()
    {
        byte[] payload = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        foreach (bool fire2 in new[] { false, true })
        {
            var frame = new FireFrame { Options = FireFrame.Option.HAS_CONTEXT | FireFrame.Option.JUMBO_CONTEXT, Context = 0x123456789ABCDEF0, MessageNum = 12 };
            ProtoFirePacket packet = fire2
                ? new ProtoFire2Packet(new Fire2Frame { MessageNum = 12 }, [1, 2, 3], payload)
                : new ProtoFirePacket(frame, payload);
            using var encoded = new MemoryStream();
            await packet.WriteToAsync(encoded); // Calls through the base packet type, including metadata.
            byte[] wire = encoded.ToArray();
            using var fragmented = new FragmentedStream(wire);
            ProtoFirePacket? decoded = fire2 ? await ProtoFire2Packet.ReadFromAsync(fragmented) : await ProtoFirePacket.ReadFromAsync<FireFrame>(fragmented);
            Check(decoded != null && decoded.Data.SequenceEqual(payload), "Fragmented payload changed.");
            if (fire2) Check(((ProtoFire2Packet)decoded!).MetadataData.SequenceEqual(new byte[] { 1, 2, 3 }), "Fire2 metadata lost through base write.");
            else Check(decoded!.Frame.Context == 0x123456789ABCDEF0, "Growing a Fire body to jumbo overwrote its context.");
            using var truncated = new FragmentedStream(wire[..^1]);
            Check((fire2 ? await ProtoFire2Packet.ReadFromAsync(truncated) : await ProtoFirePacket.ReadFromAsync<FireFrame>(truncated)) == null, "Truncated payload accepted.");
            using var headerOnly = new MemoryStream(wire[..8]);
            Check((fire2 ? await ProtoFire2Packet.ReadFromAsync(headerOnly) : await ProtoFirePacket.ReadFromAsync<FireFrame>(headerOnly)) == null, "Truncated header accepted.");
        }
    }

    private static async Task CancelledRead()
    {
        using var stream = new BlockingReadStream();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await ProtoFirePacket.ReadFromAsync<FireFrame>(stream, 1024, cancel.Token).WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("Cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check(stream.ActiveReads == 0, "Packet cancellation left a background read running.");
    }

    private static Task HostileHeat2()
    {
        var serializer = new Heat2Serializer();
        bool Valid(byte[] bytes, Tdf? target = null) => serializer.Deserialize(new MemoryStream(bytes, false), target ?? new EmptyMessage());
        byte[] Header(byte type) => [0xDA, 0x1B, 0x35, type]; // VALU
        Check(!Valid([1]), "Trailing partial TDF header accepted.");
        Check(!Valid([.. Header(0), .. Enumerable.Repeat((byte)0x80, 11)]), "Overlong varint accepted.");
        Check(!Valid([.. Header(0), 0x80]), "Truncated varint accepted.");
        Check(!Valid([.. Header(1), 10, 1, 2]), "Declared string length beyond the frame accepted.");
        // CONF map<string,string> with a duplicate key.
        byte[] duplicate = [0x8E, 0xFB, 0xA6, 5, 1, 1, 2, 2, (byte)'a', 0, 2, (byte)'b', 0, 2, (byte)'a', 0, 2, (byte)'c', 0];
        Check(!Valid(duplicate, new FetchConfigResponse()), "Typed duplicate map key accepted.");
        Check(!Valid(duplicate), "Unknown duplicate map key accepted.");
        Check(!Valid([.. Header(5), 3, 0, 2, 0, 1, 0, 2]), "A map with unorderable struct keys escaped the parser.");
        Check(Valid([.. Header(7), 0]), "Absent variable should be valid, including unknown fields.");
        Check(!Valid([.. Header(7), 1, 0, .. Header(0), 1, 1]), "Nonzero variable terminator accepted.");
        var deep = new List<byte>(Header(3));
        for (int i = 0; i < 70; i++) deep.AddRange(Header(3));
        deep.AddRange(Enumerable.Repeat((byte)0, 71));
        Check(!Valid(deep.ToArray()), "Excessive struct depth accepted.");
        var union = new List<byte>(Header(6));
        for (int i = 0; i < 70; i++) { union.Add(0); union.AddRange(Header(6)); }
        union.Add(0x7F);
        Check(!Valid(union.ToArray()), "Excessive union depth accepted.");
        Check(Valid([.. Header(6), 2, .. Header(3), 0]), "Valid unknown union rejected.");
        var scalars = new EmptyMessage();
        scalars.GetUnknownMembers().Add(new EATDF.Members.TdfInt64(new EATDF.Members.TdfMemberInfo(0xDA1B3500, TdfType.Int64)) { Value = long.MinValue });
        Check(Valid(serializer.Serialize(scalars)), "Heat2 negative-zero long.MinValue compatibility changed.");
        return Task.CompletedTask;
    }

    private static async Task PipelinedRequests()
    {
        var handler = new Handler { Packet = async (c, p) => { await c.SendAsync(new ProtoFirePacket(p.Frame.CreateResponseFrame(), p.Data)); } };
        var server = new ProtoFireServer(new IPEndPoint(IPAddress.Loopback, 0), handler, FrameType.FireFrame);
        using var stop = new CancellationTokenSource();
        Task running = server.StartAsync(128, stop.Token);
        ProtoFireClient? client = null;
        try
        {
            client = await ProtoFireClient.ConnectAsync("127.0.0.1", server.Port, false, new Handler(), FrameType.FireFrame);
            var requests = Enumerable.Range(1, 48).Select(async i =>
            {
                byte[] data = BitConverter.GetBytes(i);
                var response = await client.SendRequestAsync(new ProtoFirePacket(new FireFrame { Component = 77, Command = 1, MessageNum = (uint)i }, data));
                Check(response.Frame.MessageNum == i && response.Data.SequenceEqual(data), "Concurrent request received another request's response.");
            });
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client?.Connection.Disconnect();
            if (client != null) await client.ProcessingTask.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task RequestCleanup()
    {
        var server = new ProtoFireServer(new IPEndPoint(IPAddress.Loopback, 0), new Handler(), FrameType.FireFrame);
        using var stop = new CancellationTokenSource();
        Task running = server.StartAsync(128, stop.Token);
        var client = await ProtoFireClient.ConnectAsync("127.0.0.1", server.Port, false, new Handler(), FrameType.FireFrame);
        ProtoFirePacket Request(uint n) => new(new FireFrame { Component = 77, Command = 1, MessageNum = n }, []);
        try
        {
            client.RequestTimeout = 50;
            for (int i = 0; i < 3; i++)
            {
                try { await client.SendRequestAsync(Request(1)); throw new Exception("Missing reply did not time out."); }
                catch (TimeoutException) { }
            }
            client.RequestTimeout = 5000;
            Task<ProtoFirePacket> first = client.SendRequestAsync(Request(7));
            try { await client.SendRequestAsync(Request(7)); throw new Exception("Duplicate pending message number accepted."); }
            catch (InvalidOperationException) { }
            client.Connection.Disconnect();
            try { await first.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Disconnected request completed successfully."); }
            catch (IOException) { }
        }
        finally { client.Connection.Disconnect(); await client.ProcessingTask.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task OutboundBackpressure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Sent = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        var server = new ProtoFireServer(new IPEndPoint(IPAddress.Loopback, 0), handler, FrameType.FireFrame) { OutboundQueueCapacity = 2 };
        using var stop = new CancellationTokenSource();
        Task running = server.StartAsync(128, stop.Token);
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port);
            ProtoFireConnection connection = await handler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            ProtoFirePacket Packet() => new(new FireFrame { MessageType = MessageType.Notification }, [1]);
            Task<bool> first = connection.SendAsync(Packet());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<bool> second = connection.SendAsync(Packet());
            Task<bool> third = connection.SendAsync(Packet());
            Check(!connection.TryEnqueueSend(Packet()), "Full outgoing queue accepted an extra frame.");
            Check(!connection.Connected, "Overload did not close the slow peer.");
            Check(!(await second.WaitAsync(TimeSpan.FromSeconds(2))) && !(await third.WaitAsync(TimeSpan.FromSeconds(2))), "Queued send was left unresolved on disconnect.");
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task ShutdownCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Disconnected = async _ => { entered.TrySetResult(); await release.Task; } };
        var server = new ProtoFireServer(new IPEndPoint(IPAddress.Loopback, 0), handler, FrameType.FireFrame);
        using var stop = new CancellationTokenSource();
        Task running = server.StartAsync(128, stop.Token);
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port);
            await handler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!running.IsCompleted, "Listener shutdown returned before disconnect cleanup.");
        }
        finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private static async Task ReplyOrdering()
    {
        foreach (bool fire2 in new[] { false, true })
        {
            var router = new BlazeRouter(); router.AddComponent(new NotificationComponent());
            var server = new BlazeServer(new BlazeServerConfig { LocalEndpoint = new IPEndPoint(IPAddress.Loopback, 0), Router = router, PacketFrameEncoding = fire2 ? FrameEncoding.Fire2 : FrameEncoding.Fire }, NullLogger<BlazeServer>.Instance);
            using var stop = new CancellationTokenSource();
            Task running = server.StartAsync(stop.Token);
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, server.LocalEndpoint.Port);
                for (uint i = 1; i <= 3; i++)
                {
                    ProtoFirePacket request = fire2 ? new ProtoFire2Packet(new Fire2Frame { Component = 77, Command = 1, MessageNum = i }, [], []) : new ProtoFirePacket(new FireFrame { Component = 77, Command = 1, MessageNum = i }, []);
                    await request.WriteToAsync(socket.GetStream());
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                for (uint i = 1; i <= 3; i++)
                {
                    var reply = fire2 ? await ProtoFire2Packet.ReadFromAsync(socket.GetStream(), cancellationToken: deadline.Token) : await ProtoFirePacket.ReadFromAsync<FireFrame>(socket.GetStream(), cancellationToken: deadline.Token);
                    Check(reply?.Frame.MessageType == MessageType.Reply && reply.Frame.MessageNum == i, "Notification preceded its initiating response.");
                    var notification = fire2 ? await ProtoFire2Packet.ReadFromAsync(socket.GetStream(), cancellationToken: deadline.Token) : await ProtoFirePacket.ReadFromAsync<FireFrame>(socket.GetStream(), cancellationToken: deadline.Token);
                    Check(notification?.Frame.MessageType == MessageType.Notification, "Queued notification missing.");
                    var config = new FetchConfigResponse();
                    Check(new Heat2Serializer().Deserialize(notification!.GetDataStream(), config) && config.Config["value"] == "before", "Queued notification retained mutable application data.");
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }

    private static async Task InvalidMetadata()
    {
        var server = new BlazeServer(new BlazeServerConfig { LocalEndpoint = new IPEndPoint(IPAddress.Loopback, 0), PacketFrameEncoding = FrameEncoding.Fire2 }, NullLogger<BlazeServer>.Instance);
        using var stop = new CancellationTokenSource();
        Task running = server.StartAsync(stop.Token);
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.LocalEndpoint.Port);
            await new ProtoFire2Packet(new Fire2Frame { Component = 77, Command = 1, MessageNum = 42 }, [0x8E, 0xED, 0x38, 0, 0x80], []).WriteToAsync(socket.GetStream());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var reply = await ProtoFire2Packet.ReadFromAsync(socket.GetStream(), cancellationToken: deadline.Token);
            Check(reply?.Frame.MessageType == MessageType.ErrorReply && reply.Frame.MessageNum == 42 && reply.MetadataData.Length != 0, "Malformed metadata left its request hanging.");
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    private sealed class NotificationComponent : BlazeComponent
    {
        public override ushort Id => 77;
        public override string Name => "TransportTest";
        public override string GetErrorName(ushort errorCode) => errorCode.ToString();
        internal NotificationComponent()
        {
            RegisterCommand(new RpcCommandFunc<EmptyMessage, EmptyMessage, EmptyMessage>
            {
                Id = 1, Name = "notify", IsSupported = true,
                Func = (_, context) =>
                {
                    var data = new FetchConfigResponse { Config = new SortedDictionary<string, string> { ["value"] = "before" } };
                    ((BlazeRpcConnection)context.Connection).EnqequeSend(packet =>
                    {
                        packet.Frame.Component = 77; packet.Frame.Command = 7; packet.Frame.MessageType = MessageType.Notification; packet.Data = data;
                    });
                    data.Config["value"] = "after";
                    return Task.FromResult<Tdf>(new EmptyMessage());
                }
            });
        }
    }

    private sealed class Handler : IProtoFireHandler
    {
        internal readonly TaskCompletionSource<ProtoFireConnection> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<ProtoFireConnection, ProtoFirePacket, Task>? Packet;
        internal Func<ProtoFireConnection, ProtoFirePacket, Task>? Sent;
        internal Func<ProtoFireConnection, Task>? Disconnected;
        public Task<bool> OnConnectedAsync(ProtoFireConnection c) => Task.FromResult(true);
        public Task OnAuthenticatedAsync(ProtoFireConnection c, bool secure) { Ready.TrySetResult(c); return Task.CompletedTask; }
        public Task OnDisconnectedAsync(ProtoFireConnection c) => Disconnected?.Invoke(c) ?? Task.CompletedTask;
        public Task OnPacketReceivedAsync(ProtoFireConnection c, ProtoFirePacket p) => Packet?.Invoke(c, p) ?? Task.CompletedTask;
        public Task OnPacketSentAsync(ProtoFireConnection c, ProtoFirePacket p) => Sent?.Invoke(c, p) ?? Task.CompletedTask;
        public Task OnErrorAsync(ProtoFireConnection c, Exception e) => Task.CompletedTask;
        public ProtoSSLCertificate? SelectCertificate(ProtoFireConnection c) => null;
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 3)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

    private sealed class BlockingReadStream : Stream
    {
        internal int ActiveReads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActiveReads);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            finally { Interlocked.Decrement(ref ActiveReads); }
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
