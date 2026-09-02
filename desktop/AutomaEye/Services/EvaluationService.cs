using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutomaEye.Models;

namespace AutomaEye.Services;

public class EvalStatBlock
{
    public int N { get; set; }
    public double Mean { get; set; }
    public double Median { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Std { get; set; }
}

public class EvalDeviceInfo
{
    public string Device { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public double? RamGb { get; set; }
    public string Torch { get; set; } = "";
}

public class EvalTiming
{
    public EvalDeviceInfo Device { get; set; } = new();
    public int Imgsz { get; set; }
    public double Conf { get; set; }
    public double Iou { get; set; }
    public int NImages { get; set; }
    public int NMeasured { get; set; }
    public double ColdStartMs { get; set; }
    public System.Collections.Generic.Dictionary<string, EvalStatBlock> PerStage { get; set; } = new();
    public double ThroughputPerMinute { get; set; }
    public double Fps { get; set; }
}

public class EvalOverall
{
    public double Map50 { get; set; }
    public double Map5095 { get; set; }
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1 { get; set; }
}

public class EvalPerClass
{
    public string Name { get; set; } = "";
    public double Precision { get; set; }
    public double Recall { get; set; }
    public double Map50 { get; set; }
    public double Map5095 { get; set; }
}

public class EvalResult
{
    public string Split { get; set; } = "";
    public string SavedDir { get; set; } = "";
    public EvalOverall Overall { get; set; } = new();
    public System.Collections.Generic.List<EvalPerClass> PerClass { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, string> Plots { get; set; } = new();
    public EvalTiming Timing { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Runs python/evaluate.py (near-verbatim port of the reference's Test-tab
/// script) - the "edge computing" per-stage inference timing report is
/// exactly what the thesis's Rumusan 2 (cycle time vs. manual) needs, and
/// it only exists in Ultralytics' own val()/predict() speed reporting, so
/// there's no reason to reimplement it in C#.
/// </summary>
public class EvaluationService
{
    private static readonly Regex ResultRegex = new(@"EVAL_RESULT (\{.*\})", RegexOptions.Compiled | RegexOptions.Singleline);

    public string PythonExe { get; set; } = "python";
    public event Action<string>? Log;

    public async Task<EvalResult> RunAsync(Model model, string split, float confidence, float iou, CancellationToken ct)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "python", "evaluate.py");
        var weights = Path.Combine(model.Dir, "weights", "best.pt");
        if (!File.Exists(weights)) weights = Path.Combine(model.Dir, "weights", "best.onnx");
        var dataYaml = Path.Combine(model.Dir, "dataset", "data.yaml");
        var outDir = Path.Combine(model.Dir, "eval");

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--weights"); psi.ArgumentList.Add(weights);
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(dataYaml);
        psi.ArgumentList.Add("--split"); psi.ArgumentList.Add(split);
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--imgsz"); psi.ArgumentList.Add(model.Training.ImgSize.ToString());
        psi.ArgumentList.Add("--conf"); psi.ArgumentList.Add(confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--iou"); psi.ArgumentList.Add(iou.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string? resultJson = null;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log?.Invoke(e.Data);
            var m = ResultRegex.Match(e.Data);
            if (m.Success) resultJson = m.Groups[1].Value;
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        await process.WaitForExitAsync(CancellationToken.None);

        if (resultJson == null)
        {
            return new EvalResult { Success = false, Error = $"evaluate.py exited with code {process.ExitCode} and no result - see the log above." };
        }

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<EvalResult>(resultJson, opts) ?? new EvalResult();
        result.Success = true;
        return result;
    }
}
