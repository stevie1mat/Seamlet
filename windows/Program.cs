using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Seamlet;

static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new Controller()); }
}

sealed class Controller : Form
{
    readonly ComboBox host = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 340, DropDownWidth = 440 };
    readonly Button refresh = new() { Text = "Refresh", Width = 88 };
    readonly Label discoveryStatus = new() { Text = "Select a Mac below, or type its IP address.", Width = 440, Height = 40 };
    CancellationTokenSource? discoveryScan;
    readonly TextBox key = new() { PlaceholderText = "Enter the same pairing code as on the Mac", Width = 440, UseSystemPasswordChar = true };
    readonly ComboBox side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    readonly Button connect = new() { Text = "Connect", AutoSize = true };
    readonly Label status = new() { Text = "Start receiving on the Mac, then connect here.", AutoSize = false, Width = 455, Height = 60 };
    readonly Label fileStatus = new() { Text = "Connect, then copy text or files to share them with the Mac.", Width = 455, Height = 50 };
    readonly Label handoffStatus = new() { Text = "", Width = 455, Height = 24 };
    FileClipboard? fileClipboard;
    readonly DeskPreview deskPreview;
    readonly EdgeBubble edgeBubble = new();
    readonly Native.HookProc hookProc;
    readonly System.Windows.Forms.Timer safety = new() { Interval = 100 };
    nint hook;
    Link? link;
    bool ready, remote, right, warping;
    Rectangle screen;
    Point anchor;
    Point previousLocalPoint;
    long nextDisplayCheck;
    bool closing;

    public Controller()
    {
        side.Items.AddRange(["Mac is to the RIGHT of Windows", "Mac is to the LEFT of Windows"]); side.SelectedIndex = 0;
        deskPreview = BrandUI.Build(this, host, refresh, discoveryStatus, key, side, connect, status, fileStatus, handoffStatus);
        refresh.Click += (_, _) => RefreshDevices();
        connect.Click += (_, _) => Toggle();
        hookProc = MouseHook;
        Shown += (_, _) =>
        {
            RefreshDevices();
            if (!Native.RegisterHotKey(Handle, 1, 0x4003, 0x1B)) { status.Text = "Emergency shortcut unavailable. Close the other app using Ctrl+Alt+Escape and restart."; connect.Enabled = false; return; }
            hook = Native.SetWindowsHookExW(14, hookProc, Native.GetModuleHandleW(null), 0);
            if (hook == 0) { status.Text = "Mouse hook failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; connect.Enabled = false; }
        };
        safety.Tick += (_, _) =>
        {
            fileClipboard?.Tick();
            if (!remote && Environment.TickCount64 >= nextDisplayCheck)
            {
                screen = Screen.PrimaryScreen!.Bounds;
                nextDisplayCheck = Environment.TickCount64 + 500;
            }
            if (remote && ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0 && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0 && (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)) ReturnLocal(0.5, true);
        };
        safety.Start();
        FormClosing += (_, _) => { closing = true; edgeBubble.Dispose(); fileClipboard?.Dispose(); discoveryScan?.Cancel(); ReturnLocal(0.5, true); link?.Dispose(); Native.UnregisterHotKey(Handle, 1); if (hook != 0) Native.UnhookWindowsHookEx(hook); safety.Dispose(); };
    }
    async void RefreshDevices()
    {
        if (link != null) return;
        discoveryScan?.Cancel();
        using var scan = new CancellationTokenSource(); discoveryScan = scan;
        string previousAddress = host.SelectedItem is DiscoveredDevice previous ? previous.Address : host.Text.Trim();
        host.Items.Clear(); host.Text = previousAddress;
        discoveryStatus.Text = "Searching for Macs running Seamlet…"; refresh.Enabled = false;
        int count = 0;
        try
        {
            await Task.Run(() => Discovery.Scan(device => UI(() =>
            {
                if (discoveryScan != scan || scan.IsCancellationRequested || link != null) return;
                host.Items.Add(device); count++;
                if (host.Text == device.Address || (string.IsNullOrWhiteSpace(host.Text) && host.SelectedIndex < 0)) host.SelectedItem = device;
                discoveryStatus.Text = $"Found {count} Mac(s). Select a device to connect.";
            }), scan.Token));
            if (discoveryScan == scan && link == null && !closing)
                discoveryStatus.Text = count == 0 ? "No Macs found. Start receiving on the Mac, then Refresh. You can also type its IP." : $"Found {count} Mac(s). Select a device to connect.";
        }
        catch (Exception) { if (!closing && discoveryScan == scan && link == null) discoveryStatus.Text = "Discovery unavailable. You can still type the Mac’s IP address."; }
        finally
        {
            if (discoveryScan == scan) { discoveryScan = null; if (!closing) refresh.Enabled = link == null; }
        }
    }
    void UI(Action action)
    {
        if (closing || IsDisposed) return;
        try { BeginInvoke((Action)(() => { if (!closing) action(); })); } catch (InvalidOperationException) { }
    }
    void Toggle()
    {
        if (link != null) { ReturnLocal(0.5, true); var old = link; link = null; old.Dispose(); SetDisconnected("Disconnected. Mouse is on Windows."); return; }
        byte[] token;
        try { token = Wire.PairingToken(key.Text); }
        catch (ArgumentException error) { status.Text = error.Message; return; }
        string address = host.SelectedItem is DiscoveredDevice device && host.Text == device.ToString() ? device.Address : host.Text.Trim();
        if (string.IsNullOrWhiteSpace(address)) { status.Text = "Select a Mac from the dropdown, or type its IP address."; return; }
        discoveryScan?.Cancel();
        right = side.SelectedIndex == 0;
        screen = Screen.PrimaryScreen!.Bounds;
        Native.GetCursorPos(out var initialPoint); previousLocalPoint = new Point(initialPoint.X, initialPoint.Y);
        var current = new Link(); link = current; ready = false;
        handoffStatus.Text = "";
        current.HandoffMeasured += milliseconds => UI(() => { if (link == current) handoffStatus.Text = $"Last entry acknowledgement: {milliseconds:F0} ms round trip"; });
        current.Control += message => UI(() =>
        {
            if (link != current) return;
            string? type = message.GetProperty("type").GetString();
            if (type == "ready")
            {
                fileClipboard?.Dispose(); fileClipboard = null;
                if (message.TryGetProperty("filesKey", out var secret))
                {
                    byte[] fileKey = Convert.FromBase64String(secret.GetString()!);
                    if (fileKey.Length != 32) { fileStatus.Text = "File sharing unavailable."; return; }
                    fileClipboard = new FileClipboard(address, fileKey, UI, text => fileStatus.Text = text, message.TryGetProperty("text", out var textSupport) && textSupport.GetInt32() == 1);
                    fileStatus.Text = "Clipboard connected. Copy text or files, wait for Ready, then paste on the other computer.";
                }
                else fileStatus.Text = "File sharing unavailable. Update/restart the Mac receiver.";
            }
            else if (type == "files") fileClipboard?.Offer(message.GetProperty("id").GetString()!, message.TryGetProperty("kind", out var kind) && kind.GetString() == "text");
        });
        connect.Text = "Disconnect"; refresh.Enabled = host.Enabled = key.Enabled = side.Enabled = false; status.Text = "Connecting and pairing…";
        current.Message += (type, y) => UI(() =>
        {
            if (link != current) return;
            if (type == "ready") { ready = true; deskPreview.Connected = true; status.Text = "Connected. Move past the " + (right ? "right" : "left") + " edge to control the Mac."; }
            else if (type == "return") ReturnLocal(y, false);
        });
        current.Closed += reason => UI(() => { if (link != current) return; ReturnLocal(0.5, false); link = null; SetDisconnected(reason); });
        // Keep socket and cryptographic work entirely off the hook/UI thread.
        _ = Task.Run(() => current.Run(address, token));
    }
    void SetDisconnected(string reason) { deskPreview.Connected = false; fileClipboard?.Dispose(); fileClipboard = null; fileStatus.Text = "File sharing disconnected."; ready = false; connect.Text = "Connect"; refresh.Enabled = host.Enabled = key.Enabled = side.Enabled = true; status.Text = reason; }
    bool Send(object message)
    {
        if (link?.Send(message) == true) return true;
        ReturnLocal(0.5, false); return false;
    }
    void ReturnLocal(double y, bool notify)
    {
        // Returning 12 pixels inside Windows provides spatial separation without
        // a timed lockout that delays the next crossing.
        bool wasRemote = remote; remote = false;
        if (notify) link?.Send(new { type = "leave" });
        if (wasRemote)
        {
            Warp(right ? screen.Right - 12 : screen.Left + 12, screen.Top + (int)(Math.Clamp(y, 0, 1) * (screen.Height - 1)));
            status.Text = "Mouse is on Windows. Move away from the edge before crossing again.";
        }
    }
    void Warp(int x, int y) { warping = true; try { Native.SetCursorPos(x, y); previousLocalPoint = new Point(x, y); } finally { warping = false; } }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312 && m.WParam == 1) { ReturnLocal(0.5, true); return; }
        base.WndProc(ref m);
    }
    nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || warping) return Native.CallNextHookEx(hook, code, message, data);
        var mouse = Marshal.PtrToStructure<Native.Mouse>(data);
        if ((mouse.Flags & 1) != 0) return Native.CallNextHookEx(hook, code, message, data);
        int msg = (int)message;
        if (!remote)
        {
            if (ready && msg == 0x200)
            {
                bool edge = ScreenEdge.Crossed(screen, right, previousLocalPoint, new Point(mouse.Point.X, mouse.Point.Y));
                if (edge && !MouseButtonHeld())
                {
                    anchor = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
                    if (link?.Enter(right, (double)(mouse.Point.Y - screen.Top) / Math.Max(1, screen.Height - 1)) == true)
                    {
                        remote = true; Warp(anchor.X, anchor.Y);
                        edgeBubble.Play(screen, right, mouse.Point.Y);
                        UI(() => { if (remote) status.Text = "Controlling Mac. Ctrl + Alt + Escape returns to Windows."; });
                        return 1;
                    }
                }
            }
            if (msg == 0x200) previousLocalPoint = new Point(mouse.Point.X, mouse.Point.Y);
            return Native.CallNextHookEx(hook, code, message, data);
        }
        switch (msg)
        {
            case 0x200:
                int dx = mouse.Point.X - anchor.X, dy = mouse.Point.Y - anchor.Y;
                if (dx != 0 || dy != 0) Send(new { type = "move", dx, dy });
                if (remote) Warp(anchor.X, anchor.Y);
                break;
            case 0x201: Send(new { type = "button", button = 0, down = true }); break;
            case 0x202: Send(new { type = "button", button = 0, down = false }); break;
            case 0x204: Send(new { type = "button", button = 1, down = true }); break;
            case 0x205: Send(new { type = "button", button = 1, down = false }); break;
            case 0x207: Send(new { type = "button", button = 2, down = true }); break;
            case 0x208: Send(new { type = "button", button = 2, down = false }); break;
            case 0x20A: case 0x20E: Send(new { type = "scroll", delta = (short)(mouse.Data >> 16), horizontal = msg == 0x20E }); break;
        }
        return 1;
    }
    static bool MouseButtonHeld() =>
        Native.GetAsyncKeyState(1) < 0 || Native.GetAsyncKeyState(2) < 0 ||
        Native.GetAsyncKeyState(4) < 0 || Native.GetAsyncKeyState(5) < 0 ||
        Native.GetAsyncKeyState(6) < 0;
}
