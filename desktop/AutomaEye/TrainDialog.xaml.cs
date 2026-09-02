using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutomaEye.Models;
using AutomaEye.Services;

namespace AutomaEye;

/// <summary>
/// Runs python/train.py for one model and shows its live progress - the C#
/// side of the "Dataset -> Annotate -> Train -> Test" loop that the previous
/// version of this app was entirely missing (inference-only, everything had
/// to be trained externally and imported as a finished .onnx).
/// </summary>
public partial class TrainDialog : System.Windows.Window
{
    private readonly ProjectManager _mgr;
    private readonly Project _project;
    private readonly Model _model;
    private readonly TrainingService _training = new();
    private CancellationTokenSource? _cts;
    private readonly System.Collections.Generic.List<EpochMetrics> _history = new();

    public TrainDialog(ProjectManager mgr, Project project, Model model)
    {
        InitializeComponent();
        _mgr = mgr;
        _project = project;
        _model = model;

        TitleText.Text = $"Train \"{model.Name}\" ({model.Type.Label()})";
        var stats = DatasetService.Stats(model);
        DatasetStatusText.Text = $"Dataset: {stats.Train} train / {stats.Val} val / {stats.Test} test images ({stats.Annotated} labeled, {stats.Augmented} augmented)";

        EpochsInput.Text = model.Training.Epochs.ToString();
        BatchInput.Text = model.Training.Batch.ToString();
        ImgSizeInput.Text = model.Training.ImgSize.ToString();
        LearnRateInput.Text = model.Training.LearnRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var lastCkpt = Path.Combine(model.Dir, "runs", "train", "weights", "last.pt");
        ResumeButton.Visibility = File.Exists(lastCkpt) ? Visibility.Visible : Visibility.Collapsed;

        _training.Log += line => Dispatcher.Invoke(() => AppendLog(line));
        _training.Progress += m => Dispatcher.Invoke(() => UpdateProgress(m));
    }

    private void AppendLog(string line)
    {
        LogText.Text += line + "\n";
        LogScroll.ScrollToBottom();
    }

    private void UpdateProgress(EpochMetrics m)
    {
        if (m.Total > 0)
        {
            ProgressBarCtl.Value = 100.0 * m.Epoch / m.Total;
            ProgressText.Text = $"Epoch {m.Epoch}/{m.Total}";
        }
        if (m.MAP50 > 0 || m.Precision > 0)
        {
            MetricsText.Text = $"mAP50 {m.MAP50:F3}  mAP50-95 {m.MAP5095:F3}  P {m.Precision:F3}  R {m.Recall:F3}  F1 {m.F1:F3}";

            // EPOCH_METRICS lines only carry real numbers once validation has
            // run for that epoch (mAP/precision > 0) - the plain
            // PROGRESS_EPOCH tick can fire first with everything zeroed.
            if (_history.Count == 0 || _history[^1].Epoch != m.Epoch)
                _history.Add(m);
            else
                _history[^1] = m;

            DrawChart(MapChartCanvas, new (List<double>, Brush)[]
            {
                (_history.Select(h => (double)h.MAP50).ToList(), Brushes.LimeGreen),
                (_history.Select(h => (double)h.MAP5095).ToList(), Brushes.DodgerBlue),
            });
            DrawChart(LossChartCanvas, new (List<double>, Brush)[]
            {
                (_history.Select(h => (double)h.BoxLoss).ToList(), Brushes.OrangeRed),
                (_history.Select(h => (double)h.ValBox).ToList(), Brushes.Gold),
            });
            UpdateFitVerdict();
        }
    }

    private static void DrawChart(Canvas canvas, (List<double> values, Brush color)[] series)
    {
        canvas.Children.Clear();
        var w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 300;
        var h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 140;
        var all = series.SelectMany(s => s.values).Where(v => v > 0).ToList();
        if (all.Count < 2) return;

        var min = all.Min();
        var max = Math.Max(all.Max(), min + 0.0001);
        foreach (var (values, color) in series)
        {
            if (values.Count < 2) continue;
            var points = new PointCollection();
            for (int i = 0; i < values.Count; i++)
            {
                var x = values.Count > 1 ? i / (double)(values.Count - 1) * w : 0;
                var y = h - (values[i] - min) / (max - min) * h;
                points.Add(new System.Windows.Point(x, y));
            }
            canvas.Children.Add(new System.Windows.Shapes.Polyline { Points = points, Stroke = color, StrokeThickness = 2 });
        }
    }

