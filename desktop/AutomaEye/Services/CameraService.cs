using System;
using OpenCvSharp;

namespace AutomaEye.Services;

/// <summary>Camera capture via OpenCvSharp's VideoCapture (DirectShow backend on Windows).</summary>
public class CameraService : IDisposable
{
    private readonly VideoCapture _capture;

    public CameraService(int index, int width, int height, int fps)
    {
        _capture = new VideoCapture(index);
        if (!_capture.IsOpened()) throw new InvalidOperationException($"Could not open camera {index}");
        _capture.Set(VideoCaptureProperties.FrameWidth, width);
        _capture.Set(VideoCaptureProperties.FrameHeight, height);
        _capture.Set(VideoCaptureProperties.Fps, fps);
    }

    public static string[] ListDevices()
    {
        // OpenCvSharp has no device enumeration API; probe the first few
        // indices instead, which is what most single/dual-camera factory
        // rigs need. Labeled generically since DirectShow doesn't expose
        // friendly names through this API either.
        var found = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 4; i++)
        {
            using var cap = new VideoCapture(i);
            if (cap.IsOpened()) found.Add($"Camera {i}");
        }
        return found.ToArray();
    }

    public Mat Read()
    {
        var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty())
        {
            frame.Dispose();
            throw new InvalidOperationException("Failed to read frame from camera");
        }
        return frame;
    }

    public void Dispose() => _capture.Dispose();
}
