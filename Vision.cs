using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

public sealed partial class InputTest
{
    private readonly ComboBox captureMonitor = new ComboBox();
    private readonly NumericUpDown matchThreshold = new NumericUpDown();
    private readonly TextBox visionSummary = new TextBox();
    private readonly TextBox visionLog = new TextBox();
    private readonly Stopwatch visionClock = Stopwatch.StartNew();

    private System.Threading.Tasks.Task<double[]> captureTask;
    private int captureGeneration;
    private int taskGeneration;
    private double elapsedCreditMs;
    private double lastProcessingMs;
    private long lastSampleAt = -1;
    private long lastSampleIntervalMs;
    private long maximumSampleIntervalMs;

    private bool visionReady;
    private bool sessionConfirmed;
    private bool waitingForReturn;
    private bool iconStable;
    private bool? iconPending;
    private bool? previousRaw;
    private bool previousEstimate;
    private bool freshSprintReady;
    private long pendingIconSince;
    private long nextCaptureAt;
    private long? rawGapSince;
    private long lastGapMs = -1;
    private int rawGapCount;
    private int disappearanceCount;
    private double lastScore;
    private int lastCandidateCount;
    private string captureError = "";
    private double[] referenceSymbol;
    private double bandLeft, bandRight, bandTop, bandBottom;
    private double iconHeightFraction;