    /// <summary>
    /// Approximate version of the reference's overfit/underfit heuristic:
    /// compares the average train vs. validation box loss over the first
    /// third of epochs so far against the last third. A validation curve
    /// that tracks the training curve almost exactly usually means there's
    /// no real val split; a growing gap with val not improving is
    /// classic overfitting; both staying flat/high is underfitting.
    /// </summary>
    private void UpdateFitVerdict()
    {
        if (_history.Count < 5) { FitVerdictText.Text = "Fit: menilai..."; return; }

        var train = _history.Select(h => (double)h.BoxLoss).ToList();
        var val = _history.Select(h => (double)h.ValBox).ToList();
        var third = Math.Max(1, _history.Count / 3);

        double AvgFirst(List<double> v) => v.Take(third).Average();
        double AvgLast(List<double> v) => v.TakeLast(third).Average();

        var trainFirst = AvgFirst(train); var trainLast = AvgLast(train);
        var valFirst = AvgFirst(val); var valLast = AvgLast(val);

        var sameCurve = val.Zip(train, (v, t) => Math.Abs(v - t)).Average() < 0.01;
        if (sameCurve) { FitVerdictText.Text = "Fit: ⚠ Val = Train (kemungkinan tidak ada val split nyata)"; return; }

        var trainTrend = trainLast - trainFirst;   // negative = improving
        var valTrend = valLast - valFirst;
        var gap = valLast - trainLast;              // positive = val worse than train

        if (gap > 0.1 * Math.Max(trainLast, 0.01) && valTrend >= -0.001)
            FitVerdictText.Text = "Fit: ⚠ Overfitting (val loss tidak turun, train terus turun)";
        else if (trainTrend >= -0.001 && valTrend >= -0.001)
            FitVerdictText.Text = "Fit: ⚠ Underfitting (loss belum turun signifikan)";
        else
            FitVerdictText.Text = "Fit: ✓ Fit seimbang";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunTraining(resume: false);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await RunTraining(resume: true);

    private async System.Threading.Tasks.Task RunTraining(bool resume)
    {
        if (!int.TryParse(EpochsInput.Text, out var epochs) || epochs <= 0) { MessageBox.Show("Enter a valid epoch count.", "AutomaEye"); return; }
        if (!int.TryParse(BatchInput.Text, out var batch) || batch <= 0) { MessageBox.Show("Enter a valid batch size.", "AutomaEye"); return; }
        if (!int.TryParse(ImgSizeInput.Text, out var imgsz) || imgsz <= 0) { MessageBox.Show("Enter a valid image size.", "AutomaEye"); return; }
        if (!float.TryParse(LearnRateInput.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lr) || lr <= 0) { MessageBox.Show("Enter a valid learning rate.", "AutomaEye"); return; }

        _model.Training.Epochs = epochs;
        _model.Training.Batch = batch;
        _model.Training.ImgSize = imgsz;
        _model.Training.LearnRate = lr;

        LogText.Text = "";
        MetricsText.Text = "";
        ProgressBarCtl.Value = 0;
        StartButton.IsEnabled = false;
        ResumeButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        _cts = new CancellationTokenSource();
        try
        {
            var result = await _training.TrainAsync(_project, _model, resume, _cts.Token);
            if (result.Success)
            {
                _model.Trained = true;
                _model.LastMAP = result.MAP50;
                _model.LastPrecision = result.Precision;
                _model.LastRecall = result.Recall;
                _model.LastF1 = result.F1;

                var nextId = (_model.Versions.Count > 0 ? _model.Versions.Max(v => v.Id) : 0) + 1;
                _model.Versions.Add(new ModelVersion
                {
                    Id = nextId,
                    MAP50 = result.MAP50,
                    MAP5095 = result.MAP5095,
                    Precision = result.Precision,
                    Recall = result.Recall,
                    F1 = result.F1,
                    Classes = _model.Classes.ToList(),
                });
                _model.ActiveVersion = nextId;

                var versionDir = Path.Combine(_model.Dir, "versions", $"v{nextId}");
                Directory.CreateDirectory(versionDir);
                var onnx = Path.Combine(_model.Dir, "weights", "best.onnx");
                if (File.Exists(onnx)) File.Copy(onnx, Path.Combine(versionDir, "best.onnx"), overwrite: true);

                _mgr.Save(_project);
                AppendLog($"\nTraining complete - v{nextId} saved and set active.");
            }
            else
            {
                AppendLog($"\n{result.Error}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"\n[X] {ex.Message}");
        }
        finally
        {
            StartButton.IsEnabled = true;
            ResumeButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_training.IsRunning) _training.Cancel();
    }
}
