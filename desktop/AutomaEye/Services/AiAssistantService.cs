using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AutomaEye.Models;

namespace AutomaEye.Services;

public class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

/// <summary>
/// OpenAI-compatible chat-completions client - branded "NVIDIA NIM" in the
/// reference app, but it's a generic client against whatever base_url is
/// configured (the reference's own default is a proxy, not NVIDIA's actual
/// endpoint - "NIM" there is marketing, not a special protocol). Backs the
/// Report/Analyze/Chat tabs.
/// </summary>
public class AiAssistantService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string ReportSystemPrompt =
        "Anda adalah asisten quality control pabrik. Buat laporan harian Bahasa Indonesia dalam 5 bagian: " +
        "Ringkasan, Statistik (total/OK/NG/rasio), Cycle Time, Analisis Step Bermasalah, Rekomendasi. " +
        "JANGAN mengarang angka - gunakan hanya data yang diberikan.";

    private const string AnalyzeSystemPrompt =
        "Anda adalah engineer Six Sigma. Lakukan root cause analysis pada data NG yang diberikan. " +
        "Jawab dalam Bahasa Indonesia, maksimal 400 kata, fokus pada step mana yang paling sering NG dan kemungkinan penyebabnya.";

    private const string ChatSystemPrompt =
        "You are an expert quality control engineer for a YOLOv11-based visual inspection system. " +
        "Answer in Bahasa Indonesia unless asked otherwise. Be concise and technical.";

    public static List<ChatMessage> NewChat() => new() { new ChatMessage { Role = "system", Content = ChatSystemPrompt } };

    public async Task<string> GenerateReportAsync(DailySummary summary, DateTime date)
    {
        var data = $"Tanggal: {date:yyyy-MM-dd}\nTotal: {summary.Total}\nOK: {summary.Ok}\nNG: {summary.Ng}\n" +
                   $"Rata-rata cycle time: {summary.AvgCycleMs:F1} ms\nNG per step: " +
                   string.Join(", ", summary.NgByStep.Select(kv => $"{kv.Key}={kv.Value}"));
        return await CompleteAsync(ReportSystemPrompt, data);
    }

    public async Task<string> AnalyzeNgAsync(DailySummary summary, DateTime date)
    {
        var data = $"Tanggal: {date:yyyy-MM-dd}\nTotal NG: {summary.Ng} dari {summary.Total}\nNG per step: " +
                   string.Join(", ", summary.NgByStep.Select(kv => $"{kv.Key}={kv.Value}"));
        return await CompleteAsync(AnalyzeSystemPrompt, data);
    }

    public async Task<string> ChatAsync(List<ChatMessage> messages)
    {
        var cfg = ConfigService.Current.Ai;
        return await SendAsync(cfg, messages);
    }

    private async Task<string> CompleteAsync(string systemPrompt, string userContent)
    {
        var cfg = ConfigService.Current.Ai;
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userContent },
        };
        return await SendAsync(cfg, messages);
    }

    private static async Task<string> SendAsync(AiAssistantSettings cfg, List<ChatMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new InvalidOperationException("No AI Assistant API key configured - set one in Settings.");

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.BaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        req.Content = JsonContent.Create(new
        {
            model = cfg.Model,
            messages,
            temperature = 0.3,
            max_tokens = 1200,
            stream = false,
        });

        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI Assistant request failed ({(int)resp.StatusCode}): {body}");

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
