using AForge.Video;
using AForge.Video.DirectShow;
using Accord.Video.FFMPEG;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WpfAppBrioRecorder
{
    public partial class RecorderWindow : Window
    {
        private readonly ObservableCollection<RecordedVideoItem> recordedVideos = new ObservableCollection<RecordedVideoItem>();
        private readonly object syncRoot = new object();
        private readonly string recordingsFolder;
        private readonly DispatcherTimer recordingTimer;
        private readonly RecordingQualityPreset[] recordingQualityPresets;
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private VideoFileWriter videoWriter;
        private bool isRecording;
        private string currentRecordingFilePath;
        private DateTime? recordingStartedAtUtc;
        private int recordingBitrate;
        private int recordingWidth;
        private int recordingHeight;
        private int recordingFrameRate;

        public RecorderWindow()
        {
            InitializeComponent();
            recordingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BrioRecorder");
            recordingTimer = new DispatcherTimer();
            recordingTimer.Interval = TimeSpan.FromSeconds(1);
            recordingTimer.Tick += RecordingTimer_Tick;
            recordingQualityPresets = new[]
            {
                new RecordingQualityPreset("Low", 640, 480, 15, 1500000),
                new RecordingQualityPreset("Medium", 1280, 720, 20, 4000000),
                new RecordingQualityPreset("High", 1920, 1080, 30, 8000000)
            };
            Directory.CreateDirectory(recordingsFolder);
            RecordingsListBox.ItemsSource = recordedVideos;
            QualityComboBox.ItemsSource = recordingQualityPresets;
            QualityComboBox.SelectedItem = recordingQualityPresets[1];
            RecordingFolderTextBlock.Text = recordingsFolder;
            SelectedFileTextBlock.Text = "Select a recording to play.";
            RecordingTimeTextBlock.Text = "00:00:00";
            UpdateQualityInformation();
            Loaded += RecorderWindow_Loaded;
            Closing += RecorderWindow_Closing;
        }

        private void RecorderWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRecordedVideos();
            RefreshCameras();
        }

        private void RecorderWindow_Closing(object sender, CancelEventArgs e)
        {
            StopRecording();
            StopPreview();
        }

        private void RefreshCamerasButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCameras();
        }

        private void CameraComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded || isRecording)
            {
                return;
            }

            StartPreviewForSelectedCamera();
        }

        private void QualityComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateQualityInformation();

            if (!IsLoaded || isRecording)
            {
                return;
            }

            if (CameraComboBox.SelectedItem != null)
            {
                StartPreviewForSelectedCamera();
            }
        }

        private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            StartRecording();
        }

        private void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(recordingsFolder);
                Process.Start(new ProcessStartInfo(recordingsFolder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateUiState("Could not open folder: " + ex.Message);
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var helpFilePath = ResolveHelpFilePath();
                if (!File.Exists(helpFilePath))
                {
                    UpdateUiState("Help file not found: " + helpFilePath);
                    return;
                }

                Process.Start(new ProcessStartInfo(helpFilePath) { UseShellExecute = true });
                UpdateUiState("Opened Help.txt");
            }
            catch (Exception ex)
            {
                UpdateUiState("Could not open help file: " + ex.Message);
            }
        }

        private static string ResolveHelpFilePath()
        {
            var baseDirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Help.txt");
            if (File.Exists(baseDirectoryPath))
            {
                return baseDirectoryPath;
            }

            var projectDirectoryPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\Help.txt"));
            if (File.Exists(projectDirectoryPath))
            {
                return projectDirectoryPath;
            }

            return baseDirectoryPath;
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            UpdateRecordingTimeDisplay();
        }

        private void RecordingsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selectedFile = RecordingsListBox.SelectedItem as RecordedVideoItem;
            SelectedFileTextBlock.Text = selectedFile != null ? selectedFile.FilePath : "Select a recording to play.";
            UpdateUiState(StatusTextBlock.Text);
        }

        private void RecordingsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PlaySelectedRecording();
        }

        private void PlaySelectedButton_Click(object sender, RoutedEventArgs e)
        {
            PlaySelectedRecording();
        }

        private void RefreshCameras()
        {
            StopPreview();
            CameraComboBox.ItemsSource = null;

            try
            {
                videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            }
            catch (Exception ex)
            {
                UpdateUiState("Could not enumerate cameras: " + ex.Message);
                return;
            }

            var cameraItems = videoDevices.Cast<FilterInfo>()
                .Select(device => new CameraDeviceItem(device.Name, device.MonikerString))
                .ToList();

            CameraComboBox.ItemsSource = cameraItems;

            if (cameraItems.Count == 0)
            {
                PreviewImage.Source = null;
                PreviewPlaceholderTextBlock.Visibility = Visibility.Visible;
                UpdateUiState("No camera found. Connect the Logitech Brio 100 and click Refresh.");
                return;
            }

            var preferredCamera = cameraItems.FirstOrDefault(item => item.Name.IndexOf("Logitech Brio 100", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? cameraItems.FirstOrDefault(item => item.Name.IndexOf("Brio", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? cameraItems.FirstOrDefault(item => item.Name.IndexOf("Logitech", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? cameraItems.First();

            CameraComboBox.SelectedItem = preferredCamera;
            UpdateUiState("Camera ready: " + preferredCamera.Name);
        }

        private void StartPreviewForSelectedCamera()
        {
            var selectedCamera = CameraComboBox.SelectedItem as CameraDeviceItem;
            if (selectedCamera == null)
            {
                PreviewImage.Source = null;
                PreviewPlaceholderTextBlock.Visibility = Visibility.Visible;
                UpdateUiState("Select a camera to start preview.");
                return;
            }

            StopPreview();

            try
            {
                videoSource = new VideoCaptureDevice(selectedCamera.MonikerString);
                var capability = SelectVideoCapability(videoSource, GetSelectedQualityPreset());
                if (capability != null)
                {
                    videoSource.VideoResolution = capability;
                }

                videoSource.NewFrame += VideoSource_NewFrame;
                videoSource.Start();
                UpdateUiState("Preview started: " + selectedCamera.Name);
            }
            catch (Exception ex)
            {
                PreviewImage.Source = null;
                PreviewPlaceholderTextBlock.Visibility = Visibility.Visible;
                UpdateUiState("Could not start preview: " + ex.Message);
            }
        }

        private void StopPreview()
        {
            var source = videoSource;
            videoSource = null;

            if (source != null)
            {
                source.NewFrame -= VideoSource_NewFrame;
                if (source.IsRunning)
                {
                    source.SignalToStop();
                    source.WaitForStop();
                }
            }

            PreviewImage.Source = null;
            PreviewPlaceholderTextBlock.Visibility = Visibility.Visible;
        }

        private void StartRecording()
        {
            if (isRecording)
            {
                return;
            }

            if (CameraComboBox.SelectedItem == null)
            {
                UpdateUiState("Select a camera before recording.");
                return;
            }

            if (videoSource == null || !videoSource.IsRunning)
            {
                StartPreviewForSelectedCamera();
            }

            if (videoSource == null || !videoSource.IsRunning)
            {
                UpdateUiState("Preview is not running.");
                return;
            }

            var selectedQualityPreset = GetSelectedQualityPreset();
            var capability = videoSource.VideoResolution ?? SelectVideoCapability(videoSource, selectedQualityPreset);
            recordingWidth = capability != null ? capability.FrameSize.Width : selectedQualityPreset.Width;
            recordingHeight = capability != null ? capability.FrameSize.Height : selectedQualityPreset.Height;
            recordingFrameRate = capability != null && capability.AverageFrameRate > 0 ? capability.AverageFrameRate : selectedQualityPreset.FrameRate;
            recordingFrameRate = Math.Max(5, Math.Min(recordingFrameRate, selectedQualityPreset.FrameRate));
            recordingBitrate = selectedQualityPreset.Bitrate;
            currentRecordingFilePath = Path.Combine(recordingsFolder, string.Format("BrioRecording_{0:yyyyMMdd_HHmmss}.avi", DateTime.Now));

            try
            {
                lock (syncRoot)
                {
                    videoWriter = new VideoFileWriter();
                    videoWriter.Open(currentRecordingFilePath, recordingWidth, recordingHeight, recordingFrameRate, VideoCodec.MPEG4, recordingBitrate);
                    isRecording = true;
                }

                recordingStartedAtUtc = DateTime.UtcNow;
                UpdateRecordingTimeDisplay();
                recordingTimer.Start();

                UpdateUiState("Recording to " + Path.GetFileName(currentRecordingFilePath));
            }
            catch (Exception ex)
            {
                lock (syncRoot)
                {
                    isRecording = false;
                    if (videoWriter != null)
                    {
                        videoWriter.Dispose();
                        videoWriter = null;
                    }
                }

                recordingStartedAtUtc = null;
                recordingTimer.Stop();
                UpdateRecordingTimeDisplay();
                currentRecordingFilePath = null;
                UpdateUiState("Could not start recording: " + ex.Message);
            }
        }

        private void StopRecording()
        {
            string savedFilePath = null;

            lock (syncRoot)
            {
                if (!isRecording && videoWriter == null)
                {
                    return;
                }

                isRecording = false;
                savedFilePath = currentRecordingFilePath;
                currentRecordingFilePath = null;

                if (videoWriter != null)
                {
                    try
                    {
                        videoWriter.Close();
                    }
                    finally
                    {
                        videoWriter.Dispose();
                        videoWriter = null;
                    }
                }
            }

            recordingTimer.Stop();
            recordingStartedAtUtc = null;
            UpdateRecordingTimeDisplay();
            LoadRecordedVideos();
            SelectRecordedFile(savedFilePath);
            UpdateUiState(savedFilePath != null ? "Recording saved: " + Path.GetFileName(savedFilePath) : "Recording stopped.");
        }

        private void PlaySelectedRecording()
        {
            var selectedFile = RecordingsListBox.SelectedItem as RecordedVideoItem;
            if (selectedFile == null)
            {
                UpdateUiState("Select a recorded file to play.");
                return;
            }

            if (!File.Exists(selectedFile.FilePath))
            {
                LoadRecordedVideos();
                UpdateUiState("The selected file was not found.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(selectedFile.FilePath) { UseShellExecute = true });
                UpdateUiState("Opened " + selectedFile.DisplayName);
            }
            catch (Exception ex)
            {
                UpdateUiState("Could not play the selected file: " + ex.Message);
            }
        }

        private void LoadRecordedVideos()
        {
            Directory.CreateDirectory(recordingsFolder);

            var files = new DirectoryInfo(recordingsFolder)
                .GetFiles("*.*")
                .Where(file => string.Equals(file.Extension, ".avi", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.Extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(file.Extension, ".wmv", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Select(file => new RecordedVideoItem(file.FullName, string.Format("{0}    {1:yyyy-MM-dd HH:mm:ss}", file.Name, file.CreationTime)))
                .ToList();

            recordedVideos.Clear();
            foreach (var file in files)
            {
                recordedVideos.Add(file);
            }

            if (recordedVideos.Count == 0)
            {
                SelectedFileTextBlock.Text = "No recordings yet.";
            }
        }

        private void SelectRecordedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            var recordedFile = recordedVideos.FirstOrDefault(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (recordedFile != null)
            {
                RecordingsListBox.SelectedItem = recordedFile;
                RecordingsListBox.ScrollIntoView(recordedFile);
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            Bitmap previewBitmap = null;
            Bitmap recordingBitmap = null;

            try
            {
                previewBitmap = (Bitmap)eventArgs.Frame.Clone();
                var previewSource = CreateBitmapSource(previewBitmap);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PreviewImage.Source = previewSource;
                    PreviewPlaceholderTextBlock.Visibility = Visibility.Collapsed;
                    if (StartRecordingButton.IsEnabled != (!isRecording && videoSource != null && videoSource.IsRunning))
                    {
                        UpdateUiState(StatusTextBlock.Text);
                    }
                }));

                lock (syncRoot)
                {
                    if (isRecording && videoWriter != null)
                    {
                        recordingBitmap = ResizeFrame((Bitmap)eventArgs.Frame.Clone(), recordingWidth, recordingHeight);
                        videoWriter.WriteVideoFrame(recordingBitmap);
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateUiState("Frame processing error: " + ex.Message)));
            }
            finally
            {
                if (previewBitmap != null)
                {
                    previewBitmap.Dispose();
                }

                if (recordingBitmap != null)
                {
                    recordingBitmap.Dispose();
                }
            }
        }

        private void UpdateUiState(string statusText)
        {
            StatusTextBlock.Text = statusText;
            StartRecordingButton.IsEnabled = !isRecording && videoSource != null && videoSource.IsRunning;
            StopRecordingButton.IsEnabled = isRecording;
            RefreshCamerasButton.IsEnabled = !isRecording;
            CameraComboBox.IsEnabled = !isRecording && CameraComboBox.Items.Count > 0;
            QualityComboBox.IsEnabled = !isRecording && QualityComboBox.Items.Count > 0;
            PlaySelectedButton.IsEnabled = RecordingsListBox.SelectedItem != null;
            OpenFolderButton.IsEnabled = true;
            HelpButton.IsEnabled = true;
        }

        private RecordingQualityPreset GetSelectedQualityPreset()
        {
            return QualityComboBox.SelectedItem as RecordingQualityPreset ?? recordingQualityPresets[1];
        }

        private void UpdateQualityInformation()
        {
            var selectedQualityPreset = GetSelectedQualityPreset();
            RecordingQualityTextBlock.Text = string.Format("{0} ({1}x{2}, {3} FPS, {4:0.0} Mbps)", selectedQualityPreset.Name, selectedQualityPreset.Width, selectedQualityPreset.Height, selectedQualityPreset.FrameRate, selectedQualityPreset.Bitrate / 1000000d);
            EstimatedSizeTextBlock.Text = string.Format("Approx. {0:0.0} MB per 1 minute of recording.", CalculateEstimatedMegabytesPerMinute(selectedQualityPreset.Bitrate));
        }

        private void UpdateRecordingTimeDisplay()
        {
            var elapsed = recordingStartedAtUtc.HasValue ? DateTime.UtcNow - recordingStartedAtUtc.Value : TimeSpan.Zero;
            RecordingTimeTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss");
        }

        private static double CalculateEstimatedMegabytesPerMinute(int bitrate)
        {
            return bitrate * 60d / 8d / 1024d / 1024d;
        }

        private static VideoCapabilities SelectVideoCapability(VideoCaptureDevice source, RecordingQualityPreset selectedQualityPreset)
        {
            if (source == null || source.VideoCapabilities == null || source.VideoCapabilities.Length == 0)
            {
                return null;
            }

            if (selectedQualityPreset != null)
            {
                var exactMatch = source.VideoCapabilities
                    .Where(capability => capability.FrameSize.Width == selectedQualityPreset.Width && capability.FrameSize.Height == selectedQualityPreset.Height)
                    .OrderByDescending(capability => capability.AverageFrameRate)
                    .FirstOrDefault();
                if (exactMatch != null)
                {
                    return exactMatch;
                }

                var bestWithinPreset = source.VideoCapabilities
                    .Where(capability => capability.FrameSize.Width <= selectedQualityPreset.Width && capability.FrameSize.Height <= selectedQualityPreset.Height)
                    .OrderByDescending(capability => capability.FrameSize.Width * capability.FrameSize.Height)
                    .ThenByDescending(capability => capability.AverageFrameRate)
                    .FirstOrDefault();
                if (bestWithinPreset != null)
                {
                    return bestWithinPreset;
                }

                var closestMatch = source.VideoCapabilities
                    .OrderBy(capability => Math.Abs(capability.FrameSize.Width - selectedQualityPreset.Width) + Math.Abs(capability.FrameSize.Height - selectedQualityPreset.Height))
                    .ThenByDescending(capability => capability.AverageFrameRate)
                    .FirstOrDefault();
                if (closestMatch != null)
                {
                    return closestMatch;
                }
            }

            return source.VideoCapabilities.OrderByDescending(capability => capability.FrameSize.Width * capability.FrameSize.Height).FirstOrDefault();
        }

        private static Bitmap ResizeFrame(Bitmap source, int width, int height)
        {
            var resizedBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(resizedBitmap))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            source.Dispose();
            return resizedBitmap;
        }

        private static BitmapSource CreateBitmapSource(Bitmap bitmap)
        {
            var bitmapHandle = bitmap.GetHbitmap();
            try
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                return bitmapSource;
            }
            finally
            {
                DeleteObject(bitmapHandle);
            }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private sealed class CameraDeviceItem
        {
            public CameraDeviceItem(string name, string monikerString)
            {
                Name = name;
                MonikerString = monikerString;
            }

            public string Name { get; private set; }

            public string MonikerString { get; private set; }
        }

        private sealed class RecordingQualityPreset
        {
            public RecordingQualityPreset(string name, int width, int height, int frameRate, int bitrate)
            {
                Name = name;
                Width = width;
                Height = height;
                FrameRate = frameRate;
                Bitrate = bitrate;
                DisplayName = string.Format("{0} ({1}x{2})", name, width, height);
            }

            public string Name { get; private set; }

            public int Width { get; private set; }

            public int Height { get; private set; }

            public int FrameRate { get; private set; }

            public int Bitrate { get; private set; }

            public string DisplayName { get; private set; }
        }

        private sealed class RecordedVideoItem
        {
            public RecordedVideoItem(string filePath, string displayName)
            {
                FilePath = filePath;
                DisplayName = displayName;
            }

            public string FilePath { get; private set; }

            public string DisplayName { get; private set; }
        }
    }
}
