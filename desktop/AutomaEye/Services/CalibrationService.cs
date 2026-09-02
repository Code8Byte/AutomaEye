using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutomaEye.Models;
using OpenCvSharp;

namespace AutomaEye.Services;

public class CalibrationRow
{
    public float Confidence { get; set; }
    public int Tp, Fp, Fn, Tn;
    public double Precision, Recall, F1;
}

public class CalibrationResult
{
    public float BestConfidence { get; set; }
    public double BestF1 { get; set; }
    public List<CalibrationRow> Table { get; set; } = new();
    public int Evaluated { get; set; }
}

/// <summary>
/// Auto-calibration ("self-parametrization"): instead of the operator
/// guessing a confidence threshold, sweep a set of candidates against the
/// labeled validation split, score each by F1 against ground truth, and pick
/// the best. Ported from the reference Electron app's lib/calibration.js -
/// same candidate list, same image-level ground-truth rule (an image is
/// "defective" iff its YOLO label file has any line whose class isn't "OK").
/// </summary>
public static class CalibrationService
{
    private static readonly float[] Candidates = { 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f, 0.45f, 0.50f, 0.55f, 0.60f };

    public static CalibrationResult Calibrate(Model model, Action<int, int>? onProgress = null)
    {
        var onnxPath = Path.Combine(model.Dir, ProjectManager.WeightsDir, "best.onnx");
        if (!File.Exists(onnxPath))
            throw new InvalidOperationException("Model hasn't been trained (best.onnx missing) - train it first.");

        var valImages = ListValImages(model);
        if (valImages.Count == 0)
            throw new InvalidOperationException("No images in the val split - split the dataset first.");

        // Run inference once per image at a low confidence floor so every
        // candidate threshold's detections are already present; thresholding
        // itself happens in-memory below, not by re-running the model.
        using var engine = new YoloEngine(new ModelSettings
        {
            OnnxPath = onnxPath,
            Classes = model.Classes,
            Confidence = 0.05f,
            Iou = 0.45f,
            ImgSz = model.Training.ImgSize > 0 ? model.Training.ImgSize : 640,
        });

        var perImage = new List<(bool gtDefective, float topConf)>();
        for (int i = 0; i < valImages.Count; i++)
        {
            var (imgPath, lblPath) = valImages[i];
            try
            {
                using var frame = Cv2.ImRead(imgPath, ImreadModes.Color);
                if (!frame.Empty())
                {
                    var result = engine.Infer(frame);
                    var topConf = result.Detections.Where(d => d.ClassName != "OK").Select(d => d.Confidence).DefaultIfEmpty(0f).Max();
                    perImage.Add((IsDefectiveGroundTruth(lblPath, model.Classes), topConf));
                }
            }
            catch
            {
                // one bad image shouldn't fail the whole calibration run
            }
            onProgress?.Invoke(i + 1, valImages.Count);
        }

        if (perImage.Count == 0)
            throw new InvalidOperationException("No val images could be evaluated.");

        var table = Candidates.Select(conf =>
        {
            int tp = 0, fp = 0, fn = 0, tn = 0;
            foreach (var (gtDefective, topConf) in perImage)
            {
                bool predDefective = topConf >= conf;
                if (gtDefective && predDefective) tp++;
                else if (!gtDefective && predDefective) fp++;
                else if (gtDefective && !predDefective) fn++;
                else tn++;
            }
            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            return new CalibrationRow { Confidence = conf, Tp = tp, Fp = fp, Fn = fn, Tn = tn, Precision = precision, Recall = recall, F1 = f1 };
        }).ToList();

        // Best F1 wins; ties go to the higher threshold (fewer false alarms).
        var best = table[0];
        foreach (var row in table)
        {
            if (row.F1 > best.F1 + 1e-9 || (Math.Abs(row.F1 - best.F1) < 1e-9 && row.Confidence > best.Confidence))
                best = row;
        }

        return new CalibrationResult { BestConfidence = best.Confidence, BestF1 = best.F1, Table = table, Evaluated = perImage.Count };
    }

    private static List<(string img, string lbl)> ListValImages(Model model)
    {
        var imgDir = Path.Combine(model.Dir, "dataset", "images", "val");
        var lblDir = Path.Combine(model.Dir, "dataset", "labels", "val");
        if (!Directory.Exists(imgDir)) return new();

        return Directory.GetFiles(imgDir)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(f => (f, Path.Combine(lblDir, Path.GetFileNameWithoutExtension(f) + ".txt")))
            .ToList();
    }

    private static bool IsDefectiveGroundTruth(string labelPath, List<string> classes)
    {
        if (!File.Exists(labelPath)) return false;
        int okIndex = classes.IndexOf("OK");
        foreach (var line in File.ReadAllLines(labelPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out var classId)) continue;
            if (classId != okIndex) return true; // a non-OK class label is present
        }
        return false;
    }
}
