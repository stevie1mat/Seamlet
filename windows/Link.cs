using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;

namespace MultipleMouse;

sealed class Link : IDisposable
{
    readonly TcpClient client = new() { NoDelay = true };
    readonly CancellationTokenSource cancel = new();
    readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public event Action<string, double>? Message;
    public event Action<string>? Closed;
    public event Action<JsonElement>? Control;
    public event Action<double>? HandoffMeasured;
    long entryID, entryStarted;
    public bool Enter(bool right, double y)
    {
        long id = Interlocked.Increment(ref entryID);
        Interlocked.Exchange(ref entryStarted, Stopwatch.GetTimestamp());
        return Send(new { type = "enter", right, y, id });
    }
    long lastSeen = Environment.TickCount64;
    int stopped;

    // Called by the hook: never wait on socket I/O or encryption here.
    public bool Send(object message)
    {
        if (Volatile.Read(ref stopped) != 0) return false;
        if (queue.Writer.TryWrite(message)) return true;
        Stop("Input queue filled. Returned to Windows."); return false;
    }
    public async Task Run(string host, byte[] token, int port = 24872)
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, handshake.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, nonce = Convert.ToBase64String(nonce) }).AsMemory(), handshake.Token);
            using var hello = JsonDocument.Parse(await ReadLine(reader, handshake.Token));
            if (hello.RootElement.GetProperty("v").GetInt32() != 1) throw new InvalidDataException("Incompatible protocol.");
            using var wire = new Wire(token, nonce, Convert.FromBase64String(hello.RootElement.GetProperty("nonce").GetString()!));
            await writer.WriteLineAsync(wire.Seal(new { type = "hello", files = 1, text = 1 }).AsMemory(), handshake.Token);
            using var ready = wire.Open(await ReadLine(reader, handshake.Token));
            if (ready.RootElement.GetProperty("type").GetString() != "ready") throw new InvalidDataException("Pairing failed.");
            if (ready.RootElement.TryGetProperty("filesKey", out var fileSecret) && Convert.FromBase64String(fileSecret.GetString()!).Length != 32)
                throw new InvalidDataException("Invalid file-sharing key.");
            Control?.Invoke(ready.RootElement.Clone());
            Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
            Message?.Invoke("ready", 0);
            var sender = Task.Run(async () =>
            {
                await foreach (var item in queue.Reader.ReadAllAsync(cancel.Token))
                    await writer.WriteLineAsync(wire.Seal(item).AsMemory(), cancel.Token);
            });
            var receiver = Task.Run(async () =>
            {
                while (!cancel.IsCancellationRequested)
                {
                    using var doc = wire.Open(await ReadLine(reader, cancel.Token));
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString()!;
                    if (type == "entered")
                    {
                        if (root.GetProperty("id").GetInt64() == Interlocked.Read(ref entryID))
                            HandoffMeasured?.Invoke(Stopwatch.GetElapsedTime(Interlocked.Read(ref entryStarted)).TotalMilliseconds);
                        Interlocked.Exchange(ref lastSeen, Environment.TickCount64); continue;
                    }
                    if (type == "files")
                    {
                        string? id = root.GetProperty("id").GetString();
                        if (id == null || id.Length > 64) throw new InvalidDataException("Invalid file offer.");
                        Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
                        Control?.Invoke(root.Clone()); continue;
                    }
                    if (type != "pong" && type != "return") throw new InvalidDataException("Unexpected message.");
                    double y = type == "return" ? root.GetProperty("y").GetDouble() : 0;
                    if (!double.IsFinite(y) || y < 0 || y > 1) throw new InvalidDataException("Invalid position.");
                    Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
                    Message?.Invoke(type, y);
                }
            });
            var heartbeat = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                while (await timer.WaitForNextTickAsync(cancel.Token))
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > 2500) throw new IOException("Mac stopped responding. Returned to Windows.");
                    if (!Send(new { type = "ping" })) return;
                }
            });
            var finished = await Task.WhenAny(sender, receiver, heartbeat);
            try { await finished; }
            finally
            {
                cancel.Cancel(); client.Close();
                try { await Task.WhenAll(sender, receiver, heartbeat); } catch { /* First completion determines the error. */ }
            }
        }
        catch (OperationCanceledException) { Stop("Disconnected or connection timed out."); }
        catch (Exception error) { Stop("Connection ended: " + error.Message); }
        finally { Stop("Disconnected. Mouse is on Windows."); }
    }
    static async Task<string> ReadLine(StreamReader reader, CancellationToken token)
    {
        // ReadLineAsync has no length limit; impose a hard cap on untrusted peers.
        var text = new StringBuilder(); char[] c = new char[1];
        while (text.Length < 8192)
        {
            if (await reader.ReadAsync(c.AsMemory(), token) == 0) throw new IOException("Mac disconnected.");
            if (c[0] == '\n') return text.ToString();
            text.Append(c[0]);
        }
        throw new InvalidDataException("Message too large.");
    }
    void Stop(string reason)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        cancel.Cancel(); queue.Writer.TryComplete(); client.Close(); Closed?.Invoke(reason);
    }
    public void Dispose() => Stop("Disconnected. Mouse is on Windows.");
}
