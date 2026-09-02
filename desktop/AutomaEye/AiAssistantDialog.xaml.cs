using System;
using System.Windows;
using AutomaEye.Models;
using AutomaEye.Services;

namespace AutomaEye;

/// <summary>Report/Analyze/Chat - mirrors the reference app's ai.html exactly (same three tabs, same purpose per tab).</summary>
public partial class AiAssistantDialog : System.Windows.Window
{
    private readonly Project _project;
    private readonly AiAssistantService _ai = new();
    private System.Collections.Generic.List<ChatMessage> _chat = AiAssistantService.NewChat();

    public AiAssistantDialog(Project project)
    {
        InitializeComponent();
        _project = project;
        ReportDatePicker.SelectedDate = DateTime.Today;
        AnalyzeDatePicker.SelectedDate = DateTime.Today;
    }

    private async void GenerateReport_Click(object sender, RoutedEventArgs e)
    {
        var date = ReportDatePicker.SelectedDate ?? DateTime.Today;
        ReportText.Text = "Generating...";
        try
        {
            var summary = DailySummaryService.Aggregate(_project, date);
            ReportText.Text = await _ai.GenerateReportAsync(summary, date);
        }
        catch (Exception ex) { ReportText.Text = ex.Message; }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var date = AnalyzeDatePicker.SelectedDate ?? DateTime.Today;
        AnalyzeText.Text = "Analyzing...";
        try
        {
            var summary = DailySummaryService.Aggregate(_project, date);
            AnalyzeText.Text = await _ai.AnalyzeNgAsync(summary, date);
        }
        catch (Exception ex) { AnalyzeText.Text = ex.Message; }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;

        _chat.Add(new ChatMessage { Role = "user", Content = text });
        ChatInput.Text = "";
        RenderChat();
        ChatSendButton.IsEnabled = false;
        try
        {
            var reply = await _ai.ChatAsync(_chat);
            _chat.Add(new ChatMessage { Role = "assistant", Content = reply });
            RenderChat();
        }
        catch (Exception ex)
        {
            _chat.Add(new ChatMessage { Role = "assistant", Content = $"[error] {ex.Message}" });
            RenderChat();
        }
        finally { ChatSendButton.IsEnabled = true; }
    }

    private void RenderChat()
    {
        var lines = new System.Text.StringBuilder();
        foreach (var m in _chat)
        {
            if (m.Role == "system") continue;
            lines.AppendLine($"[{m.Role}] {m.Content}");
            lines.AppendLine();
        }
        ChatText.Text = lines.ToString();
        ChatScroll.ScrollToBottom();
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _chat = AiAssistantService.NewChat();
        ChatText.Text = "";
    }
}
