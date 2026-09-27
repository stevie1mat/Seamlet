using MultipleMouse;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

static class FileTransferTests
{
    static async Task Reject(Func<Task> action)
    {
        bool rejected = false; try { await action(); } catch { rejected = true; }
        if (!rejected) throw new Exception("Invalid file transfer was accepted.");
    }
    public static async Task Run(string executable, string? extraFile = null)
    {
        foreach (string name in new[] { "../outside", "..", "a/b", "a\\b", "a:b", "CON.txt", "NUL", "COM1", "trailing.", "line\nname" })
            if (FileTransfer.ValidName(name)) throw new Exception("Unsafe name accepted: " + name);
        await Reject(() => { FileTransfer.Validate([new("a", 1), new("A", 2)]); return Task.CompletedTask; });
        await Reject(() => { FileTransfer.Validate([new("big", FileTransfer.MaxBytes + 1)]); return Task.CompletedTask; });
        string root = Path.Combine(Path.GetTempPath(), "MultipleMouse-Test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var payloads = new Dictionary<string, byte[]> { ["empty.txt"] = [], ["café.txt"] = System.Text.Encoding.UTF8.GetBytes("Copy and paste — both ways."), ["binary.bin"] = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17) };
        foreach (var pair in payloads) await File.WriteAllBytesAsync(Path.Combine(source, pair.Key), pair.Value);
        if (extraFile != null) File.Copy(extraFile, Path.Combine(source, Path.GetFileName(extraFile)));
        var hashes = Directory.GetFiles(source).ToDictionary(p => Path.GetFileName(p).Normalize(), p => { using var input = File.OpenRead(p); return SHA256.HashData(input); });
        using var process = Process.Start(new ProcessStartInfo(executable) { ArgumentList = { root }, RedirectStandardOutput = true, UseShellExecute = false })!;
        var stagedFolders = new HashSet<string>();
        try
        {
            using var info = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            int port = info.RootElement.GetProperty("port").GetInt32(); byte[] key = Convert.FromBase64String(info.RootElement.GetProperty("key").GetString()!);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            long lastProgress = -1, expectedBytes = Directory.GetFiles(source).Sum(p => new FileInfo(p).Length);
            string[] downloaded = await FileTransfer.Download("127.0.0.1", key, "fixture", deadline.Token, port, (name, bytes, total) =>
            {
                if (!hashes.ContainsKey(name.Normalize()) || bytes < lastProgress || bytes > total || total != expectedBytes)
                    throw new Exception("Invalid download progress.");
                lastProgress = bytes;
            });
            if (lastProgress != expectedBytes) throw new Exception("Missing completed download progress.");
            foreach (string path in downloaded)
            {
                stagedFolders.Add(Path.GetDirectoryName(path)!);
                using var input = File.OpenRead(path);
                if (!SHA256.HashData(input).SequenceEqual(hashes[Path.GetFileName(path).Normalize()])) throw new Exception("Mac → Windows file mismatch.");
            }
            await FileTransfer.Upload("127.0.0.1", key, Directory.GetFiles(source), deadline.Token, port);
            string[] uploaded = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(root, "uploaded.json")))!;
            foreach (string path in uploaded)
            {
                stagedFolders.Add(Path.GetDirectoryName(path)!);
                using var input = File.OpenRead(path);
                if (!SHA256.HashData(input).SequenceEqual(hashes[Path.GetFileName(path).Normalize()])) throw new Exception("Windows → Mac file mismatch.");
            }
            await Reject(async () => await FileTransfer.Download("127.0.0.1", new byte[32], "fixture", deadline.Token, port));
            await Reject(async () => await FileTransfer.Download("127.0.0.1", key, "stale-id", deadline.Token, port));
            using (var client = new TcpClient())
            using (var wire = await FileTransfer.Connect(client, "127.0.0.1", key, deadline.Token, port))
            {
                await wire.Send(new { type = "put", entries = new[] { new FileEntry("../escape", 0) } }, deadline.Token);
                await Reject(async () => { using var _ = await wire.Read(deadline.Token); });
            }
            using (var client = new TcpClient())
            using (var wire = await FileTransfer.Connect(client, "127.0.0.1", key, deadline.Token, port))
            {
                await wire.Send(new { type = "put", entries = new[] { new FileEntry("corrupt.txt", 3) } }, deadline.Token);
                using var ready = await wire.Read(deadline.Token);
                await wire.Send(new { type = "chunk", index = 0, data = Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, deadline.Token);
                await wire.Send(new { type = "fileEnd", index = 0, sha256 = new string('0', 64) }, deadline.Token);
                await Reject(async () => { using var _ = await wire.Read(deadline.Token); });
            }
            using (var client = new TcpClient())
            using (var wire = await FileTransfer.Connect(client, "127.0.0.1", key, deadline.Token, port))
            {
                await wire.Send(new { type = "put", entries = new[] { new FileEntry("incomplete.txt", 20) } }, deadline.Token);
                using var ready = await wire.Read(deadline.Token);
                client.Close(); // Abrupt disconnect must never publish a partial file.
            }
            // The service remains usable after rejected and interrupted transfers.
            var again = await FileTransfer.Download("127.0.0.1", key, "fixture", deadline.Token, port);
            foreach (string path in again) stagedFolders.Add(Path.GetDirectoryName(path)!);
            foreach (string text in new[] { "", "Hello\r\nSecond line\n中文 café 👋 العربية\t\"quoted\" \\ end", new string('x', 32767) + "🌍" + new string('y', 80000), new string('z', FileTransfer.MaxTextBytes) })
            {
                await FileTransfer.UploadText("127.0.0.1", key, text, deadline.Token, port);
                string restored = await FileTransfer.DownloadText("127.0.0.1", key, "text-fixture", deadline.Token, port);
                if (restored != text) throw new Exception("Unicode text clipboard round trip mismatch.");
            }
            await Reject(() => FileTransfer.UploadText("127.0.0.1", key, new string('x', FileTransfer.MaxTextBytes + 1), deadline.Token, port));
            await Reject(() => FileTransfer.DownloadText("127.0.0.1", key, "stale-text", deadline.Token, port));
            await Reject(() => FileTransfer.DownloadText("127.0.0.1", new byte[32], "text-fixture", deadline.Token, port));
            foreach (bool invalidEncoding in new[] { false, true })
            {
                using var client = new TcpClient();
                using var wire = await FileTransfer.Connect(client, "127.0.0.1", key, deadline.Token, port);
                await wire.Send(new { type = "putText", size = invalidEncoding ? 1 : FileTransfer.MaxTextBytes + 1 }, deadline.Token);
                if (invalidEncoding)
                {
                    using var ready = await wire.Read(deadline.Token);
                    await wire.Send(new { type = "textChunk", data = Convert.ToBase64String(new byte[] { 255 }) }, deadline.Token);
                    await wire.Send(new { type = "end" }, deadline.Token);
                }
                await Reject(async () => { using var _ = await wire.Read(deadline.Token); });
            }
            string retainedText = await FileTransfer.DownloadText("127.0.0.1", key, "text-fixture", deadline.Token, port);
            if (retainedText != new string('z', FileTransfer.MaxTextBytes)) throw new Exception("Rejected text changed the clipboard.");
            Console.WriteLine("PASS: bidirectional text and private Mac pasteboard (empty, multiline, Unicode split across chunks, 1 MB), oversized/invalid UTF-8/stale/wrong-key rejection; rejected text preserves prior clipboard.");
            Console.WriteLine("PASS: Swift ↔ C# encrypted files (empty, Unicode, multi-chunk binary), wrong key/stale offer/path traversal/checksum rejection, interrupted-transfer recovery.");
            if (extraFile != null) Console.WriteLine($"PASS: actual {Path.GetFileName(extraFile)} ({new FileInfo(extraFile).Length:N0} bytes) transferred with matching SHA-256 in both directions; no executable was run.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(); await process.WaitForExitAsync();
            foreach (var folder in stagedFolders) if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Delete(root, true);
        }
    }
}
