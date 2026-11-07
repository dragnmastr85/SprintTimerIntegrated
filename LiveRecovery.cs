using System;
using System.Collections.Generic;
using System.Drawing;

public sealed partial class InputTest
{
    // Accessed only on the UI thread. Workers receive copied values.
    private bool recoveryHasTrack;
    private int recoveryX;
    private int recoveryY;
    private long recoveryNextWideAt;
    private int recoveryLastSource;
    private int recoveryNarrowCount;
    private int recoveryWideCount;
    private int recoveryWideAttempts;

    private void ClearRecoveryTracking()
    {
        recoveryHasTrack = false;
        recoveryX = 0;
        recoveryY = 0;
        recoveryNextWideAt = 0;
        recoveryLastSource = 0;
        recoveryNarrowCount = 0;
        recoveryWideCount = 0;
        recoveryWideAttempts = 0;
    }

    private static string RecoverySourceName(int source)
    {
        return source == 1 ? "original" :
            source == 2 ? "narrow" :
            source == 3 ? "wide" : "none";
    }

    private void AcceptRecoveryResult(double[] result)
    {
        recoveryHasTrack = result[4] != 0;
        recoveryX = (int)result[5];
        recoveryY = (int)result[6];
        recoveryNextWideAt = (long)result[7];
        int source = (int)result[8];

        if (source == 2) recoveryNarrowCount++;
        if (source == 3) recoveryWideCount++;
        if (result[9] != 0) recoveryWideAttempts++;

        if (source != recoveryLastSource || source == 3)
        {
            LogVision(
                "Match stage: " + RecoverySourceName(source) +
                "; score: " + result[0].ToString("0.000") +
                "; track: " + (recoveryHasTrack
                    ? recoveryX + "," + recoveryY : "none") +
                "; wider attempted: " + (result[9] != 0 ? "yes" : "no")
            );
        }
        recoveryLastSource = source;
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

    private static Hit WideSearch(
        byte[] pixels, int width, int height, double[] reference,
        Seed seed, int[] sizes, out int evaluations)
    {
        evaluations = 0;
        List<Hit> leaders = new List<Hit>();
        foreach (int size in sizes)
        {
            for (int dy = -4; dy <= 4; dy += 2)
            for (int dx = -40; dx <= 40; dx += 4)
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
            throw new Exception("No valid wider-search crops.");

        Hit best = leaders[0];
        HashSet<string> visited = new HashSet<string>();
        foreach (Hit leader in leaders)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                Rectangle box = new Rectangle(
                    leader.Box.X + dx, leader.Box.Y + dy,
                    leader.Box.Width, leader.Box.Height);
                if (box.X < 0 || box.Y < 0 ||
                    box.Right > width || box.Bottom > height ||
                    Math.Abs(box.X - seed.X) > 40 ||
                    Math.Abs(box.Y - seed.Y) > 4) continue;

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

}
