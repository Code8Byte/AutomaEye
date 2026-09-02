using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Jint;

namespace AutomaEye.Services;

public interface ISerialWriter
{
    void Write(string text);
}

public class ScriptStepResult
{
    public string ModelName = "";
    public string Verdict = "";
    public double Confidence;
}

public class ScriptResult
{
    public string Verdict = "OK";
    public double Confidence;
    public double TotalMs;
    public List<ScriptStepResult> Steps = new();
}

/// <summary>
/// Lets the user replace the fixed OK/NG signal with their own JavaScript,
/// run in-process via Jint (pure .NET, no external runtime needed on the
/// factory PC). The script only needs to define onResult(result); helpers
/// exposed to it can drive an Arduino/PLC over serial, hit an HTTP endpoint,
/// or do whatever else that particular line actually needs.
/// </summary>
public class ScriptRunner
{
    private readonly Engine _engine;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public ScriptRunner(string source, ISerialWriter? serial)
    {
        _engine = new Engine();
        _engine.SetValue("serial_write", (string s) => serial?.Write(s));
        _engine.SetValue("http_post", (string url, string body) =>
        {
            var resp = Http.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json")).Result;
            return (int)resp.StatusCode;
        });
        _engine.SetValue("log", (string s) => Console.WriteLine($"[output script] {s}"));
        _engine.SetValue("sleep_ms", (int ms) => Thread.Sleep(ms));

        _engine.Execute(source);
        if (_engine.GetValue("onResult").IsUndefined())
            throw new InvalidOperationException("Script must define a function onResult(result)");
    }

    public void Call(ScriptResult result)
    {
        var obj = new
        {
            verdict = result.Verdict,
            confidence = result.Confidence,
            total_ms = result.TotalMs,
            steps = result.Steps.Select(s => new { model_name = s.ModelName, verdict = s.Verdict, confidence = s.Confidence }).ToArray(),
        };
        _engine.Invoke("onResult", obj);
    }

    /// <summary>Compiles source and calls onResult() once with a synthetic OK result - used by the Output tab's "Test" button.</summary>
    public static void Test(string source)
    {
        var runner = new ScriptRunner(source, serial: null);
        runner.Call(new ScriptResult { Verdict = "OK", Confidence = 0.97, TotalMs = 12.4 });
    }
}
