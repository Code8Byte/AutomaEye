using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutomaEye.Models;
using OpenCvSharp;

namespace AutomaEye.Services;

public class SelfLearningStatus
{
    public bool Enabled { get; set; }
    public int Pending { get; set; }
    public int RetrainEveryN { get; set; }
    public bool NeedsRetrain { get; set; }
}

/// <summary>
/// Ported from the reference's lib/selflearning.js: runs on every inspection
/// (not user-triggered), not just when asked. "Uncertainty" is the single
/// detection confidence across every step closest to 0.5 - the most
/// ambiguous call the model made this run (least-confidence-near-boundary
/// active-learning sampling). A "hard sample" in [low, high] gets saved for
/// a human to review and eventually fold back into training - never
/// auto-applied as a label.
/// </summary>
public static class SelfLearningService
{
    public static void Collect(Project project, RunResult result, Mat frame)
    {
        var cfg = ConfigService.Current.SelfLearning;
        if (!cfg.Enabled) return;

        StepResult? closestStep = null;
        Detection? closestDet = null;
        double closestDist = double.MaxValue;

        foreach (var step in result.Steps)
        {
            foreach (var d in step.Detections)
            {
                var dist = Math.Abs(d.Confidence - 0.5);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestDet = d;
                    closestStep = step;
                }
            }
        }
        if (closestStep == null || closestDet == null) return;
        if (closestDet.Confidence < cfg.UncertaintyLow || closestDet.Confidence > cfg.UncertaintyHigh) return;

        var model = project.FindModel(closestStep.ModelName);
        if (model == null) return;

        var dir = Path.Combine(model.Dir, "self_learning", "hard_samples");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var imgPath = Path.Combine(dir, $"{stamp}.jpg");
        frame.SaveImage(imgPath);

        var meta = new
        {
            timestamp = DateTime.Now.ToString("o"),
            model = model.Name,
            uncertainClass = closestDet.ClassName,
            uncertainConfidence = closestDet.Confidence,
            allDetections = result.Steps.SelectMany(s => s.Detections.Select(d => new { step = s.ModelName, d.ClassName, d.Confidence })),
        };
        File.WriteAllText(Path.Combine(dir, $"{stamp}.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static SelfLearningStatus Status(Model model)
    {
        var cfg = ConfigService.Current.SelfLearning;
        var dir = Path.Combine(model.Dir, "self_learning", "hard_samples");
        var pending = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").Length : 0;
        return new SelfLearningStatus
        {
            Enabled = cfg.Enabled,
            Pending = pending,
            RetrainEveryN = cfg.RetrainEveryN,
            NeedsRetrain = cfg.Enabled && pending >= cfg.RetrainEveryN,
        };
    }

    /// <summary>Moves pending hard samples into a dated archive folder (after a human has reviewed/re-annotated/retrained on them), resetting the pending count without deleting the samples.</summary>
    public static void Archive(Model model)
    {
        var dir = Path.Combine(model.Dir, "self_learning", "hard_samples");
        if (!Directory.Exists(dir)) return;

        var archiveDir = Path.Combine(model.Dir, "self_learning", "archive", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(archiveDir);
        foreach (var file in Directory.GetFiles(dir))
        {
            var dest = Path.Combine(archiveDir, Path.GetFileName(file));
            File.Move(file, dest, overwrite: true);
        }
    }
}
