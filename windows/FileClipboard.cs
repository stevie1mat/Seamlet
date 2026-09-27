using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace MultipleMouse;

// Created/ticked on the UI (STA) thread. Disk and network work runs separately.
sealed class FileClipboard : IDisposable
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    readonly string host;
    readonly byte[] key;
    readonly bool textEnabled;
    readonly Action<Action> ui;
    readonly Action<string> status;
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? sending, receiving;
    uint seen = GetClipboardSequenceNumber();
    bool disposed;
    public FileClipboard(string host, byte[] key, Action<Action> ui, Action<string> status, bool textEnabled = false)
    {
        this.textEnabled = textEnabled; this.host = host; this.key = key; this.ui = ui; this.status = status;
        try
        {
            string[] retained = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : [];
            _ = Task.Run(() => FileTransfer.CleanOldCache(retained));
        }
        catch (ExternalException) { } // Skip cleanup if the live clipboard cannot be inspected.
    }
    public void Tick()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (disposed || sequence == seen) return;
        string[] paths; string? text = null;
        try
        {
            paths = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : [];
            if (paths.Length == 0 && Clipboard.ContainsText(TextDataFormat.UnicodeText))
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (GetClipboardSequenceNumber() != sequence) return;
            seen = sequence;
        }
        catch (ExternalException) { return; } // Clipboard is temporarily locked; retry next tick.
        sending?.Cancel(); receiving?.Cancel();
        if (paths.Length == 0)
        {
            if (text != null)
            {
                if (textEnabled) SendText(text);
                else status("Update the Mac app to enable text copying.");
            }
            return;
        }
        // Files we staged must never bounce back to their source.
        if (paths.All(p => Path.GetFullPath(p).StartsWith(FileTransfer.CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation.CancelAfter(TimeSpan.FromMinutes(15)); sending = operation;
        status("Syncing copied files to Mac…");
        _ = Task.Run(async () =>
        {
            try { await FileTransfer.Upload(host, key, paths, operation.Token); Report(operation, true, "Files ready to paste on Mac (Cmd+V)."); }
            catch (OperationCanceledException) { Report(operation, true, "Clipboard transfer cancelled or timed out. Copy again to retry."); }
            catch (Exception e) { Report(operation, true, "Clipboard transfer: " + e.Message); }
            finally { ui(() => { if (sending == operation) sending = null; operation.Dispose(); }); }
        });
    }
    void SendText(string text)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > FileTransfer.MaxTextBytes)
        { status("Text copies are limited to 1 MB."); return; }
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(30)); sending = operation;
        status("Syncing copied text to Mac…");
        _ = Task.Run(async () =>
        {
            try { await FileTransfer.UploadText(host, key, text, operation.Token); Report(operation, true, "Text ready to paste on Mac (Cmd+V)."); }
            catch (OperationCanceledException) { Report(operation, true, "Text copy cancelled. Copy again to retry."); }
            catch (Exception e) { Report(operation, true, "Text copy: " + e.Message); }
            finally { ui(() => { if (sending == operation) sending = null; operation.Dispose(); }); }
        });
    }
    public void Offer(string id, bool isText = false)
    {
        if (disposed || (isText && !textEnabled)) return;
        receiving?.Cancel();
        sending?.Cancel();
        uint baseline = GetClipboardSequenceNumber();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation.CancelAfter(TimeSpan.FromMinutes(15)); receiving = operation;
        status(isText ? "Receiving copied text from Mac…" : "Receiving copied files from Mac — wait before pasting…");
        _ = Task.Run(async () =>
        {
            try
            {
                // Replace the previous file list immediately, without blocking the mouse UI
                // on WinForms' default clipboard retries. This format cannot be pasted as a file.
                baseline = await Publish(operation, baseline, () =>
                {
                    var pending = new DataObject(); pending.SetData("MultipleMouse.PendingFiles", id); return pending;
                });
                if (isText)
                {
                    string text = await FileTransfer.DownloadText(host, key, id, operation.Token);
                    await Publish(operation, baseline, () =>
                    {
                        var data = new DataObject(); data.SetData(DataFormats.UnicodeText, true, text); return data;
                    });
                    Report(operation, false, "Text ready to paste here (Ctrl+V).");
                }
                else
                {
                    string[] paths = await FileTransfer.Download(host, key, id, operation.Token, progress: (name, bytes, total) =>
                        Report(operation, false, $"Receiving {name}: {bytes / 1048576.0:F1} / {total / 1048576.0:F1} MB — wait before pasting…"));
                    await Publish(operation, baseline, () =>
                    {
                        var files = new StringCollection(); files.AddRange(paths);
                        var data = new DataObject(); data.SetFileDropList(files); return data;
                    });
                    Report(operation, false, "Files ready to paste here (Ctrl+V).");
                }
            }
            catch (OperationCanceledException) { Report(operation, false, "Clipboard transfer cancelled or timed out. Copy again to retry."); }
            catch (Exception e) { Report(operation, false, "Clipboard transfer: " + e.Message); }
            finally { ui(() => { if (receiving == operation) receiving = null; operation.Dispose(); }); }
        });
    }
    async Task<uint> Publish(CancellationTokenSource operation, uint baseline, Func<DataObject> create)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            operation.Token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ui(() =>
            {
                try
                {
                    if (disposed || receiving != operation || operation.IsCancellationRequested)
                    { completion.TrySetCanceled(); return; }
                    if (GetClipboardSequenceNumber() != baseline)
                        throw new IOException("Clipboard changed. Copy on the Mac again to retry.");
                    Clipboard.SetDataObject(create(), true, 0, 0);
                    seen = GetClipboardSequenceNumber(); completion.TrySetResult(seen);
                }
                catch (ExternalException) { completion.TrySetResult(null); }
                catch (Exception e) { completion.TrySetException(e); }
            });
            uint? result = await completion.Task.WaitAsync(operation.Token);
            if (result.HasValue) return result.Value;
            await Task.Delay(50, operation.Token);
        }
        throw new IOException("Clipboard busy. Copy on the Mac again to retry.");
    }
    void Report(CancellationTokenSource operation, bool upload, string message) => ui(() => { if (!disposed && (upload ? sending : receiving) == operation) status(message); });
    public void Dispose() { disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
