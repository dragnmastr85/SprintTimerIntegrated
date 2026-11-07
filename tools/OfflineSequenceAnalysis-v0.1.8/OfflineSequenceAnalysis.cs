using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyVersion("0.1.8.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.8.0")]

internal static class OfflineSequenceAnalysis
{
    private const string Version = "0.1.8";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(double value)
    {
        return value.ToString("0.000000000", Inv);
    }

    private static void Row(StreamWriter writer, params object[] fields)
    {
        string[] values = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            values[i] = "\"" +
                Convert.ToString(fields[i], Inv).Replace("\"", "\"\"") + "\"";
        writer.WriteLine(String.Join(",", values));
    }

    private static double? MetadataNumber(string text, string key)
    {
        Match m = Regex.Match(text,
            "^" + Regex.Escape(key) + @":\s*([0-9.]+)",
            RegexOptions.Multiline);
        return m.Success
            ? (double?)Double.Parse(m.Groups[1].Value, Inv)
            : null;
    }

    private static string Reject(Rectangle box, int minimum, int maximum)
    {
        List<string> reasons = new List<string>();
        if (box.Width < minimum) reasons.Add("width_below_min");
        if (box.Width > maximum) reasons.Add("width_above_max");
        if (box.Height < minimum) reasons.Add("height_below_min");
        if (box.Height > maximum) reasons.Add("height_above_max");
        double ratio = (double)box.Width / box.Height;
        if (ratio < 0.70) reasons.Add("aspect_below_0.70");
        if (ratio > 1.40) reasons.Add("aspect_above_1.40");
        return String.Join(";", reasons.ToArray());
    }

