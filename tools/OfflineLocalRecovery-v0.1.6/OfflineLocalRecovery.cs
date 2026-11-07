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

[assembly: System.Reflection.AssemblyVersion("0.1.6.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.6.0")]

internal static class OfflineLocalRecovery
{
    private const string Version = "0.1.6";
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

    public static int Main(string[] args)
    {
        try
        {
            Console.Title = "Offline Local Recovery v" + Version;
            Console.WriteLine("Offline Local Recovery v" + Version);
            Console.WriteLine("Supplied test seeds only. No automatic tracking. Timer unchanged.");

            if (args.Length == 2 && args[0] == "--report")
            {
                Console.WriteLine(File.ReadAllText(
                    Path.Combine(args[1], "local-summary.csv")));
                return 0;
            }
            if (args.Length != 5)
                throw new Exception(
                    "Usage: OfflineLocalRecovery.exe inputs output screenWidth screenHeight threshold");

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            int screenWidth = Int32.Parse(args[2], Inv);
            int screenHeight = Int32.Parse(args[3], Inv);
            double threshold = Double.Parse(args[4], Inv);
            if (Directory.Exists(output))
                throw new Exception("Output folder already exists.");
            Directory.CreateDirectory(output);

            Dictionary<string, object> cfg =
                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(input, "detector.json")));
            int bandWidth =
                (int)Math.Round(screenWidth * Convert.ToDouble(cfg["band_right"], Inv)) -
                (int)Math.Round(screenWidth * Convert.ToDouble(cfg["band_left"], Inv));
            int bandHeight =
                (int)Math.Round(screenHeight * Convert.ToDouble(cfg["band_bottom"], Inv)) -
                (int)Math.Round(screenHeight * Convert.ToDouble(cfg["band_top"], Inv));
            double expectedSize = screenHeight *
                Convert.ToDouble(cfg["icon_height_fraction"], Inv);
            int[] sizes = {
                (int)Math.Round(expectedSize * 0.95),
                (int)Math.Round(expectedSize),
                (int)Math.Round(expectedSize * 1.05)
            };
            int minimum = Math.Max(8, (int)(expectedSize * 0.65));
            int maximum = (int)(expectedSize * 1.5);

            double[] reference;
            using (Bitmap template = new Bitmap(Path.Combine(input, "icon.png")))
                reference = MakeSymbol(ReadPixels(template), template.Width,
                    0, 0, template.Width, template.Height);

            Seed[] seeds = {
                new Seed("known_test_seed", 364, 11, true),
                new Seed("x_minus_8", 356, 11, true),
                new Seed("x_plus_8", 372, 11, true),
                new Seed("y_minus_8", 364, 3, true),
                new Seed("y_plus_8", 364, 19, true),
                new Seed("x_minus_20_outside_range", 344, 11, false),
                new Seed("x_plus_20_outside_range", 384, 11, false)
            };
            string[] stems = {
                "20261107-023630-545-raw-gap-start-ea659622",
                "20261107-023630-859-confirmed-disappearance-b452095b",
                "20261107-023636-234-raw-gap-start-1ec3f5ad",
                "20261107-023636-545-confirmed-disappearance-7521332a",
                "20261107-023640-611-raw-gap-start-d3a6ddfc",
                "20261107-023640-921-confirmed-disappearance-648cc3c3"
            };

