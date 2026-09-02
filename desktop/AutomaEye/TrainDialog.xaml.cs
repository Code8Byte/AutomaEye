using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
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
        }
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