    private static void SaveMask(bool[] mask, int width, int height, string path)
    {
        using (Bitmap image = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y,
                    mask[y * width + x] ? Color.White : Color.Black);
            image.Save(path, ImageFormat.Png);
        }
    }

    private sealed class Hit
    {
        internal Rectangle Box;
        internal double Score;
    }

    private sealed class Seed
    {
        internal string Name;
        internal int X;
        internal int Y;
        internal bool WithinTestRange;
        internal Seed(string name, int x, int y, bool within)
        {
            Name = name; X = x; Y = y; WithinTestRange = within;
        }
    }

    private static void Keep(List<Hit> hits, Hit hit)
    {
        hits.Add(hit);
        hits.Sort(delegate(Hit a, Hit b) {
            return b.Score.CompareTo(a.Score);
        });
        if (hits.Count > 3) hits.RemoveAt(hits.Count - 1);
    }

    private static Hit LocalSearch(
        byte[] pixels, int width, int height, double[] reference,
        Seed seed, int[] sizes, out int evaluations)
    {
        const int radius = 8;
        evaluations = 0;
        List<Hit> leaders = new List<Hit>();

        foreach (int size in sizes)
        {
            for (int dy = -radius; dy <= radius; dy += 2)
            for (int dx = -radius; dx <= radius; dx += 2)
            {
                Rectangle box = new Rectangle(
                    seed.X + dx, seed.Y + dy, size, size);
                if (box.X < 0 || box.Y < 0 ||
                    box.Right > width || box.Bottom > height) continue;

                double score = SymbolSimilarity(reference, MakeSymbol(
                    pixels, width, box.X, box.Y, box.Width, box.Height));
                Keep(leaders, new Hit { Box = box, Score = score });
                evaluations++;
            }
        }

        if (leaders.Count == 0)
            throw new Exception("No valid local crops for seed " + seed.Name);

        Hit best = leaders[0];
        HashSet<string> visited = new HashSet<string>();
        foreach (Hit leader in leaders)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                Rectangle box = new Rectangle(
                    leader.Box.X + dx, leader.Box.Y + dy,
                    leader.Box.Width, leader.Box.Height);

                if (box.X < 0 || box.Y < 0 ||
                    box.Right > width || box.Bottom > height ||
                    Math.Abs(box.X - seed.X) > radius ||
                    Math.Abs(box.Y - seed.Y) > radius) continue;

                string key = box.X + ":" + box.Y + ":" + box.Width;
                if (!visited.Add(key)) continue;
                double score = SymbolSimilarity(reference, MakeSymbol(
                    pixels, width, box.X, box.Y, box.Width, box.Height));
                evaluations++;
                if (score > best.Score)
                    best = new Hit { Box = box, Score = score };
            }
        }
        return best;
    }

    private sealed class Frame
    {
        internal int Index;
        internal string File;
        internal double Time;
    }

    private sealed class Confirmation
    {
        internal bool EverFound;
        internal bool Present;
        internal double Gap = -1;

        internal string Step(bool found, double time)
        {
            if (found)
            {
                bool returning = EverFound && !Present;
                bool first = !EverFound;
                EverFound = true;
                Present = true;
                Gap = -1;
                return first ? "first_found" :
                    (returning ? "found_after_confirmed_absence" : "");
            }
            if (!EverFound || !Present) return "";
            if (Gap < 0) Gap = time;
            if (time - Gap >= 300.0)
            {
                Present = false;
                return "absence_confirmed_300ms";
            }
            return "";
        }
    }

    private static List<Frame> LoadFrames(string recording)
    {
        string[] lines = File.ReadAllLines(Path.Combine(recording, "frames.csv"));
        if (lines.Length < 2 ||
            !lines[0].TrimStart('\uFEFF').StartsWith(
                "frame,utc,capture_start_ms,interval_ms,capture_ms,save_ms,foreground_pid,status,file,error"))
            throw new Exception("Unexpected recorder CSV header.");

        List<Frame> frames = new List<Frame>();
        double last = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (String.IsNullOrWhiteSpace(lines[i])) continue;
            string[] cells = lines[i].Split(',');
            if (cells.Length < 10 || cells[7] != "ok")
                throw new Exception("Capture error or invalid row at CSV line " + (i + 1));
            double time = Double.Parse(cells[2], Inv);
            if (time < 0 || time <= last)
                throw new Exception("Non-increasing frame times.");
            string name = cells[8];
            if (Path.GetFileName(name) != name || !name.EndsWith(".png"))
                throw new Exception("Unsafe or unexpected frame filename.");
            if (!File.Exists(Path.Combine(recording, name)))
                throw new Exception("Missing frame: " + name);
            frames.Add(new Frame {
                Index = Int32.Parse(cells[0], Inv), File = name, Time = time
            });
            last = time;
        }
        if (frames.Count == 0) throw new Exception("No frames.");
        return frames;
    }

    private static void SaveReview(
        string recording, string destination, List<Frame> frames,
        int position, int width, int height)
    {
        int panelHeight = height + 26;
        using (Bitmap sheet = new Bitmap(width, panelHeight * 3))
        using (Graphics g = Graphics.FromImage(sheet))
        using (Font font = new Font(FontFamily.GenericSansSerif, 10))
        {
            g.Clear(Color.Black);
            for (int slot = 0; slot < 3; slot++)
            {
                int at = position + slot - 1;
                if (at < 0 || at >= frames.Count) continue;
                Frame f = frames[at];
                int y = slot * panelHeight;
                g.DrawString(
                    "frame " + f.Index + " | " + F(f.Time / 1000.0) + " seconds",
                    font, Brushes.White, 3, y + 3);
                using (Bitmap image = new Bitmap(Path.Combine(recording, f.File)))
                    g.DrawImageUnscaled(image, 0, y + 26);
            }
            sheet.Save(destination, ImageFormat.Png);
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("Offline Sequence Analysis v" + Version);
            if (args.Length != 3)
                throw new Exception("Usage: OfflineSequenceAnalysis.exe recording inputs output");

            string recording = Path.GetFullPath(args[0]);
            string inputs = Path.GetFullPath(args[1]);
            string output = Path.GetFullPath(args[2]);
            if (Directory.Exists(output))
                throw new Exception("Output already exists.");
            List<Frame> frames = LoadFrames(recording);

            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> manifest =
                json.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(recording, "manifest.json")));
            Dictionary<string, object> cfg =
                json.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(inputs, "detector.json")));

            int screenHeight = Convert.ToInt32(manifest["screen_height"], Inv);
            int width = Convert.ToInt32(manifest["band_width"], Inv);
            int height = Convert.ToInt32(manifest["band_height"], Inv);
            if (width <= 0 || height <= 0 || screenHeight <= 0)
                throw new Exception("Invalid recording geometry.");

            double expectedSize = screenHeight *
                Convert.ToDouble(cfg["icon_height_fraction"], Inv);
            int minimum = Math.Max(8, (int)(expectedSize * 0.65));
            int maximum = (int)(expectedSize * 1.5);
            int[] sizes = {
                (int)Math.Round(expectedSize * 0.95),
                (int)Math.Round(expectedSize),
                (int)Math.Round(expectedSize * 1.05)
            };

            double? thresholdValue = null;
            string[] sidecars = Directory.GetFiles(inputs, "*raw-gap-start*.txt");
            foreach (string sidecar in sidecars)
            {
                double? value = MetadataNumber(File.ReadAllText(sidecar), "Threshold");
                if (!value.HasValue) continue;
                if (thresholdValue.HasValue &&
                    Math.Abs(thresholdValue.Value - value.Value) > 0.0000001)
                    throw new Exception("Inconsistent saved thresholds.");
                thresholdValue = value;
            }
            if (!thresholdValue.HasValue)
                throw new Exception("Cannot establish threshold from saved diagnostics.");
            double threshold = thresholdValue.Value;

            double[] reference;
            using (Bitmap icon = new Bitmap(Path.Combine(inputs, "icon.png")))
                reference = MakeSymbol(ReadPixels(icon), icon.Width,
                    0, 0, icon.Width, icon.Height);

            Directory.CreateDirectory(output);
            string reviews = Path.Combine(output, "reviews");
            Directory.CreateDirectory(reviews);

            Confirmation originalState = new Confirmation();
            Confirmation combinedState = new Confirmation();
            Seed tracked = null;
            bool previousOriginal = false;
            bool previousCombined = false;
            int originals = 0, recoveries = 0, searches = 0;
            double totalOriginalMs = 0, totalLocalMs = 0;
            double maxCombinedMs = 0;
            List<int> reviewPositions = new List<int>();

            using (StreamWriter results = new StreamWriter(
                Path.Combine(output, "sequence-results.csv"), false, Encoding.UTF8))
            using (StreamWriter events = new StreamWriter(
                Path.Combine(output, "events.csv"), false, Encoding.UTF8))
            {
                Row(results, "frame", "time_seconds", "file",
                    "original_candidates", "original_score", "original_found",
                    "original_x", "original_y", "original_size",
                    "local_attempted", "local_score", "local_found",
                    "local_x", "local_y", "local_size", "local_evaluations",
                    "combined_found", "accepted_source",
                    "tracked_x", "tracked_y", "accepted_dx", "accepted_dy",
                    "original_confirmed_present", "combined_confirmed_present",
                    "original_ms", "local_ms", "combined_ms",
                    "pixel_conversion_ms", "frame_gap_ms");
                Row(events, "frame", "time_seconds", "event", "detail", "review_file");

                for (int position = 0; position < frames.Count; position++)
                {
                    Frame frame = frames[position];
                    byte[] pixels;
                    double conversionMs;
                    using (Bitmap bitmap = new Bitmap(Path.Combine(recording, frame.File)))
                    {
                        if (bitmap.Width != width || bitmap.Height != height)
                            throw new Exception("Unexpected dimensions: " + frame.File);
                        System.Diagnostics.Stopwatch conversion =
                            System.Diagnostics.Stopwatch.StartNew();
                        pixels = ReadPixels(bitmap);
                        conversion.Stop();
                        conversionMs = conversion.Elapsed.TotalMilliseconds;
                    }

                    System.Diagnostics.Stopwatch clock =
                        System.Diagnostics.Stopwatch.StartNew();
                    bool[] yellow = new bool[width * height];
                    for (int i = 0; i < yellow.Length; i++)
                    {
                        int p = i * 3;
                        yellow[i] = IsYellow(pixels[p + 2], pixels[p + 1], pixels[p]);
                    }
                    bool[] closed = CloseMask(yellow, width, height);
                    Hit original = null;
                    int candidates = 0;
                    foreach (Rectangle box in Components(closed, width, height))
                    {
                        if (Reject(box, minimum, maximum).Length != 0) continue;
                        candidates++;
                        double score = SymbolSimilarity(reference, MakeSymbol(
                            pixels, width, box.X, box.Y, box.Width, box.Height));
                        if (original == null || score > original.Score)
                            original = new Hit { Box = box, Score = score };
                    }
                    clock.Stop();
                    double originalMs = clock.Elapsed.TotalMilliseconds;
                    bool originalFound = original != null && original.Score >= threshold;
                    Hit local = null;
                    bool localAttempted = !originalFound && tracked != null;
                    int evaluations = 0;
                    double localMs = 0;
                    if (localAttempted)
                    {
                        searches++;
                        clock.Restart();
                        local = LocalSearch(pixels, width, height, reference,
                            tracked, sizes, out evaluations);
                        clock.Stop();
                        localMs = clock.Elapsed.TotalMilliseconds;
                    }

                    bool localFound = local != null && local.Score >= threshold;
                    bool found = originalFound || localFound;
                    Hit accepted = originalFound ? original : (localFound ? local : null);
                    string acceptedSource = originalFound ? "original" :
                        (localFound ? "local" : "none");
                    int dx = 0, dy = 0;
                    bool moved = false;
                    if (accepted != null)
                    {
                        if (tracked != null)
                        {
                            dx = accepted.Box.X - tracked.X;
                            dy = accepted.Box.Y - tracked.Y;
                            moved = dx != 0 || dy != 0;
                        }
                        tracked = new Seed("last_accepted",
                            accepted.Box.X, accepted.Box.Y, true);
                    }
                    if (originalFound) originals++;
                    if (localFound) recoveries++;

                    string originalEvent = originalState.Step(originalFound, frame.Time);
                    string combinedEvent = combinedState.Step(found, frame.Time);
                    List<string> reasons = new List<string>();
                    if (position == 0) reasons.Add("recording_start");
                    if (position > 0 && originalFound != previousOriginal)
                        reasons.Add("original_raw_" + (originalFound ? "found" : "missing"));
                    if (position > 0 && found != previousCombined)
                        reasons.Add("combined_raw_" + (found ? "found" : "missing"));
                    if (originalEvent.Length > 0)
                        reasons.Add("original_" + originalEvent);
                    if (combinedEvent.Length > 0)
                        reasons.Add("combined_" + combinedEvent);
                    if (moved)
                        reasons.Add("accepted_position_change");

                    if (reasons.Count > 0)
                    {
                        string review = "review-frame-" + frame.Index.ToString("D6", Inv) + ".png";
                        reviewPositions.Add(position);
                        foreach (string reason in reasons)
                            Row(events, frame.Index, F(frame.Time / 1000.0), reason,
                                "source=" + acceptedSource + "; dx=" + dx + "; dy=" + dy,
                                review);
                    }

                    double combinedMs = originalMs + localMs + conversionMs;
                    totalOriginalMs += originalMs;
                    totalLocalMs += localMs;
                    maxCombinedMs = Math.Max(maxCombinedMs, combinedMs);

                    Row(results, frame.Index, F(frame.Time / 1000.0), frame.File,
                        candidates, original == null ? "" : F(original.Score), originalFound,
                        original == null ? "" : original.Box.X.ToString(Inv),
                        original == null ? "" : original.Box.Y.ToString(Inv),
                        original == null ? "" : original.Box.Width.ToString(Inv),
                        localAttempted, local == null ? "" : F(local.Score), localFound,
                        local == null ? "" : local.Box.X.ToString(Inv),
                        local == null ? "" : local.Box.Y.ToString(Inv),
                        local == null ? "" : local.Box.Width.ToString(Inv),
                        evaluations, found, acceptedSource,
                        tracked == null ? "" : tracked.X.ToString(Inv),
                        tracked == null ? "" : tracked.Y.ToString(Inv),
                        accepted == null ? "" : dx.ToString(Inv),
                        accepted == null ? "" : dy.ToString(Inv),
                        originalState.Present, combinedState.Present,
                        F(originalMs), F(localMs), F(combinedMs), F(conversionMs),
                        position == 0 ? "" : F(frame.Time - frames[position - 1].Time));

                    previousOriginal = originalFound;
                    previousCombined = found;
                    if ((position + 1) % 25 == 0 || position == frames.Count - 1)
                    {
                        results.Flush();
                        events.Flush();
                        Console.WriteLine("Processed {0}/{1}", position + 1, frames.Count);
                    }
                }
            }

            Console.WriteLine("Creating {0} transition/position-change review sheets...",
                reviewPositions.Count);
            foreach (int position in reviewPositions)
                SaveReview(recording, Path.Combine(reviews,
                    "review-frame-" + frames[position].Index.ToString("D6", Inv) + ".png"),
                    frames, position, width, height);

            Dictionary<string, object> summary = new Dictionary<string, object>();
            summary["version"] = Version;
            summary["frames"] = frames.Count;
            summary["threshold"] = threshold;
            summary["original_found_frames"] = originals;
            summary["local_search_frames"] = searches;
            summary["local_recovered_frames"] = recoveries;
            summary["combined_found_frames"] = originals + recoveries;
            summary["mean_original_ms"] = totalOriginalMs / frames.Count;
            summary["mean_local_ms_when_attempted"] =
                searches == 0 ? 0 : totalLocalMs / searches;
            summary["max_combined_processing_ms"] = maxCombinedMs;
            summary["review_sheets"] = reviewPositions.Count;
            summary["last_original_confirmed_present"] = originalState.Present;
            summary["last_combined_confirmed_present"] = combinedState.Present;
            summary["last_original_gap_start_ms"] = originalState.Gap;
            summary["last_combined_gap_start_ms"] = combinedState.Gap;
            summary["end_of_recording_is_not_an_end_event"] = true;
            File.WriteAllText(Path.Combine(output, "summary.json"), json.Serialize(summary));

            File.WriteAllText(Path.Combine(output, "README.txt"),
                "Offline Sequence Analysis v" + Version + Environment.NewLine +
                "No supplied seed. Original detection initializes or relocates the track." +
                Environment.NewLine +
                "Local recovery runs only when original matching fails and a track exists." +
                Environment.NewLine +
                "All accepted original/local matches update the track; no confidence hysteresis." +
                Environment.NewLine +
                "The last accepted track is retained after absence to probe stale-track false positives." +
                Environment.NewLine +
                "300ms confirmation is an offline model, not execution of the live timer state machine." +
                Environment.NewLine +
                "Capture-start timestamps drive confirmation; no synthetic end event at EOF." +
                Environment.NewLine +
                "Position changes may be crop jitter, relocation, or a wrong match; inspect review images." +
                Environment.NewLine +
                "Combined timing includes pixel conversion, original detection and any local search." +
                Environment.NewLine +
                "Disk image loading, review generation and live screen capture are excluded." +
                Environment.NewLine +
                "First-frame JIT/allocation effects are included; this is not a steady-state benchmark." +
                Environment.NewLine +
                "User-confirmed first full activation and one relocation; exact boundaries still require review." +
                Environment.NewLine);

            Console.WriteLine("COMPLETE: " + output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex.ToString());
            return 1;
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