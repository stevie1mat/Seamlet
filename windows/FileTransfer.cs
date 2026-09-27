using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultipleMouse;

sealed class FileWire : IDisposable
{
    readonly Stream stream;
    readonly Wire wire;
    readonly byte[] buffer = new byte[16384];
    int offset, available;
    public FileWire(Stream stream, Wire wire) { this.stream = stream; this.wire = wire; }
    public async Task<string> Line(CancellationToken ct)
    {
        using var result = new MemoryStream();
        while (result.Length <= 131072)
        {
            if (offset == available) { available = await stream.ReadAsync(buffer, ct); offset = 0; if (available == 0) throw new IOException("File connection closed."); }
            int end = Array.IndexOf(buffer, (byte)10, offset, available - offset);
            int count = (end < 0 ? available : end) - offset;
            if (result.Length + count > 131072) throw new InvalidDataException("File message too large.");
            result.Write(buffer, offset, count); offset += count;
            if (end >= 0) { offset++; return Encoding.UTF8.GetString(result.ToArray()); }
        }
        throw new InvalidDataException("File message too large.");
    }
    public async Task Send(object value, CancellationToken ct) => await stream.WriteAsync(Encoding.UTF8.GetBytes(wire.Seal(value) + "\n"), ct);
    public async Task<JsonDocument> Read(CancellationToken ct)
    {
        var doc = wire.Open(await Line(ct));
        if (doc.RootElement.GetProperty("type").GetString() == "error") { doc.Dispose(); throw new IOException("The other computer could not complete this file transfer."); }
        return doc;
    }
    public void Dispose() => wire.Dispose();
}

sealed record FileEntry(string name, long size);

