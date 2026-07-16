# WpfAppBrioRecorder

WPF desktop application for recording webcam video on Windows 10/11, designed for devices such as the Logitech Brio 100.

## Creator

Efim Zabarsky

## Features

- Live camera preview
- Camera selection with Logitech Brio 100 preference
- Start and stop recording
- Quality selector: Low, Medium, High
- **Recording mode selector:**
  - **Regular** — records until manually stopped
  - **Loop** — records in rolling segments, keeps only the last 1–12 hours
- Live recording timer
- Estimated size display:
  - Regular mode: approximate MB per minute
  - Loop mode: approximate total retained size for the selected hour window
- Destination folder selector — change and persist the save location
- Recorded files list showing each file's name, date, and full path
- **Select All** checkbox for bulk selection
- **Play Selected** — opens the file in the default Windows media player
- **Delete Selected** — removes the currently selected recording
- **Delete Checked** — removes all checked recordings with confirmation
- Open Folder — opens the folder of the selected file, or the current destination folder
- Built-in help file

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
& 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe' BrioRecorder.csproj /restore /p:Configuration=Debug
```

## Run

Start the application from Visual Studio or run:

```text
bin\Debug\BrioRecorder.exe
```

## How to use

1. Connect your webcam.
2. Start the application.
3. Select a camera.
4. Select the recording quality.
5. Select the recording mode:
   - **Regular** — one continuous file until stopped.
   - **Loop** — rolling segments; choose how many hours to retain.
6. If using Loop mode, choose the retention period (1–12 hours).
7. Wait for the preview to appear.
8. Click `Start Recording`.
9. Click `Stop Recording` when finished.
10. Select a file from the Recorded Files list.
11. Click `Play Selected` to open it, or `Delete Selected` to remove it.
12. Use checkboxes and `Delete Checked` for bulk deletion.

## Recording quality presets

| Preset | Resolution | FPS | Bitrate | ~Size/min |
|--------|-----------|-----|---------|-----------|
| Low    | 640×480   | 15  | 1.5 Mbps | ~10.7 MB |
| Medium | 1280×720  | 20  | 4.0 Mbps | ~28.6 MB |
| High   | 1920×1080 | 30  | 8.0 Mbps | ~57.2 MB |

### Estimated loop storage (12 hours)

| Preset | ~Total size |
|--------|------------|
| Low    | ~7.5 GB    |
| Medium | ~20.1 GB   |
| High   | ~40.2 GB   |

## Recording modes

### Regular

Records a single file until `Stop Recording` is clicked. The file is saved to the selected destination folder.

### Loop

Records in 10-minute segments. After each segment, files older than the selected retention window are automatically deleted. Only the most recent N hours of footage are kept on disk at any time. Useful for continuous monitoring where only recent footage matters.

## Output folder

By default, recordings are saved to:

```text
%USERPROFILE%\Videos\BrioRecorder
```

The folder can be changed at any time using the `Change Folder` button. The selection is persisted across sessions. Recorded file paths are tracked individually so the list remains accurate even after folder changes.

## Notes

- Recordings are saved as `.avi` files.
- The application targets `x86` for compatibility with the video encoding library.
- Loop mode cleanup only removes files from the active recordings folder.
- If the help file is not beside the executable, the app also resolves it from the project folder.

## Troubleshooting

### No camera found

- Reconnect the webcam
- Click `Refresh`
- Make sure another application is not using the camera

### Preview does not start

- Confirm a camera is selected
- Close other applications that may be using the webcam

### Recording does not start

- Make sure the preview is running
- Try a lower quality preset if the selected mode is not well supported by the camera

### Loop recording does not delete old files

- Confirm Loop mode is selected before starting recording
- Make sure the destination folder is writable
