using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using AutomaEye.Models;
using AutomaEye.Services;

namespace AutomaEye;

/// <summary>The Test tab from the reference's model.html, as a dialog: runs python/evaluate.py and shows the "edge computing" per-stage timing report the thesis's cycle-time requirement needs.</summary>
public partial class EvaluateDialog : System.Windows.Window
{
    private readonly Model _model;
    private readonly EvaluationService _svc = new();
    private string _split = "test";
    private string? _savedDir;

    public EvaluateDialog(Model model)
    {
        InitializeComponent();
        _model = model;
        TitleText.Text = $"Test / Evaluate \"{model.Name}\"";
        _svc.Log += line => Dispatcher.Invoke(() => LogText.Text += line + "\n");
        HighlightSplit();
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string split }) return;
        _split = split;
        HighlightSplit();
    }

    private void HighlightSplit()
    {
        foreach (var (btn, tag) in new[] { (SplitTestButton, "test"), (SplitValButton, "val"), (SplitTrainButton, "train") })
        {
            bool sel = tag == _split;
            btn.Background = sel ? (System.Windows.Media.Brush)FindResource("AccentBrush") : (System.Windows.Media.Brush)FindResource("PanelBrush");
            btn.Foreground = sel ? System.Windows.Media.Brushes.Black : (System.Windows.Media.Brush)FindResource("TextBrush");
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        StatusText.Text = "Running...";
        LogText.Text = "";
        try
        {
            var result = await _svc.RunAsync(_model, _split, _model.CalibratedConfidence ?? 0.25f, 0.45f, CancellationToken.None);
            if (!result.Success)
            {
                StatusText.Text = result.Error ?? "Evaluation failed.";
                return;
            }

            _savedDir = result.SavedDir;
            OpenFolderButton.IsEnabled = !string.IsNullOrEmpty(_savedDir);
            StatusText.Text = $"Split: {result.Split}  -  {result.Timing.NImages} image(s)";

            var o = result.Overall;
            OverallText.Text = $"F1 {o.F1:F3}   mAP50 {o.Map50:F3}   mAP50-95 {o.Map5095:F3}   Precision {o.Precision:F3}   Recall {o.Recall:F3}   " +
                                $"Waktu Inferensi (mean) {result.Timing.PerStage.GetValueOrDefault("total")?.Mean:F1} ms  ({result.Timing.Fps:F1} FPS)";

            var t = result.Timing;
            var rows = t.PerStage.Select(kv => $"  {kv.Key,-12} n={kv.Value.N,-4} mean={kv.Value.Mean,6:F1}  median={kv.Value.Median,6:F1}  min={kv.Value.Min,6:F1}  max={kv.Value.Max,6:F1}  std={kv.Value.Std,6:F1}");
            TimingText.Text = $"Device: {t.Device.Device} {t.Device.Cpu} {t.Device.Gpu}  RAM {t.Device.RamGb}GB  torch {t.Device.Torch}  imgsz={t.Imgsz} conf={t.Conf} iou={t.Iou}\n" +
                               $"Cold-start (first image, excluded from stats): {t.ColdStartMs} ms\n" + string.Join("\n", rows);

            PerClassText.Text = string.Join("\n", result.PerClass.Select(c => $"  {c.Name,-16} P={c.Precision:F3}  R={c.Recall:F3}  mAP50={c.Map50:F3}  mAP50-95={c.Map5095:F3}"));
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_savedDir) || !System.IO.Directory.Exists(_savedDir)) return;
        Process.Start(new ProcessStartInfo { FileName = _savedDir, UseShellExecute = true });
    }
}
