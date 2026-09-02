// Project & Model domain (Keyence-style), ported from the Go prototype's
// internal/project package. Folder layout on disk stays identical so an old
// project directory (if any) can be dropped in unchanged:
//
//   projects/<project_name>/project.json
//                            models/<model_name>/model.json
//                                                 dataset/images/{train,val}
//                                                 dataset/labels/{train,val}
//                                                 weights/best.onnx
//                            outputs/YYYY-MM-DD/NNN-HHMM.jpg (+ .json)
//                            outputs/daily_summary.csv
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutomaEye.Models;

public enum AIType
{
    Detection,
    Segmentation,
    Classification,
    OCR,
}

public static class AITypeExtensions
{
    public static string Label(this AIType t) => t switch
    {
        AIType.Detection => "AI Detection",
        AIType.Segmentation => "AI Segmentation",
        AIType.Classification => "AI Classification",
        AIType.OCR => "AI OCR",
        _ => t.ToString(),
    };
}

// Rule-based tools that layer on top of an AIType, matching the split real
// Keyence CV-X systems make between "AI tools" (Detection/Classification/
// Segmentation/OCR - already covered by AIType above) and classical
// geometric/decode tools that aren't themselves a form of AI inference.
// Deliberately NOT here: a "Scratches" toggle (a scratch is just a class
// name inside a plain Detection model - it needs no addon of its own) and
// a "Character Recognition" toggle (that's AIType.OCR itself - listing it
// twice would just be the same feature under two names).
public enum Addon
{
    PresenceCheck,
    GdtMeasurement,
    Positioning,
    ColorInspection,
    Count,
    Code1D,
    Code2D,
}

public class TrainingConfig
{
    public int Epochs { get; set; } = 100;
    public int Batch { get; set; } = 16;
    public int ImgSize { get; set; } = 640;
    public float LearnRate { get; set; } = 0.01f;
    public bool AugRotate { get; set; } = true;
    public bool AugBlur { get; set; }
    public bool AugExposure { get; set; } = true;
    public bool AugFlip { get; set; } = true;
    public bool AugNoise { get; set; }
}

public class DatasetStats
{
    public int Train { get; set; }
    public int Val { get; set; }
    public int Annotated { get; set; }
    public int Augmented { get; set; }
}

public class AugOptions
{
    public bool Rotate { get; set; }
    public bool Blur { get; set; }
    public bool Exposure { get; set; }
    public bool Flip { get; set; }
    public bool Noise { get; set; }
    public int Multiplier { get; set; } = 2;
}

public class Model
{
    public string Name { get; set; } = "";
    public AIType Type { get; set; } = AIType.Detection;
    public List<Addon> Addons { get; set; } = new();
    public List<string> Classes { get; set; } = new();
    public TrainingConfig Training { get; set; } = new();
    public bool Trained { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Confidence threshold picked by CalibrationService.Calibrate(), if run. Null = use the run's default confidence.</summary>
    public float? CalibratedConfidence { get; set; }

    /// <summary>
    /// GD&amp;T Measurement addon only. Pixels-per-millimetre from the manual
    /// two-point calibration (Models tab > Calibrate) - null until that's
    /// been done at least once. No physical measurement sensor is assumed;
    /// this is the "no sensor -> manual calibration" path.
    /// </summary>
    public double? PxPerMm { get; set; }

    /// <summary>Acceptable measured size range in mm, once calibrated. A detection outside this range is NG.</summary>
    public double? GdtToleranceMinMm { get; set; }
    public double? GdtToleranceMaxMm { get; set; }

    public float LastMAP { get; set; }
    public float LastPrecision { get; set; }
    public float LastRecall { get; set; }
    public float LastF1 { get; set; }
    public int DatasetCount { get; set; }
    public int AnnotatedCount { get; set; }

    // Absolute path, filled in at load time - not persisted.
    [JsonIgnore]
    public string Dir { get; set; } = "";
}

public enum Category
{
    Capture,
    Positioning,
    Inspection,
    Communication,
    Options,
}

public class WorkflowStep
{
    public int StepIndex { get; set; }
    public string ModelName { get; set; } = "";
    public Category Category { get; set; } = Category.Inspection;

    /// <summary>
    /// "always" | "on_ok" | "on_ng" - whether the chain continues past this step.
    /// </summary>
    public string ContinueOn { get; set; } = "always";
}

public class Workflow
{
    public List<WorkflowStep> Steps { get; set; } = new();

    /// <summary>"stop_and_report" | "continue"</summary>
    public string OnFirstNG { get; set; } = "stop_and_report";
}

/// <summary>
/// How each frame's verdict is sent out. "signal" sends a fixed serial byte
/// (see OutputSignalConfig); "script" runs the saved JS's onResult(result)
/// instead, so the user can wire an Arduino/PLC/HTTP endpoint/anything else
/// themselves without the app knowing the details.
/// </summary>
public class OutputConfig
{
    public string Mode { get; set; } = "signal";
    public string Script { get; set; } = "";
}

public class Project
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<Model> Models { get; set; } = new();
    public Workflow Workflow { get; set; } = new();
    public OutputConfig Output { get; set; } = new();

    [JsonIgnore]
    public string Dir { get; set; } = "";

    public Model? FindModel(string name) => Models.Find(m => m.Name == name);
}