    private void InitializeVision()
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        Dictionary<string, object> cfg =
            new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(folder, "detector.json"))
            );

        bandLeft = Convert.ToDouble(cfg["band_left"]);
        bandRight = Convert.ToDouble(cfg["band_right"]);
        bandTop = Convert.ToDouble(cfg["band_top"]);
        bandBottom = Convert.ToDouble(cfg["band_bottom"]);
        iconHeightFraction = Convert.ToDouble(cfg["icon_height_fraction"]);

        using (Bitmap template = new Bitmap(Path.Combine(folder, "icon.png")))
        {
            byte[] pixels = ReadPixels(template);
            referenceSymbol = MakeSymbol(
                pixels, template.Width, 0, 0, template.Width, template.Height
            );
        }

        Label monitorLabel = new Label();
        monitorLabel.Text = "Capture monitor";
        monitorLabel.SetBounds(12, 310, 115, 22);
        Controls.Add(monitorLabel);

        captureMonitor.DropDownStyle = ComboBoxStyle.DropDownList;
        captureMonitor.SetBounds(130, 307, 330, 26);

        foreach (Screen screen in Screen.AllScreens)
        {
            captureMonitor.Items.Add(
                screen.DeviceName + "  " +
                screen.Bounds.Width + " x " + screen.Bounds.Height
            );
        }

        int savedMonitor = Convert.ToInt32(cfg["monitor"]) - 1;
        captureMonitor.SelectedIndex = Math.Max(
            0, Math.Min(savedMonitor, Screen.AllScreens.Length - 1)
        );
        captureMonitor.SelectedIndexChanged += delegate
        {
            ResetVisionSession();
        };
        Controls.Add(captureMonitor);

        Label thresholdLabel = new Label();
        thresholdLabel.Text = "Threshold";
        thresholdLabel.SetBounds(475, 310, 70, 22);
        Controls.Add(thresholdLabel);

        matchThreshold.DecimalPlaces = 3;
        matchThreshold.Minimum = 0.400M;
        matchThreshold.Maximum = 0.950M;
        matchThreshold.Increment = 0.005M;
        matchThreshold.Value = cfg.ContainsKey("threshold")
            ? Convert.ToDecimal(cfg["threshold"]) : 0.700M;
        matchThreshold.SetBounds(550, 307, 100, 26);
        matchThreshold.ValueChanged += delegate
        {
            ResetVisionSession();
        };
        Controls.Add(matchThreshold);

        visionSummary.Multiline = true;
        visionSummary.ReadOnly = true;
        visionSummary.SetBounds(12, 345, 675, 100);
        visionSummary.Font = new Font("Consolas", 10);
        Controls.Add(visionSummary);

        Button clear = new Button();
        clear.Text = "Clear diagnostics";
        clear.SetBounds(12, 455, 150, 28);
        clear.Click += delegate
        {
            rawGapCount = 0;
            disappearanceCount = 0;
            recoveryNarrowCount = 0;
            recoveryWideCount = 0;
            recoveryWideAttempts = 0;
            lastGapMs = -1;
            visionLog.Clear();
            LogVision(
                "Diagnostic counters cleared; timer, track, wider cooldown, " +
                "and confirmation/session state retained."
            );
        };
        Controls.Add(clear);

        Button copy = new Button();
        copy.Text = "Copy diagnostics";
        copy.SetBounds(175, 455, 150, 28);
        copy.Click += delegate
        {
            try
            {
                Clipboard.SetText(
                    visionSummary.Text + Environment.NewLine +
                    Environment.NewLine + visionLog.Text
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Clipboard unavailable");
            }
        };
        Controls.Add(copy);

        visionLog.Multiline = true;
        visionLog.ReadOnly = true;
        visionLog.ScrollBars = ScrollBars.Vertical;
        visionLog.WordWrap = false;
        visionLog.Font = new Font("Consolas", 9);
        visionLog.SetBounds(12, 493, 675, 140);
        Controls.Add(visionLog);

        visionReady = true;
        ResetVisionSession();
        LogVision("Reference loaded. Verify the selected capture monitor.");
        LogVision(
            "Background capture enabled; 50 ms target, 300 ms confirmation."
        );
    }

    private void ResetVisionSession()
    {
        captureGeneration++;
        ClearRecoveryTracking();
        elapsedCreditMs = 0;
        lastSampleAt = -1;
        lastSampleIntervalMs = 0;
        maximumSampleIntervalMs = 0;
        sessionConfirmed = false;
        waitingForReturn = false;
        iconStable = false;
        iconPending = null;
        previousRaw = null;
        previousEstimate = false;
        freshSprintReady = false;
        rawGapSince = null;
        pendingIconSince = 0;
        nextCaptureAt = 0;
        captureError = "";
        elapsed.Reset();

        if (visionReady)
            LogVision("Timer session reset; waiting for initial input or icon.");
    }

    private void LogVision(string text)
    {
        if (suppressFocusResetLog)
            return;

        if (!visionReady)
            return;

        if (visionLog.TextLength > 24000)
            visionLog.Clear();

        visionLog.AppendText(
            DateTime.Now.ToString("HH:mm:ss.fff") + "  " +
            text + Environment.NewLine
        );
    }

    private void PollInput(object sender, EventArgs e)
    {
        if (!AllowArcGameplayPolling(sender, e))
            return;

        PollInputCore(sender, e);

        if (!visionReady || capturing)
            return;

        // Input cannot override a confirmed visible icon.
        if (!sessionConfirmed && !waitingForReturn)
        {
            if (active && !previousEstimate)
            {
                elapsed.Restart();
                LogVision("Initial pre-icon input estimate started.");
            }
            else if (!active && previousEstimate)
            {
                double clearedInitialMs =
                    elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs;
                elapsed.Reset();
                LogVision(
                    "Initial pre-icon input estimate cleared; input inactive; " +
                    "timer before clear: " +
                    (clearedInitialMs / 1000.0).ToString("0.000") + " s."
                );
            }
        }
        else if (waitingForReturn)
        {
            if (!active)
            {
                // Inactive input after disappearance permits a fresh start.
                freshSprintReady = true;

                if (elapsed.IsRunning)
                {
                    elapsed.Reset();
                    elapsedCreditMs = 0;
                    LogVision(
                        "Fresh sprint estimate cleared; input inactive."
                    );
                }
            }
            else if (!previousEstimate && freshSprintReady)
            {
                elapsedCreditMs = 0;
                elapsed.Restart();
                freshSprintReady = false;
                LogVision(
                    "Fresh sprint after inactive input; estimated timer started."
                );
            }
        }
        previousEstimate = active;

        long now = visionClock.ElapsedMilliseconds;

        // Consume completed work on the UI thread.
        if (captureTask != null && captureTask.IsCompleted)
        {
            System.Threading.Tasks.Task<double[]> completed = captureTask;
            captureTask = null;

            try
            {
                double[] result = completed.GetAwaiter().GetResult();

                // Ignore a result captured before a session/settings reset.
                if (taskGeneration == captureGeneration)
                {
                    AcceptRecoveryResult(result);
                    lastScore = result[0];
                    lastCandidateCount = (int)result[3];
                    lastProcessingMs = result[1];
                    long sampleAt = (long)result[2];

                    if (lastSampleAt >= 0)
                    {
                        lastSampleIntervalMs = sampleAt - lastSampleAt;
                        maximumSampleIntervalMs = Math.Max(
                            maximumSampleIntervalMs, lastSampleIntervalMs
                        );
                    }

                    lastSampleAt = sampleAt;
                    captureError = "";
                    ProcessIcon(
                        lastScore >= (double)matchThreshold.Value,
                        sampleAt
                    );
                }
            }
            catch (Exception ex)
            {
                if (taskGeneration == captureGeneration)
                {
                    string error = ex.Message;
                    if (captureError != error)
                        LogVision("CAPTURE ERROR: " + error);

                    captureError = error;
                    ClearRecoveryTracking();
                    iconPending = null;
                    previousRaw = null;
                    rawGapSince = null;
                    lastSampleAt = -1;
                }
            }
        }

        // At most one capture is running. All control access stays here.
        if (captureTask == null && now >= nextCaptureAt)
        {
            Rectangle bounds =
                Screen.AllScreens[captureMonitor.SelectedIndex].Bounds;

            double captureThreshold = (double)matchThreshold.Value;
            bool tracked = recoveryHasTrack;
            int trackedX = recoveryX;
            int trackedY = recoveryY;
            long nextWide = recoveryNextWideAt;

            taskGeneration = captureGeneration;
            nextCaptureAt = now + 50;

            captureTask =
                System.Threading.Tasks.Task.Factory.StartNew<double[]>(
                    delegate {
                        return CaptureAndDetect(
                            bounds, captureThreshold,
                            tracked, trackedX, trackedY, nextWide);
                    }
                );
        }

        if (captureError.Length > 0)
        {
            stateLabel.Text = "DETECTOR ERROR";
            stateLabel.ForeColor = Color.Red;
        }
        else if (sessionConfirmed && iconStable)
        {
            stateLabel.Text = "ICON CONFIRMED";
            stateLabel.ForeColor = Color.ForestGreen;
        }
        else if (waitingForReturn && !elapsed.IsRunning)
        {
            stateLabel.Text = "WAITING FOR ICON";
            stateLabel.ForeColor = Color.DarkOrange;
        }
        else if (elapsed.IsRunning)
        {
            stateLabel.Text = "ESTIMATED / WAITING FOR ICON";
            stateLabel.ForeColor = Color.DarkGoldenrod;
        }
        else
        {
            stateLabel.Text = "READY / NO ICON YET";
            stateLabel.ForeColor = Color.DimGray;
        }

        counterLabel.Text =
            ((elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) / 1000.0)
            .ToString("0.0") + " s";

        string raw = previousRaw.HasValue
            ? (previousRaw.Value ? "FOUND" : "NOT FOUND") : "UNKNOWN";

        string gap = rawGapSince.HasValue
            ? (visionClock.ElapsedMilliseconds - rawGapSince.Value) + " ms"
            : "--";

        visionSummary.Text = String.Format(
            "Raw: {0}   Confirmed: {1}   Similarity: {2:0.000}   Threshold: {3:0.000}\r\n" +
            "Raw gaps: {4}   Confirmed disappearances: {5}\r\n" +
            "Current gap: {6}   Last completed gap: {7}\r\n" +
            "{8}",
            raw, iconStable ? "FOUND" : "NOT FOUND",
            lastScore, matchThreshold.Value,
            rawGapCount, disappearanceCount, gap,
            lastGapMs < 0 ? "--" : lastGapMs + " ms",
            captureError.Length > 0
                ? captureError
                : String.Format(
                    "Target: 50 ms | Processing: {0:0.0} ms | Sample interval: {1} ms | Max: {2} ms",
                    lastProcessingMs,
                    lastSampleIntervalMs,
                    maximumSampleIntervalMs
                ) + String.Format(
                    " | Stage: {0} | N/W: {1}/{2} | W attempts: {3}",
                    RecoverySourceName(recoveryLastSource),
                    recoveryNarrowCount, recoveryWideCount,
                    recoveryWideAttempts
                )
        );
    }

    private double[] CaptureAndDetect(
        Rectangle bounds, double threshold,
        bool hasTrack, int trackX, int trackY, long nextWideAt)
    {
        Stopwatch processing = Stopwatch.StartNew();

        int left = (int)Math.Round(bounds.Width * bandLeft);
        int right = (int)Math.Round(bounds.Width * bandRight);
        int top = (int)Math.Round(bounds.Height * bandTop);
        int bottom = (int)Math.Round(bounds.Height * bandBottom);
        int width = right - left;
        int height = bottom - top;

        if (width <= 0 || height <= 0)
            throw new Exception("Invalid saved search band.");

        using (Bitmap frame = new Bitmap(
            width, height, PixelFormat.Format24bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(frame))
            {
                graphics.CopyFromScreen(
                    bounds.Left + left, bounds.Top + top,
                    0, 0, new Size(width, height),
                    CopyPixelOperation.SourceCopy
                );
            }

            long sampleAt = visionClock.ElapsedMilliseconds;
            byte[] pixels = ReadPixels(frame);
            bool[] yellow = new bool[width * height];

            for (int i = 0; i < yellow.Length; i++)
            {
                int p = i * 3;
                yellow[i] = IsYellow(
                    pixels[p + 2], pixels[p + 1], pixels[p]
                );
            }

            yellow = CloseMask(yellow, width, height);
            double expected = bounds.Height * iconHeightFraction;
            int minimum = Math.Max(8, (int)(expected * 0.65));
            int maximum = (int)(expected * 1.5);
            double best = 0;
            int acceptedCandidates = 0;
            Rectangle bestBox = Rectangle.Empty;

            foreach (Rectangle box in Components(yellow, width, height))
            {
                double ratio = (double)box.Width / box.Height;
                if (box.Width < minimum || box.Width > maximum ||
                    box.Height < minimum || box.Height > maximum ||
                    ratio < 0.70 || ratio > 1.40)
                    continue;

                acceptedCandidates++;
                double[] candidate = MakeSymbol(
                    pixels, width, box.X, box.Y, box.Width, box.Height
                );
                double score = SymbolSimilarity(referenceSymbol, candidate);
                if (score > best)
                {
                    best = score;
                    bestBox = box;
                }
            }

            int source = 0;
            bool wideAttempted = false;
            Rectangle acceptedBox = Rectangle.Empty;

            if (best >= threshold && !bestBox.IsEmpty)
            {
                source = 1;
                acceptedBox = bestBox;
            }
            else if (hasTrack)
            {
                int[] sizes = {
                    (int)Math.Round(expected * 0.95),
                    (int)Math.Round(expected),
                    (int)Math.Round(expected * 1.05)
                };
                Seed seed = new Seed("last_accepted", trackX, trackY, true);
                int evaluations;
                Hit narrow = LocalSearch(
                    pixels, width, height, referenceSymbol,
                    seed, sizes, out evaluations);
                best = Math.Max(best, narrow.Score);

                if (narrow.Score >= threshold)
                {
                    source = 2;
                    acceptedBox = narrow.Box;
                }
                else if (sampleAt >= nextWideAt)
                {
                    wideAttempted = true;
                    nextWideAt = sampleAt + 200;
                    Hit wide = WideSearch(
                        pixels, width, height, referenceSymbol,
                        seed, sizes, out evaluations);
                    best = Math.Max(best, wide.Score);
                    if (wide.Score >= threshold)
                    {
                        source = 3;
                        acceptedBox = wide.Box;
                    }
                }
            }

            if (source != 0)
            {
                hasTrack = true;
                trackX = acceptedBox.X;
                trackY = acceptedBox.Y;
            }

            RememberDiagnosticFrame(frame, best);
            return new double[] {
                best,
                processing.Elapsed.TotalMilliseconds,
                sampleAt,
                acceptedCandidates,
                hasTrack ? 1 : 0,
                trackX,
                trackY,
                nextWideAt,
                source,
                wideAttempted ? 1 : 0
            };
        }
    }

    private void ProcessIcon(bool detected, long now)
    {

        if (!detected && (!previousRaw.HasValue || previousRaw.Value))
            SaveDiagnosticFrame("raw-gap-start", now);

        if (previousRaw.HasValue && previousRaw.Value && !detected)
        {
            rawGapCount++;
            rawGapSince = now;
            LogVision(
                "Raw gap #" + rawGapCount + " started. " +
                "Candidates: " + lastCandidateCount +
                "; similarity: " + lastScore.ToString("0.000") +
                "; threshold: " + matchThreshold.Value.ToString("0.000")
            );
        }
        else if (detected && rawGapSince.HasValue)
        {
            lastGapMs = now - rawGapSince.Value;
            rawGapSince = null;
            LogVision("Raw match returned; gap about " + lastGapMs + " ms.");
        }
        previousRaw = detected;

        if (!iconPending.HasValue || iconPending.Value != detected)
        {
            iconPending = detected;
            pendingIconSince = now;
            return;
        }

        if (now - pendingIconSince < 300 || iconStable == detected)
            return;

        iconStable = detected;

        if (detected)
        {
            if (!sessionConfirmed)
            {
                sessionConfirmed = true;

                bool retainedInitialEstimate = elapsed.IsRunning;
                double retainedInitialMs =
                    elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs;

                if (!elapsed.IsRunning)
                {
                    elapsed.Restart();
                    elapsedCreditMs = Math.Max(
                        0,
                        visionClock.ElapsedMilliseconds - pendingIconSince
                    );
                }

                if (retainedInitialEstimate)
                {
                    LogVision(
                        "First icon confirmation; estimate already running: yes; " +
                        "retained elapsed: " +
                        (retainedInitialMs / 1000.0).ToString("0.000") + " s."
                    );
                }
                else
                {
                    LogVision(
                        "First icon confirmation; estimate already running: no; " +
                        "timer started and credited " +
                        elapsedCreditMs.ToString("0") +
                        " ms since first matching sample."
                    );
                }
            }
            else
            {
                if (elapsed.IsRunning)
                {
                    LogVision(
                        "Icon returned; estimate already running: yes; " +
                        "retained fresh sprint estimate elapsed: " +
                        ((elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs) /
                            1000.0).ToString("0.000") + " s."
                    );
                }
                else
                {
                    elapsed.Restart();
                    elapsedCreditMs = Math.Max(
                        0,
                        visionClock.ElapsedMilliseconds - pendingIconSince
                    );
                    LogVision(
                        "Icon returned; estimate already running: no; timer credited " +
                        elapsedCreditMs.ToString("0") +
                        " ms since first matching sample."
                    );
                }
            }
            waitingForReturn = false;
            freshSprintReady = false;
        }
        else if (sessionConfirmed)
        {
            SaveDiagnosticFrame("confirmed-disappearance", now);
            disappearanceCount++;
            waitingForReturn = true;
            freshSprintReady = !active;
            double timerBeforeResetMs =
                elapsed.Elapsed.TotalMilliseconds + elapsedCreditMs;
            elapsedCreditMs = 0;
            elapsed.Reset();
            LogVision(
                "Confirmed disappearance #" + disappearanceCount +
                "; timer before reset: " +
                (timerBeforeResetMs / 1000.0).ToString("0.000") +
                " s; timer reset. " +
                (freshSprintReady
                    ? "Input inactive; next fresh sprint may start an estimate."
                    : "Held-input restart blocked; waiting for inactive input.")
            );
        }
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        Rectangle rectangle = new Rectangle(
            0, 0, bitmap.Width, bitmap.Height
        );
        using (Bitmap converted = bitmap.Clone(
            rectangle, PixelFormat.Format24bppRgb))
        {
            BitmapData data = converted.LockBits(
                rectangle, ImageLockMode.ReadOnly,
                PixelFormat.Format24bppRgb
            );
            try
            {
                int rowBytes = bitmap.Width * 3;
                byte[] result = new byte[rowBytes * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, y * data.Stride),
                        result, y * rowBytes, rowBytes
                    );
                }
                return result;
            }
            finally
            {
                converted.UnlockBits(data);
            }
        }
    }

    private static bool IsYellow(int r, int g, int b)
    {
        int high = Math.Max(r, Math.Max(g, b));
        int low = Math.Min(r, Math.Min(g, b));
        int delta = high - low;

        if (high < 110 || delta == 0 ||
            delta * 255.0 / high < 95)
            return false;

        double hue;
        if (high == r)
            hue = 60.0 * (g - b) / delta;
        else if (high == g)
            hue = 120.0 + 60.0 * (b - r) / delta;
        else
            hue = 240.0 + 60.0 * (r - g) / delta;

        if (hue < 0)
            hue += 360;

        return hue >= 28 && hue <= 80;
    }

    private static bool[] CloseMask(bool[] source, int width, int height)
    {
        bool[] expanded = new bool[source.Length];
        bool[] result = new bool[source.Length];

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool value = false;
            for (int dy = -1; dy <= 1 && !value; dy++)
            for (int dx = -1; dx <= 1 && !value; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < width && yy >= 0 && yy < height)
                    value = source[yy * width + xx];
            }
            expanded[y * width + x] = value;
        }

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool value = true;
            for (int dy = -1; dy <= 1 && value; dy++)
            for (int dx = -1; dx <= 1 && value; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < width && yy >= 0 && yy < height)
                    value = expanded[yy * width + xx];
            }
            result[y * width + x] = value;
        }

        return result;
    }

    private static List<Rectangle> Components(
        bool[] mask, int width, int height)
    {
        List<Rectangle> result = new List<Rectangle>();
        bool[] visited = new bool[mask.Length];
        int[] queue = new int[mask.Length];

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
                continue;

            int head = 0, tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int minX = start % width, maxX = minX;
            int minY = start / width, maxY = minY;

            while (head < tail)
            {
                int item = queue[head++];
                int x = item % width, y = item / width;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || xx >= width || yy < 0 || yy >= height)
                        continue;

                    int next = yy * width + xx;
                    if (mask[next] && !visited[next])
                    {
                        visited[next] = true;
                        queue[tail++] = next;
                    }
                }
            }

            result.Add(new Rectangle(
                minX, minY, maxX - minX + 1, maxY - minY + 1
            ));
        }
        return result;
    }

    private static double[] MakeSymbol(
        byte[] pixels, int strideWidth, int left, int top,
        int width, int height)
    {
        double[] result = new double[1024];

        // Area-weighted resampling approximates the Python INTER_AREA step.
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            double cx = x - 15.5, cy = y - 15.5;
            if (cx * cx + cy * cy > 11.7 * 11.7)
                continue;

            double x0 = x * width / 32.0;
            double x1 = (x + 1) * width / 32.0;
            double y0 = y * height / 32.0;
            double y1 = (y + 1) * height / 32.0;
            double red = 0, green = 0, blue = 0, total = 0;

            for (int sy = (int)Math.Floor(y0);
                 sy < Math.Ceiling(y1) && sy < height; sy++)
            for (int sx = (int)Math.Floor(x0);
                 sx < Math.Ceiling(x1) && sx < width; sx++)
            {
                double weight =
                    (Math.Min(x1, sx + 1) - Math.Max(x0, sx)) *
                    (Math.Min(y1, sy + 1) - Math.Max(y0, sy));

                int p = ((top + sy) * strideWidth + left + sx) * 3;
                blue += pixels[p] * weight;
                green += pixels[p + 1] * weight;
                red += pixels[p + 2] * weight;
                total += weight;
            }

            if (total <= 0)
                continue;

            red = Math.Round(red / total);
            green = Math.Round(green / total);
            blue = Math.Round(blue / total);
            double low = Math.Min(red, Math.Min(green, blue));
            double high = Math.Max(red, Math.Max(green, blue));
            result[y * 32 + x] = 0.299 * red + 0.587 * green + 0.114 * blue;
        }
        return result;
    }

    private static double SymbolSimilarity(
        double[] reference, double[] candidate)
    {
        double best = 0;

        // Compare only the circular symbol interior.
        // Both samples must remain inside this region when shifted.
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int count = 0;
            double sumR = 0, sumC = 0;
            double sumRR = 0, sumCC = 0, sumRC = 0;

            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                int sx = x - dx;
                int sy = y - dy;

                if (sx < 0 || sx >= 32 || sy < 0 || sy >= 32)
                    continue;

                double rx = x - 15.5;
                double ry = y - 15.5;
                double cx = sx - 15.5;
                double cy = sy - 15.5;

                if (rx * rx + ry * ry > 11.7 * 11.7 ||
                    cx * cx + cy * cy > 11.7 * 11.7)
                    continue;

                double r = reference[y * 32 + x];
                double c = candidate[sy * 32 + sx];

                count++;
                sumR += r;
                sumC += c;
                sumRR += r * r;
                sumCC += c * c;
                sumRC += r * c;
            }

            if (count < 100)
                continue;

            double varianceR = sumRR - sumR * sumR / count;
            double varianceC = sumCC - sumC * sumC / count;

            // Reject effectively flat patches rather than divide by zero.
            if (varianceR <= 0.000001 || varianceC <= 0.000001)
                continue;

            double covariance = sumRC - sumR * sumC / count;
            double score = covariance /
                Math.Sqrt(varianceR * varianceC);

            // Negative correlation is not a valid target match.
            best = Math.Max(best, Math.Min(1.0, score));
        }

        return best;
    }
}