using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public sealed partial class InputTest
{
    private readonly NumericUpDown reminderSeconds = new NumericUpDown();
    private readonly NumericUpDown overlayX = new NumericUpDown();
    private readonly NumericUpDown overlayY = new NumericUpDown();
    private readonly NumericUpDown overlayOpacity = new NumericUpDown();
    private readonly CheckBox overlayEnabled = new CheckBox();
    private readonly CheckBox showElapsedSeconds = new CheckBox();
    private readonly Label overlayNotice = new Label();
    private ReminderOverlay reminderOverlay;

    // Presentation only: these never feed back into the session or detector.
    private bool overlayEnded;
    private double overlayFinalSeconds;
    private double overlayFinalDuration;

    private double OverlayRankDuration
    {
        get
        {
            int rank = swiftRank.SelectedIndex;
            return (double)SwiftDurations[rank >= 0 && rank < SwiftDurations.Length ? rank : 4];
        }
    }

    private void ClearOverlayProgress()
    {
        overlayEnded = false;
        overlayFinalSeconds = 0;
        overlayFinalDuration = 0;
    }

    private void RetainOverlayEnd(double seconds)
    {
        overlayFinalSeconds = Math.Max(0, seconds);
        overlayFinalDuration = OverlayRankDuration;
        overlayEnded = true;
    }

    private void InitializeReminderOverlay()
    {
        ClientSize = new Size(ClientSize.Width, 795);
        Label reminderTitle = new Label();
        reminderTitle.Text = "Reminder at (seconds; 0 = off)";
        reminderTitle.SetBounds(12, 647, 205, 23);
        Controls.Add(reminderTitle);
        reminderSeconds.DecimalPlaces = 1;
        reminderSeconds.Minimum = 0;
        reminderSeconds.Maximum = 3600;
        reminderSeconds.Increment = 0.5M;
        reminderSeconds.Value = 0;
        reminderSeconds.SetBounds(220, 644, 90, 26);
        Controls.Add(reminderSeconds);

        overlayEnabled.Text = "Show overlay";
        overlayEnabled.Checked = true;
        overlayEnabled.SetBounds(335, 645, 145, 25);
        Controls.Add(overlayEnabled);
        showElapsedSeconds.Text = "Show elapsed seconds";
        showElapsedSeconds.AutoSize = true;
        showElapsedSeconds.Checked = true;

        Label xTitle = new Label();
        xTitle.Text = "X offset";
        xTitle.SetBounds(12, 685, 60, 23);
        Controls.Add(xTitle);
        overlayX.Minimum = 0;
        overlayX.Maximum = 20000;
        overlayX.Value = 20;
        overlayX.SetBounds(75, 682, 80, 26);
        Controls.Add(overlayX);

        Label yTitle = new Label();
        yTitle.Text = "Y offset";
        yTitle.SetBounds(175, 685, 60, 23);
        Controls.Add(yTitle);
        overlayY.Minimum = 0;
        overlayY.Maximum = 20000;
        overlayY.Value = 20;
        overlayY.SetBounds(238, 682, 80, 26);
        Controls.Add(overlayY);

        Label opacityTitle = new Label();
        opacityTitle.Text = "Opacity %";
        opacityTitle.SetBounds(340, 685, 75, 23);
        Controls.Add(opacityTitle);
        overlayOpacity.Minimum = 20;
        overlayOpacity.Maximum = 100;
        overlayOpacity.Value = 78;
        overlayOpacity.SetBounds(420, 682, 75, 26);
        Controls.Add(overlayOpacity);
        overlayNotice.SetBounds(12, 722, 675, 58);
        overlayNotice.Text = "Position is relative to the selected capture monitor.";
        Controls.Add(overlayNotice);
        reminderOverlay = new ReminderOverlay();
        FormClosed += delegate
        {
            if (reminderOverlay != null)
            {
                reminderOverlay.Dispose();
                reminderOverlay = null;
            }
        };
    }

    private void UpdateReminderOverlay(object sender, EventArgs e)
    {
        if (reminderOverlay == null || reminderOverlay.IsDisposed) return;

        bool running = elapsed.IsRunning && !capturing;
        if (running) overlayEnded = false;
        reminderOverlay.ConfigureSeconds(showElapsedSeconds.Checked);
        if (!overlayEnabled.Checked)
        {
            reminderOverlay.Hide();
            return;
        }
        Screen[] screens = Screen.AllScreens;
        int monitor = ActiveCaptureMonitorIndex;
        if (monitor < 0 || monitor >= screens.Length)
        {
            reminderOverlay.Hide();
            overlayNotice.Text = "Overlay hidden: capture monitor unavailable.";
            return;
        }
        Rectangle bounds = screens[monitor].Bounds;
        int maxX = Math.Max(0, bounds.Width - reminderOverlay.Width);
        int maxY = Math.Max(0, bounds.Height - reminderOverlay.Height);
        int x = Math.Min((int)overlayX.Value, maxX);
        int y = Math.Min((int)overlayY.Value, maxY);
        Rectangle overlayBounds = new Rectangle(bounds.Left + x, bounds.Top + y,
            reminderOverlay.Width, reminderOverlay.Height);
        int left = (int)Math.Round(bounds.Width * bandLeft);
        int right = (int)Math.Round(bounds.Width * bandRight);
        int top = (int)Math.Round(bounds.Height * bandTop);
        int bottom = (int)Math.Round(bounds.Height * bandBottom);
        Rectangle detectorBand = new Rectangle(bounds.Left + left, bounds.Top + top,
            Math.Max(0, right - left), Math.Max(0, bottom - top));
        if (overlayBounds.IntersectsWith(detectorBand))
        {
            reminderOverlay.Hide();
            overlayNotice.Text = "Overlay hidden: it overlaps the saved detector band.\r\n" +
                "Change X/Y offsets to keep the capture area unobstructed.";
            return;
        }
        overlayNotice.Text = "Position is relative to the selected capture monitor.";
        if (reminderOverlay.Bounds != overlayBounds) reminderOverlay.Bounds = overlayBounds;
        double opacity = (double)overlayOpacity.Value / 100.0;
        if (Math.Abs(reminderOverlay.Opacity - opacity) > 0.001)
            reminderOverlay.Opacity = opacity;

        bool failed = captureError.Length > 0;
        bool ended = overlayEnded && !running && !capturing && !failed;
        double seconds = ended ? overlayFinalSeconds : running
            ? (elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) / 1000.0 : 0;
        double duration = ended ? overlayFinalDuration : OverlayRankDuration;
        double threshold = EffectiveReminderSeconds;
        bool warning = running && !failed && threshold > 0 && seconds >= threshold;
        bool bright = warning && (visionClock.ElapsedMilliseconds / 400) % 2 == 0;
        reminderOverlay.SetProgress(seconds, duration, threshold,
            running, sessionConfirmed && iconStable, ended, failed, warning, bright);
        if (!reminderOverlay.Visible) reminderOverlay.Show();
    }
}

