using System.Drawing.Drawing2D;
using System.Reflection;

namespace Seamlet;

static class BrandUI
{
    public static readonly Color Ink = Color.FromArgb(23, 53, 54);
    public static readonly Color Muted = Color.FromArgb(96, 113, 113);
    public static readonly Color Accent = Color.FromArgb(19, 121, 104);
    public static readonly Color Paper = Color.FromArgb(244, 243, 238);
    public static Label Label(string text, float size = 10, bool bold = false, int height = 20) => new()
    {
        Text = text, Width = 620, Height = height, ForeColor = Muted, UseMnemonic = false,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0)
    };
    public static void Button(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(206, 219, 209);
        button.BackColor = primary ? Accent : Color.White; button.ForeColor = primary ? Color.White : Ink;
        button.Height = 36; button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI", 10, primary ? FontStyle.Bold : FontStyle.Regular);
    }
    public static FlowLayoutPanel Card(params Control[] controls)
    {
        var card = new FlowLayoutPanel { Width = 640, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 12, 20, 12),
            BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) };
        foreach (var control in controls) { control.Margin = new Padding(0, 0, 0, 4); card.Controls.Add(control); }
        return card;
    }
    public static DeskPreview Build(Form form, ComboBox host, Button refresh, Label discovery, TextBox key,
        ComboBox side, Button connect, Label status, Label clipboard, Label handoff)
    {
        form.Text = "Seamlet"; form.MinimumSize = new Size(720, 600);
        form.AutoScaleMode = AutoScaleMode.Dpi; form.AutoScaleDimensions = new SizeF(96, 96);
        form.BackColor = Paper; form.ForeColor = Ink; form.Font = new Font("Segoe UI", 10);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.FormBorderStyle = FormBorderStyle.Sizable;
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        form.ClientSize = new Size(704, Math.Min(850, workArea.Height - SystemInformation.CaptionHeight - SystemInformation.FrameBorderSize.Height * 2));
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Seamlet.ico"))
            if (stream != null) { using var icon = new Icon(stream); form.Icon = (Icon)icon.Clone(); }
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var root = new FlowLayoutPanel { Location = new Point(28, 24), Width = 648, AutoSize = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 24) };
        scroll.Controls.Add(root); form.Controls.Add(scroll);
        var header = new Panel { Width = 640, Height = 60, Margin = new Padding(0, 0, 0, 12) };
        var picture = new PictureBox { Bounds = new Rectangle(0, 0, 54, 54), SizeMode = PictureBoxSizeMode.Zoom };
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Seamlet.png"))
            if (stream != null) { using var image = Image.FromStream(stream); picture.Image = new Bitmap(image); }
        form.Disposed += (_, _) => picture.Image?.Dispose();
        var wordmark = Label("Seamlet", 23, true, 38); wordmark.Location = new Point(66, 0); wordmark.Width = 550; wordmark.ForeColor = Ink;
        var tagline = Label("A little less between you and your screens.", 9, false, 22); tagline.Location = new Point(68, 39); tagline.Width = 550;
        header.Controls.AddRange([picture, wordmark, tagline]); root.Controls.Add(header);
        var eyebrow = Label("WINDOWS CONTROLLER", 8, true, 20); eyebrow.ForeColor = Accent; root.Controls.Add(eyebrow);
        var title = Label("Your desk, connected.", 25, true, 42); title.ForeColor = Ink; root.Controls.Add(title);
        root.Controls.Add(Label("One mouse. Two computers. Your clipboard comes along.", 10, false, 30));
        var preview = new DeskPreview { Width = 640, Height = 124, Margin = new Padding(0, 0, 0, 12) }; root.Controls.Add(preview);
        var deviceRow = new FlowLayoutPanel { Width = 600, Height = 38, WrapContents = false, Margin = new Padding(0) };
        host.Width = 482; host.Margin = new Padding(0, 3, 10, 0); host.AccessibleName = "Mac device or IP address";
        refresh.Width = 108; refresh.Margin = new Padding(0); Button(refresh); deviceRow.Controls.AddRange([host, refresh]);
        discovery.Width = 600; discovery.Height = 32; discovery.ForeColor = Muted; discovery.Font = new Font("Segoe UI", 9);
        key.Width = 600; key.AccessibleName = "Pairing code"; key.PlaceholderText = "The same code you entered on your Mac";
        side.Width = 600; side.AccessibleName = "Mac screen position";
        side.SelectedIndexChanged += (_, _) => { preview.MacOnRight = side.SelectedIndex == 0; preview.Invalidate(); };
        var section = Label("01  /  PAIR YOUR COMPUTERS", 8, true); section.ForeColor = Accent; section.Width = 600;
        var keyLabel = Label("Pairing code", 9, true, 20); keyLabel.Width = 600; keyLabel.ForeColor = Ink;
        var sideLabel = Label("Where is your Mac?", 9, true, 20); sideLabel.Width = 600; sideLabel.ForeColor = Ink;
        connect.AutoSize = false; connect.Width = 600; Button(connect, true); form.AcceptButton = connect;
        root.Controls.Add(Card(section, deviceRow, discovery, keyLabel, key, side, connect));
        var live = Label("02  /  CONNECTION & CLIPBOARD", 8, true); live.ForeColor = Accent; live.Width = 600;
        status.Width = clipboard.Width = handoff.Width = 600; status.Height = 32; clipboard.Height = 36; handoff.Height = 20;
        status.ForeColor = Ink; clipboard.ForeColor = Muted; handoff.ForeColor = Muted; handoff.Font = new Font("Segoe UI", 8);
        root.Controls.Add(Card(live, status, clipboard, handoff));
        root.Controls.Add(Label("Ctrl + Alt + Esc returns your mouse here.\nUse each computer’s keyboard to copy and paste. Release mouse buttons before crossing.", 9, false, 44));
        return preview;
    }
}