            using (StreamWriter summary = new StreamWriter(
                Path.Combine(output, "local-summary.csv"), false, Encoding.UTF8))
            using (StreamWriter timing = new StreamWriter(
                Path.Combine(output, "search-timings.csv"), false, Encoding.UTF8))
            using (StreamWriter baselineCsv = new StreamWriter(
                Path.Combine(output, "baseline-summary.csv"), false, Encoding.UTF8))
            {
                Row(summary, "image", "reported_target_present", "seed",
                    "seed_x", "seed_y", "nominal_nearby_seed",
                    "score", "detected", "best_x", "best_y", "best_size",
                    "evaluations", "mean_search_ms", "min_search_ms", "max_search_ms",
                    "measured_runs", "agrees_with_reported_presence");
                Row(timing, "image", "seed", "iteration", "warmup",
                    "search_ms", "score", "evaluations");
                Row(baselineCsv, "image", "candidates", "score",
                    "logged_score", "score_matches", "logged_candidates",
                    "candidate_count_matches");

                for (int imageIndex = 0; imageIndex < stems.Length; imageIndex++)
                {
                    string stem = stems[imageIndex];
                    bool reportedPresent = imageIndex < 4;
                    Console.WriteLine("Image {0}/6: {1}", imageIndex + 1, stem);
                    string metadata = File.ReadAllText(
                        Path.Combine(input, stem + ".txt"));
                    double? logged = MetadataNumber(metadata, "Similarity");
                    double? savedThreshold = MetadataNumber(metadata, "Threshold");
                    if (!logged.HasValue || !savedThreshold.HasValue ||
                        Math.Abs(savedThreshold.Value - threshold) > 0.0000001)
                        throw new Exception("Invalid sidecar: " + stem);

                    using (Bitmap frame = new Bitmap(Path.Combine(input, stem + ".png")))
                    {
                        if (frame.Width != bandWidth || frame.Height != bandHeight)
                            throw new Exception("Unexpected band dimensions: " + stem);
                        byte[] pixels = ReadPixels(frame);

                        bool[] yellow = new bool[frame.Width * frame.Height];
                        for (int i = 0; i < yellow.Length; i++)
                        {
                            int p = i * 3;
                            yellow[i] = IsYellow(pixels[p + 2], pixels[p + 1], pixels[p]);
                        }
                        bool[] closed = CloseMask(yellow, frame.Width, frame.Height);
                        int candidates = 0;
                        double baseline = 0;
                        foreach (Rectangle box in Components(closed, frame.Width, frame.Height))
                        {
                            if (Reject(box, minimum, maximum).Length != 0) continue;
                            candidates++;
                            baseline = Math.Max(baseline,
                                SymbolSimilarity(reference, MakeSymbol(
                                    pixels, frame.Width, box.X, box.Y,
                                    box.Width, box.Height)));
                        }
                        int? knownCount = imageIndex == 0 ? (int?)0 :
                            (imageIndex == 2 || imageIndex == 4 ? (int?)1 : null);
                        bool matches = Math.Abs(baseline - logged.Value) <= 0.00000051;
                        Row(baselineCsv, stem, candidates, F(baseline), F(logged.Value),
                            matches, knownCount.HasValue ? knownCount.Value.ToString(Inv) : "",
                            knownCount.HasValue ? (candidates == knownCount.Value).ToString() : "");
                        if (!matches || (knownCount.HasValue && candidates != knownCount.Value))
                            throw new Exception("Baseline reproduction mismatch: " + stem);

                        foreach (Seed seed in seeds)
                        {
                            double sum = 0, min = Double.MaxValue, max = 0;
                            Hit hit = null;
                            int evaluations = 0;

                            for (int iteration = 0; iteration < 4; iteration++)
                            {
                                System.Diagnostics.Stopwatch watch =
                                    System.Diagnostics.Stopwatch.StartNew();
                                hit = LocalSearch(pixels, frame.Width, frame.Height,
                                    reference, seed, sizes, out evaluations);
                                watch.Stop();
                                double ms = watch.Elapsed.TotalMilliseconds;
                                Row(timing, stem, seed.Name, iteration,
                                    iteration == 0, F(ms), F(hit.Score), evaluations);
                                if (iteration > 0)
                                {
                                    sum += ms;
                                    min = Math.Min(min, ms);
                                    max = Math.Max(max, ms);
                                }
                            }

                            bool detected = hit.Score >= threshold;
                            Row(summary, stem, reportedPresent, seed.Name,
                                seed.X, seed.Y, seed.WithinTestRange,
                                F(hit.Score), detected,
                                hit.Box.X, hit.Box.Y, hit.Box.Width,
                                evaluations, F(sum / 3), F(min), F(max), 3,
                                detected == reportedPresent);
                            summary.Flush();

                            Console.WriteLine("  {0}: score={1}; mean search={2} ms",
                                seed.Name, F(hit.Score), F(sum / 3));

                            using (Bitmap crop = frame.Clone(
                                hit.Box, PixelFormat.Format24bppRgb))
                                crop.Save(Path.Combine(output,
                                    stem + "-" + seed.Name + "-crop.png"), ImageFormat.Png);
                        }
                    }
                }
            }

            File.WriteAllText(Path.Combine(output, "README.txt"),
                "Offline Local Recovery v" + Version + Environment.NewLine +
                "Radius +/-8 pixels, coarse stride 2; +/-1 refinement of top three crops." +
                Environment.NewLine +
                "Sizes: " + sizes[0] + ", " + sizes[1] + ", " + sizes[2] +
                "; threshold: " + F(threshold) + Environment.NewLine +
                "Seed (364,11) supplied from prior experiment, not automatically acquired." +
                Environment.NewLine +
                "Offsets label seed displacement, not known tracking error in a real sequence." +
                Environment.NewLine +
                "The target in frame 36-234 sits lower; nominal nearby seeds need not cover its exact best box." +
                Environment.NewLine +
                "One warm-up and three measured searches per scenario; same process." +
                Environment.NewLine +
                "Timing includes search allocations and matching, excludes image I/O and baseline detection." +
                Environment.NewLine +
                "Search is bounded and heuristic; results do not validate live tracking, slot changes, or general false-positive behavior." +
                Environment.NewLine);

            Console.WriteLine("LOCAL EXPERIMENT COMPLETE: " + output);
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