internal sealed class ReminderOverlay : Form
{
    private readonly Font timeFont = new Font("Segoe UI", 10, FontStyle.Bold);
    private bool secondsVisible = true;
    private int fillPixels;
    private int warningTick = -1;
    private string timeText = "0.0 s";
    private Color fillColor = Color.FromArgb(51, 204, 128);
    private bool dashed;
    private bool failed;
    private string renderKey = "";

    internal ReminderOverlay()
    {
        Text = "Sprint progress";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(204, 48);
        TopMost = true;
        Opacity = 0.78;
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;
        DoubleBuffered = true;
    }

    internal void ConfigureSeconds(bool visible)
    {
        if (secondsVisible == visible) return;
        secondsVisible = visible;
        ClientSize = new Size(204, visible ? 48 : 22);
        renderKey = "";
        Invalidate();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            return cp;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }

    internal void SetProgress(double seconds, double duration, double threshold,
        bool running, bool confirmed, bool ended, bool error, bool warning, bool bright)
    {
        int pixels = error || (!running && !ended) ? 0 :
            (int)Math.Round(200 * Math.Max(0, Math.Min(1, seconds / duration)));
        int tick = threshold > 0 && threshold < duration
            ? (int)Math.Round(200 * threshold / duration) : -1;
        string text = error ? "--" : seconds.ToString("0.0") + " s";
        Color color = ended ? Color.FromArgb(239, 78, 85) : warning
            ? (bright ? Color.FromArgb(255, 179, 64) : Color.FromArgb(226, 139, 38))
            : Color.FromArgb(51, 204, 128);
        bool estimated = running && !confirmed;
        string key = pixels + "|" + tick + "|" + text + "|" + color.ToArgb() +
            "|" + estimated + "|" + error;
        if (renderKey == key) return;
        renderKey = key;
        fillPixels = pixels;
        warningTick = tick;
        timeText = text;
        fillColor = color;
        dashed = estimated;
        failed = error;
        Invalidate();
    }

    private static GraphicsPath Capsule(RectangleF rect)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = Math.Min(rect.Width, rect.Height);
        if (diameter <= 0) return path;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 90, 180);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF bar = new RectangleF(2, 2, 200, 18);
        using (GraphicsPath track = Capsule(bar))
        {
            using (Brush brush = new SolidBrush(Color.FromArgb(26, 31, 38)))
                g.FillPath(brush, track);
            if (fillPixels > 0)
            {
                GraphicsState clip = g.Save();
                g.SetClip(track);
                using (GraphicsPath fill = Capsule(new RectangleF(2, 2, fillPixels, 18)))
                using (Brush brush = new SolidBrush(fillColor)) g.FillPath(brush, fill);
                g.Restore(clip);
            }
            using (Pen border = new Pen(failed ? Color.FromArgb(150, 154, 164)
                : Color.FromArgb(110, 122, 134), 1))
            {
                if (dashed || failed) border.DashStyle = DashStyle.Dash;
                g.DrawPath(border, track);
            }
        }
        if (warningTick > 0 && warningTick < 200 && !failed)
        {
            using (Pen marker = new Pen(Color.FromArgb(205, 212, 221), 1))
                g.DrawLine(marker, 2 + warningTick, 6, 2 + warningTick, 16);
        }
        if (secondsVisible)
        {
            using (GraphicsPath glyphs = new GraphicsPath())
            {
                glyphs.AddString(timeText, timeFont.FontFamily, (int)timeFont.Style,
                    13.5f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF textBounds = glyphs.GetBounds();
                using (Matrix move = new Matrix())
                {
                    move.Translate((ClientSize.Width - textBounds.Width) / 2 - textBounds.Left,
                        26 - textBounds.Top);
                    glyphs.Transform(move);
                }
                using (Pen outline = new Pen(Color.FromArgb(12, 15, 19), 2.5f))
                {
                    outline.LineJoin = LineJoin.Round;
                    g.DrawPath(outline, glyphs);
                }
                using (Brush ink = new SolidBrush(Color.FromArgb(240, 243, 247)))
                    g.FillPath(ink, glyphs);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timeFont.Dispose();
        base.Dispose(disposing);
    }
}
