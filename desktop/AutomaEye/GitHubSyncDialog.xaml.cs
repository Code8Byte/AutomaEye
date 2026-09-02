using System;
using System.Windows;
using AutomaEye.Services;

namespace AutomaEye;

public partial class GitHubSyncDialog : System.Windows.Window
{
    private readonly GitHubSyncService _sync;

    /// <summary>Raised after a successful Pull, so the caller can refresh its project list.</summary>
    public event Action? ProjectsChanged;

    public GitHubSyncDialog(string projectsRoot)
    {
        InitializeComponent();
        _sync = new GitHubSyncService(projectsRoot);
        Loaded += async (_, _) => await RefreshStatus();
    }

    private async System.Threading.Tasks.Task RefreshStatus()
    {
        var status = await _sync.GetStatus();
        if (!status.IsRepo)
        {
            StatusText.Text = "Not connected yet - enter a repo URL and click Connect.";
        }
        else if (!status.HasRemote)
        {
            StatusText.Text = "Git repo exists locally but has no GitHub remote - enter a repo URL and click Connect.";
        }
        else
        {
            RepoUrlInput.Text = status.Remote;
            StatusText.Text = $"Connected to {status.Remote} (branch {status.Branch}). " +
                (status.Dirty ? $"{status.Changes} unsaved local change(s)." : "Nothing to push.");
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RepoUrlInput.Text))
        {
            System.Windows.MessageBox.Show("Enter a repo URL first.", "AutomaEye");
            return;
        }
        LogText.Text = "Connecting...";
        var log = await _sync.Connect(RepoUrlInput.Text.Trim());
        LogText.Text = log;
        await RefreshStatus();
    }

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "Pushing...";
        var (ok, log) = await _sync.Push("");
        LogText.Text = log;
        await RefreshStatus();
        if (!ok) System.Windows.MessageBox.Show("Push did not complete - see the log for details.", "AutomaEye");
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        LogText.Text = "Pulling...";
        var (ok, log) = await _sync.Pull();
        LogText.Text = log;
        await RefreshStatus();
        if (ok) ProjectsChanged?.Invoke();
        else System.Windows.MessageBox.Show("Pull did not complete - see the log for details.", "AutomaEye");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
