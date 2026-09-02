using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutomaEye.Models;

namespace AutomaEye.Services;

/// <summary>
/// Drives an external Label Studio server (python/run_label_studio.py) via
/// its REST API, opened in the system's default browser rather than
/// embedded in-app - the reference app embeds it via an Electron
/// &lt;webview&gt;, but that needs WebView2 in WPF, which is a much bigger
/// dependency for the same annotation workflow: start server, open in
/// browser, annotate, sync YOLO labels back into the dataset.
/// </summary>
public class LabelStudioService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private Process? _server;

    public bool IsRunning => _server is { HasExited: false };

    public void StartServer(string pythonExe)
    {
        if (IsRunning) return;
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "python", "run_label_studio.py");
        _server = Process.Start(new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" start --no-browser",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public void StopServer()
    {
        try { if (IsRunning) _server!.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
        _server = null;
    }

    public static void OpenInBrowser(int? projectId = null)
    {
        var cfg = ConfigService.Current.Annotation;
        var url = projectId is { } id ? $"{cfg.BaseUrl}/projects/{id}/data" : cfg.BaseUrl;
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path)
    {
        var cfg = ConfigService.Current.Annotation;
        var req = new HttpRequestMessage(method, cfg.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(cfg.AccessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Token", cfg.AccessToken);
        return req;
    }

    public async Task<bool> TestAuthAsync()
    {
        using var req = Authed(HttpMethod.Get, "/api/projects?page_size=1");
        using var resp = await Http.SendAsync(req);
        return resp.IsSuccessStatusCode;
    }

    private static string LabelConfigFor(Model model)
    {
        var labels = string.Join("", model.Classes.Select(c => $"<Label value=\"{System.Security.SecurityElement.Escape(c)}\"/>"));
        return model.Type switch
        {
            AIType.Segmentation =>
                $"<View><Image name=\"image\" value=\"$image\"/><PolygonLabels name=\"label\" toName=\"image\">{labels}</PolygonLabels></View>",
            AIType.Classification =>
                $"<View><Image name=\"image\" value=\"$image\"/><Choices name=\"choice\" toName=\"image\">{labels.Replace("<Label", "<Choice").Replace("</Label>", "</Choice>")}</Choices></View>",
            AIType.OCR =>
                "<View><Image name=\"image\" value=\"$image\"/><RectangleLabels name=\"bbox\" toName=\"image\"><Label value=\"text\"/></RectangleLabels>" +
                "<TextArea name=\"transcription\" toName=\"image\" editable=\"true\" perRegion=\"true\"/></View>",
            _ => $"<View><Image name=\"image\" value=\"$image\"/><RectangleLabels name=\"label\" toName=\"image\">{labels}</RectangleLabels></View>",
        };
    }

    /// <summary>Creates a Label Studio project pre-configured for the model's AI type and uploads its train images. Returns the new project id.</summary>
    public async Task<int> AutoSetupProjectAsync(Model model)
    {
        using var createReq = Authed(HttpMethod.Post, "/api/projects");
        createReq.Content = JsonContent.Create(new { title = model.Name, label_config = LabelConfigFor(model) });
        using var createResp = await Http.SendAsync(createReq);
        createResp.EnsureSuccessStatusCode();
        var project = await createResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = project.GetProperty("id").GetInt32();

        var images = DatasetService.ListImages(model, "train");
        if (images.Length > 0)
        {
            using var content = new MultipartFormDataContent();
            foreach (var img in images)
            {
                var bytes = await File.ReadAllBytesAsync(img);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(img.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
                content.Add(part, Path.GetFileName(img), Path.GetFileName(img));
            }
            using var importReq = Authed(HttpMethod.Post, $"/api/projects/{projectId}/import");
            importReq.Content = content;
            using var importResp = await Http.SendAsync(importReq);
            importResp.EnsureSuccessStatusCode();
        }

        return projectId;
    }

    /// <summary>
    /// Downloads the project's YOLO export and drops matching .txt label
    /// files into dataset/labels/train. Label Studio typically renames
    /// uploaded images with an "&lt;8-hex-chars&gt;-original-name.ext" prefix, so
    /// this strips that prefix to recover the original stem; anything that
    /// still doesn't match a known dataset image is written under its
    /// exported name instead of being dropped, so nothing is silently lost.
    /// </summary>
    public async Task<(int matched, int unmatched)> SyncYoloLabelsAsync(Model model, int projectId)
    {
        using var req = Authed(HttpMethod.Get, $"/api/projects/{projectId}/export?exportType=YOLO&download_all_tasks=true");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var tmpZip = Path.GetTempFileName();
        await using (var fs = File.Create(tmpZip)) await resp.Content.CopyToAsync(fs);

        var tmpDir = Path.Combine(Path.GetTempPath(), "automaeye_ls_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(tmpZip, tmpDir);
        File.Delete(tmpZip);

        var labelsDir = Directory.GetDirectories(tmpDir, "labels", SearchOption.AllDirectories).FirstOrDefault()
                        ?? tmpDir;
        var knownStems = DatasetService.ListImages(model, "train").Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var destDir = Path.Combine(model.Dir, "dataset", "labels", "train");
        Directory.CreateDirectory(destDir);

        int matched = 0, unmatched = 0;
        var hashPrefix = new Regex(@"^[0-9a-fA-F]{8}-");
        foreach (var file in Directory.GetFiles(labelsDir, "*.txt"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var strippedStem = hashPrefix.Replace(stem, "");
            var targetStem = knownStems.Contains(strippedStem) ? strippedStem : stem;
            if (knownStems.Contains(strippedStem)) matched++; else unmatched++;
            File.Copy(file, Path.Combine(destDir, targetStem + ".txt"), overwrite: true);
        }

        try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
        return (matched, unmatched);
    }
}
