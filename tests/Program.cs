using MultipleMouse;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

if (args.Length == 2 && args[0] == "discovery") { await DiscoveryTests.Run(args[1]); return; }
if (args.Length is 2 or 3 && args[0] == "files") { await FileTransferTests.Run(args[1], args.Length == 3 ? args[2] : null); return; }

byte[] token = Wire.PairingToken("Mouse123");
byte[] clientNonce = Enumerable.Range(32, 32).Select(x => (byte)x).ToArray();
byte[] serverNonce = Enumerable.Range(64, 32).Select(x => (byte)x).ToArray();
void ExpectFailure(Action action)
{
    bool failed = false; try { action(); } catch { failed = true; }
    if (!failed) throw new Exception("Expected rejection.");
}
if (args.Length == 2 && args[0] == "verify")
{
    using var client = new Wire(token, clientNonce, serverNonce);
    using var message = client.Open(File.ReadAllText(args[1]).Trim());
    if (message.RootElement.GetProperty("type").GetString() != "ready") throw new Exception("Swift reply mismatch.");
    Console.WriteLine("PASS: Swift → C# encrypted message."); return;
}
using var sender = new Wire(token, clientNonce, serverNonce);
ExpectFailure(() => Wire.PairingToken(" \t\r\n"));
ExpectFailure(() => Wire.PairingToken(new string('a', 257)));
var codes = new[] { "1", "a", "Mouse123", "  Mouse123\r\n", "Caf\u00e9", "Cafe\u0301", "mouse123" };
var codeVectors = codes.Select(code => new { code, token = Convert.ToBase64String(Wire.PairingToken(code)) }).ToArray();
if (codeVectors[2].token != codeVectors[3].token || codeVectors[4].token != codeVectors[5].token || codeVectors[2].token == codeVectors[6].token)
    throw new Exception("Code normalization/case handling failed.");
if (args.Length == 1) File.WriteAllText(args[0] + ".pairing.json", JsonSerializer.Serialize(codeVectors));
using var receiver = new Wire(token, clientNonce, serverNonce, true);
string first = sender.Seal(new { type = "move", dx = -13, dy = 9 });
using (var message = receiver.Open(first))
    if (message.RootElement.GetProperty("dx").GetInt32() != -13) throw new Exception("Round trip failed.");
ExpectFailure(() => receiver.Open(first));
string second = sender.Seal(new { type = "ping" });
byte[] corrupt = Convert.FromBase64String(second); corrupt[^1] ^= 1;
ExpectFailure(() => receiver.Open(Convert.ToBase64String(corrupt)));
using (receiver.Open(second)) { }
using var wrong = new Wire(new byte[32], clientNonce, serverNonce, true);
ExpectFailure(() => wrong.Open(first));
using var reorder = new Wire(token, clientNonce, serverNonce, true);
ExpectFailure(() => reorder.Open(second));
ExpectFailure(() => receiver.Open("bad base64"));
ExpectFailure(() => new Wire(new byte[1], clientNonce, serverNonce));
if (args.Length == 1) File.WriteAllText(args[0], first + "\n");
Console.WriteLine("PASS: C# round trip, wrong key, tampering, replay, order, malformed input.");

var screen = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
if (!ScreenEdge.Crossed(screen, true, new(-4, 300), new(8, 300)) ||
    !ScreenEdge.Crossed(screen, false, new(-1918, 300), new(-1930, 300)) ||
    !ScreenEdge.Crossed(screen, true, new(-12, 300), new(-1, 300)) ||
    ScreenEdge.Crossed(screen, true, new(-12, 300), new(-8, 300)) ||
    ScreenEdge.Crossed(screen, true, new(-4, 1079), new(2, 1080)) ||
    ScreenEdge.Crossed(screen, true, new(100, 300), new(200, 300)))
    throw new Exception("Screen edge overshoot/return geometry failed.");
Console.WriteLine("PASS: left/right edge overshoot, immediate re-entry geometry, adjacent-screen exclusion.");

// Real socket exercise for Link: pairing, ordered input, return, heartbeat timeout.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    var receivedMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var handoff = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var link = new Link();
    link.Message += (type, y) =>
    {
        if (type == "ready") { link.Enter(true, 0.5); link.Send(new { type = "move", dx = 25, dy = -7 }); }
        if (type == "return" && y == 0.25) returned.TrySetResult();
    };
    link.Closed += reason => closed.TrySetResult(reason);
    link.HandoffMeasured += milliseconds => handoff.TrySetResult(milliseconds);
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync();
        using var stream = peer.GetStream(); using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var hello = JsonDocument.Parse((await reader.ReadLineAsync())!);
        var cn = Convert.FromBase64String(hello.RootElement.GetProperty("nonce").GetString()!);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, nonce = Convert.ToBase64String(serverNonce) }));
        using var wire = new Wire(token, cn, serverNonce, true);
        using var proof = wire.Open((await reader.ReadLineAsync())!);
        if (proof.RootElement.GetProperty("type").GetString() != "hello") throw new Exception("Bad proof.");
        await writer.WriteLineAsync(wire.Seal(new { type = "ready" }));
        using var enter = wire.Open((await reader.ReadLineAsync())!);
        if (enter.RootElement.GetProperty("type").GetString() != "enter") throw new Exception("Input ordering failed.");
        await writer.WriteLineAsync(wire.Seal(new { type = "entered", id = enter.RootElement.GetProperty("id").GetInt64() }));
        using var move = wire.Open((await reader.ReadLineAsync())!);
        if (move.RootElement.GetProperty("dx").GetInt32() != 25) throw new Exception("Movement mismatch.");
        receivedMove.TrySetResult();
        await writer.WriteLineAsync(wire.Seal(new { type = "return", y = 0.25 }));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(6)); // Intentionally withhold heartbeat replies.
    });
    var running = link.Run("127.0.0.1", token, ((IPEndPoint)listener.LocalEndpoint).Port);
    await receivedMove.Task.WaitAsync(TimeSpan.FromSeconds(6));
    await returned.Task.WaitAsync(TimeSpan.FromSeconds(6));
    double elapsed = await handoff.Task.WaitAsync(TimeSpan.FromSeconds(6));
    if (!double.IsFinite(elapsed) || elapsed < 0) throw new Exception("Invalid handoff measurement.");
    Console.WriteLine($"PASS: entry acknowledgement; localhost protocol round trip {elapsed:F1} ms (not a hardware latency measurement).");
    string reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(6));
    if (!reason.Contains("stopped responding")) throw new Exception("Unexpected timeout result: " + reason);
    await Task.WhenAll(server, running);
    Console.WriteLine("PASS: TCP pairing, input ordering, return position, heartbeat failover.");
}
finally { listener.Stop(); }
