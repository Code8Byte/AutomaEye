using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AutomaEye.Services;

public class GitStatus
{
    public bool IsRepo;
    public bool HasRemote;
    public string Remote = "";
    public string Branch = "main";
    public bool Dirty;
    public int Changes;
}

/// <summary>
/// Syncs the whole local projects folder to a GitHub repo the user connects,
/// mirroring the original app's lib/gitsync.js: the projects root itself IS
/// the git working tree, each project is just a subfolder in it, and auth
/// uses whatever git credentials are already stored on the machine (gh CLI /
/// Git Credential Manager) rather than the app holding a token itself.
///
/// Large files (dataset images, .onnx/.pt weights) go through Git LFS so the
/// repo doesn't balloon with binary diffs - see EnsureLfsConfigured().
/// </summary>
public class GitHubSyncService
{
    private readonly string _root;

    public GitHubSyncService(string projectsRoot)
    {
        _root = projectsRoot;
    }

    private async Task<(int Code, string Output)> Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git", string.Join(' ', Array.ConvertAll(args, EscapeArg)))
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, (stdout + stderr).Trim());
    }

    private static string EscapeArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

    public async Task<GitStatus> GetStatus()
    {
        var (code, _) = await Git("rev-parse", "--is-inside-work-tree");
        if (code != 0) return new GitStatus { IsRepo = false };

        var (remoteCode, remoteOut) = await Git("remote", "get-url", "origin");
        var (_, branchOut) = await Git("rev-parse", "--abbrev-ref", "HEAD");
        var (_, dirtyOut) = await Git("status", "--porcelain");
        var changes = string.IsNullOrWhiteSpace(dirtyOut) ? 0 : dirtyOut.Split('\n').Length;

        return new GitStatus
        {
            IsRepo = true,
            HasRemote = remoteCode == 0,
            Remote = remoteOut,
            Branch = string.IsNullOrWhiteSpace(branchOut) ? "main" : branchOut,
            Dirty = changes > 0,
            Changes = changes,
        };
    }

    /// <summary>Initializes the projects root as a git repo (if not already one) and points origin at repoUrl.</summary>
    public async Task<string> Connect(string repoUrl)
    {
        var log = new StringBuilder();
        var status = await GetStatus();
        if (!status.IsRepo)
        {
            log.AppendLine((await Git("init")).Output);
            // Git's local default branch name (master) predates GitHub
            // defaulting new repos to "main" - align so the first push lands
            // on the branch GitHub already created rather than a stray one.
            log.AppendLine((await Git("checkout", "-b", "main")).Output);
        }
        if (status.HasRemote)
        {
            log.AppendLine((await Git("remote", "set-url", "origin", repoUrl)).Output);
        }
        else
        {
            log.AppendLine((await Git("remote", "add", "origin", repoUrl)).Output);
        }
        await EnsureLfsConfigured();
        return log.ToString().Trim();
    }

    /// <summary>
    /// Tracks the file types that matter for this app (dataset images, ONNX/PT
    /// weights) via Git LFS, so pushing a project with hundreds of training
    /// images doesn't bloat the repo with binary diffs.
    /// </summary>
    private async Task EnsureLfsConfigured()
    {
        await Git("lfs", "install", "--local");
        var attrPath = Path.Combine(_root, ".gitattributes");
        var needed = new[]
        {
            "*.jpg filter=lfs diff=lfs merge=lfs -text",
            "*.jpeg filter=lfs diff=lfs merge=lfs -text",
            "*.png filter=lfs diff=lfs merge=lfs -text",
            "*.onnx filter=lfs diff=lfs merge=lfs -text",
            "*.pt filter=lfs diff=lfs merge=lfs -text",
        };
        var existing = File.Exists(attrPath) ? await File.ReadAllTextAsync(attrPath) : "";
        var missing = Array.FindAll(needed, line => !existing.Contains(line));
        if (missing.Length > 0)
        {
            await File.AppendAllTextAsync(attrPath, string.Join('\n', missing) + "\n");
        }
    }

    /// <summary>git add -A + commit + push - uploads every project's dataset/model/workflow changes.</summary>
    public async Task<(bool Ok, string Log)> Push(string message)
    {
        var status = await GetStatus();
        if (!status.IsRepo) return (false, "Not connected to a git repo yet - click Connect first.");
        if (!status.HasRemote) return (false, "No GitHub repo set - click Connect first.");

        var log = new StringBuilder();
        log.AppendLine((await Git("add", "-A")).Output);

        var commitMsg = string.IsNullOrWhiteSpace(message) ? $"AutomaEye sync {DateTime.UtcNow:o}" : message;
        var commit = await Git("commit", "-m", commitMsg);
        log.AppendLine(commit.Output);

        // Re-resolve the branch name now that a commit (and so a real HEAD)
        // exists - on a brand-new repo, resolving it beforehand returns git's
        // "unborn HEAD" error text instead of a branch name.
        var (branchCode, branchOut) = await Git("rev-parse", "--abbrev-ref", "HEAD");
        var branch = branchCode == 0 && !string.IsNullOrWhiteSpace(branchOut) ? branchOut.Trim() : "main";

        var push = await Git("push", "-u", "origin", $"HEAD:{branch}");
        log.AppendLine(push.Output);

        if (push.Code != 0 && (push.Output.Contains("rejected") || push.Output.Contains("fetch first") || push.Output.Contains("non-fast-forward")))
        {
            return (false, log + "\n\nThe repo has changes you don't have locally - click Pull first, then Push again.");
        }
        return (push.Code == 0, log.ToString().Trim());
    }

    /// <summary>git pull --ff-only - only fast-forwards, so it never silently overwrites unpushed local work.</summary>
    public async Task<(bool Ok, string Log)> Pull()
    {
        var status = await GetStatus();
        if (!status.IsRepo) return (false, "Not connected to a git repo yet - click Connect first.");
        if (!status.HasRemote) return (false, "No GitHub repo set - click Connect first.");
        if (status.Dirty)
        {
            return (false, $"You have {status.Changes} unsaved local change(s) - Push first so Pull doesn't need to guess how to merge them.");
        }

        var pull = await Git("pull", "--ff-only", "origin", status.Branch);
        if (pull.Code != 0 && (pull.Output.Contains("not possible to fast-forward") || pull.Output.Contains("diverg")))
        {
            return (false, pull.Output + "\n\nLocal history has diverged from GitHub - this needs manual resolution.");
        }
        return (pull.Code == 0, pull.Output);
    }
}
