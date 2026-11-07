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

[assembly: System.Reflection.AssemblyVersion("0.1.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.4.0")]

internal static class OfflineCropExperiment
{
    private const string Version = "0.1.4";
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

    private static double ScoreCrop(
        byte[] pixels, int strideWidth, Rectangle box, double[] reference)
    {
        return SymbolSimilarity(reference, MakeSymbol(
            pixels, strideWidth, box.X, box.Y, box.Width, box.Height));
    }

    private static void KeepBest(List<Hit> hits, Hit hit, int limit)
    {
        hits.Add(hit);
        hits.Sort(delegate(Hit a, Hit b) {
            return b.Score.CompareTo(a.Score);
        });
        if (hits.Count > limit) hits.RemoveAt(hits.Count - 1);
    }

    private static List<Hit> Search(
        byte[] pixels, int width, int height, double[] reference,
        List<int> sizes, out int evaluations)
    {
        evaluations = 0;
        List<Hit> seeds = new List<Hit>();

        foreach (int size in sizes)
        {
            List<Hit> perSize = new List<Hit>();
            int maxX = width - size;
            int maxY = height - size;
            Console.WriteLine("  Coarse search: {0}x{0}", size);

            for (int y = 0; ; y = Math.Min(y + 4, maxY))
            {
                for (int x = 0; ; x = Math.Min(x + 4, maxX))
                {
                    Rectangle box = new Rectangle(x, y, size, size);
                    KeepBest(perSize, new Hit {
                        Box = box,
                        Score = ScoreCrop(pixels, width, box, reference)
                    }, 4);
                    evaluations++;
                    if (x == maxX) break;
                }
                if (y == maxY) break;
            }
            seeds.AddRange(perSize);
        }

        List<Hit> best = new List<Hit>();
        HashSet<string> visited = new HashSet<string>();

        foreach (Hit seed in seeds)
        {
            for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                Rectangle box = new Rectangle(
                    seed.Box.X + dx, seed.Box.Y + dy,
                    seed.Box.Width, seed.Box.Height);
                if (box.X < 0 || box.Y < 0 ||
                    box.Right > width || box.Bottom > height) continue;

                string key = box.X + ":" + box.Y + ":" + box.Width;
                if (!visited.Add(key)) continue;

                KeepBest(best, new Hit {
                    Box = box,
                    Score = ScoreCrop(pixels, width, box, reference)
                }, 20);
                evaluations++;
            }
        }

