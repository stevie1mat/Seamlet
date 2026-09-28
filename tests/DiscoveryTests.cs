using Seamlet;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static class DiscoveryTests
{
    public static async Task Run(string hostExecutable)
    {
        string nonce = new('a', 32);
        var sender = new IPEndPoint(IPAddress.Loopback, 24873);
        byte[] offer = JsonSerializer.SerializeToUtf8Bytes(new { service = "Seamlet", v = 1, type = "offer", nonce, name = "Mac", address = "fake-address" });
        if (Discovery.Parse(offer, sender, nonce)?.Address != "127.0.0.1") throw new Exception("Wrong discovery address.");
        if (Discovery.Parse(offer, sender, new string('b', 32)) != null) throw new Exception("Stale response accepted.");
        foreach (var input in new[] { "{}", "null", "[]", "broken", new string('x', 1025), Encoding.UTF8.GetString(offer).Replace("Seamlet", "OtherApp"), Encoding.UTF8.GetString(offer).Replace("\"v\":1", "\"v\":2") })
            if (Discovery.Parse(Encoding.UTF8.GetBytes(input), sender, nonce) != null) throw new Exception("Invalid discovery accepted.");
        Console.WriteLine("PASS: discovery validation, stale response rejection, sender address selection.");
        using var process = Process.Start(new ProcessStartInfo(hostExecutable) { RedirectStandardOutput = true, UseShellExecute = false })!;
        try
        {
            string line = (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            int port = int.Parse(line);
            var target = new IPEndPoint(IPAddress.Loopback, port);
            using (var junk = new UdpClient())
            {
                await junk.SendAsync(Encoding.UTF8.GetBytes("invalid request"), target);
                await junk.SendAsync(new byte[2048], target);
            }
            var found = new List<DiscoveredDevice>();
            await Discovery.Scan(found.Add, CancellationToken.None, [target], TimeSpan.FromSeconds(2));
            if (found.Count != 1 || found[0].Name != "Test MacBook" || found[0].Address != "127.0.0.1") throw new Exception("Swift/C# discovery failed or duplicates were not removed.");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Discovery.Scan(_ => throw new Exception("Cancelled scan returned a device."), cancel.Token, [target]);
            Console.WriteLine("PASS: C# discovers Swift receiver over UDP, retries deduplicate, malformed requests ignored, scan cancellation.");
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
    }
}
