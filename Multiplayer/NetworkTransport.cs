using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace BlockCircuit;

/// <summary>One bounded TCP connection. Only byte/JSON work runs on its background tasks.</summary>
public sealed class NetPeer : IDisposable
{
    readonly TcpClient client;
    readonly NetworkStream stream;
    readonly CancellationTokenSource cancellation = new();
    readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(NetworkProtocol.MaximumQueuedMessages)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    readonly ConcurrentQueue<(NetMessage Message, int Bytes)> incoming = new();
    readonly object outgoingGate = new(), incomingGate = new();
    readonly Task reader, writer;
    int connected = 1, disposed, outgoingCount, outgoingBytes, incomingCount, incomingBytes;
    Exception? error;
    long totalSentBytes,totalReceivedBytes,totalSentMessages,totalReceivedMessages;
    public long TotalSentBytes=>Interlocked.Read(ref totalSentBytes);
    public long TotalReceivedBytes=>Interlocked.Read(ref totalReceivedBytes);
    public long TotalSentMessages=>Interlocked.Read(ref totalSentMessages);
    public long TotalReceivedMessages=>Interlocked.Read(ref totalReceivedMessages);
    internal event Action<NetPeer>? Closed;
    public bool IsConnected => Volatile.Read(ref connected) != 0;
    public Exception? Error => Volatile.Read(ref error);
    public int PendingIncomingMessages { get { lock (incomingGate) return incomingCount; } }
    public int PendingOutgoingMessages { get { lock (outgoingGate) return outgoingCount; } }
    public int PendingIncomingBytes { get { lock (incomingGate) return incomingBytes; } }
    public int PendingOutgoingBytes { get { lock (outgoingGate) return outgoingBytes; } }
    public EndPoint? RemoteEndPoint { get; }

    internal NetPeer(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        stream = client.GetStream();
        RemoteEndPoint = client.Client.RemoteEndPoint;
        reader = Task.Run(ReadLoop);
        writer = Task.Run(WriteLoop);
    }

