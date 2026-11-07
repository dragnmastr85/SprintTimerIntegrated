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

[assembly: System.Reflection.AssemblyVersion("0.1.3.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.3.0")]

internal static class OfflineDetectorDiagnostics
{
    private const string Version = "0.1.3";
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

    public static int Main(string[] args)
    {
        try
        {
            Console.Title = "Offline Detector Diagnostics v" + Version;
            Console.WriteLine("Offline Detector Diagnostics v" + Version);
            Console.WriteLine("Saved images only. Timer application unchanged.");

            if (args.Length != 5)
                throw new Exception(
                    "Usage: OfflineDetectorDiagnostics.exe inputs output screenWidth screenHeight threshold");

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

            double[] reference;
            using (Bitmap template = new Bitmap(Path.Combine(input, "icon.png")))
                reference = MakeSymbol(ReadPixels(template), template.Width,
                    0, 0, template.Width, template.Height);

            string[] images = Directory.GetFiles(input, "*.png");
            Array.Sort(images, StringComparer.Ordinal);
            int processed = 0;

            using (StreamWriter summary = new StreamWriter(
                Path.Combine(output, "summary.csv"), false, Encoding.UTF8))
            using (StreamWriter components = new StreamWriter(
                Path.Combine(output, "components.csv"), false, Encoding.UTF8))
            {
                Row(summary, "image", "width", "height", "raw_components",
                    "closed_components", "accepted_candidates", "best_score",
                    "logged_score", "score_delta", "matches_logged_rounding",
                    "threshold", "detected", "logged_candidate_count",
                    "candidate_count_matches");
                Row(components, "image", "mask", "component_id", "x", "y",
                    "width", "height", "aspect", "passes_geometry",
                    "rejection_reasons", "score", "meets_threshold");

                foreach (string path in images)
                {
                    if (String.Equals(Path.GetFileName(path), "icon.png",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    string stem = Path.GetFileNameWithoutExtension(path);
                    string sidecar = Path.ChangeExtension(path, ".txt");
                    if (!File.Exists(sidecar))
                        throw new Exception("Missing sidecar: " + sidecar);
                    string metadata = File.ReadAllText(sidecar);
                    double? logged = MetadataNumber(metadata, "Similarity");
                    double? savedThreshold = MetadataNumber(metadata, "Threshold");
                    if (!logged.HasValue || !savedThreshold.HasValue)
                        throw new Exception("Missing score/threshold metadata: " + stem);
                    if (Math.Abs(savedThreshold.Value - threshold) > 0.0000001)
                        throw new Exception("Threshold disagrees with sidecar: " + stem);

                    using (Bitmap frame = new Bitmap(path))
                    {
                        if (frame.Width != expectedWidth || frame.Height != expectedHeight)
                            throw new Exception(String.Format(Inv,
                                "{0}: dimensions {1}x{2}; expected {3}x{4}",
                                stem, frame.Width, frame.Height,
                                expectedWidth, expectedHeight));

                        byte[] pixels = ReadPixels(frame);
                        bool[] raw = new bool[frame.Width * frame.Height];
                        for (int i = 0; i < raw.Length; i++)
                        {
                            int p = i * 3;
                            raw[i] = IsYellow(pixels[p + 2], pixels[p + 1], pixels[p]);
                        }

                        bool[] closed = CloseMask(raw, frame.Width, frame.Height);
                        SaveMask(raw, frame.Width, frame.Height,
                            Path.Combine(output, stem + "-mask-raw.png"));
                        SaveMask(closed, frame.Width, frame.Height,
                            Path.Combine(output, stem + "-mask-closed.png"));

                        List<Rectangle> rawBoxes = Components(raw, frame.Width, frame.Height);
                        List<Rectangle> closedBoxes = Components(closed, frame.Width, frame.Height);
                        int accepted = 0;
                        double best = 0;

                        using (Bitmap annotated = frame.Clone(
                            new Rectangle(0, 0, frame.Width, frame.Height),
                            PixelFormat.Format24bppRgb))
                        using (Graphics graphics = Graphics.FromImage(annotated))
                        using (Font font = new Font("Consolas", 7))
                        {
                            for (int pass = 0; pass < 2; pass++)
                            {
                                List<Rectangle> boxes = pass == 0 ? rawBoxes : closedBoxes;
                                for (int index = 0; index < boxes.Count; index++)
                                {
                                    Rectangle box = boxes[index];
                                    string reason = Reject(box, minimum, maximum);
                                    bool passes = reason.Length == 0;
                                    string scoreText = "";
                                    string meetsText = "";

                                    if (pass == 1 && passes)
                                    {
                                        double score = SymbolSimilarity(reference,
                                            MakeSymbol(pixels, frame.Width,
                                                box.X, box.Y, box.Width, box.Height));
                                        accepted++;
                                        best = Math.Max(best, score);
                                        scoreText = F(score);
                                        meetsText = (score >= threshold).ToString();
                                    }

                                    Row(components, stem, pass == 0 ? "raw" : "closed",
                                        index + 1, box.X, box.Y, box.Width, box.Height,
                                        F((double)box.Width / box.Height), passes,
                                        reason, scoreText, meetsText);

                                    if (pass == 1 &&
                                        (passes || box.Width >= 8 || box.Height >= 8))
                                    {
                                        using (Pen pen = new Pen(
                                            passes ? Color.Lime : Color.Red, 1))
                                            graphics.DrawRectangle(pen, box.X, box.Y,
                                                Math.Max(0, box.Width - 1),
                                                Math.Max(0, box.Height - 1));
                                        graphics.DrawString((index + 1).ToString(Inv),
                                            font, Brushes.Cyan, box.X,
                                            Math.Max(0, box.Y - 10));
                                    }
                                }
                            }
                            annotated.Save(Path.Combine(output, stem + "-boxes.png"),
                                ImageFormat.Png);
                        }

                        int? loggedCandidates = null;
                        if (stem == "20261107-023630-545-raw-gap-start-ea659622")
                            loggedCandidates = 0;
                        if (stem == "20261107-023636-234-raw-gap-start-1ec3f5ad" ||
                            stem == "20261107-023640-611-raw-gap-start-d3a6ddfc")
                            loggedCandidates = 1;

                        Row(summary, stem, frame.Width, frame.Height,
                            rawBoxes.Count, closedBoxes.Count, accepted, F(best),
                            F(logged.Value), F(best - logged.Value),
                            Math.Abs(best - logged.Value) <= 0.00000051,
                            F(threshold), best >= threshold,
                            loggedCandidates.HasValue
                                ? loggedCandidates.Value.ToString(Inv) : "",
                            loggedCandidates.HasValue
                                ? (accepted == loggedCandidates.Value).ToString() : "");

                        Console.WriteLine("{0}: candidates={1}; score={2}; logged={3}",
                            stem, accepted, F(best), F(logged.Value));
                        processed++;
                    }
                }
            }

            if (processed != 6)
                throw new Exception("Expected six evidence images; processed " + processed);

            File.WriteAllText(Path.Combine(output, "README.txt"),
                "Offline Detector Diagnostics v" + Version + Environment.NewLine +
                "Screen: " + screenWidth + "x" + screenHeight +
                "; threshold: " + F(threshold) + Environment.NewLine +
                "Geometry limits: " + minimum + " through " + maximum +
                " pixels; aspect 0.70 through 1.40 inclusive." + Environment.NewLine +
                "Detector helper methods copied verbatim from reviewed Vision.cs." +
                Environment.NewLine +
                "Raw-mask geometry is diagnostic only. Live filtering uses closed components." +
                Environment.NewLine +
                "Green boxes: accepted geometry. Red: rejected. Cyan: closed-component CSV ID." +
                Environment.NewLine +
                "Tiny rejected boxes omitted from annotation, not from CSV." +
                Environment.NewLine +
                "Three known gap-start candidate counts come from the supplied log." +
                Environment.NewLine +
                "Candidate acceptance does not establish target identity." +
                Environment.NewLine +
                "Investigate score/count mismatches before drawing conclusions." +
                Environment.NewLine);

            Console.WriteLine("Analysis complete: " + output);
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