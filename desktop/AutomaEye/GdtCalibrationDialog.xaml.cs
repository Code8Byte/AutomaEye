using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AutomaEye.Services;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace AutomaEye;

/// <summary>
/// Two-click manual calibration for GD&amp;T Measurement, used when there's no
/// physical measurement sensor: click two points a known real-world distance
/// apart, and the resulting pixels-per-mm ratio is what WorkflowExecutor uses
/// to convert a detection's bounding box into millimetres.
/// </summary>
public partial class GdtCalibrationDialog : System.Windows.Window
{
    private readonly int _cameraIndex;
    private System.Windows.Point? _p1, _p2;
    private double _imageScale = 1; // displayed-pixels -> source-frame-pixels

    /// <summary>Set when the user confirms - millimetres-per-pixel (matches the reference app's addonConfig.gdt.mmPerPixel).</summary>
    public double? ResultMmPerPixel { get; private set; }

    public GdtCalibrationDialog(int cameraIndex)
    {
        InitializeComponent();
        _cameraIndex = cameraIndex;
        Loaded += (_, _) => Capture();
        DistanceInput.TextChanged += (_, _) => UpdateConfirmEnabled();
    }

    private void Capture()
    {
        try
        {
            using var cam = new CameraService(_cameraIndex, 1280, 720, 30);
            using var frame = cam.Read();
            FrameImage.Source = frame.ToBitmapSource();
            _imageScale = frame.Cols / FrameImage.ActualWidth > 0 ? (double)frame.Cols / Math.Max(FrameImage.ActualWidth, 1) : 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not capture from camera {_cameraIndex}: {ex.Message}", "AutomaEye");
        }
        _p1 = _p2 = null;
        OverlayCanvas.Children.Clear();
        HintText.Text = "Click the first point.";
        UpdateConfirmEnabled();
    }

    private void Recapture_Click(object sender, RoutedEventArgs e) => Capture();

    private void FrameImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        System.Windows.Point pos = e.GetPosition(FrameImage);
        if (_p1 == null)
        {
            _p1 = pos;
            DrawDot(pos, Brushes.LimeGreen);
            HintText.Text = "Click the second point.";
        }
        else if (_p2 == null)
        {
            _p2 = pos;
            DrawDot(pos, Brushes.OrangeRed);
            DrawLine(_p1.Value, pos);
            HintText.Text = "Enter the real-world distance between the two points, then save.";
        }
        else
        {
            // Third click starts over.
            OverlayCanvas.Children.Clear();
            _p1 = pos;
            _p2 = null;
            DrawDot(pos, Brushes.LimeGreen);
            HintText.Text = "Click the second point.";
        }
        UpdateConfirmEnabled();
    }

    private void DrawDot(System.Windows.Point p, Brush color)
    {
        var dot = new Ellipse { Width = 10, Height = 10, Fill = color };
        Canvas.SetLeft(dot, p.X - 5);
        Canvas.SetTop(dot, p.Y - 5);
        OverlayCanvas.Children.Add(dot);
    }

    private void DrawLine(System.Windows.Point a, System.Windows.Point b)
    {
        OverlayCanvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Brushes.Yellow, StrokeThickness = 2 });
    }

    private void UpdateConfirmEnabled()
    {
        ConfirmButton.IsEnabled = _p1.HasValue && _p2.HasValue
            && double.TryParse(DistanceInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_p1 == null || _p2 == null) return;
        if (!double.TryParse(DistanceInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || mm <= 0)
        {
            MessageBox.Show("Enter a positive distance in mm.", "AutomaEye");
            return;
        }

        // Displayed-image points -> source-frame pixel distance -> px/mm.
        var dx = (_p2.Value.X - _p1.Value.X) * _imageScale;
        var dy = (_p2.Value.Y - _p1.Value.Y) * _imageScale;
        var pixelDistance = Math.Sqrt(dx * dx + dy * dy);

        ResultMmPerPixel = mm / pixelDistance;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
