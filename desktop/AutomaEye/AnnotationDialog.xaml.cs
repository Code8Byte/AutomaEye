using System;
using System.Diagnostics;
using System.Windows;
using AutomaEye.Models;
using AutomaEye.Services;

namespace AutomaEye;

/// <summary>
/// "Annotation Studio" from the reference's annotate.html, minus the
/// in-app &lt;webview&gt; embed - Label Studio opens in the system browser
/// instead (no WebView2 dependency needed for the same workflow: start
/// server, auto-create a project for this model, annotate in the browser,
/// sync YOLO labels back).
/// </summary>
public partial class AnnotationDialog : System.Windows.Window
{
    private readonly Model _model;
    private readonly LabelStudioService _svc = new();
    private int? _projectId;

    public AnnotationDialog(Model model)
    {
        InitializeComponent();
        _model = model;
        TitleText.Text = $"Annotation - \"{model.Name}\"";
        StatusText.Text = "Server: stopped";
    }

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _svc.StartServer(ConfigService.Current.PythonExe);
            StatusText.Text = "Server: starting... (open the labeling page once it's ready, may take ~20s the first time)";
        }
        catch (Exception ex) { LogText.Text = ex.Message; }
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        _svc.StopServer();
        StatusText.Text = "Server: stopped";
    }

    private async void TestToken_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "Testing...";
        try
        {
            var ok = await _svc.TestAuthAsync();
            LogText.Text = ok ? "Token OK." : "Token rejected or server not running.";
        }
        catch (Exception ex) { LogText.Text = $"Could not reach server: {ex.Message}"; }
    }

    private async void AutoSetup_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "Creating project and uploading images...";
        try
        {
            _projectId = await _svc.AutoSetupProjectAsync(_model);
            OpenLabelingButton.IsEnabled = true;
            SyncButton.IsEnabled = true;
            LogText.Text = $"Project #{_projectId} created and images uploaded.";
        }
        catch (Exception ex) { LogText.Text = ex.Message; }
    }

    private void OpenLabeling_Click(object sender, RoutedEventArgs e) => LabelStudioService.OpenInBrowser(_projectId);

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId == null) return;
        LogText.Text = "Syncing YOLO labels...";
        try
        {
            var (matched, unmatched) = await _svc.SyncYoloLabelsAsync(_model, _projectId.Value);
            LogText.Text = $"Synced: {matched} matched to dataset images, {unmatched} unmatched (saved under their exported name).";
        }
        catch (Exception ex) { LogText.Text = ex.Message; }
    }

    private void OpenDatasetFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(_model.Dir, "dataset");
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }
}
