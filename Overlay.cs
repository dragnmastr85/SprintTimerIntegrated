using System;
using System.Drawing;
using System.Windows.Forms;

public sealed partial class InputTest
{
    private readonly NumericUpDown reminderSeconds = new NumericUpDown();
    private readonly NumericUpDown overlayX = new NumericUpDown();
    private readonly NumericUpDown overlayY = new NumericUpDown();
    private readonly NumericUpDown overlayOpacity = new NumericUpDown();
    private readonly CheckBox overlayEnabled = new CheckBox();
    private readonly Label overlayNotice = new Label();

    private ReminderOverlay reminderOverlay;

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
        overlayNotice.Text =
            "Position is relative to the selected capture monitor.\r\n" +
            "Reminder only; icon confirmation still controls benefit timing.";
        Controls.Add(overlayNotice);

        reminderOverlay = new ReminderOverlay();

        // No owner: minimizing the diagnostic window should not hide it.
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
        if (reminderOverlay == null || reminderOverlay.IsDisposed)
            return;

        if (!overlayEnabled.Checked)
        {
            reminderOverlay.Hide();
            return;
        }

        Screen[] screens = Screen.AllScreens;
        int monitor = captureMonitor.SelectedIndex;
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

        Rectangle overlayBounds = new Rectangle(
            bounds.Left + x,
            bounds.Top + y,
            reminderOverlay.Width,
            reminderOverlay.Height
        );

        // Match the detector's rounded band coordinates.
        int left = (int)Math.Round(bounds.Width * bandLeft);
        int right = (int)Math.Round(bounds.Width * bandRight);
        int top = (int)Math.Round(bounds.Height * bandTop);
        int bottom = (int)Math.Round(bounds.Height * bandBottom);

        Rectangle detectorBand = new Rectangle(
            bounds.Left + left,
            bounds.Top + top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top)
        );

        if (overlayBounds.IntersectsWith(detectorBand))
        {
            reminderOverlay.Hide();
            overlayNotice.Text =
                "Overlay hidden: it overlaps the saved detector band.\r\n" +
                "Change X/Y offsets to keep the capture area unobstructed.";
            return;
        }

        overlayNotice.Text =
            "Position is relative to the selected capture monitor.\r\n" +
            "Reminder only; icon confirmation still controls benefit timing.";

        if (reminderOverlay.Bounds != overlayBounds)
            reminderOverlay.Bounds = overlayBounds;

        double opacity = (double)overlayOpacity.Value / 100.0;
        if (Math.Abs(reminderOverlay.Opacity - opacity) > 0.001)
            reminderOverlay.Opacity = opacity;

        double seconds =
            (elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) / 1000.0;

        bool running = elapsed.IsRunning && !capturing;
        bool detectorFailed = captureError.Length > 0;
        double threshold = (double)reminderSeconds.Value;

        // This is display-only. Never write to elapsed or vision state.
        bool remind = running && !detectorFailed &&
            threshold > 0 && seconds >= threshold;

        bool bright = remind &&
            (visionClock.ElapsedMilliseconds / 400) % 2 == 0;

        string status;
        Color accent;

        if (capturing)
        {
            status = "ASSIGNING INPUT";
            accent = Color.Silver;
        }
        else if (detectorFailed)
        {
            status = "DETECTOR ERROR";
            accent = Color.LightCoral;
        }
        else if (sessionConfirmed && iconStable)
        {
            status = "ICON CONFIRMED";
            accent = Color.LightGreen;
        }
        else if (waitingForReturn && !running)
        {
            status = "WAITING FOR ICON";
            accent = Color.Orange;
        }
        else if (running)
        {
            status = "ESTIMATED";
            accent = Color.Khaki;
        }
        else
        {
            status = "READY / NO ICON";
            accent = Color.Silver;
        }

        string footer;
        if (detectorFailed)
            footer = "Capture unavailable; time is unverified";
        else if (remind)
            footer = "REMINDER - not an end-of-benefit signal";
        else if (threshold > 0)
            footer = "Reminder at " + threshold.ToString("0.0") + " s";
        else
            footer = "Reminder disabled";

        reminderOverlay.SetDisplay(
            seconds, status, footer, accent, bright
        );

        if (!reminderOverlay.Visible)
            reminderOverlay.Show();
    }
}

internal sealed class ReminderOverlay : Form
{
    private readonly Font timeFont =
        new Font("Segoe UI", 25, FontStyle.Bold);
    private readonly Font statusFont =
        new Font("Segoe UI", 9, FontStyle.Bold);
    private readonly Font footerFont =
        new Font("Segoe UI", 8);

    private string timeText = "0.0 s";
    private string statusText = "READY / NO ICON";
    private string footerText = "Reminder disabled";
    private Color accentColor = Color.Silver;
    private bool flashBright;

    internal ReminderOverlay()
    {
        Text = "Sprint reminder";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(310, 100);
        TopMost = true;
        Opacity = 0.78;
        BackColor = Color.FromArgb(20, 24, 29);
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // LAYERED | TRANSPARENT | NOACTIVATE | TOOLWINDOW
            cp.ExStyle |= 0x00080000 | 0x00000020 |
                          0x08000000 | 0x00000080;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;

        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = new IntPtr(MA_NOACTIVATE);
            return;
        }

        base.WndProc(ref m);
    }

    internal void SetDisplay(
        double seconds, string status, string footer,
        Color accent, bool bright)
    {
        string nextTime = seconds.ToString("0.0") + " s";

        if (timeText == nextTime &&
            statusText == status &&
            footerText == footer &&
            accentColor == accent &&
            flashBright == bright)
            return;

        timeText = nextTime;
        statusText = status;
        footerText = footer;
        accentColor = accent;
        flashBright = bright;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Color background = flashBright
            ? Color.FromArgb(125, 65, 12)
            : Color.FromArgb(20, 24, 29);

        e.Graphics.Clear(background);

        using (Pen border = new Pen(
            flashBright ? Color.Orange : accentColor, 2))
        {
            e.Graphics.DrawRectangle(
                border, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3
            );
        }

        TextRenderer.DrawText(
            e.Graphics, statusText, statusFont,
            new Rectangle(12, 7, ClientSize.Width - 24, 20),
            accentColor,
            TextFormatFlags.Left | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine
        );

        TextRenderer.DrawText(
            e.Graphics, timeText, timeFont,
            new Rectangle(12, 25, ClientSize.Width - 24, 45),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine
        );

        TextRenderer.DrawText(
            e.Graphics, footerText, footerFont,
            new Rectangle(12, 76, ClientSize.Width - 24, 18),
            Color.WhiteSmoke,
            TextFormatFlags.Left | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timeFont.Dispose();
            statusFont.Dispose();
            footerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}