sealed class DeskPreview : Control
{
    public bool MacOnRight = true;
    bool connected;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Connected { get => connected; set { if (connected != value) { connected = value; Invalidate(); } } }
    public DeskPreview() { DoubleBuffered = true; AccessibleName = "Screen arrangement preview"; }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f; g.ScaleTransform(scale, scale); float w = Width / scale;
        using var background = new SolidBrush(Color.FromArgb(231, 238, 231));
        using var surface = Rounded(new RectangleF(0, 0, w, Height / scale), 16); g.FillPath(background, surface);
        using var dark = new SolidBrush(BrandUI.Ink); using var mint = new SolidBrush(Color.FromArgb(101, 214, 187));
        using var accent = new SolidBrush(BrandUI.Accent); using var line = new Pen(BrandUI.Accent, 2);
        using var font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel); using var centered = new StringFormat { Alignment = StringAlignment.Center };
        for (int i = 0; i < 2; i++)
        {
            float x = w / 2 + (i == 0 ? -188 : 40);
            using var frame = Rounded(new RectangleF(x, 18, 148, 70), 9); g.FillPath(dark, frame);
            using var screen = Rounded(new RectangleF(x + 7, 25, 134, 56), 4); g.FillPath(mint, screen);
            string caption = (i == 1) == MacOnRight ? "Mac · receiver" : "This PC · mouse";
            g.DrawString(caption, font, dark, new RectangleF(x - 12, 97, 172, 22), centered);
        }
        g.DrawLine(line, w / 2 - 30, 53, w / 2 + 30, 53);
        g.FillEllipse(Connected ? accent : Brushes.White, w / 2 - 15, 38, 30, 30);
        using var symbol = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(Connected ? "↔" : "+", symbol, Connected ? Brushes.White : accent, new RectangleF(w / 2 - 15, 38, 30, 30), centered);
    }
}