        return best;
    }

    public static int Main(string[] args)
    {
        try
        {
            Console.Title = "Offline Crop Experiment v" + Version;
            Console.WriteLine("Offline Crop Experiment v" + Version);
            Console.WriteLine("Saved images only. Timer application unchanged.");

            if (args.Length == 2 && args[0] == "--report")
            {
                Console.WriteLine(File.ReadAllText(
                    Path.Combine(args[1], "experiment-summary.csv")));
                return 0;
            }

            if (args.Length != 5)
                throw new Exception(
                    "Usage: OfflineCropExperiment.exe inputs output screenWidth screenHeight threshold");

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            int screenWidth = Int32.Parse(args[2], Inv);
            int screenHeight = Int32.Parse(args[3], Inv);
            double threshold = Double.Parse(args[4], Inv);

            if (Directory.Exists(output))
                throw new Exception("Output folder exists; refusing to overwrite.");
            Directory.CreateDirectory(output);

            Dictionary<string, object> cfg =
                new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(input, "detector.json")));

            int expectedWidth =
                (int)Math.Round(screenWidth * Convert.ToDouble(cfg["band_right"], Inv)) -
                (int)Math.Round(screenWidth * Convert.ToDouble(cfg["band_left"], Inv));
            int expectedHeight =
                (int)Math.Round(screenHeight * Convert.ToDouble(cfg["band_bottom"], Inv)) -
                (int)Math.Round(screenHeight * Convert.ToDouble(cfg["band_top"], Inv));

            double expected = screenHeight *
                Convert.ToDouble(cfg["icon_height_fraction"], Inv);
            int minimum = Math.Max(8, (int)(expected * 0.65));
            int maximum = (int)(expected * 1.5);

            List<int> sizes = new List<int>();
            for (int step = -3; step <= 3; step++)
            {
                int size = (int)Math.Round(expected * (1.0 + step * 0.05));
                if (size > 0 && size <= expectedHeight &&
                    size <= expectedWidth && !sizes.Contains(size))
                    sizes.Add(size);
            }
            if (sizes.Count == 0)
                throw new Exception("No valid crop sizes.");

            double[] reference;
            using (Bitmap template = new Bitmap(Path.Combine(input, "icon.png")))
                reference = MakeSymbol(ReadPixels(template), template.Width,
                    0, 0, template.Width, template.Height);

            string[] stems = {
                "20261107-023630-545-raw-gap-start-ea659622",
                "20261107-023630-859-confirmed-disappearance-b452095b",
                "20261107-023636-234-raw-gap-start-1ec3f5ad",
                "20261107-023636-545-confirmed-disappearance-7521332a",
                "20261107-023640-611-raw-gap-start-d3a6ddfc",
                "20261107-023640-921-confirmed-disappearance-648cc3c3"
            };

            using (StreamWriter summary = new StreamWriter(
                Path.Combine(output, "experiment-summary.csv"), false, Encoding.UTF8))
            using (StreamWriter ranked = new StreamWriter(
                Path.Combine(output, "top-crops.csv"), false, Encoding.UTF8))
            {
                Row(summary, "image", "reported_target_present",
                    "baseline_candidates", "baseline_score", "logged_score",
                    "baseline_score_matches", "logged_candidates",
                    "baseline_candidates_match",
                    "search_score", "search_detected", "best_x", "best_y",
                    "best_width", "best_height", "evaluations",
                    "agrees_with_reported_presence");
                Row(ranked, "image", "rank", "x", "y", "width", "height",
                    "score", "meets_threshold");

                for (int imageIndex = 0; imageIndex < stems.Length; imageIndex++)
                {
                    string stem = stems[imageIndex];
                    bool targetReportedPresent = imageIndex < 4;
                    string metadata = File.ReadAllText(
                        Path.Combine(input, stem + ".txt"));
                    double? logged = MetadataNumber(metadata, "Similarity");
                    double? savedThreshold = MetadataNumber(metadata, "Threshold");

                    if (!logged.HasValue || !savedThreshold.HasValue ||
                        Math.Abs(savedThreshold.Value - threshold) > 0.0000001)
                        throw new Exception("Invalid score/threshold metadata: " + stem);

                    Console.WriteLine();
                    Console.WriteLine("Image {0}/6: {1}", imageIndex + 1, stem);

                    using (Bitmap frame = new Bitmap(Path.Combine(input, stem + ".png")))
                    {
                        if (frame.Width != expectedWidth ||
                            frame.Height != expectedHeight)
                            throw new Exception("Unexpected band dimensions: " + stem);

                        byte[] pixels = ReadPixels(frame);
                        bool[] yellow = new bool[frame.Width * frame.Height];
                        for (int i = 0; i < yellow.Length; i++)
                        {
                            int p = i * 3;
                            yellow[i] = IsYellow(
                                pixels[p + 2], pixels[p + 1], pixels[p]);
                        }

                        bool[] closed = CloseMask(yellow, frame.Width, frame.Height);
                        int candidates = 0;
                        double baseline = 0;
                        foreach (Rectangle box in Components(
                            closed, frame.Width, frame.Height))
                        {
                            if (Reject(box, minimum, maximum).Length != 0) continue;
                            candidates++;
                            baseline = Math.Max(baseline,
                                ScoreCrop(pixels, frame.Width, box, reference));
                        }

                        bool baselineMatches =
                            Math.Abs(baseline - logged.Value) <= 0.00000051;
                        int? loggedCandidates = null;
                        if (imageIndex == 0) loggedCandidates = 0;
                        if (imageIndex == 2 || imageIndex == 4) loggedCandidates = 1;

                        if (!baselineMatches ||
                            (loggedCandidates.HasValue &&
                             candidates != loggedCandidates.Value))
                            throw new Exception(
                                "Baseline reproduction mismatch; experiment stopped: " + stem);

                        int evaluations;
                        List<Hit> hits = Search(pixels, frame.Width, frame.Height,
                            reference, sizes, out evaluations);
                        if (hits.Count == 0)
                            throw new Exception("Search produced no results: " + stem);

                        Hit best = hits[0];
                        bool detected = best.Score >= threshold;

                        Row(summary, stem, targetReportedPresent, candidates,
                            F(baseline), F(logged.Value), baselineMatches,
                            loggedCandidates.HasValue
                                ? loggedCandidates.Value.ToString(Inv) : "",
                            loggedCandidates.HasValue
                                ? (candidates == loggedCandidates.Value).ToString() : "",
                            F(best.Score), detected,
                            best.Box.X, best.Box.Y, best.Box.Width, best.Box.Height,
                            evaluations, detected == targetReportedPresent);

                        for (int rank = 0; rank < hits.Count; rank++)
                        {
                            Hit hit = hits[rank];
                            Row(ranked, stem, rank + 1,
                                hit.Box.X, hit.Box.Y, hit.Box.Width, hit.Box.Height,
                                F(hit.Score), hit.Score >= threshold);
                        }

                        using (Bitmap crop = frame.Clone(
                            best.Box, PixelFormat.Format24bppRgb))
                            crop.Save(Path.Combine(output, stem + "-best-crop.png"),
                                ImageFormat.Png);

                        using (Bitmap annotated = frame.Clone(
                            new Rectangle(0, 0, frame.Width, frame.Height),
                            PixelFormat.Format24bppRgb))
                        using (Graphics g = Graphics.FromImage(annotated))
                        using (Pen pen = new Pen(detected ? Color.Lime : Color.Red, 1))
                        {
                            g.DrawRectangle(pen, best.Box.X, best.Box.Y,
                                best.Box.Width - 1, best.Box.Height - 1);
                            annotated.Save(Path.Combine(output, stem + "-best-box.png"),
                                ImageFormat.Png);
                        }

                        Console.WriteLine(
                            "Baseline={0}; search={1}; detected={2}; reported present={3}",
                            F(baseline), F(best.Score), detected, targetReportedPresent);
                    }
                }
            }

            List<string> sizeStrings = new List<string>();
            foreach (int size in sizes) sizeStrings.Add(size.ToString(Inv));
            File.WriteAllText(Path.Combine(output, "README.txt"),
                "Offline Crop Experiment v" + Version + Environment.NewLine +
                "Square crop sizes: " + String.Join(", ", sizeStrings.ToArray()) +
                Environment.NewLine +
                "Coarse stride: 4 pixels. Four strongest seeds per size." +
                Environment.NewLine +
                "Refinement: +/-3 pixels in X/Y around each seed; same crop size." +
                Environment.NewLine +
                "Existing normalization and correlation helpers unchanged." +
                Environment.NewLine +
                "Threshold: " + F(threshold) + Environment.NewLine +
                "First four frames reported target-present; final two target-absent." +
                Environment.NewLine +
                "Search is bounded, not exhaustive. Best-scoring crop may be a wrong object." +
                Environment.NewLine +
                "Twenty top crops can overlap; they are not twenty distinct detections." +
                Environment.NewLine +
                "Six-frame results do not establish general reliability or live performance." +
                Environment.NewLine);

            Console.WriteLine("Experiment complete: " + output);
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