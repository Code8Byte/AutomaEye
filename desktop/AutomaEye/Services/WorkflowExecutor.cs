using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AutomaEye.Models;
using OpenCvSharp;

namespace AutomaEye.Services;

public class StepResult
{
    public int StepIndex { get; set; }
    public string ModelName { get; set; } = "";
    public string Verdict { get; set; } = "";
    public float Confidence { get; set; }
    public List<Detection> Detections { get; set; } = new();
    public double InferenceMs { get; set; }
    public bool Skipped { get; set; }
    public string? Note { get; set; }
}

public class RunResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string FinalVerdict { get; set; } = "OK";
    public List<StepResult> Steps { get; set; } = new();
    public double TotalMs { get; set; }
}

/// <summary>Runs a project's workflow chain against one frame. Engines are loaded lazily and cached.</summary>
public class WorkflowExecutor : IDisposable
{
    private readonly Project _project;
    private readonly ModelSettings _baseSettings;
    private readonly Dictionary<string, YoloEngine> _engines = new();

    public WorkflowExecutor(Project project, ModelSettings baseSettings)
    {
        _project = project;
        _baseSettings = baseSettings;
    }

    private YoloEngine LoadEngine(Model model)
    {
        if (_engines.TryGetValue(model.Name, out var cached)) return cached;

        var onnxPath = Path.Combine(model.Dir, ProjectManager.WeightsDir, "best.onnx");
        if (!File.Exists(onnxPath)) throw new FileNotFoundException($"best.onnx missing at {onnxPath} - run training + export first");

        var cfg = new ModelSettings
        {
            OnnxPath = onnxPath,
            Classes = model.Classes,
            // A calibrated threshold (Models tab > Calibrate) overrides the run's default.
            Confidence = model.CalibratedConfidence ?? _baseSettings.Confidence,
            Iou = _baseSettings.Iou,
            ImgSz = _baseSettings.ImgSz,
        };
        var engine = new YoloEngine(cfg);
        _engines[model.Name] = engine;
        return engine;
    }

    /// <summary>
    /// Matches the reference app's evaluateAddons() exactly: with no wired
    /// addon configured, the default rule is "at least one detection = OK".
    /// Each wired addon (PresenceCheck/Count/GdtMeasurement) that IS
    /// configured is one more required check, ANDed together - it does not
    /// replace the default rule, it adds to it. A GD&T note string is always
    /// attached when that addon is present, even if it has no nominal set
    /// yet (measure-only mode).
    /// </summary>
    private static (string verdict, float confidence, string? note) EvaluateAddons(Model model, List<Detection> detections)
    {
        var wired = model.Addons.Where(a => a.IsWired()).ToList();
        var confidence = detections.Count > 0 ? detections.Max(d => d.Confidence) : 0f;
        string? note = null;

        bool passed;
        if (wired.Count == 0)
        {
            passed = detections.Count >= 1;
        }
        else
        {
            passed = true;
            if (wired.Contains(Addon.PresenceCheck))
                passed &= detections.Count >= 1;

            if (wired.Contains(Addon.Count) && model.AddonConfig.CountExpected is { } expected)
                passed &= detections.Count == expected;

            if (wired.Contains(Addon.GdtMeasurement))
            {
                var (gdtOk, gdtNote) = EvaluateGdt(model, detections);
                passed &= gdtOk;
                note = gdtNote;
            }
        }

        return (passed ? "OK" : "NG", confidence, note);
    }

