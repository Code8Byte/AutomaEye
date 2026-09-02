namespace AutomaEye.Models;

/// <summary>Global, cross-project settings - mirrors the reference app's config.yaml (settings.html), persisted as one JSON file at Documents/AutomaEye/config.json.</summary>
public class ArduinoSettings
{
    public string Port { get; set; } = "COM3";
    public int Baud { get; set; } = 9600;
    public string OkSignal { get; set; } = "0";
    public string NgSignal { get; set; } = "1";
    public bool SignalOnOk { get; set; }
}

public class ModelDefaults
{
    public float Confidence { get; set; } = 0.35f;
    public float Iou { get; set; } = 0.45f;
    public int ImgSize { get; set; } = 640;
}

/// <summary>OpenAI-compatible chat completion endpoint - branded "NVIDIA NIM" by the reference app, but any compatible provider/proxy works (the reference's own default base_url is a proxy, not NVIDIA's actual endpoint).</summary>
public class AiAssistantSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
    public string Model { get; set; } = "meta/llama-3.3-70b-instruct";
}

public class SelfLearningSettings
{
    public bool Enabled { get; set; }
    public int RetrainEveryN { get; set; } = 100;
    public double UncertaintyLow { get; set; } = 0.3;
    public double UncertaintyHigh { get; set; } = 0.7;
}

public class AppConfig
{
    public string PythonExe { get; set; } = "python";
    public ModelDefaults Model { get; set; } = new();
    public ArduinoSettings Arduino { get; set; } = new();
    public AiAssistantSettings Ai { get; set; } = new();
    public SelfLearningSettings SelfLearning { get; set; } = new();
    public bool SaveOkImages { get; set; }
}
