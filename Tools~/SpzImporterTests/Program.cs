// Standalone validator for SpzReader.cs. Compile + run with:
//   csc -nologo -langversion:latest Program.cs ../../Editor/SpzReader.cs -out:run.exe
//   mono run.exe path/to/file.spz [path/to/another.spz ...]
//
// Reports decoded statistics and bounding-box info per file. Designed to
// run against the sample fixtures from
//   github.com/nianticlabs/spz/tree/main/samples
// (hornedlizard.spz and racoonfamily.spz).

using System;
using System.Diagnostics;
using System.IO;
using Gsplat.Editor;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: run.exe <file.spz> [<file.spz> ...]");
            return 64;
        }

        int failures = 0;
        foreach (string path in args)
        {
            Console.WriteLine();
            Console.WriteLine("=== " + path + " ===");
            if (!File.Exists(path))
            {
                Console.WriteLine("  ERROR: file not found");
                failures++;
                continue;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var cloud = SpzReader.Load(path);
                sw.Stop();

                ReportCloud(cloud, sw.ElapsedMilliseconds, new FileInfo(path).Length);
                if (!Validate(cloud)) failures++;
            }
            catch (Exception e)
            {
                Console.WriteLine("  ERROR: " + e.GetType().Name + ": " + e.Message);
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All files OK." : (failures + " file(s) FAILED."));
        return failures == 0 ? 0 : 1;
    }

    private static void ReportCloud(SpzGaussianCloud c, long ms, long compressedSize)
    {
        Console.WriteLine("  numPoints       : " + c.NumPoints.ToString("N0"));
        Console.WriteLine("  shDegree        : " + c.ShDegree + " (dim/channel = " + c.ShDimPerChannel + ")");
        Console.WriteLine("  antialiased     : " + c.Antialiased);
        Console.WriteLine("  decode time     : " + ms + " ms");
        Console.WriteLine("  compressed size : " + (compressedSize / 1024.0 / 1024.0).ToString("F1") + " MB");

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < c.NumPoints; i++)
        {
            float x = c.Positions[i * 3 + 0];
            float y = c.Positions[i * 3 + 1];
            float z = c.Positions[i * 3 + 2];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        Console.WriteLine("  pos bbox min    : (" + minX.ToString("F3") + ", " + minY.ToString("F3") + ", " + minZ.ToString("F3") + ")");
        Console.WriteLine("  pos bbox max    : (" + maxX.ToString("F3") + ", " + maxY.ToString("F3") + ", " + maxZ.ToString("F3") + ")");

        int badQuats = 0;
        for (int i = 0; i < c.NumPoints; i++)
        {
            float qx = c.Rotations[i * 4 + 0];
            float qy = c.Rotations[i * 4 + 1];
            float qz = c.Rotations[i * 4 + 2];
            float qw = c.Rotations[i * 4 + 3];
            float len = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (MathF.Abs(len - 1f) > 0.01f) badQuats++;
        }
        Console.WriteLine("  non-unit quats  : " + badQuats + " / " + c.NumPoints);

        int n = c.NumPoints;
        float aMin = float.PositiveInfinity, aMax = float.NegativeInfinity, aSum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (c.Alphas[i] < aMin) aMin = c.Alphas[i];
            if (c.Alphas[i] > aMax) aMax = c.Alphas[i];
            aSum += c.Alphas[i];
        }
        Console.WriteLine("  alpha (logit)   : min=" + aMin.ToString("F3") + " max=" + aMax.ToString("F3") + " mean=" + (aSum / n).ToString("F3"));

        float cMin = float.PositiveInfinity, cMax = float.NegativeInfinity;
        for (int i = 0; i < n * 3; i++)
        {
            if (c.Colors[i] < cMin) cMin = c.Colors[i];
            if (c.Colors[i] > cMax) cMax = c.Colors[i];
        }
        Console.WriteLine("  colors (sh-dc)  : min=" + cMin.ToString("F3") + " max=" + cMax.ToString("F3"));
    }

    private static bool Validate(SpzGaussianCloud c)
    {
        bool ok = true;

        if (c.NumPoints <= 0) { Console.WriteLine("  FAIL: numPoints <= 0"); ok = false; }
        if (c.Positions.Length != c.NumPoints * 3) { Console.WriteLine("  FAIL: positions length mismatch"); ok = false; }
        if (c.Scales.Length    != c.NumPoints * 3) { Console.WriteLine("  FAIL: scales length mismatch");    ok = false; }
        if (c.Rotations.Length != c.NumPoints * 4) { Console.WriteLine("  FAIL: rotations length mismatch"); ok = false; }
        if (c.Alphas.Length    != c.NumPoints)     { Console.WriteLine("  FAIL: alphas length mismatch");    ok = false; }
        if (c.Colors.Length    != c.NumPoints * 3) { Console.WriteLine("  FAIL: colors length mismatch");    ok = false; }
        if (c.ShDimPerChannel > 0 &&
            c.Sh.Length != c.NumPoints * c.ShDimPerChannel * 3)
        {
            Console.WriteLine("  FAIL: SH length mismatch");
            ok = false;
        }

        if (HasNonFinite(c.Positions, "positions")) ok = false;
        if (HasNonFinite(c.Scales,    "scales"))    ok = false;
        if (HasNonFinite(c.Rotations, "rotations")) ok = false;
        if (HasNonFinite(c.Alphas,    "alphas"))    ok = false;
        if (HasNonFinite(c.Colors,    "colors"))    ok = false;
        if (HasNonFinite(c.Sh,        "sh"))        ok = false;

        Console.WriteLine(ok ? "  OK" : "  FAIL");
        return ok;
    }

    private static bool HasNonFinite(float[] a, string name)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (float.IsNaN(a[i]) || float.IsInfinity(a[i]))
            {
                Console.WriteLine("  FAIL: " + name + "[" + i + "] is NaN/Inf");
                return true;
            }
        }
        return false;
    }
}