static class FileTransfer
{
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const int ChunkSize = 32768;
    public static string CacheRoot => Path.Combine(Path.GetTempPath(), "MultipleMouse-Files");
    public static void CleanOldCache(string[] clipboardPaths)
    {
        var keep = clipboardPaths.Select(Path.GetDirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            foreach (string folder in Directory.EnumerateDirectories(CacheRoot))
            {
                try
                {
                    if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && !keep.Contains(folder) &&
                        (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1))
                        Directory.Delete(folder, true);
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static bool ValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Encoding.UTF8.GetByteCount(name) > 240 || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        string stem = name.Split('.')[0].ToUpperInvariant();
        return !(new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem) ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])));
    }
    public static void Validate(FileEntry[] entries)
    {
        if (entries.Length is < 1 or > 100) throw new IOException("Copy 1–100 regular files at a time.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in entries)
        {
            if (!ValidName(entry.name) || !names.Add(entry.name) || entry.size < 0 || entry.size > MaxBytes) throw new IOException("Unsupported file name, duplicate name, or file size.");
            total += entry.size;
        }
        if (total > MaxBytes) throw new IOException("File copies are limited to 1 GB per selection.");
    }
    public static FileEntry[] Describe(string[] paths)
    {
        var entries = paths.Select(path =>
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("Copy regular files only. Zip folders first.");
            return new FileEntry(file.Name.Normalize(NormalizationForm.FormC), file.Length);
        }).ToArray(); Validate(entries); return entries;
    }
    public static async Task SendFiles(FileWire wire, string[] paths, FileEntry[] entries, CancellationToken ct)
    {
        for (int i = 0; i < paths.Length; i++)
        {
            using var input = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, true);
            if (input.Length != entries[i].size) throw new IOException("Copied file changed. Copy it again.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] data = new byte[ChunkSize]; long sent = 0;
            while (sent < entries[i].size)
            {
                int count = await input.ReadAsync(data.AsMemory(0, (int)Math.Min(data.Length, entries[i].size - sent)), ct);
                if (count == 0) throw new IOException("Copied file changed.");
                hash.AppendData(data, 0, count); sent += count;
                await wire.Send(new { type = "chunk", index = i, data = Convert.ToBase64String(data, 0, count) }, ct);
            }
            await wire.Send(new { type = "fileEnd", index = i, sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() }, ct);
        }
        await wire.Send(new { type = "end" }, ct);
    }
    public static async Task<string[]> ReceiveFiles(FileWire wire, FileEntry[] entries, CancellationToken ct, Action<string, long, long>? progress = null)
    {
        Validate(entries); Directory.CreateDirectory(CacheRoot);
        string folder = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var paths = new List<string>();
            long completed = 0, total = entries.Sum(entry => entry.size);
            var updates = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < entries.Length; i++)
            {
                string path = Path.Combine(folder, entries[i].name);
                progress?.Invoke(entries[i].name, completed, total);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long received = 0;
                while (true)
                {
                    using var doc = await wire.Read(ct); var item = doc.RootElement;
                    if (item.GetProperty("index").GetInt32() != i) throw new IOException("Invalid file order.");
                    if (item.GetProperty("type").GetString() == "fileEnd")
                    {
                        if (received != entries[i].size || item.GetProperty("sha256").GetString() != Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()) throw new IOException("File verification failed.");
                        break;
                    }
                    if (item.GetProperty("type").GetString() != "chunk") throw new IOException("Invalid file message.");
                    byte[] bytes = Convert.FromBase64String(item.GetProperty("data").GetString()!);
                    if (bytes.Length is < 1 or > ChunkSize || received + bytes.Length > entries[i].size) throw new IOException("Invalid file size.");
                    await output.WriteAsync(bytes, ct); hash.AppendData(bytes); received += bytes.Length;
                    if (updates.ElapsedMilliseconds >= 250)
                    { progress?.Invoke(entries[i].name, completed + received, total); updates.Restart(); }
                }
                completed += received;
                progress?.Invoke(entries[i].name, completed, total);
                paths.Add(path);
            }
            using var end = await wire.Read(ct);
            if (end.RootElement.GetProperty("type").GetString() != "end") throw new IOException("Incomplete file transfer.");
            return paths.ToArray();
        }
        catch { Directory.Delete(folder, true); throw; }
    }
    public const int MaxTextBytes = 1024 * 1024;
    static readonly UTF8Encoding TextEncoding = new(false, true);
    static async Task SendText(FileWire wire, byte[] bytes, CancellationToken ct)
    {
        for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            await wire.Send(new { type = "textChunk", data = Convert.ToBase64String(bytes, offset, Math.Min(ChunkSize, bytes.Length - offset)) }, ct);
        await wire.Send(new { type = "end" }, ct);
    }
    static async Task<string> ReceiveText(FileWire wire, int size, CancellationToken ct)
    {
        if (size < 0 || size > MaxTextBytes) throw new IOException("Text copies are limited to 1 MB.");
        using var bytes = new MemoryStream();
        while (bytes.Length < size)
        {
            using var chunk = await wire.Read(ct);
            if (chunk.RootElement.GetProperty("type").GetString() != "textChunk") throw new IOException("Incomplete text transfer.");
            byte[] data = Convert.FromBase64String(chunk.RootElement.GetProperty("data").GetString()!);
            if (data.Length is < 1 or > ChunkSize || bytes.Length + data.Length > size) throw new IOException("Invalid text size.");
            bytes.Write(data);
        }
        using var end = await wire.Read(ct);
        if (end.RootElement.GetProperty("type").GetString() != "end") throw new IOException("Incomplete text transfer.");
        return TextEncoding.GetString(bytes.ToArray());
    }
    public static async Task UploadText(string host, byte[] key, string text, CancellationToken ct, int port = 24874)
    {
        byte[] bytes = TextEncoding.GetBytes(text);
        if (bytes.Length > MaxTextBytes) throw new IOException("Text copies are limited to 1 MB.");
        using var client = new TcpClient(); using var wire = await Connect(client, host, key, ct, port);
        await wire.Send(new { type = "putText", size = bytes.Length }, ct);
        using var ready = await wire.Read(ct);
        if (ready.RootElement.GetProperty("type").GetString() != "ready") throw new IOException("Text receiver unavailable.");
        await SendText(wire, bytes, ct);
        using var done = await wire.Read(ct);
        if (done.RootElement.GetProperty("type").GetString() != "done" || !done.RootElement.GetProperty("applied").GetBoolean())
            throw new IOException("Mac clipboard changed. Copy the text again to retry.");
    }
    public static async Task<string> DownloadText(string host, byte[] key, string id, CancellationToken ct, int port = 24874)
    {
        using var client = new TcpClient(); using var wire = await Connect(client, host, key, ct, port);
        await wire.Send(new { type = "getText", id }, ct);
        using var begin = await wire.Read(ct);
        if (begin.RootElement.GetProperty("type").GetString() != "textBegin") throw new IOException("Copied text is no longer available.");
        return await ReceiveText(wire, begin.RootElement.GetProperty("size").GetInt32(), ct);
    }
    public static async Task<FileWire> Connect(TcpClient client, string host, byte[] key, CancellationToken ct, int port = 24874)
    {
        await client.ConnectAsync(host, port, ct); client.NoDelay = true;
        var stream = client.GetStream(); byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, nonce = Convert.ToBase64String(nonce) });
        await stream.WriteAsync(hello.Concat(new byte[] { 10 }).ToArray(), ct);
        // The small unencrypted hello is bounded separately; do not buffer past it.
        var line = new List<byte>(); var one = new byte[1];
        while (line.Count < 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("File connection closed.");
            if (one[0] == 10) break; line.Add(one[0]);
        }
        if (line.Count >= 1024) throw new IOException("Invalid file handshake.");
        using var response = JsonDocument.Parse(line.ToArray());
        if (response.RootElement.GetProperty("v").GetInt32() != 1) throw new IOException("Incompatible file protocol.");
        return new FileWire(stream, new Wire(key, nonce, Convert.FromBase64String(response.RootElement.GetProperty("nonce").GetString()!)));
    }
    public static async Task Upload(string host, byte[] key, string[] paths, CancellationToken ct, int port = 24874)
    {
        var entries = Describe(paths); using var client = new TcpClient();
        using var wire = await Connect(client, host, key, ct, port);
        await wire.Send(new { type = "put", entries }, ct);
        using var ready = await wire.Read(ct);
        if (ready.RootElement.GetProperty("type").GetString() != "ready") throw new IOException("File receiver unavailable.");
        await SendFiles(wire, paths, entries, ct);
        using var done = await wire.Read(ct);
        if (done.RootElement.GetProperty("type").GetString() != "done" || !done.RootElement.GetProperty("applied").GetBoolean()) throw new IOException("Mac clipboard changed during transfer. Copy again to retry.");
    }
    public static async Task<string[]> Download(string host, byte[] key, string id, CancellationToken ct, int port = 24874, Action<string, long, long>? progress = null)
    {
        using var client = new TcpClient(); using var wire = await Connect(client, host, key, ct, port);
        await wire.Send(new { type = "get", id }, ct);
        using var begin = await wire.Read(ct);
        if (begin.RootElement.GetProperty("type").GetString() != "begin") throw new IOException("Copied files are no longer available.");
        return await ReceiveFiles(wire, begin.RootElement.GetProperty("entries").Deserialize<FileEntry[]>()!, ct, progress);
    }
}
