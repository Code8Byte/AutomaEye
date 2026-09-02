using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AutomaEye.Models;

namespace AutomaEye.Services;

public class DailySummary
{
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Ng { get; set; }
    public double AvgCycleMs { get; set; }
    public Dictionary<string, int> NgByStep { get; set; } = new();
}

/// <summary>Re-parses outputs/daily_summary.csv (written by OutputRecorder) the same way the reference app's lib/nvidia.js does to feed the Report/Analyze prompts and the Run tab's counters.</summary>
public static class DailySummaryService
{
    public static DailySummary Aggregate(Project project, DateTime date)
    {
        var summary = new DailySummary();
        var path = Path.Combine(project.Dir, ProjectManager.OutputsDir, "daily_summary.csv");
        if (!File.Exists(path)) return summary;

        var dayKey = date.ToString("yyyy-MM-dd");
        var totalMs = 0.0;

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var cols = SplitCsvLine(line);
            if (cols.Length < 6 || cols[0] != dayKey) continue;

            summary.Total++;
            totalMs += double.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
            var verdict = cols[3];
            if (verdict == "OK") summary.Ok++; else summary.Ng++;

            if (verdict == "NG")
            {
                foreach (var stepStr in cols[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var nameEnd = stepStr.IndexOf(':');
                    if (nameEnd < 0) continue;
                    var name = stepStr[..nameEnd];
                    var stepVerdict = stepStr[(nameEnd + 1)..];
                    if (stepVerdict.StartsWith("NG"))
                        summary.NgByStep[name] = summary.NgByStep.GetValueOrDefault(name) + 1;
                }
            }
        }

        summary.AvgCycleMs = summary.Total > 0 ? totalMs / summary.Total : 0;
        return summary;
    }

    // daily_summary.csv's "steps" column packs name:VERDICT(conf) entries with ';' - none of the
    // other columns can contain a comma, so a plain split is safe (no quoting is ever written).
    private static string[] SplitCsvLine(string line) => line.Split(',');
}
