// Project & Model domain, ported field-for-field from the reference Electron
// app (github.com/CodeVouz/AutomaEye, lib/projects.js + lib/workflow.js) so
// this C# rewrite matches its actual thesis-graded behavior instead of an
// invented redesign. Folder layout on disk:
//
//   projects/<project_name>/project.json   (models live INLINE in this file
//                                            - there is no per-model model.json)
//                            models/<model_name>/dataset/images/{train,val,test}
//                                                 dataset/labels/{train,val,test}
//                                                 dataset/data.yaml
//                                                 weights/best.onnx (+ best.pt if trained via Python)
//                                                 versions/v<N>/best.onnx
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

/// <summary>
/// Rule-based tools offered in the "New Model" wizard - the exact 10 tiles
/// the reference app's new_model.html ships, in its exact order. Only
/// PresenceCheck, Count and GdtMeasurement have real evaluation logic
/// there (and here) - the other 7 are inert wizard tiles the reference app
/// itself labels "belum aktif (placeholder)". They stay in the list because
/// removing them would just be a different, unreviewed redesign again -
/// <see cref="AddonExtensions.IsWired"/> is what the UI uses to show the
/// same "not wired yet" hint the reference app shows.
/// </summary>
public enum Addon
{
    PresenceCheck,
    Scratches,
    GdtMeasurement,
    Positioning,
    ColorInspection,
    Count,
    CharacterRecognition,
    Code1D,
    Code2D,
    Calibration,
}

public static class AddonExtensions
{
    public static string Label(this Addon a) => a switch
    {
        Addon.PresenceCheck => "Presence Check",
        Addon.Scratches => "Scratches",
        Addon.GdtMeasurement => "GD&T Measurement",
        Addon.Positioning => "Positioning",
        Addon.ColorInspection => "Color Inspection",
        Addon.Count => "Count",
        Addon.CharacterRecognition => "Character Recognition",
        Addon.Code1D => "1D Code",
        Addon.Code2D => "2D Code",
        Addon.Calibration => "Calibration",
        _ => a.ToString(),
    };

    /// <summary>True for the 3 addons WorkflowExecutor actually evaluates.</summary>
    public static bool IsWired(this Addon a) => a is Addon.PresenceCheck or Addon.Count or Addon.GdtMeasurement;
}

public enum GdtShape { Circle, Rect }

/// <summary>Per-class GD&T dimension config - nominal+tolerance in mm. Null nominal = measure-only, no pass/fail.</summary>
public class GdtClassConfig
{
    public GdtShape Shape { get; set; } = GdtShape.Circle;

    /// <summary>Expected feature count for this class - informational only, never gates the verdict (matches the reference, which explicitly dropped a stricter count-gate so it wouldn't hold up the Arduino signal).</summary>
    public int? Count { get; set; }

    public double? NominalDiameterMm { get; set; }
    public double? ToleranceDiameterMm { get; set; }
    public double? NominalLongMm { get; set; }
    public double? ToleranceLongMm { get; set; }
    public double? NominalShortMm { get; set; }
    public double? ToleranceShortMm { get; set; }
}

/// <summary>Parameters for the addons that are actually wired (see <see cref="AddonExtensions.IsWired"/>).</summary>
public class AddonConfig
{
    /// <summary>Count addon: exact number of detections required for OK. Null = informational only, always passes.</summary>
    public int? CountExpected { get; set; }

    /// <summary>GD&T Measurement: shared px-to-mm ratio from manual calibration.</summary>
    public double? MmPerPixel { get; set; }

    /// <summary>GD&T Measurement: per-class shape + nominal/tolerance, keyed by class name.</summary>
    public Dictionary<string, GdtClassConfig> GdtPerClass { get; set; } = new();

    /// <summary>true = measure from the axis-aligned bounding box (stable); false = prefer segmentation contour measurements when available.</summary>
    public bool MeasureFromBox { get; set; } = true;
}

public class TrainingConfig
{
    public int Epochs { get; set; } = 100;
    public int Batch { get; set; } = 16;
    public int ImgSize { get; set; } = 640;
    public float LearnRate { get; set; } = 0.01f;
}

public class DatasetStats
{
    public int Train { get; set; }
    public int Val { get; set; }
    public int Test { get; set; }
    public int Annotated { get; set; }
    public int Augmented { get; set; }
}

public class AugOptions
{
    public bool Rotate { get; set; }
    public double RotateDegrees { get; set; } = 15;
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public bool Blur { get; set; }
    public double BlurSigma { get; set; } = 2.0;
    public bool Exposure { get; set; }
    public double ExposureAlpha { get; set; } = 1.2;
    public bool Noise { get; set; }
    public double NoiseSigma { get; set; } = 8;
    public int Multiplier { get; set; } = 2;
}

/// <summary>One snapshot of best.onnx/best.pt after a successful training run - mirrors the reference's Roboflow-style version history.</summary>
public class ModelVersion
{
    public int Id { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public float MAP50 { get; set; }
    public float MAP5095 { get; set; }
    public float Precision { get; set; }
    public float Recall { get; set; }
    public float F1 { get; set; }
    public List<string> Classes { get; set; } = new();
}

public class Model
{
    public string Name { get; set; } = "";
    public AIType Type { get; set; } = AIType.Detection;
    public List<Addon> Addons { get; set; } = new();
    public AddonConfig AddonConfig { get; set; } = new();
    public List<string> Classes { get; set; } = new();
    public TrainingConfig Training { get; set; } = new();
    public bool Trained { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Confidence threshold picked by CalibrationService.Calibrate(), if run. Null = use the run's default confidence.</summary>
    public float? CalibratedConfidence { get; set; }

    public List<ModelVersion> Versions { get; set; } = new();
    public int? ActiveVersion { get; set; }

    public float LastMAP { get; set; }
    public float LastPrecision { get; set; }
    public float LastRecall { get; set; }
    public float LastF1 { get; set; }

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

    /// <summary>Pin a specific trained version instead of always using the model's current active version. Null = use active version.</summary>
    public int? Version { get; set; }

    /// <summary>Positioning/Inspection only: treat "nothing detected" as a pass-through OK instead of NG (matches the reference's passOnNoDetect checkbox).</summary>
    public bool PassOnNoDetect { get; set; }

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
