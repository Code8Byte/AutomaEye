using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AutomaEye.Models;
using OpenCvSharp;

namespace AutomaEye.Services;

public class SignalConfig
{
    public string OkSignal { get; set; } = "0\n";
    public string NgSignal { get; set; } = "1\n";
    public bool SignalOnOk { get; set; }
    public bool SaveOkImages { get; set; }
}

/// <summary>
/// Saves inspection output to the daily folder structure and dispatches the
/// verdict outward - either the fixed serial signal, or the project's custom
/// output script if Output.Mode == "script".
/// </summary>
public class OutputRecorder
{
    private readonly Project _project;
    private readonly SignalConfig _signals;
    private readonly ISerialWriter? _serial;
    private readonly ScriptRunner? _script;
    private readonly Dictionary<string, int> _dailyCount = new();
    private readonly object _lock = new();

    public OutputRecorder(Project project, SignalConfig signals, ISerialWriter? serial)
    {
        _project = project;
        _signals = signals;
        _serial = serial;

        if (project.Output.Mode == "script" && !string.IsNullOrWhiteSpace(project.Output.Script))
        {
            try
            {
                _script = new ScriptRunner(project.Output.Script, serial);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Output script: {ex.Message}", ex);
            }
        }
    }

    public (string? imagePath, string? metaPath) Record(Mat frame, RunResult result)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var dayKey = now.ToString("yyyy-MM-dd");
            var timeKey = now.ToString("HHmm");
            var dayDir = Path.Combine(_project.Dir, ProjectManager.OutputsDir, dayKey);
            Directory.CreateDirectory(dayDir);

            if (!_dailyCount.ContainsKey(dayKey))
                _dailyCount[dayKey] = CountExisting(dayDir);
            var seq = ++_dailyCount[dayKey];

            var baseName = $"{seq:D3}-{timeKey}";
            var saveImage = result.FinalVerdict == "NG" || _signals.SaveOkImages;

            string? imagePath = null, metaPath = null;
            if (saveImage)
            {
                imagePath = Path.Combine(dayDir, baseName + ".jpg");
                if (!frame.SaveImage(imagePath)) throw new IOException($"Failed to write {imagePath}");

                var meta = new
                {
                    seq,
                    timestamp = now.ToString("o"),
                    final_verdict = result.FinalVerdict,
                    total_ms = result.TotalMs,
                    steps = result.Steps,
                    image = Path.GetFileName(imagePath),
                };
                metaPath = Path.Combine(dayDir, baseName + ".json");
                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }

            AppendCsv(dayKey, seq, now, result);

            if (_script != null)
            {
                _script.Call(ToScriptResult(result));
            }
            else if (_serial != null)
            {
                if (result.FinalVerdict == "NG") _serial.Write(_signals.NgSignal);
                else if (_signals.SignalOnOk) _serial.Write(_signals.OkSignal);
            }

            return (imagePath, metaPath);
        }
    }

    private static ScriptResult ToScriptResult(RunResult res)
    {
        var steps = res.Steps.Select(s => new ScriptStepResult { ModelName = s.ModelName, Verdict = s.Verdict, Confidence = s.Confidence }).ToList();
        var minConf = res.FinalVerdict == "NG"
            ? res.Steps.Where(s => s.Verdict == "NG").Select(s => (double)s.Confidence).DefaultIfEmpty(1.0).Min()
            : 1.0;
        return new ScriptResult { Verdict = res.FinalVerdict, Confidence = minConf, TotalMs = res.TotalMs, Steps = steps };
    }

    private void AppendCsv(string dayKey, int seq, DateTime ts, RunResult res)
    {
        var path = Path.Combine(_project.Dir, ProjectManager.OutputsDir, "daily_summary.csv");
        var isNew = !File.Exists(path);
        using var writer = new StreamWriter(path, append: true);
        if (isNew) writer.WriteLine("date,seq,timestamp,final_verdict,total_ms,steps");

        var stepsStr = string.Join(";", res.Steps.Select(s => $"{s.ModelName}:{s.Verdict}({s.Confidence.ToString("F2", CultureInfo.InvariantCulture)})"));
        writer.WriteLine($"{dayKey},{seq:D3},{ts:o},{res.FinalVerdict},{res.TotalMs.ToString("F1", CultureInfo.InvariantCulture)},{stepsStr}");
    }

    private static int CountExisting(string dir) =>
        Directory.GetFiles(dir).Count(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
}
