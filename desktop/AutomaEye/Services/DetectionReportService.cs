using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutomaEye.Models;

namespace AutomaEye.Services;

/// <summary>Mirrors the shape OutputRecorder.Record() writes to outputs/&lt;date&gt;/*.json.</summary>
internal class FrameMeta
{
    public int Seq { get; set; }
    public string Timestamp { get; set; } = "";
    [JsonPropertyName("final_verdict")] public string FinalVerdict { get; set; } = "";
    [JsonPropertyName("total_ms")] public double TotalMs { get; set; }
    public List<StepResult> Steps { get; set; } = new();
    public string Image { get; set; } = "";
}

/// <summary>
/// Ported from the reference's lib/detreport.js: builds the "Excel Deteksi"
/// export by re-reading each frame's saved JSON, taking the Inspection
/// step's detections, and spatially binning them left-to-right into fixed
/// box/hole slots (by GD&amp;T shape config) so column position stays stable
/// across frames even when detection order isn't. This is inherently
/// specific to a part shaped like the reference's own socket-holder (a grid
/// of rectangular box features and circular holes) - the slot counts are
/// parameters here rather than hardcoded so it can be adapted, but the
/// report itself only makes sense for a similarly-shaped part.
/// </summary>
public static class DetectionReportService
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public static string Generate(Project project, DateTime date, int boxSlots = 8, int holeSlots = 6)
    {
        var dir = Path.Combine(project.Dir, ProjectManager.OutputsDir, date.ToString("yyyy-MM-dd"));
        var headers = new List<string> { "ID", "Gambar", "Verdict" };
        for (int i = 1; i <= boxSlots; i++) { headers.Add($"Kotak {i} Panjang (mm)"); headers.Add($"Kotak {i} Lebar (mm)"); }
        for (int i = 1; i <= holeSlots; i++) headers.Add($"Lubang {i} Ø (mm)");
        headers.Add("Waktu Deteksi (ms)");

        var rows = new List<IReadOnlyList<object?>>();
        if (Directory.Exists(dir))
        {
            foreach (var jsonPath in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                FrameMeta? meta;
                try { meta = JsonSerializer.Deserialize<FrameMeta>(File.ReadAllText(jsonPath), ReadOpts); }
                catch { continue; }
                if (meta == null) continue;

                rows.Add(BuildRow(project, meta, boxSlots, holeSlots));
            }
        }

        var outPath = Path.Combine(dir, $"deteksi_{date:yyyyMMdd}.xlsx");
        Directory.CreateDirectory(dir);
        XlsxWriter.Write(outPath, headers, rows);
        return outPath;
    }

    private static IReadOnlyList<object?> BuildRow(Project project, FrameMeta meta, int boxSlots, int holeSlots)
    {
        var boxCandidates = new List<(double x, double lengthMm, double widthMm)>();
        var holeCandidates = new List<(double x, double diaMm)>();

        foreach (var step in meta.Steps)
        {
            var model = project.FindModel(step.ModelName);
            if (model == null) continue;
            var mmPerPixel = model.AddonConfig.MmPerPixel;
            if (mmPerPixel is not { } mm || mm <= 0) continue;

            foreach (var d in step.Detections)
            {
                if (!model.AddonConfig.GdtPerClass.TryGetValue(d.ClassName, out var cfg)) continue;
                var cx = (d.X1 + d.X2) / 2.0;
                var w = (d.X2 - d.X1) * mm;
                var h = (d.Y2 - d.Y1) * mm;

                if (cfg.Shape == GdtShape.Rect) boxCandidates.Add((cx, Math.Max(w, h), Math.Min(w, h)));
                else holeCandidates.Add((cx, (w + h) / 2.0));
            }
        }

        // Assumes a fixed jig (parts don't reorder between frames): sorting
        // left-to-right by x and assigning to slots in that order keeps a
        // given physical box/hole in the same report column across frames,
        // without needing the frame's pixel width to normalize positions.
        var boxes = boxCandidates.OrderBy(b => b.x).Take(boxSlots).ToList();
        var holes = holeCandidates.OrderBy(h => h.x).Take(holeSlots).ToList();

        var row = new List<object?> { meta.Seq, meta.Image, meta.FinalVerdict };
        for (int i = 0; i < boxSlots; i++)
        {
            row.Add(i < boxes.Count ? boxes[i].lengthMm.ToString("F2") : "");
            row.Add(i < boxes.Count ? boxes[i].widthMm.ToString("F2") : "");
        }
        for (int i = 0; i < holeSlots; i++)
            row.Add(i < holes.Count ? holes[i].diaMm.ToString("F2") : "");
        row.Add(meta.TotalMs.ToString("F1"));
        return row;
    }
}