    public static async Task<NetPeer> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            throw new ArgumentException("Enter a host name or IP address.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        cancellationToken.ThrowIfCancellationRequested();
        var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(client);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("The server did not accept the connection within 10 seconds.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Nonblocking. A disconnected, malformed, or overloaded connection returns false.</summary>
    public bool Send(NetMessage message)
    {
        if (!IsConnected) return false;
        byte[] frame;
        try { frame = NetworkProtocol.EncodeFrame(message); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        { Close(failure); return false; }
        lock (outgoingGate)
        {
            if (!IsConnected) return false;
            if (outgoingCount >= NetworkProtocol.MaximumQueuedMessages ||
                outgoingBytes > NetworkProtocol.MaximumQueuedBytes - frame.Length)
            {
                Close(new IOException("Network send queue exceeded its bounded budget."));
                return false;
            }
            outgoingCount++; outgoingBytes += frame.Length;
            if (outgoing.Writer.TryWrite(frame)) return true;
            outgoingCount--; outgoingBytes -= frame.Length;
            Close(new IOException("Network send queue is closed."));
            return false;
        }
    }

    public bool TryReceive(out NetMessage message)
    {
        lock (incomingGate)
        {
            if (incoming.TryDequeue(out var next))
            {
                incomingCount--; incomingBytes -= next.Bytes;
                message = next.Message;
                return true;
            }
        }
        message = null!;
        return false;
    }

    async Task ReadLoop()
    {
        try
        {
            byte[] header = new byte[4];
            while (!cancellation.IsCancellationRequested)
            {
                if (!await ReadFramePart(header).ConfigureAwait(false)) break;
                uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length is 0 or > NetworkProtocol.MaximumFrameBytes)
                    throw new InvalidDataException("Network frame length is outside the 8 MiB limit.");
                lock (incomingGate)
                    if (incomingCount >= NetworkProtocol.MaximumQueuedMessages ||
                        incomingBytes > NetworkProtocol.MaximumQueuedBytes - (int)length)
                        throw new IOException("Network receive queue exceeded its bounded budget.");
                byte[] body = new byte[(int)length];
                if (!await ReadFramePart(body).ConfigureAwait(false))
                    throw new EndOfStreamException("Network peer closed inside a frame.");
                var message = NetworkProtocol.Decode(body);
                lock (incomingGate)
                {
                    if (!IsConnected) return;
                    incoming.Enqueue((message, body.Length));
                    incomingCount++; incomingBytes += body.Length;
                    Interlocked.Increment(ref totalReceivedMessages);
                }
            }
            Close(null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure) when (failure is IOException or InvalidDataException or SocketException or ObjectDisposedException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { if (!cancellation.IsCancellationRequested) Close(failure); }
        finally { Close(null); }
    }

    async Task<bool> ReadFramePart(Memory<byte> destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int count = await stream.ReadAsync(destination[offset..], cancellation.Token).ConfigureAwait(false);
            if (count == 0)
            {
                if (offset != 0) throw new EndOfStreamException("Network peer sent a partial frame.");
                return false;
            }
            offset += count;
            Interlocked.Add(ref totalReceivedBytes,count);
        }
        return true;
    }

    async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in outgoing.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                lock (outgoingGate) { outgoingCount--; outgoingBytes -= frame.Length; }
                await stream.WriteAsync(frame, cancellation.Token).ConfigureAwait(false);
                Interlocked.Add(ref totalSentBytes,frame.Length);Interlocked.Increment(ref totalSentMessages);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure) when (failure is IOException or SocketException or ObjectDisposedException)
        { if (!cancellation.IsCancellationRequested) Close(failure); }
        finally { Close(null); }
    }

    void Close(Exception? failure)
    {
        if (Interlocked.Exchange(ref connected, 0) == 0) return;
        if (failure is not null) Interlocked.CompareExchange(ref error, failure, null);
        cancellation.Cancel();
        outgoing.Writer.TryComplete();
        try { client.Dispose(); } catch (SocketException) { }
        lock (incomingGate)
        {
            incoming.Clear(); incomingCount = incomingBytes = 0;
        }
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Close(null);
        try { Task.WhenAll(reader, writer).GetAwaiter().GetResult(); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        lock (outgoingGate)
        {
            while (outgoing.Reader.TryRead(out _)) { }
            outgoingCount = outgoingBytes = 0;
        }
        cancellation.Dispose();
    }
}

/// <summary>Accepts up to the configured number of TCP peers without touching live game state.</summary>
public sealed class NetListener : IDisposable
{
    readonly TcpListener listener;
    readonly CancellationTokenSource cancellation = new();
    readonly ConcurrentQueue<NetPeer> accepted = new();
    readonly ConcurrentDictionary<NetPeer, byte> peers = new();
    readonly Task acceptTask;
    readonly int maximumPeers;
    int pendingAccepted, disposed;
    Exception? error;
    public int Port { get; }
    public Exception? Error => Volatile.Read(ref error);
    public int ActivePeerCount => peers.Count;

    public NetListener(IPAddress bind, int port, int maximumPeers = 8)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (maximumPeers is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumPeers));
        this.maximumPeers = maximumPeers;
        listener = new(bind, port);
        listener.Start(maximumPeers * 2);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested || peers.Count >= maximumPeers ||
                    Volatile.Read(ref pendingAccepted) >= maximumPeers)
                { client.Dispose(); continue; }
                NetPeer peer;
                try { peer = new(client); }
                catch (Exception failure) when (failure is SocketException or IOException or InvalidOperationException or ObjectDisposedException)
                { client.Dispose(); continue; }
                peers.TryAdd(peer, 0);
                peer.Closed += Remove;
                if (!peer.IsConnected) Remove(peer);
                Interlocked.Increment(ref pendingAccepted);
                accepted.Enqueue(peer);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure) when (failure is SocketException or IOException or InvalidOperationException)
        { if (!cancellation.IsCancellationRequested) Interlocked.CompareExchange(ref error, failure, null); }
    }

    void Remove(NetPeer peer)
    {
        peers.TryRemove(peer, out _);
        peer.Closed -= Remove;
    }

    public bool TryAccept(out NetPeer peer)
    {
        while (accepted.TryDequeue(out var next))
        {
            Interlocked.Decrement(ref pendingAccepted);
            if (next.IsConnected) { peer = next; return true; }
            next.Dispose();
        }
        peer = null!;
        return false;
    }
    public bool PollAccepted(out NetPeer peer) => TryAccept(out peer);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel();
        listener.Stop();
        try { acceptTask.GetAwaiter().GetResult(); }
        catch (Exception failure) when (failure is SocketException or OperationCanceledException or ObjectDisposedException) { }
        foreach (var peer in peers.Keys) peer.Dispose();
        peers.Clear();
        while (accepted.TryDequeue(out var peer)) peer.Dispose();
        pendingAccepted = 0;
        cancellation.Dispose();
    }
}
