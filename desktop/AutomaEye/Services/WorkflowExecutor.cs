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
    /// Verdict depends on what the model is for, not just whether it found
    /// something: a Presence Check model is OK when its target IS detected
    /// (NG when absent), while every other addon (Scratches, defect
    /// detectors, ...) is OK when nothing NG-classed was found. A class
    /// literally named "OK" is never itself treated as a defect hit.
    /// </summary>
    private static (string verdict, float confidence) ComputeVerdict(Model model, List<Detection> detections)
    {
        var hits = detections.Where(d => d.ClassName != "OK").ToList();
        bool isPresenceCheck = model.Addons.Contains(Addon.PresenceCheck);

        if (isPresenceCheck)
        {
            return hits.Count > 0 ? ("OK", hits.Max(d => d.Confidence)) : ("NG", 0f);
        }
        return hits.Count > 0 ? ("NG", hits.Min(d => d.Confidence)) : ("OK", 1.0f);
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
            (sr.Verdict, sr.Confidence) = ComputeVerdict(model, detections);

            // GD&T Measurement: report each detection's real-world size once
            // the model has been calibrated (Models tab > Calibrate). No
            // tolerance-based pass/fail yet - this surfaces the measurement
            // so the custom output script (or a future tolerance UI) can act on it.
            if (model.Addons.Contains(Addon.GdtMeasurement) && model.PxPerMm is { } pxPerMm and > 0 && detections.Count > 0)
            {
                var sizes = detections.Select(d => $"{(d.X2 - d.X1) / pxPerMm:F1}x{(d.Y2 - d.Y1) / pxPerMm:F1}mm");
                sr.Note = "Measured: " + string.Join(", ", sizes);
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
        return result;
    }

    public void Dispose()
    {
        foreach (var engine in _engines.Values) engine.Dispose();
        _engines.Clear();
    }
}
