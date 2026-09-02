using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutomaEye.Models;
using AutomaEye.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace AutomaEye;

public class ModelDisplay
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string TrainedLabel { get; set; } = "";
    public bool IsGdt { get; set; }
    public bool HasAddonConfig { get; set; }
    public string GdtStatus { get; set; } = "";
}

public class WorkflowStepDisplay
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
}

public class AddStepOption
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public Category Category { get; set; }
}

public class CategorySlotDisplay
{
    public Category Category { get; set; }
    public string StageName { get; set; } = "";
    public bool HasAssignment { get; set; }
    public string StatusText { get; set; } = "";
    public System.Windows.Media.Brush StatusBrush { get; set; } = System.Windows.Media.Brushes.Gray;
    public List<AddStepOption> Options { get; set; } = new();
}

public class CameraOption
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
}

public partial class MainWindow : System.Windows.Window
{
    private const string DefaultScript = """
        // Called once per inspection result.
        // result = { verdict: "OK"|"NG", confidence, total_ms, steps: [...] }
        // Helpers available: serial_write(str), http_post(url, jsonBody), log(str)
        function onResult(result) {
          if (result.verdict === "NG") {
            serial_write("1\n");
          } else {
            serial_write("0\n");
          }
        }
        """;

    private readonly ProjectManager _mgr;
    private Project? _current;

    // Live-run state
    private CameraService? _cam;
    private ArduinoGate? _gate;
    private WorkflowExecutor? _executor;
    private OutputRecorder? _recorder;
    private DispatcherTimer? _runTimer;
    private bool _running;
    private int _total, _ok, _ng;
    private int _selectedCamera;
    private string _outputMode = "signal";

    public MainWindow()
    {
        InitializeComponent();

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutomaEye", "projects");
        _mgr = new ProjectManager(root);

        RefreshProjects();
        // No idle camera polling - this is an edge-computing app, the camera
        // only turns on for a one-off "Preview" click or for Start inspection,
        // never continuously in the background.

        var cameras = SafeListCameras();
        CameraList.ItemsSource = cameras.Select((label, i) => new CameraOption { Index = i, Label = i == 0 ? label + " (selected)" : label }).ToList();
    }

