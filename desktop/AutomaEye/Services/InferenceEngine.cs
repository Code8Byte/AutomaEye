using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AutomaEye.Services;

public class Detection
{
    // Properties, not fields: System.Text.Json only serializes properties by
    // default, and OutputRecorder writes StepResult (which nests these) to
    // outputs/<date>/*.json - as fields, every saved detection silently came
    // out as an empty object, with no bbox/class/confidence recoverable
    // afterward despite the file existing specifically to record that.
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }
    public float Confidence { get; set; }
    public int ClassId { get; set; }
    public string ClassName { get; set; } = "";
}

public class InferenceResult
{
    public List<Detection> Detections = new();
    public string Verdict = "OK";
    public float MinConfidence = 1.0f;
}

public class ModelSettings
{
    public string OnnxPath = "";
    public List<string> Classes = new();
    public float Confidence = 0.35f;
    public float Iou = 0.45f;
    public int ImgSz = 640;
}

/// <summary>
/// YOLO ONNX inference. Preprocessing (letterbox, normalize, HWC-&gt;CHW) and
/// postprocessing (NMS, undo letterbox) run in C# against OpenCvSharp Mats;
/// Microsoft.ML.OnnxRuntime does the actual forward pass.
/// </summary>
public class YoloEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly ModelSettings _cfg;

    public YoloEngine(ModelSettings cfg)
    {
        _cfg = cfg;
        _session = new InferenceSession(cfg.OnnxPath);
    }

    public InferenceResult Infer(Mat frame)
    {
        int origW = frame.Cols, origH = frame.Rows;
        var (letterboxed, scale, padX, padY) = Letterbox(frame, _cfg.ImgSz);
        using (letterboxed)
        {
            var input = PreprocessToChw(letterboxed, _cfg.ImgSz);
            var inputName = _session.InputMetadata.Keys.First();
            using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) });
            var output = results.First().AsTensor<float>();

            var detections = Postprocess(output, scale, padX, padY, origW, origH);

            var result = new InferenceResult { Detections = detections };
            var ngDetections = detections.Where(d => d.ClassName != "OK").ToList();
            if (ngDetections.Count > 0)
            {
                result.Verdict = "NG";
                result.MinConfidence = ngDetections.Min(d => d.Confidence);
            }
            return result;
        }
    }

    private static (Mat, float scale, int padX, int padY) Letterbox(Mat src, int targetSize)
    {
        int origW = src.Cols, origH = src.Rows;
        float scale = (float)targetSize / Math.Max(origW, origH);
        int newW = (int)(origW * scale), newH = (int)(origH * scale);

        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);

        int padX = (targetSize - newW) / 2, padY = (targetSize - newH) / 2;
        var padded = new Mat(new Size(targetSize, targetSize), MatType.CV_8UC3, Scalar.All(114));
        using var roi = new Mat(padded, new Rect(padX, padY, newW, newH));
        resized.CopyTo(roi);
        return (padded, scale, padX, padY);
    }

    private static DenseTensor<float> PreprocessToChw(Mat letterboxed, int size)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(letterboxed, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>(new[] { 1, 3, size, size });
        var rows = rgb.AsRows<Vec3b>(); // fast row-major access, no per-pixel marshalling
        for (int y = 0; y < size; y++)
        {
            var row = rows[y];
            for (int x = 0; x < size; x++)
            {
                var px = row[x];
                tensor[0, 0, y, x] = px.Item0 / 255f; // R
                tensor[0, 1, y, x] = px.Item1 / 255f; // G
                tensor[0, 2, y, x] = px.Item2 / 255f; // B
            }
        }
        return tensor;
    }

    private List<Detection> Postprocess(Tensor<float> output, float scale, int padX, int padY, int origW, int origH)
    {
        // YOLOv8/v11 export shape: [1, 4+numClasses, numAnchors]
        int numOut = output.Dimensions[1];
        int numAnchors = output.Dimensions[2];
        int numClasses = numOut - 4;

        var candidates = new List<Detection>();
        for (int i = 0; i < numAnchors; i++)
        {
            float bestScore = 0;
            int bestCls = -1;
            for (int c = 0; c < numClasses; c++)
            {
                var s = output[0, 4 + c, i];
                if (s > bestScore) { bestScore = s; bestCls = c; }
            }
            if (bestScore < _cfg.Confidence) continue;

            float cx = output[0, 0, i], cy = output[0, 1, i], w = output[0, 2, i], h = output[0, 3, i];
            float x1 = (cx - w / 2 - padX) / scale;
            float y1 = (cy - h / 2 - padY) / scale;
            float x2 = (cx + w / 2 - padX) / scale;
            float y2 = (cy + h / 2 - padY) / scale;

            candidates.Add(new Detection
            {
                X1 = Clamp(x1, 0, origW - 1),
                Y1 = Clamp(y1, 0, origH - 1),
                X2 = Clamp(x2, 0, origW - 1),
                Y2 = Clamp(y2, 0, origH - 1),
                Confidence = bestScore,
                ClassId = bestCls,
                ClassName = bestCls >= 0 && bestCls < _cfg.Classes.Count ? _cfg.Classes[bestCls] : "",
            });
        }
        return Nms(candidates, _cfg.Iou);
    }

    private static List<Detection> Nms(List<Detection> dets, float iouThres)
    {
        var sorted = dets.OrderByDescending(d => d.Confidence).ToList();
        var suppressed = new bool[sorted.Count];
        var kept = new List<Detection>();
        for (int i = 0; i < sorted.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(sorted[i]);
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (suppressed[j] || sorted[i].ClassId != sorted[j].ClassId) continue;
                if (Iou(sorted[i], sorted[j]) > iouThres) suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float Iou(Detection a, Detection b)
    {
        float interX1 = Math.Max(a.X1, b.X1), interY1 = Math.Max(a.Y1, b.Y1);
        float interX2 = Math.Min(a.X2, b.X2), interY2 = Math.Min(a.Y2, b.Y2);
        float interW = Math.Max(0, interX2 - interX1), interH = Math.Max(0, interY2 - interY1);
        float interArea = interW * interH;
        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);
        float union = areaA + areaB - interArea;
        return union <= 0 ? 0 : interArea / union;
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

    public void Dispose() => _session.Dispose();
}
