using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AutomaEye.Models;
using OpenCvSharp;

namespace AutomaEye.Services;

/// <summary>
/// Dataset ops per model: capture, import, augmentation. Runs entirely on
/// OpenCvSharp's prebuilt native binaries - no manual OpenCV build required,
/// unlike the Go prototype's GoCV dependency.
/// </summary>
public static class DatasetService
{
    private static readonly string[] Splits = { "train", "val", "test" };

    public static DatasetStats Stats(Model model)
    {
        return new DatasetStats
        {
            Train = CountFiles(Path.Combine(model.Dir, "dataset", "images", "train"), ".jpg", ".png"),
            Val = CountFiles(Path.Combine(model.Dir, "dataset", "images", "val"), ".jpg", ".png"),
            Test = CountFiles(Path.Combine(model.Dir, "dataset", "images", "test"), ".jpg", ".png"),
            Annotated = CountFiles(Path.Combine(model.Dir, "dataset", "labels", "train"), ".txt"),
            Augmented = CountFiles(Path.Combine(model.Dir, "dataset", "images", "train"), ".aug.jpg"),
        };
    }

    public static string SaveCapture(Model model, Mat frame)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss.fff");
        var path = Path.Combine(model.Dir, "dataset", "images", "train", $"capture_{ts}.jpg");
        if (!frame.SaveImage(path)) throw new IOException($"Failed to save capture to {path}");
        return path;
    }

    public static string ImportImage(Model model, string srcPath, string split)
    {
        split = Splits.Contains(split) ? split : "train";
        var dst = Path.Combine(model.Dir, "dataset", "images", split, Path.GetFileName(srcPath));
        File.Copy(srcPath, dst, overwrite: true);
        return dst;
    }

    public static string[] ListImages(Model model, string split)
    {
        split = Splits.Contains(split) ? split : "train";
        var dir = Path.Combine(model.Dir, "dataset", "images", split);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static void DeleteImage(Model model, string imgPath)
    {
        File.Delete(imgPath);
        var labelPath = Path.Combine(model.Dir, "dataset", "labels", "train", Path.GetFileNameWithoutExtension(imgPath) + ".txt");
        if (File.Exists(labelPath)) File.Delete(labelPath);
    }

    /// <summary>
    /// Generates augmented variants of every non-augmented train image.
    /// Rotate/flip carry the YOLO label boxes forward by transforming their
    /// coordinates the same way the pixels were transformed - previously
    /// these two ops silently dropped the label file, leaving augmented
    /// images unlabeled and useless for training.
    /// </summary>
    public static int Augment(Model model, AugOptions opts)
    {
        var multiplier = opts.Multiplier <= 0 ? 2 : opts.Multiplier;
        var images = ListImages(model, "train").Where(p => !p.Contains(".aug")).ToArray();
        var rng = new Random();
        int generated = 0;

        foreach (var path in images)
        {
            using var src = Cv2.ImRead(path, ImreadModes.Color);
            if (src.Empty()) continue;
            var boxes = ReadYoloLabels(model, path);

            for (int i = 0; i < multiplier; i++)
            {
                using var outMat = src.Clone();
                var outBoxes = boxes.Select(b => b).ToList();

                if (opts.Rotate)
                {
                    var angle = (rng.NextDouble() * 2 - 1) * opts.RotateDegrees;
                    var center = new Point2f(outMat.Cols / 2f, outMat.Rows / 2f);
                    using var rot = Cv2.GetRotationMatrix2D(center, angle, 1.0);
                    Cv2.WarpAffine(outMat, outMat, rot, outMat.Size());
                    outBoxes = outBoxes.Select(b => RotateBox(b, rot, outMat.Cols, outMat.Rows)).ToList();
                }
                if (opts.FlipHorizontal)
                {
                    Cv2.Flip(outMat, outMat, FlipMode.Y);
                    outBoxes = outBoxes.Select(b => b with { Cx = 1 - b.Cx }).ToList();
                }
                if (opts.FlipVertical)
                {
                    Cv2.Flip(outMat, outMat, FlipMode.X);
                    outBoxes = outBoxes.Select(b => b with { Cy = 1 - b.Cy }).ToList();
                }
                if (opts.Blur)
                {
                    var sigma = Math.Max(0.1, opts.BlurSigma);
                    var k = (int)(sigma * 3) | 1; // odd kernel size covering ~3 sigma
                    Cv2.GaussianBlur(outMat, outMat, new Size(k, k), sigma);
                }
                if (opts.Exposure)
                {
                    outMat.ConvertTo(outMat, -1, opts.ExposureAlpha, 0);
                }
                if (opts.Noise)
                {
                    using var noise = new Mat(outMat.Size(), outMat.Type());
                    Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(opts.NoiseSigma, opts.NoiseSigma, opts.NoiseSigma));
                    Cv2.Add(outMat, noise, outMat);
                }

                var outPath = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path) + $".aug{i}.jpg");
                outMat.SaveImage(outPath);
                WriteYoloLabels(model, outPath, outBoxes);
                generated++;
            }
        }
        return generated;
    }

    private record YoloBox(int ClassId, double Cx, double Cy, double W, double H);

    private static List<YoloBox> ReadYoloLabels(Model model, string imgPath)
    {
        var labelPath = Path.Combine(model.Dir, "dataset", "labels", "train", Path.GetFileNameWithoutExtension(imgPath) + ".txt");
        if (!File.Exists(labelPath)) return new List<YoloBox>();

        var boxes = new List<YoloBox>();
        foreach (var line in File.ReadAllLines(labelPath))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!int.TryParse(parts[0], out var classId)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy)) continue;
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) continue;
            if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) continue;
            boxes.Add(new YoloBox(classId, cx, cy, w, h));
        }
        return boxes;
    }

    private static void WriteYoloLabels(Model model, string imgPath, List<YoloBox> boxes)
    {
        var labelPath = Path.Combine(model.Dir, "dataset", "labels", "train", Path.GetFileNameWithoutExtension(imgPath) + ".txt");
        var lines = boxes.Select(b => string.Join(' ', new[]
        {
            b.ClassId.ToString(CultureInfo.InvariantCulture),
            b.Cx.ToString("F6", CultureInfo.InvariantCulture),
            b.Cy.ToString("F6", CultureInfo.InvariantCulture),
            b.W.ToString("F6", CultureInfo.InvariantCulture),
            b.H.ToString("F6", CultureInfo.InvariantCulture),
        }));
        File.WriteAllLines(labelPath, lines);
    }

    /// <summary>Rotates a normalized YOLO box by the same affine matrix used on the pixels, then re-derives an axis-aligned box from the 4 rotated corners (a rotated box is no longer axis-aligned, so this is necessarily a looser bound than the true rotated rectangle).</summary>
    private static YoloBox RotateBox(YoloBox b, Mat rot, int imgW, int imgH)
    {
        var x1 = (b.Cx - b.W / 2) * imgW;
        var y1 = (b.Cy - b.H / 2) * imgH;
        var x2 = (b.Cx + b.W / 2) * imgW;
        var y2 = (b.Cy + b.H / 2) * imgH;
        var corners = new[] { new Point2f((float)x1, (float)y1), new Point2f((float)x2, (float)y1), new Point2f((float)x2, (float)y2), new Point2f((float)x1, (float)y2) };

        double m00 = rot.At<double>(0, 0), m01 = rot.At<double>(0, 1), m02 = rot.At<double>(0, 2);
        double m10 = rot.At<double>(1, 0), m11 = rot.At<double>(1, 1), m12 = rot.At<double>(1, 2);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var c in corners)
        {
            var nx = m00 * c.X + m01 * c.Y + m02;
            var ny = m10 * c.X + m11 * c.Y + m12;
            minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
            minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
        }
        minX = Math.Clamp(minX, 0, imgW); maxX = Math.Clamp(maxX, 0, imgW);
        minY = Math.Clamp(minY, 0, imgH); maxY = Math.Clamp(maxY, 0, imgH);

        return new YoloBox(b.ClassId, (minX + maxX) / 2 / imgW, (minY + maxY) / 2 / imgH, (maxX - minX) / imgW, (maxY - minY) / imgH);
    }

    private static int CountFiles(string dir, params string[] exts)
    {
        if (!Directory.Exists(dir)) return 0;
        return Directory.GetFiles(dir).Count(f => exts.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }
}
