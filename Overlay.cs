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
    private readonly CheckBox alwaysShowOverlayCheck = new CheckBox();
    private readonly CheckBox exhaustionSoundCheck = new CheckBox();
    private readonly NumericUpDown exhaustionSoundVolume = new NumericUpDown();
    private readonly NumericUpDown exhaustionGraceMs = new NumericUpDown();
    private readonly SprintOverlayCuePolicy overlayCues = new SprintOverlayCuePolicy();
    private System.Media.SoundPlayer exhaustionPlayer;
    private System.IO.MemoryStream exhaustionStream;

    private void LogOverlayCue(string text)
    {
        if (diagnosticLoggingEnabled)
            LogVision("Overlay cue @" + visionClock.ElapsedMilliseconds + " ms: " + text);
    }

    private void PrepareExhaustionSound()
    {
        DisposeExhaustionSound();
        if (exhaustionSoundVolume.Value <= 0) return;
        try
        {
            exhaustionStream = new System.IO.MemoryStream(
                ExhaustionChimeWave.Create((int)exhaustionSoundVolume.Value), false);
            exhaustionPlayer = new System.Media.SoundPlayer(exhaustionStream);
            exhaustionPlayer.Load();
        }
        catch (Exception ex)
        {
            DisposeExhaustionSound();
            LogOverlayCue("Audio preparation failed: " + ex.Message);
        }
    }

    private void DisposeExhaustionSound()
    {
        if (exhaustionPlayer != null)
        {
            exhaustionPlayer.Stop();
            exhaustionPlayer.Dispose();
            exhaustionPlayer = null;
        }
        if (exhaustionStream != null)
        {
            exhaustionStream.Dispose();
            exhaustionStream = null;
        }
    }

    private bool ReadOverlaySprintActivity()
    {
        if (capturing) return false;
        if (modes.SelectedIndex == 1 && devices.SelectedIndex > 0 &&
            controller != IntPtr.Zero)
        {
            if (SDL.SDL_GameControllerGetAttached(controller) == 0) return false;
            double x = SDL.SDL_GameControllerGetAxis(controller, 0) / 32768.0;
            double y = SDL.SDL_GameControllerGetAxis(controller, 1) / 32768.0;
            return Math.Sqrt(x * x + y * y) >= NeutralDeadZone &&
                y < -ForwardKeepThreshold;
        }
        return active;
    }

    private void StopExhaustionSound()
    {
        if (exhaustionPlayer != null) exhaustionPlayer.Stop();
    }
    private void PlayExhaustionSound(bool preview)
    {
        if ((!preview && !exhaustionSoundCheck.Checked) || exhaustionSoundVolume.Value <= 0)
            return;
        if (exhaustionPlayer == null)
        {
            LogOverlayCue("Playback skipped: prepared audio unavailable.");
            if (preview) MessageBox.Show(debugWindow, "Prepared audio is unavailable. Change volume to retry.",
                "Sound preview unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            LogOverlayCue("Playback requested" + (preview ? " (preview)." : "."));
            exhaustionPlayer.Play();
            LogOverlayCue("Playback call returned; audible onset is not measured.");
        }
        catch (Exception ex)
        {
            LogOverlayCue("Playback failed: " + ex.Message);
            if (preview) MessageBox.Show(debugWindow, ex.Message,
                "Sound preview unavailable", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
    private void InitializeOverlayDebugControls()
    {
        alwaysShowOverlayCheck.Text = "Always show bar UI";
        alwaysShowOverlayCheck.SetBounds(420, 12, 300, 25);
        alwaysShowOverlayCheck.Checked = false;
        debugWindow.Controls.Add(alwaysShowOverlayCheck);

        debugWindow.ClientSize = new Size(debugWindow.ClientSize.Width, 755);
        exhaustionSoundCheck.Text = "Exhaustion sound";
        exhaustionSoundCheck.Checked = true;
        exhaustionSoundCheck.SetBounds(12, 680, 180, 26);
        exhaustionSoundCheck.CheckedChanged += delegate
        {
            if (!exhaustionSoundCheck.Checked) StopExhaustionSound();
        };
        debugWindow.Controls.Add(exhaustionSoundCheck);
        Label volume = new Label();
        volume.Text = "Volume %";
        volume.SetBounds(210, 683, 78, 23);
        debugWindow.Controls.Add(volume);
        exhaustionSoundVolume.Minimum = 0;
        exhaustionSoundVolume.Maximum = 100;
        exhaustionSoundVolume.Value = 40;
        exhaustionSoundVolume.SetBounds(290, 680, 65, 26);
        debugWindow.Controls.Add(exhaustionSoundVolume);
        Button preview = new Button();
        preview.Text = "Preview sound";
        preview.SetBounds(375, 678, 130, 29);
        preview.Click += delegate { PlayExhaustionSound(true); };
        debugWindow.Controls.Add(preview);
        Label notice = new Label();
        notice.Text = "Always-show and audio controls are session-only. Preview ignores mute; volume 0 stays silent.";
        notice.SetBounds(12, 718, 730, 28);
        debugWindow.Controls.Add(notice);
        debugWindow.ClientSize = new Size(debugWindow.ClientSize.Width, 810);
        Label graceLabel = new Label();
        graceLabel.Text = "Exhaustion grace (ms)";
        graceLabel.SetBounds(12, 760, 165, 24);
        debugWindow.Controls.Add(graceLabel);
        exhaustionGraceMs.Minimum = 0;
        exhaustionGraceMs.Maximum = 2000;
        exhaustionGraceMs.Increment = 50;
        exhaustionGraceMs.Value = 300;
        exhaustionGraceMs.SetBounds(180, 757, 85, 26);
        debugWindow.Controls.Add(exhaustionGraceMs);
        Label graceNotice = new Label();
        graceNotice.Text = "Session-only; applies to the next confirmed end.";
        graceNotice.SetBounds(280, 760, 450, 24);
        debugWindow.Controls.Add(graceNotice);
        exhaustionSoundVolume.ValueChanged += delegate { PrepareExhaustionSound(); };
        PrepareExhaustionSound();
        FormClosed += delegate { DisposeExhaustionSound(); };
    }

    private readonly SprintOverlayVisibilityPolicy overlayVisibility =
        new SprintOverlayVisibilityPolicy();

    private void BeginOverlayEpisode(long firstSampleOrInputAt)
    {
        overlayVisibility.Begin(firstSampleOrInputAt);
        if (overlayCues.PendingExhaustion || overlayCues.ExhaustedActive)
            LogOverlayCue("Previous exhaustion cancelled by new episode.");
        StopExhaustionSound();
        overlayCues.Begin(visionClock.ElapsedMilliseconds);
        overlayEnded = false;
    }

    private void AcknowledgeOverlayRetriggerInput()
    {
        long now = visionClock.ElapsedMilliseconds;
        if (!sessionConfirmed || captureError.Length != 0 || capturing ||
            !overlayVisibility.CanAcknowledgeRetrigger(now)) return;
        StopExhaustionSound();
        overlayCues.AcknowledgeInput(now);
        LogOverlayCue("Fresh retrigger input; provisional white glow (not benefit confirmation).");
    }

    private void AbandonOverlayEstimate()
    {
        overlayEnded = false;
        overlayFinalSeconds = 0;
        overlayFinalDuration = 0;
        overlayVisibility.Abandon();
        StopExhaustionSound();
        LogOverlayCue("Unconfirmed estimate abandoned; presentation cleared.");
        overlayCues.Reset();
    }

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
        overlayVisibility.Reset();
        overlayCues.Reset();
        StopExhaustionSound();
        overlayEnded = false;
        overlayFinalSeconds = 0;
        overlayFinalDuration = 0;
    }

    private void RetainOverlayEnd(double seconds)
    {
        overlayFinalSeconds = Math.Max(0, seconds);
        overlayFinalDuration = OverlayRankDuration;
        overlayEnded = true;
        StopExhaustionSound();
        overlayCues.GraceMs = (long)exhaustionGraceMs.Value;
        overlayCues.End(visionClock.ElapsedMilliseconds, seconds, overlayFinalDuration);
        LogOverlayCue("Confirmed end; elapsed=" + seconds.ToString("0.000") +
            " s; qualified=" + overlayCues.PendingExhaustion +
            "; grace deadline=" + (overlayCues.PendingExhaustion
                ? (overlayCues.ExpiryAt + overlayCues.GraceMs).ToString() : "none") + " ms.");
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
        long presentationNow = visionClock.ElapsedMilliseconds;
        bool presentationSprint = ReadOverlaySprintActivity();
        bool confirmedActivity = sessionConfirmed && iconStable && captureError.Length == 0;

        bool failed = captureError.Length > 0;
        bool focused = lastArcFocus.GetValueOrDefault(false);
        bool cueWasOpen = overlayCues.PendingExhaustion || overlayCues.ExhaustedActive;
        bool cueAllowed = focused && !failed && !capturing;
        bool returnPending = iconPending.HasValue && iconPending.Value && !iconStable;
        if (overlayCues.PendingExhaustion && returnPending != overlayCues.ReturnDeferred)
            LogOverlayCue(returnPending ? "Exhaustion deferred: icon return pending."
                : "Pending icon return ended; exhaustion grace eligibility resumes.");
        bool playChime;
        overlayCues.UpdateExhaustion(presentationNow,
            presentationSprint && cueAllowed, returnPending, out playChime);
        if (cueWasOpen && !overlayCues.PendingExhaustion && !overlayCues.ExhaustedActive)
        {
            StopExhaustionSound();
            LogOverlayCue("Exhaustion cancelled: inactive input, focus, assignment, or capture error.");
        }
        if (playChime)
        {
            LogOverlayCue("Exhaustion activated; red pulse starts now.");
            PlayExhaustionSound(false);
        }

        bool postExpiry = overlayEnded && !running && cueAllowed && presentationSprint &&
            (overlayCues.PendingExhaustion || overlayCues.ExhaustedActive);
        bool unconfirmedEstimate = running && !confirmedActivity;
        double fadeFraction = overlayVisibility.Step(presentationNow,
            presentationSprint, running || postExpiry, confirmedActivity, postExpiry,
            alwaysShowOverlayCheck.Checked);

        reminderOverlay.ConfigureSeconds(showElapsedSeconds.Checked);
        if (!lastArcFocus.GetValueOrDefault(false))
        {
            reminderOverlay.Hide();
            return;
        }
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
        double opacity = (double)overlayOpacity.Value / 100.0 * fadeFraction;
        if (Math.Abs(reminderOverlay.Opacity - opacity) > 0.001)
            reminderOverlay.Opacity = opacity;

        bool ended = overlayEnded && !capturing &&
            overlayCues.ShowEnded(presentationSprint, running, failed);
        double seconds = SprintOverlayCuePolicy.DisplaySeconds(
            (elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) / 1000.0,
            overlayFinalSeconds, running, ended);
        double duration = ended ? overlayFinalDuration : OverlayRankDuration;
        double threshold = EffectiveReminderSeconds;
        bool warning = running && !failed && threshold > 0 && seconds >= threshold;
        bool bright = warning && (visionClock.ElapsedMilliseconds / 400) % 2 == 0;
        // Retain the actual rendered fill and color through grace and exhaustion.
        reminderOverlay.SetHoldProgress(postExpiry);
        reminderOverlay.SetProgress(unconfirmedEstimate ? 0 : seconds, duration, threshold,
            running && !unconfirmedEstimate, sessionConfirmed && iconStable,
            ended, failed, warning && !unconfirmedEstimate, bright && !unconfirmedEstimate);
        reminderOverlay.SetBorderCue(overlayCues.White(presentationNow, failed),
            overlayCues.Red(presentationNow, presentationSprint, running, failed));
        if (fadeFraction <= 0)
        {
            reminderOverlay.Hide();
        }
        else if (!reminderOverlay.Visible)
        {
            reminderOverlay.Show();
        }
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
    private int whiteBorder;
    private int redBorder;
    private bool holdProgress;

    internal void SetHoldProgress(bool hold) { holdProgress = hold; }

    internal void SetBorderCue(double white, double red)
    {
        int w = (int)Math.Round(Math.Max(0, Math.Min(1, white)) * 255);
        int r = (int)Math.Round(Math.Max(0, Math.Min(1, red)) * 255);
        if (w == whiteBorder && r == redBorder) return;
        whiteBorder = w;
        redBorder = r;
        Invalidate();
    }

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
        if (holdProgress && !error) return;
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
                using (Brush brush = new SolidBrush(redBorder > 0
                    ? Color.FromArgb(
                        (fillColor.R * (255 - redBorder) + 255 * redBorder) / 255,
                        (fillColor.G * (255 - redBorder) + 65 * redBorder) / 255,
                        (fillColor.B * (255 - redBorder) + 76 * redBorder) / 255)
                    : fillColor)) g.FillPath(brush, fill);
                g.Restore(clip);
            }
            if (!failed && (whiteBorder > 0 || redBorder > 0))
            {
                Color glow = whiteBorder > 0
                    ? Color.FromArgb(whiteBorder, 255, 255, 255)
                    : Color.FromArgb(redBorder, 255, 65, 76);
                using (Pen halo = new Pen(Color.FromArgb(glow.A / 3,
                    glow.R, glow.G, glow.B), 4)) g.DrawPath(halo, track);
                using (Pen edge = new Pen(glow, 2)) g.DrawPath(edge, track);
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

internal sealed class SprintOverlayVisibilityPolicy
{
    internal const long InactiveDelayMs = 5000;
    internal const double FadeMs = 250.0;
    internal long StartedAt = -1;
    internal long LastActiveAt = -1;
    internal long LastTickAt = -1;
    internal bool Eligible;
    internal bool SessionOpen;
    internal bool PreviouslyRevealed;
    internal double Alpha;

    internal void Reset()
    {
        StartedAt = LastActiveAt = LastTickAt = -1;
        Eligible = SessionOpen = PreviouslyRevealed = false;
        Alpha = 0;
    }

    internal bool CanAcknowledgeRetrigger(long now)
    {
        return Eligible && SessionOpen && PreviouslyRevealed &&
            LastActiveAt >= 0 && now - LastActiveAt < InactiveDelayMs;
    }

    internal void Begin(long now)
    {
        if (!SessionOpen)
        {
            StartedAt = now;
            PreviouslyRevealed = false;
            Alpha = 0;
        }
        LastActiveAt = now;
        Eligible = SessionOpen = true;
    }

    internal void Abandon() { Eligible = false; }

    internal double Step(long now, bool inputActive, bool hasDisplay,
        bool confirmedActive, bool postExpiry, bool forceVisible)
    {
        long delta = LastTickAt < 0 ? 0 : Math.Max(0, now - LastTickAt);
        LastTickAt = now;
        if (confirmedActive)
        {
            if (!SessionOpen) StartedAt = now;
            Eligible = SessionOpen = PreviouslyRevealed = true;
            LastActiveAt = now;
        }
        else if (SessionOpen && (inputActive || postExpiry)) LastActiveAt = now;

        bool recent = LastActiveAt >= 0 && now - LastActiveAt < InactiveDelayMs;
        // Shell continuity does not depend on timer-running or exhaustion state.
        bool reveal = confirmedActive || postExpiry ||
            (Eligible && SessionOpen && PreviouslyRevealed && recent);
        if (confirmedActive) Alpha = 1;
        else Alpha = reveal ? Math.Min(1, Alpha + delta / FadeMs)
            : Math.Max(0, Alpha - delta / FadeMs);

        if (SessionOpen && !recent && Alpha <= 0)
        {
            SessionOpen = Eligible = PreviouslyRevealed = false;
            StartedAt = -1;
        }
        return forceVisible ? 1 : Alpha;
    }
}
internal sealed class SprintOverlayCuePolicy
{
    internal long GraceMs = 300;
    internal bool ReturnDeferred;
    internal bool PendingExhaustion;
    internal long ExpiryAt = -1;
    internal bool ExhaustedActive;
    internal long WhiteSince = -1;
    internal long EndedAt = -1;

    internal void Reset()
    {
        PendingExhaustion = ExhaustedActive = ReturnDeferred = false;
        ExpiryAt = WhiteSince = EndedAt = -1;
    }

    internal void Begin(long now)
    {
        Reset();
        // Input estimates and first activations do not acknowledge a refresh.
    }

    internal void ConfirmReturn(long now)
    {
        // Confirmation cancels exhaustion, but never duplicates an input glow.
        PendingExhaustion = ExhaustedActive = ReturnDeferred = false;
        ExpiryAt = EndedAt = -1;
    }

    internal void AcknowledgeInput(long now)
    {
        Reset();
        WhiteSince = now;
    }

    internal void End(long now, double elapsedSeconds, double fullDuration)
    {
        Reset();
        if (elapsedSeconds >= fullDuration - 0.60)
        {
            PendingExhaustion = true;
            ExpiryAt = now;
        }
    }

    internal bool UpdateExhaustion(long now, bool sprinting, bool returnPending, out bool playChime)
    {
        playChime = false;
        if (!sprinting)
        {
            PendingExhaustion = ExhaustedActive = ReturnDeferred = false;
            ExpiryAt = EndedAt = -1;
            return false;
        }
        ReturnDeferred = PendingExhaustion && returnPending;
        if (PendingExhaustion && !ReturnDeferred && now - ExpiryAt >= GraceMs)
        {
            PendingExhaustion = false;
            ExhaustedActive = true;
            EndedAt = now;
            playChime = true;
        }
        return ExhaustedActive;
    }

    internal bool ShowEnded(bool sprinting, bool running, bool failed)
    {
        return ExhaustedActive && sprinting && !running && !failed;
    }

    internal static double DisplaySeconds(double runningSeconds,
        double finalSeconds, bool running, bool ended)
    {
        return ended ? finalSeconds : running ? runningSeconds : 0;
    }

    internal double White(long now, bool failed)
    {
        if (failed || WhiteSince < 0 || now < WhiteSince) return 0;
        return Math.Max(0, 1 - (now - WhiteSince) / 300.0);
    }

    internal double Red(long now, bool sprinting, bool running, bool failed)
    {
        if (!ShowEnded(sprinting, running, failed)) return 0;
        double phase = Math.Max(0, now - EndedAt) * 2 * Math.PI / 1000.0;
        return 0.5 + 0.5 * Math.Cos(phase);
    }
}
internal static class ExhaustionChimeWave
{
    internal static byte[] Create(int volume)
    {
        const int rate = 22050;
        const double length = 0.32;
        int samples = (int)(rate * length);
        double gain = System.Math.Max(0, System.Math.Min(100, volume)) / 100.0;
        using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
        using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate;
                double local = t < 0.12 ? t : t - 0.15;
                double duration = t < 0.12 ? 0.12 : 0.17;
                double frequency = t < 0.12 ? 880 : 660;
                double sample = 0;
                if (local >= 0 && local < duration)
                {
                    double attack = System.Math.Min(1, local / 0.012);
                    double release = System.Math.Min(1, (duration - local) / 0.045);
                    double envelope = attack * release;
                    double phase = 2 * System.Math.PI * frequency * local;
                    sample = (0.8 * System.Math.Sin(phase) +
                        0.12 * System.Math.Sin(2 * phase)) * envelope * gain * 0.65;
                }
                writer.Write((short)(System.Math.Max(-1, System.Math.Min(1, sample)) * 32767));
            }
            writer.Flush();
            return stream.ToArray();
        }
    }
}
