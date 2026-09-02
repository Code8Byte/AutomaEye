using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutomaEye.Models;

namespace AutomaEye.Services;

public class EpochMetrics
{
    public int Epoch { get; set; }
    public int Total { get; set; }
    [JsonPropertyName("precision")] public float Precision { get; set; }
    [JsonPropertyName("recall")] public float Recall { get; set; }
    [JsonPropertyName("mAP50")] public float MAP50 { get; set; }
    [JsonPropertyName("mAP5095")] public float MAP5095 { get; set; }
    [JsonPropertyName("f1")] public float F1 { get; set; }
    [JsonPropertyName("boxLoss")] public float BoxLoss { get; set; }
    [JsonPropertyName("clsLoss")] public float ClsLoss { get; set; }
    [JsonPropertyName("dflLoss")] public float DflLoss { get; set; }
}

public class TrainingResult
{
    public bool Success { get; set; }
    public float MAP50 { get; set; }
    public float MAP5095 { get; set; }
    public float Precision { get; set; }
    public float Recall { get; set; }
    public float F1 { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Drives python/train.py (an adapted copy of the reference Electron app's
/// Ultralytics training script) via Process.Start - C# orchestrates the
/// process and parses its stdout the same way the reference's Node backend
/// did, since there is no viable from-scratch C# reimplementation of YOLO
/// training. Runs one model's training at a time; the returned onnx lands
/// at model.Dir/weights/best.onnx, ready for WorkflowExecutor immediately.
/// </summary>
public class TrainingService
{
    private static readonly Regex EpochRegex = new(@"PROGRESS_EPOCH (\d+)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex MetricsRegex = new(@"EPOCH_METRICS (\{.*\})", RegexOptions.Compiled);
    private static readonly Regex ResultRegex = new(
        @"results mAP50: ([\d.]+) mAP50-95: ([\d.]+) P: ([\d.]+) R: ([\d.]+)", RegexOptions.Compiled);

    public string PythonExe { get; set; } = "python";

    public event Action<string>? Log;
    public event Action<EpochMetrics>? Progress;

    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<TrainingResult> TrainAsync(Project project, Model model, bool resume, CancellationToken ct)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "python", "train.py");
        var dataYaml = Path.Combine(model.Dir, "dataset", "data.yaml");

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(project.Name);
        psi.ArgumentList.Add("--project-dir"); psi.ArgumentList.Add(project.Dir);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model.Name);
        psi.ArgumentList.Add("--model-dir"); psi.ArgumentList.Add(model.Dir);
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(dataYaml);
        psi.ArgumentList.Add("--epochs"); psi.ArgumentList.Add(model.Training.Epochs.ToString());
        psi.ArgumentList.Add("--batch"); psi.ArgumentList.Add(model.Training.Batch.ToString());
        psi.ArgumentList.Add("--imgsz"); psi.ArgumentList.Add(model.Training.ImgSize.ToString());
        psi.ArgumentList.Add("--lr"); psi.ArgumentList.Add(model.Training.LearnRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--type"); psi.ArgumentList.Add(model.Type.Label());
        if (resume) psi.ArgumentList.Add("--resume");

        var result = new TrainingResult();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = process;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log?.Invoke(e.Data);

            var epochMatch = EpochRegex.Match(e.Data);
            if (epochMatch.Success)
            {
                Progress?.Invoke(new EpochMetrics { Epoch = int.Parse(epochMatch.Groups[1].Value), Total = int.Parse(epochMatch.Groups[2].Value) });
            }

            var metricsMatch = MetricsRegex.Match(e.Data);
            if (metricsMatch.Success)
            {
                try
                {
                    var m = JsonSerializer.Deserialize<EpochMetrics>(metricsMatch.Groups[1].Value);
                    if (m != null) Progress?.Invoke(m);
                }
                catch { /* malformed progress line - ignore, training itself is unaffected */ }
            }

            var finalMatch = ResultRegex.Match(e.Data);
            if (finalMatch.Success)
            {
                result.MAP50 = float.Parse(finalMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                result.MAP5095 = float.Parse(finalMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                result.Precision = float.Parse(finalMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                result.Recall = float.Parse(finalMatch.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                result.F1 = (result.Precision + result.Recall) > 0 ? 2 * result.Precision * result.Recall / (result.Precision + result.Recall) : 0;
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        await process.WaitForExitAsync(CancellationToken.None);
        _process = null;

        if (ct.IsCancellationRequested)
        {
            result.Success = false;
            result.Error = "Training cancelled.";
            return result;
        }

        result.Success = process.ExitCode == 0 && File.Exists(Path.Combine(model.Dir, WeightsSubpath));
        if (!result.Success) result.Error ??= $"train.py exited with code {process.ExitCode} - see the log above.";
        return result;
    }

    public void Cancel()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }

    private const string WeightsSubpath = "weights/best.onnx";
}