    private static (bool ok, string? note) EvaluateGdt(Model model, List<Detection> detections)
    {
        var mmPerPixel = model.AddonConfig.MmPerPixel;
        var perClass = model.AddonConfig.GdtPerClass;
        if (mmPerPixel is not { } mm || mm <= 0 || detections.Count == 0) return (true, null);

        bool ok = true;
        var notes = new List<string>();
        foreach (var d in detections)
        {
            if (!perClass.TryGetValue(d.ClassName, out var cfg)) continue;
            var wPx = d.X2 - d.X1;
            var hPx = d.Y2 - d.Y1;

            if (cfg.Shape == GdtShape.Circle)
            {
                var diaMm = (wPx + hPx) / 2.0 * mm;
                notes.Add($"{d.ClassName} Ø{diaMm:F2}mm");
                if (cfg.NominalDiameterMm is { } nominal)
                {
                    var tol = cfg.ToleranceDiameterMm ?? 0;
                    if (Math.Abs(diaMm - nominal) > tol) ok = false;
                }
            }
            else
            {
                var longMm = Math.Max(wPx, hPx) * mm;
                var shortMm = Math.Min(wPx, hPx) * mm;
                notes.Add($"{d.ClassName} {longMm:F2}x{shortMm:F2}mm");
                if (cfg.NominalLongMm is { } nomLong)
                {
                    var tol = cfg.ToleranceLongMm ?? 0;
                    if (Math.Abs(longMm - nomLong) > tol) ok = false;
                }
                if (cfg.NominalShortMm is { } nomShort)
                {
                    var tol = cfg.ToleranceShortMm ?? 0;
                    if (Math.Abs(shortMm - nomShort) > tol) ok = false;
                }
            }
        }
        return (ok, notes.Count > 0 ? "Measured: " + string.Join(", ", notes) : null);
    }

    public RunResult Run(Mat frame)
    {
        if (_project.Workflow.Steps.Count == 0)
            throw new InvalidOperationException("Workflow is empty - add a step first");

        var sw = Stopwatch.StartNew();
        var result = new RunResult();
        bool stopOnFirstNG = _project.Workflow.OnFirstNG == "stop_and_report";

        foreach (var step in _project.Workflow.Steps)
        {
            var sr = new StepResult { StepIndex = step.StepIndex, ModelName = step.ModelName };

            // A step whose model doesn't exist yet or hasn't been trained is
            // skipped, not failed - normal while a workflow is still being
            // built out. Only a genuine runtime failure counts as NG.
            var model = _project.FindModel(step.ModelName);
            if (model == null || !model.Trained)
            {
                sr.Verdict = "OK";
                sr.Skipped = true;
                sr.Note = model == null ? $"Model \"{step.ModelName}\" not in project" : $"Model \"{step.ModelName}\" is not trained yet";
                result.Steps.Add(sr);
                continue;
            }

            var stepSw = Stopwatch.StartNew();
            List<Detection> detections;
            try
            {
                var engine = LoadEngine(model);
                detections = engine.Infer(frame).Detections;
            }
            catch (Exception ex)
            {
                sr.Verdict = "ERROR";
                sr.Note = ex.Message;
                result.Steps.Add(sr);
                result.FinalVerdict = "NG";
                if (stopOnFirstNG) break;
                continue;
            }
            sr.InferenceMs = stepSw.Elapsed.TotalMilliseconds;
            sr.Detections = detections;

            if (detections.Count == 0 && step.PassOnNoDetect)
            {
                sr.Verdict = "OK";
                sr.Confidence = 1f;
            }
            else
            {
                (sr.Verdict, sr.Confidence, sr.Note) = EvaluateAddons(model, detections);
            }

            result.Steps.Add(sr);

            if (sr.Verdict == "NG")
            {
                result.FinalVerdict = "NG";
                if (stopOnFirstNG) break;
            }
            if (step.ContinueOn == "on_ok" && sr.Verdict != "OK") break;
            if (step.ContinueOn == "on_ng" && sr.Verdict != "NG") break;
        }

        result.TotalMs = sw.Elapsed.TotalMilliseconds;

        // Matches the reference: this runs on every inspection, not just
        // when a user asks for it - self-learning is a passive, continuous
        // hard-sample collector, gated only by the Settings toggle.
        SelfLearningService.Collect(_project, result, frame);

        return result;
    }

    public void Dispose()
    {
        foreach (var engine in _engines.Values) engine.Dispose();
        _engines.Clear();
    }
}
