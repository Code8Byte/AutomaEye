using System;
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
    public static DatasetStats Stats(Model model)
    {
        return new DatasetStats
        {
            Train = CountFiles(Path.Combine(model.Dir, "dataset", "images", "train"), ".jpg", ".png"),
            Val = CountFiles(Path.Combine(model.Dir, "dataset", "images", "val"), ".jpg", ".png"),
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
        split = split is "train" or "val" ? split : "train";
        var dst = Path.Combine(model.Dir, "dataset", "images", split, Path.GetFileName(srcPath));
        File.Copy(srcPath, dst, overwrite: true);
        return dst;
    }

    public static string[] ListImages(Model model, string split)
    {
        split = split is "train" or "val" ? split : "train";
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

    /// <summary>Generates augmented variants of every non-augmented train image.</summary>
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

            for (int i = 0; i < multiplier; i++)
            {
                using var outMat = src.Clone();
                var geometricChange = false;

                if (opts.Rotate)
                {
                    geometricChange = true;
                    var angle = (rng.NextDouble() - 0.5) * 30; // +-15 deg
                    var center = new Point2f(outMat.Cols / 2f, outMat.Rows / 2f);
                    using var rot = Cv2.GetRotationMatrix2D(center, angle, 1.0);
                    Cv2.WarpAffine(outMat, outMat, rot, outMat.Size());
                }
                if (opts.Flip)
                {
                    geometricChange = true;
                    Cv2.Flip(outMat, outMat, FlipMode.Y);
                }
                if (opts.Blur)
                {
                    var k = 3 + 2 * rng.Next(2); // 3 or 5
                    Cv2.GaussianBlur(outMat, outMat, new Size(k, k), 0);
                }
                if (opts.Exposure)
                {
                    var alpha = 0.8 + rng.NextDouble() * 0.5; // 0.8-1.3
                    outMat.ConvertTo(outMat, -1, alpha, 0);
                }
                if (opts.Noise)
                {
                    using var noise = new Mat(outMat.Size(), outMat.Type());
                    Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(8, 8, 8));
                    Cv2.Add(outMat, noise, outMat);
                }

                var outPath = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetFileNameWithoutExtension(path) + $".aug{i}.jpg");
                outMat.SaveImage(outPath);

                if (!geometricChange) CopyLabel(model, path, outPath);
                generated++;
            }
        }
        return generated;
    }

    private static void CopyLabel(Model model, string srcImg, string dstImg)
    {
        var srcLabel = Path.Combine(model.Dir, "dataset", "labels", "train", Path.GetFileNameWithoutExtension(srcImg) + ".txt");
        if (!File.Exists(srcLabel)) return;
        var dstLabel = Path.Combine(model.Dir, "dataset", "labels", "train", Path.GetFileNameWithoutExtension(dstImg) + ".txt");
        File.Copy(srcLabel, dstLabel, overwrite: true);
    }

    private static int CountFiles(string dir, params string[] exts)
    {
        if (!Directory.Exists(dir)) return 0;
        return Directory.GetFiles(dir).Count(f => exts.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }
}
