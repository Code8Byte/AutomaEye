# AutomaEye desktop (C# / WPF)

Replaces the earlier Electron+Python app. Uses OpenCvSharp4 (prebuilt native
OpenCV via NuGet - no manual build) for camera capture and dataset
augmentation, Microsoft.ML.OnnxRuntime for YOLO inference, System.IO.Ports
for Arduino/PLC output, and Jint for the optional custom-output JavaScript.

## Run

```
cd desktop/AutomaEye
dotnet run
```

## Build a distributable exe

```
dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -c Release
```

## Structure

- `Models/` - Project/Model/Workflow/Output data types, JSON-persisted per project.
- `Services/` - ProjectManager, camera capture, ONNX inference, workflow
  executor, output recording, GD&T calibration, GitHub sync (see below), the
  custom-output script runner.
- `MainWindow.xaml(.cs)` - the whole UI: Projects sidebar, and per-project
  Models / Workflow / Output / Run tabs.

## Workflow model

Matches the original app: five optional pipeline stages - Camera,
Positioning, Inference, Output, Misc - each either left empty or assigned a
model, always executed in that fixed order regardless of assignment order.

## GitHub project storage

The local projects folder (`Documents/AutomaEye/projects`) can be connected
to a GitHub repo (Sidebar > "GitHub storage"). Each project becomes a
subfolder in that repo carrying a `.automaeyes` marker file; large files
(dataset images, `.onnx`/`.pt` weights) are tracked via Git LFS. Push/Pull
wrap the system `git` CLI and reuse whatever git credentials are already
configured on the machine - no token is stored by the app itself.
