using System.Globalization;
using System.Windows;
using AutomaEye.Models;
using AutomaEye.Services;

namespace AutomaEye;

/// <summary>Global settings dialog - mirrors the reference app's settings.html, one "Save" batching every field into config.json.</summary>
public partial class SettingsDialog : System.Windows.Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        var cfg = ConfigService.Current;

        ConfidenceInput.Text = cfg.Model.Confidence.ToString(CultureInfo.InvariantCulture);
        IouInput.Text = cfg.Model.Iou.ToString(CultureInfo.InvariantCulture);
        ImgSizeInput.Text = cfg.Model.ImgSize.ToString(CultureInfo.InvariantCulture);
        PythonExeInput.Text = cfg.PythonExe;

        ArduinoPortInput.Text = cfg.Arduino.Port;
        ArduinoBaudInput.Text = cfg.Arduino.Baud.ToString(CultureInfo.InvariantCulture);
        ArduinoOkInput.Text = cfg.Arduino.OkSignal;
        ArduinoNgInput.Text = cfg.Arduino.NgSignal;
        ArduinoSignalOnOkCheck.IsChecked = cfg.Arduino.SignalOnOk;

        AiBaseUrlInput.Text = cfg.Ai.BaseUrl;
        AiApiKeyInput.Password = cfg.Ai.ApiKey;
        AiModelInput.Text = cfg.Ai.Model;

        SelfLearningEnabledCheck.IsChecked = cfg.SelfLearning.Enabled;
        RetrainEveryNInput.Text = cfg.SelfLearning.RetrainEveryN.ToString(CultureInfo.InvariantCulture);
        UncertaintyLowInput.Text = cfg.SelfLearning.UncertaintyLow.ToString(CultureInfo.InvariantCulture);
        UncertaintyHighInput.Text = cfg.SelfLearning.UncertaintyHigh.ToString(CultureInfo.InvariantCulture);
    }

    private static float ParseF(string s, float fallback) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static double ParseD(string s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int ParseI(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ConfigService.Current;

        cfg.Model.Confidence = ParseF(ConfidenceInput.Text, cfg.Model.Confidence);
        cfg.Model.Iou = ParseF(IouInput.Text, cfg.Model.Iou);
        cfg.Model.ImgSize = ParseI(ImgSizeInput.Text, cfg.Model.ImgSize);
        cfg.PythonExe = string.IsNullOrWhiteSpace(PythonExeInput.Text) ? "python" : PythonExeInput.Text.Trim();

        cfg.Arduino.Port = ArduinoPortInput.Text.Trim();
        cfg.Arduino.Baud = ParseI(ArduinoBaudInput.Text, cfg.Arduino.Baud);
        cfg.Arduino.OkSignal = ArduinoOkInput.Text;
        cfg.Arduino.NgSignal = ArduinoNgInput.Text;
        cfg.Arduino.SignalOnOk = ArduinoSignalOnOkCheck.IsChecked == true;

        cfg.Ai.BaseUrl = AiBaseUrlInput.Text.Trim();
        cfg.Ai.ApiKey = AiApiKeyInput.Password;
        cfg.Ai.Model = AiModelInput.Text.Trim();

        cfg.SelfLearning.Enabled = SelfLearningEnabledCheck.IsChecked == true;
        cfg.SelfLearning.RetrainEveryN = ParseI(RetrainEveryNInput.Text, cfg.SelfLearning.RetrainEveryN);
        cfg.SelfLearning.UncertaintyLow = ParseD(UncertaintyLowInput.Text, cfg.SelfLearning.UncertaintyLow);
        cfg.SelfLearning.UncertaintyHigh = ParseD(UncertaintyHighInput.Text, cfg.SelfLearning.UncertaintyHigh);

        ConfigService.Save(cfg);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
