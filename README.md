# WpfAppBrioRecorder

WPF desktop application for recording webcam video on Windows 10/11, designed for devices such as the Logitech Brio 100.

## Creator

Efim Zabarsky

## Features

- Live camera preview
- Camera selection with Logitech Brio 100 preference
- Start and stop recording
- Quality selector:
  - Low
  - Medium
  - High
- Live recording timer while recording
- Estimated file size in MB per 1 minute based on selected quality
- Recorded files list
- Open recordings folder
- Play selected recording with the default Windows player
- Built-in help file support

## Technology

- .NET Framework 4.8
- WPF
- AForge.Video.DirectShow
- Accord.Video.FFMPEG

## Requirements

- Windows 10 or Windows 11
- Visual Studio 2022 or compatible MSBuild tools
- Webcam connected to the PC

## Build

Open the project in Visual Studio and build the solution.

You can also build with MSBuild:

```powershell
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' WpfAppBrioRecorder.csproj /restore /p:Configuration=Debug
```

## Run

Start the application from Visual Studio or run:

```text
bin\Debug\WpfAppBrioRecorder.exe
```

## How to use

1. Connect your webcam.
2. Start the application.
3. Select a camera.
4. Select the recording quality.
5. Wait for the preview to appear.
6. Click `Start Recording`.
7. Click `Stop Recording` when finished.
8. Select a file from the recordings list.
9. Click `Play Selected` to open it.

## Recording quality presets

- Low: 640x480, 15 FPS, 1.5 Mbps
- Medium: 1280x720, 20 FPS, 4.0 Mbps
- High: 1920x1080, 30 FPS, 8.0 Mbps

Approximate file sizes per minute:

- Low: about 10.7 MB/min
- Medium: about 28.6 MB/min
- High: about 57.2 MB/min

## Output folder

Recordings are saved to:

```text
%USERPROFILE%\Videos\BrioRecorder
```

## Notes

- Recordings are currently saved as `.avi` files.
- The application targets `x86` for compatibility with the video encoding library.
- If the help file is not beside the executable, the app can also resolve it from the project folder.

## Troubleshooting

### No camera found

- Reconnect the webcam
- Click `Refresh`
- Make sure another application is not using the camera

### Preview does not start

- Confirm a camera is selected
- Close other applications that may be using the webcam

### Recording does not start

- Make sure preview is running
- Try a lower quality preset if the selected mode is not supported well by the camera