    private void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        _selectedCamera = index;
        // Re-render with a plain text marker on the selected one - simpler and
        // more robust than reaching into the visual tree to restyle a button.
        if (CameraList.ItemsSource is IEnumerable<CameraOption> current)
        {
            CameraList.ItemsSource = current.Select(c => new CameraOption
            {
                Index = c.Index,
                Label = c.Index == index ? StripSelectedMarker(c.Label) + " (selected)" : StripSelectedMarker(c.Label),
            }).ToList();
        }
    }

    private static string StripSelectedMarker(string label) => label.Replace(" (selected)", "");

    private static List<string> SafeListCameras()
    {
        try { return CameraService.ListDevices().ToList(); }
        catch { return new List<string> { "Camera 0" }; }
    }

    private void RefreshProjects()
    {
        ProjectList.ItemsSource = _mgr.List();
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "AutomaEye", MessageBoxButton.OK, MessageBoxImage.Warning);

    /* ---------------- Projects ---------------- */

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PromptDialog("New project", "Name", "Description (optional)") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value1)) return;
        try
        {
            var project = _mgr.Create(dialog.Value1, dialog.Value2 ?? "");
            RefreshProjects();
            OpenProject(project.Name);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void GitHubStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GitHubSyncDialog(_mgr.Root) { Owner = this };
        dialog.ProjectsChanged += RefreshProjects;
        dialog.ShowDialog();
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show($"Delete project \"{_current.Name}\"? This cannot be undone.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            _mgr.Delete(_current.Name);
            _current = null;
            MainArea.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Visible;
            RefreshProjects();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is Project p) OpenProject(p.Name);
    }

    private void OpenProject(string name)
    {
        try
        {
            _current = _mgr.Load(name);
            ProjectTitle.Text = _current.Name;
            MainArea.Visibility = Visibility.Visible;
            EmptyStateText.Visibility = Visibility.Collapsed;
            RenderModels();
            RenderWorkflow();
            RenderOutput();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /* ---------------- Models ---------------- */

    private void RenderModels()
    {
        if (_current == null) return;
        ModelsList.ItemsSource = _current.Models.Select(m => new ModelDisplay
        {
            Name = m.Name,
            Type = m.Type.Label(),
            TrainedLabel = m.Trained ? "trained" : "untrained",
            IsGdt = m.Addons.Contains(Addon.GdtMeasurement),
            HasAddonConfig = m.Addons.Any(a => a is Addon.GdtMeasurement or Addon.Count),
            GdtStatus = m.AddonConfig.MmPerPixel is { } mmpp ? $"Calibrated: {mmpp:F4} mm/px" : "Not calibrated yet - click Calibrate",
        }).ToList();
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dialog = new AddModelDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var model = _mgr.AddModel(_current, dialog.ModelName, dialog.SelectedType, dialog.SelectedAddons, dialog.Classes);
            if (dialog.CountExpected is { } count)
            {
                model.AddonConfig.CountExpected = count;
                _mgr.Save(_current);
            }
            OpenProject(_current.Name);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>
    /// Register an already-trained .onnx without going through the in-app
    /// training pipeline - for a line that already has a model from elsewhere.
    /// </summary>
    private void LoadModel_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dialog = new PromptDialog("Load existing model", "Model name", "Classes, comma-separated (must match the .onnx's output order)") { Owner = this, Value2 = "OK,NG" };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value1)) return;
        var classes = (dialog.Value2 ?? "OK,NG").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var fileDialog = new Microsoft.Win32.OpenFileDialog { Filter = "ONNX model (*.onnx)|*.onnx", Title = "Select the trained .onnx file" };
        if (fileDialog.ShowDialog() != true) return;

        try
        {
            var model = _mgr.AddModel(_current, dialog.Value1, AIType.Detection, new List<Addon>(), classes);
            var weightsDir = Path.Combine(model.Dir, ProjectManager.WeightsDir);
            Directory.CreateDirectory(weightsDir);
            File.Copy(fileDialog.FileName, Path.Combine(weightsDir, "best.onnx"), overwrite: true);
            model.Trained = true;
            _mgr.Save(_current);
            OpenProject(_current.Name);
            MessageBox.Show($"\"{dialog.Value1}\" is ready to use in a workflow.", "AutomaEye");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ImportImages_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string modelName }) return;
        var model = _current.FindModel(modelName);
        if (model == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            Multiselect = true,
            Title = $"Import images into \"{modelName}\"'s dataset",
        };
        if (dialog.ShowDialog() != true) return;

        var split = MessageBox.Show("Add to the validation split instead of training?\n\nYes = val, No = train", "AutomaEye", MessageBoxButton.YesNo) == MessageBoxResult.Yes
            ? "val" : "train";

        int imported = 0;
        foreach (var path in dialog.FileNames)
        {
            try { DatasetService.ImportImage(model, path, split); imported++; }
            catch { /* skip files that fail to copy, keep importing the rest */ }
        }
        MessageBox.Show($"Imported {imported} of {dialog.FileNames.Length} image(s) into \"{modelName}\" ({split}).", "AutomaEye");
    }

    private void GdtCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string modelName }) return;
        var model = _current.FindModel(modelName);
        if (model == null) return;

        var dialog = new GdtCalibrationDialog(_selectedCamera) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultMmPerPixel is { } mmPerPixel)
        {
            model.AddonConfig.MmPerPixel = mmPerPixel;
            try
            {
                _mgr.Save(_current);
                OpenProject(_current.Name);
                MessageBox.Show($"Calibrated: {mmPerPixel:F4} mm/px.", "AutomaEye");
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void ConfigureAddons_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string modelName }) return;
        var model = _current.FindModel(modelName);
        if (model == null) return;

        var dialog = new AddonConfigDialog(model) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                _mgr.Save(_current);
                OpenProject(_current.Name);
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string name }) return;
        if (MessageBox.Show($"Delete model \"{name}\"?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _mgr.DeleteModel(_current, name);
            OpenProject(_current.Name);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CalibrateModel_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string name } button) return;
        var model = _current.FindModel(name);
        if (model == null) return;

        // Calibration runs inference over the whole val split, which can take
        // real time - Task.Run keeps that off the UI thread so the window
        // stays responsive (dragging, other clicks) instead of freezing.
        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "Calibrating...";
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => CalibrationService.Calibrate(model));
            model.CalibratedConfidence = result.BestConfidence;
            _mgr.Save(_current);

            var rows = string.Join("\n", result.Table.Select(r =>
                $"  conf {r.Confidence:F2}  ->  F1 {r.F1:F3}  (P {r.Precision:F2}, R {r.Recall:F2}, tp={r.Tp} fp={r.Fp} fn={r.Fn})"));
            MessageBox.Show(
                $"Best confidence threshold: {result.BestConfidence:F2} (F1 {result.BestF1:F3})\n" +
                $"Evaluated {result.Evaluated} val images.\n\n{rows}\n\n" +
                "This threshold is now saved and will be used automatically when running this model.",
                "Calibration complete");
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
        }
    }

    private async void AugmentModel_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: string name } button) return;
        var model = _current.FindModel(name);
        if (model == null) return;

        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "Augmenting...";
        try
        {
            var opts = new AugOptions { Rotate = true, FlipHorizontal = true, Blur = true, Exposure = true, Multiplier = 2 };
            var n = await System.Threading.Tasks.Task.Run(() => DatasetService.Augment(model, opts));
            MessageBox.Show($"Generated {n} augmented images for \"{name}\".", "AutomaEye");
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
        }
    }

    /* ---------------- Workflow ---------------- */

    // Fixed pipeline order, matching the reference Electron app's workflow:
    // Camera -> Positioning (optional tracking) -> Inference (detect + output
    // per the attached model type) -> Output -> Misc (result storage). Every
    // stage is optional - a project can leave any of them empty.
    private static readonly (Category Category, string StageName)[] PipelineStages =
    {
        (Category.Capture, "Camera"),
        (Category.Positioning, "Positioning"),
        (Category.Inspection, "Inference"),
        (Category.Communication, "Output"),
        (Category.Options, "Misc"),
    };

    private void RenderWorkflow()
    {
        if (_current == null) return;

        var assigned = _current.Workflow.Steps.ToDictionary(s => s.Category, s => s.ModelName);
        var modelOptions = _current.Models.Select(m => new AddStepOption { Name = m.Name, Label = "+ " + m.Name }).ToList();

        CategorySlots.ItemsSource = PipelineStages.Select(stage =>
        {
            var hasModel = assigned.TryGetValue(stage.Category, out var modelName);
            return new CategorySlotDisplay
            {
                Category = stage.Category,
                StageName = stage.StageName,
                HasAssignment = hasModel,
                StatusText = hasModel ? modelName! : "not set",
                StatusBrush = hasModel
                    ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                    : (System.Windows.Media.Brush)FindResource("TextDimBrush"),
                Options = hasModel
                    ? new List<AddStepOption>() // already assigned - Clear first to reassign
                    : modelOptions.Select(o => new AddStepOption { Name = o.Name, Label = o.Label, Category = stage.Category }).ToList(),
            };
        }).ToList();
    }

    private void AssignSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: AddStepOption opt }) return;
        var steps = _current.Workflow.Steps.Where(s => s.Category != opt.Category).ToList();
        steps.Add(new WorkflowStep { ModelName = opt.Name, Category = opt.Category, ContinueOn = "always" });
        SaveWorkflow(steps);
    }

    private void ClearSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not Button { Tag: Category category }) return;
        var steps = _current.Workflow.Steps.Where(s => s.Category != category).ToList();
        SaveWorkflow(steps);
    }

    private void SaveWorkflow(List<WorkflowStep> steps)
    {
        if (_current == null) return;
        try
        {
            // Persist in fixed pipeline order (not insertion order) so the
            // executor always runs Camera -> Positioning -> Inference ->
            // Output -> Misc regardless of the order slots were assigned in.
            var order = PipelineStages.Select((s, i) => (s.Category, i)).ToDictionary(x => x.Category, x => x.i);
            var ordered = steps.OrderBy(s => order.TryGetValue(s.Category, out var i) ? i : int.MaxValue).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].StepIndex = i;

            _mgr.SetWorkflow(_current, ordered, _current.Workflow.OnFirstNG);
            RenderWorkflow();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /* ---------------- Output ---------------- */

    private void RenderOutput()
    {
        if (_current == null) return;
        _outputMode = string.IsNullOrEmpty(_current.Output.Mode) ? "signal" : _current.Output.Mode;
        ScriptTextBox.Text = string.IsNullOrEmpty(_current.Output.Script) ? DefaultScript : _current.Output.Script;
        TestResultText.Text = "";
        OutputSavedText.Visibility = Visibility.Collapsed;
        UpdateOutputPanelVisibility();
    }

    private void UpdateOutputPanelVisibility()
    {
        bool isScript = _outputMode == "script";
        ScriptPanel.Visibility = isScript ? Visibility.Visible : Visibility.Collapsed;
        SignalInfoText.Visibility = isScript ? Visibility.Collapsed : Visibility.Visible;
        OutputModeSignalButton.Background = isScript ? (System.Windows.Media.Brush)FindResource("PanelBrush") : (System.Windows.Media.Brush)FindResource("AccentBrush");
        OutputModeSignalButton.Foreground = isScript ? (System.Windows.Media.Brush)FindResource("TextBrush") : System.Windows.Media.Brushes.Black;
        OutputModeScriptButton.Background = isScript ? (System.Windows.Media.Brush)FindResource("AccentBrush") : (System.Windows.Media.Brush)FindResource("PanelBrush");
        OutputModeScriptButton.Foreground = isScript ? System.Windows.Media.Brushes.Black : (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private void OutputModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        _outputMode = mode;
        UpdateOutputPanelVisibility();
    }

    private void TestScript_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "Running...";
        TestResultText.Foreground = FindResource("TextDimBrush") as System.Windows.Media.Brush;
        try
        {
            ScriptRunner.Test(ScriptTextBox.Text);
            TestResultText.Text = "Script ran onResult() with a sample OK result - no errors.";
            TestResultText.Foreground = FindResource("AccentBrush") as System.Windows.Media.Brush;
        }
        catch (Exception ex)
        {
            TestResultText.Text = ex.Message;
            TestResultText.Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush;
        }
    }

    private void SaveOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            _mgr.SetOutput(_current, _outputMode, ScriptTextBox.Text);
            OutputSavedText.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            timer.Tick += (_, _) => { OutputSavedText.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /* ---------------- Run ---------------- */

    // Edge-computing app: the camera stays off until the user deliberately
    // asks for it - a single "Preview" click, or Start inspection. No idle
    // polling in the background, ever.
    private void PreviewOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return; // the run loop owns the camera while active
        try
        {
            using var cam = new CameraService(_selectedCamera, 640, 480, 30);
            using var frame = cam.Read();
            PreviewImage.Source = frame.ToBitmapSource();
            PreviewIdleText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ToggleRun_Click(object sender, RoutedEventArgs e)
    {
        if (_running) StopRun();
        else StartRun();
    }

    private void StartRun()
    {
        if (_current == null) return;
        if (_current.Workflow.Steps.Count == 0)
        {
            MessageBox.Show("Add at least one workflow step first.", "AutomaEye");
            return;
        }

        try
        {
            _cam = new CameraService(_selectedCamera, 1280, 720, 30);
            _executor = new WorkflowExecutor(_current, new ModelSettings { Confidence = 0.35f, Iou = 0.45f, ImgSz = 640 });
            _recorder = new OutputRecorder(_current, new SignalConfig(), _gate);

            _total = _ok = _ng = 0;
            UpdateCounters();
            _running = true;
            ToggleRunButton.Content = "Stop";
            VerdictText.Text = "SCANNING...";

            _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _runTimer.Tick += (_, _) => RunOneFrame();
            _runTimer.Start();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            CleanupRun();
        }
    }

    private void RunOneFrame()
    {
        if (_cam == null || _executor == null || _recorder == null) return;
        try
        {
            using var frame = _cam.Read();
            var result = _executor.Run(frame);
            _recorder.Record(frame, result);

            PreviewImage.Source = frame.ToBitmapSource();
            PreviewIdleText.Visibility = Visibility.Collapsed;
            _total++;
            if (result.FinalVerdict == "OK") _ok++; else _ng++;
            UpdateCounters();

            VerdictText.Text = result.FinalVerdict;
            VerdictBadge.Background = result.FinalVerdict == "OK"
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0f, 0x2a, 0x22))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2a, 0x14, 0x18));
        }
        catch (Exception ex)
        {
            VerdictText.Text = "ERROR";
            Console.WriteLine(ex.Message);
        }
    }

    private void UpdateCounters()
    {
        TotalCountText.Text = _total.ToString();
        OkCountText.Text = _ok.ToString();
        NgCountText.Text = _ng.ToString();
    }

    private void StopRun()
    {
        CleanupRun();
        ToggleRunButton.Content = "Start inspection";
        VerdictText.Text = "IDLE";
        PreviewImage.Source = null;
        PreviewIdleText.Visibility = Visibility.Visible;
    }

    private void CleanupRun()
    {
        _running = false;
        _runTimer?.Stop();
        _runTimer = null;
        _executor?.Dispose();
        _executor = null;
        _cam?.Dispose();
        _cam = null;
        _gate?.Dispose();
        _gate = null;
        _recorder = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CleanupRun();
        base.OnClosed(e);
    }
}
