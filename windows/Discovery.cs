using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MultipleMouse;

sealed record DiscoveredDevice(string Name, string Address)
{
    public override string ToString() => $"{Name} ({Address})";
}

static class Discovery
{
    public static DiscoveredDevice? Parse(byte[] bytes, IPEndPoint sender, string nonce)
    {
        if (bytes.Length > 1024 || sender.Address.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("service").GetString() != "MultipleMouse" || root.GetProperty("v").GetInt32() != 1 ||
                root.GetProperty("type").GetString() != "offer" || root.GetProperty("nonce").GetString() != nonce) return null;
            string? name = root.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Any(char.IsControl)) return null;
            // Use the actual sender address, never a claimed address in JSON.
            return new DiscoveredDevice(name, sender.Address.ToString());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    static IPEndPoint[] BroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                var ip = unicast.Address.GetAddressBytes(); var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 255) || mask.All(b => b == 0)) continue;
                addresses.Add(new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
            }
        }
        return addresses.Select(ip => new IPEndPoint(ip, 24873)).ToArray();
    }
    public static async Task Scan(Action<DiscoveredDevice> found, CancellationToken cancellation,
        IPEndPoint[]? targets = null, TimeSpan? duration = null)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(duration ?? TimeSpan.FromSeconds(3));
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        byte[] query = JsonSerializer.SerializeToUtf8Bytes(new { service = "MultipleMouse", v = 1, type = "discover", nonce });
        targets ??= BroadcastTargets();
        var unique = new HashSet<string>();
        var sending = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                foreach (var target in targets)
                {
                    try { await udp.SendAsync(query, target, deadline.Token); }
                    catch (SocketException) { /* One unavailable interface must not hide the others. */ }
                }
                await Task.Delay(650, deadline.Token);
            }
        });
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var response = await udp.ReceiveAsync(deadline.Token);
                var device = Parse(response.Buffer, response.RemoteEndPoint, nonce);
                if (device != null && unique.Add(device.Address)) found(device);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            deadline.Cancel();
            try { await sending; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}
