using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

public sealed partial class InputTest
{
    private readonly object diagnosticFrameLock = new object();
    private readonly CheckBox diagnosticCaptureCheck = new CheckBox();
    private readonly Label diagnosticCaptureStatus = new Label();

    private volatile bool diagnosticCaptureEnabled;
    private Bitmap diagnosticFrame;
    private double diagnosticFrameScore;
    private int diagnosticWrites;
    private string diagnosticWriteError;

    private void InitializeCaptureDiagnostics()
    {
        ClientSize = new Size(ClientSize.Width, 865);

        diagnosticCaptureCheck.Text = "Save dropout diagnostic images";
        diagnosticCaptureCheck.SetBounds(12, 790, 280, 25);
        diagnosticCaptureCheck.CheckedChanged += delegate
        {
            diagnosticCaptureEnabled = diagnosticCaptureCheck.Checked;

            if (!diagnosticCaptureEnabled)
            {
                lock (diagnosticFrameLock)
                {
                    if (diagnosticFrame != null)
                    {
                        diagnosticFrame.Dispose();
                        diagnosticFrame = null;
                    }
                }
            }
        };
        Controls.Add(diagnosticCaptureCheck);

        Button open = new Button();
        open.Text = "Open captures";
        open.SetBounds(335, 788, 145, 28);
        open.Click += delegate
        {
            try
            {
                string directory = DiagnosticDirectory();
                Directory.CreateDirectory(directory);
                System.Diagnostics.Process.Start("explorer.exe",
                    "\"" + directory + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot open captures");
            }
        };
        Controls.Add(open);

        diagnosticCaptureStatus.SetBounds(12, 825, 675, 30);
        diagnosticCaptureStatus.Text =
            "Diagnostic capture off. No images saved.";
        Controls.Add(diagnosticCaptureStatus);

        timer.Tick += delegate
        {
            string error;
            lock (diagnosticFrameLock)
                error = diagnosticWriteError;

            diagnosticCaptureStatus.Text = error != null
                ? "Capture save error: " + error
                : diagnosticCaptureEnabled
                    ? "Capture on. Files save to capture-diagnostics beside the EXE."
                    : "Diagnostic capture off. No new images saved.";
        };

        FormClosed += delegate
        {
            diagnosticCaptureEnabled = false;
            lock (diagnosticFrameLock)
            {
                if (diagnosticFrame != null)
                {
                    diagnosticFrame.Dispose();
                    diagnosticFrame = null;
                }
            }
        };
    }

    private static string DiagnosticDirectory()
    {
        return Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "capture-diagnostics"
        );
    }

    // Called by the existing capture worker after scoring the actual frame.
    private void RememberDiagnosticFrame(Bitmap frame, double score)
    {
        if (!diagnosticCaptureEnabled)
            return;

        Bitmap copy = (Bitmap)frame.Clone();

        lock (diagnosticFrameLock)
        {
            if (!diagnosticCaptureEnabled)
            {
                copy.Dispose();
                return;
            }

            if (diagnosticFrame != null)
                diagnosticFrame.Dispose();

            diagnosticFrame = copy;
            diagnosticFrameScore = score;
        }
    }

    // Called while consuming that capture result, before another capture starts.
    private void SaveDiagnosticFrame(string reason, long sampleAt)
    {
        if (!diagnosticCaptureEnabled)
            return;

        Bitmap image;
        double score;

        lock (diagnosticFrameLock)
        {
            if (diagnosticFrame == null)
                return;

            image = (Bitmap)diagnosticFrame.Clone();
            score = diagnosticFrameScore;
        }

        // Bound pending disk work rather than accumulating image copies.
        if (Interlocked.Increment(ref diagnosticWrites) > 8)
        {
            Interlocked.Decrement(ref diagnosticWrites);
            image.Dispose();
            LogVision("Diagnostic image skipped: save queue full.");
            return;
        }

        DateTime requestedAt = DateTime.Now;
        string stem =
            requestedAt.ToString("yyyyMMdd-HHmmss-fff") + "-" +
            reason + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        string metadata = String.Format(
            CultureInfo.InvariantCulture,
            "Reason: {0}\r\n" +
            "Save requested: {1:O}\r\n" +
            "Sample time on vision clock: {2} ms\r\n" +
            "Similarity: {3:0.000000}\r\n" +
            "Threshold: {4:0.000}\r\n" +
            "Capture generation: {5}\r\n" +
            "Image: actual captured search band, without annotations.\r\n",
            reason, requestedAt, sampleAt, score,
            matchThreshold.Value, captureGeneration
        );

        bool queued = false;
        try
        {
            queued = ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                try
                {
                    string directory = DiagnosticDirectory();
                    Directory.CreateDirectory(directory);

                    image.Save(
                        Path.Combine(directory, stem + ".png"),
                        ImageFormat.Png
                    );
                    File.WriteAllText(
                        Path.Combine(directory, stem + ".txt"), metadata
                    );
                }
                catch (Exception ex)
                {
                    lock (diagnosticFrameLock)
                        diagnosticWriteError = ex.Message;
                }
                finally
                {
                    image.Dispose();
                    Interlocked.Decrement(ref diagnosticWrites);
                }
            });
        }
        catch (Exception ex)
        {
            lock (diagnosticFrameLock)
                diagnosticWriteError = ex.Message;
        }

        if (!queued)
        {
            image.Dispose();
            Interlocked.Decrement(ref diagnosticWrites);
            LogVision("Diagnostic save could not be queued.");
        }
        else
        {
            LogVision("Diagnostic save queued: " + stem);
        }
    }
}