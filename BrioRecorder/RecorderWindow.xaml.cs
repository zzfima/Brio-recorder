using Accord.Video.FFMPEG;
using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Collections.Generic;
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
using Forms = System.Windows.Forms;

namespace BrioRecorder
{
    public partial class RecorderWindow : Window
    {
        private readonly ObservableCollection<RecordedVideoItem> recordedVideos = new ObservableCollection<RecordedVideoItem>();
        private readonly object syncRoot = new object();
        private string recordingsFolder;
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
        private bool isUpdatingSelectAllCheckBox;
        private readonly int[] loopRecordingHoursOptions;
        private DateTime? currentLoopSegmentStartedAtUtc;
        private static readonly TimeSpan LoopRecordingSegmentDuration = TimeSpan.FromMinutes(10);

        public RecorderWindow()
        {
            InitializeComponent();
            recordingsFolder = ResolveInitialRecordingsFolder();
            recordingTimer = new DispatcherTimer();
            recordingTimer.Interval = TimeSpan.FromSeconds(1);
            recordingTimer.Tick += RecordingTimer_Tick;
            recordingQualityPresets = new[]
            {
                new RecordingQualityPreset("Low", 640, 480, 15, 1500000),
                new RecordingQualityPreset("Medium", 1280, 720, 20, 4000000),
                new RecordingQualityPreset("High", 1920, 1080, 30, 8000000)
            };
            EnsureRecordingFolderExists();
            RecordingsListBox.ItemsSource = recordedVideos;
            QualityComboBox.ItemsSource = recordingQualityPresets;
            QualityComboBox.SelectedItem = recordingQualityPresets[1];
            loopRecordingHoursOptions = Enumerable.Range(1, 12).ToArray();
            LoopHoursComboBox.ItemsSource = loopRecordingHoursOptions;
            LoopHoursComboBox.SelectedItem = loopRecordingHoursOptions[11];
            UpdateRecordingFolderDisplay();
            SelectedFileTextBlock.Text = "Select a recording to play.";
            RecordingTimeTextBlock.Text = "00:00:00";
            UpdateRecordingModeInformation();
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
            if (!IsLoaded)
            {
                return;
            }

            UpdateQualityInformation();

            if (isRecording)
            {
                return;
            }

            if (CameraComboBox.SelectedItem != null)
            {
                StartPreviewForSelectedCamera();
            }
        }

        private void RecordingModeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateRecordingModeInformation();
            UpdateQualityInformation();
            UpdateUiState(StatusTextBlock.Text);
        }

        private void LoopHoursComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateRecordingModeInformation();
            UpdateQualityInformation();
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
                var selectedFile = RecordingsListBox.SelectedItem as RecordedVideoItem;
                var folderToOpen = selectedFile != null && File.Exists(selectedFile.FilePath)
                    ? Path.GetDirectoryName(selectedFile.FilePath)
                    : recordingsFolder;

                if (string.IsNullOrWhiteSpace(folderToOpen))
                {
                    folderToOpen = recordingsFolder;
                }

                Directory.CreateDirectory(folderToOpen);
                Process.Start(new ProcessStartInfo(folderToOpen) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateUiState("Could not open folder: " + ex.Message);
            }
        }

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording)
            {
                UpdateUiState("Stop recording before changing the destination folder.");
                return;
            }

            using (var folderBrowserDialog = new Forms.FolderBrowserDialog())
            {
                folderBrowserDialog.Description = "Select a folder for recorded videos.";
                folderBrowserDialog.ShowNewFolderButton = true;
                folderBrowserDialog.SelectedPath = Directory.Exists(recordingsFolder) ? recordingsFolder : GetDefaultRecordingsFolder();

                if (folderBrowserDialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(folderBrowserDialog.SelectedPath))
                {
                    return;
                }

                try
                {
                    SetRecordingsFolder(folderBrowserDialog.SelectedPath, true);
                    UpdateUiState("Recording folder changed.");
                }
                catch (Exception ex)
                {
                    UpdateUiState("Could not change folder: " + ex.Message);
                }
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

        private void ToggleDetailsPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (DetailsPanelContent == null || ToggleDetailsPanelButton == null)
            {
                return;
            }

            var isCollapsing = DetailsPanelContent.Visibility == Visibility.Visible;
            DetailsPanelContent.Visibility = isCollapsing ? Visibility.Collapsed : Visibility.Visible;
            ToggleDetailsPanelButton.Content = isCollapsing ? "\u25BC" : "\u25B2";
        }

        private void ToggleRecordedFilesPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecordedFilesContent == null || ToggleRecordedFilesPanelButton == null || RecordedFilesColumnDefinition == null)
            {
                return;
            }

            var isCollapsing = RecordedFilesContent.Visibility == Visibility.Visible;
            RecordedFilesContent.Visibility = isCollapsing ? Visibility.Collapsed : Visibility.Visible;
            RecordedFilesColumnDefinition.Width = isCollapsing ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            ToggleRecordedFilesPanelButton.Content = isCollapsing ? "\u25B6" : "\u25C0";
        }

        private static string ResolveInitialRecordingsFolder()
        {
            var savedRecordingFolder = Properties.Settings.Default.RecordingFolder;
            if (string.IsNullOrWhiteSpace(savedRecordingFolder))
            {
                return GetDefaultRecordingsFolder();
            }

            try
            {
                return Path.GetFullPath(savedRecordingFolder);
            }
            catch
            {
                return GetDefaultRecordingsFolder();
            }
        }

        private static string GetDefaultRecordingsFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BrioRecorder");
        }

        private void EnsureRecordingFolderExists()
        {
            Directory.CreateDirectory(recordingsFolder);
        }

        private void UpdateRecordingFolderDisplay()
        {
            RecordingFolderTextBlock.Text = recordingsFolder;
        }

        private void SetRecordingsFolder(string folderPath, bool persistSelection)
        {
            recordingsFolder = string.IsNullOrWhiteSpace(folderPath) ? GetDefaultRecordingsFolder() : Path.GetFullPath(folderPath);
            EnsureRecordingFolderExists();
            UpdateRecordingFolderDisplay();

            if (persistSelection)
            {
                Properties.Settings.Default.RecordingFolder = recordingsFolder;
                Properties.Settings.Default.Save();
            }

            RecordingsListBox.SelectedItem = null;
            LoadRecordedVideos();
            SelectedFileTextBlock.Text = recordedVideos.Count == 0 ? "No recordings yet." : "Select a recording to play.";
        }

        private bool IsLoopRecordingEnabled()
        {
            return LoopRecordingRadioButton != null && LoopRecordingRadioButton.IsChecked == true;
        }

        private int GetSelectedLoopRecordingHours()
        {
            return LoopHoursComboBox != null && LoopHoursComboBox.SelectedItem is int selectedHours ? selectedHours : 12;
        }

        private void UpdateRecordingModeInformation()
        {
            var isLoopRecordingEnabled = IsLoopRecordingEnabled();
            if (RecordingModeTextBlock != null)
            {
                RecordingModeTextBlock.Text = isLoopRecordingEnabled ? "Loop recording" : "Regular recording";
            }

            if (LoopRetentionTextBlock != null)
            {
                LoopRetentionTextBlock.Text = isLoopRecordingEnabled
                    ? string.Format("Keep last {0} hour(s). Segments rotate every {1} minutes.", GetSelectedLoopRecordingHours(), (int)LoopRecordingSegmentDuration.TotalMinutes)
                    : "Disabled";
            }

            if (LoopHoursComboBox != null)
            {
                LoopHoursComboBox.IsEnabled = isLoopRecordingEnabled && !isRecording;
            }
        }

        private string GenerateRecordingFilePath()
        {
            return Path.Combine(recordingsFolder, string.Format("BrioRecording_{0:yyyyMMdd_HHmmss}.avi", DateTime.Now));
        }

        private void OpenRecordingSegment(string filePath)
        {
            lock (syncRoot)
            {
                videoWriter = new VideoFileWriter();
                videoWriter.Open(filePath, recordingWidth, recordingHeight, recordingFrameRate, VideoCodec.MPEG4, recordingBitrate);
                currentRecordingFilePath = filePath;
                currentLoopSegmentStartedAtUtc = DateTime.UtcNow;
                isRecording = true;
            }
        }

        private string CloseCurrentRecordingSegment(bool stopRecordingSession)
        {
            string savedFilePath = null;

            lock (syncRoot)
            {
                if (!isRecording && videoWriter == null)
                {
                    return null;
                }

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

                if (stopRecordingSession)
                {
                    isRecording = false;
                    currentLoopSegmentStartedAtUtc = null;
                }
            }

            return savedFilePath;
        }

        private void DeleteExpiredLoopRecordings()
        {
            if (!IsLoopRecordingEnabled())
            {
                return;
            }

            var cutoffUtc = DateTime.UtcNow.AddHours(-GetSelectedLoopRecordingHours());
            var deletedFilePaths = new List<string>();

            foreach (var file in new DirectoryInfo(recordingsFolder)
                .GetFiles("*.*")
                .Where(file => IsSupportedRecordedVideoPath(file.FullName))
                .Where(file => !string.Equals(file.FullName, currentRecordingFilePath, StringComparison.OrdinalIgnoreCase))
                .Where(file => file.CreationTimeUtc < cutoffUtc))
            {
                try
                {
                    File.Delete(file.FullName);
                    deletedFilePaths.Add(file.FullName);
                }
                catch
                {
                }
            }

            if (deletedFilePaths.Count > 0)
            {
                ForgetRecordedFiles(deletedFilePaths);
            }
        }

        private void RotateLoopRecordingSegmentIfNeeded()
        {
            if (!IsLoopRecordingEnabled() || !isRecording || !currentLoopSegmentStartedAtUtc.HasValue)
            {
                return;
            }

            if (DateTime.UtcNow - currentLoopSegmentStartedAtUtc.Value < LoopRecordingSegmentDuration)
            {
                return;
            }

            string completedFilePath = null;

            try
            {
                completedFilePath = CloseCurrentRecordingSegment(false);
                if (!string.IsNullOrWhiteSpace(completedFilePath))
                {
                    RememberRecordedFile(completedFilePath);
                }

                var nextRecordingFilePath = GenerateRecordingFilePath();
                OpenRecordingSegment(nextRecordingFilePath);
                DeleteExpiredLoopRecordings();
                LoadRecordedVideos();
                UpdateUiState("Loop recording: " + Path.GetFileName(nextRecordingFilePath));
            }
            catch (Exception ex)
            {
                lock (syncRoot)
                {
                    isRecording = false;
                    currentRecordingFilePath = null;
                    currentLoopSegmentStartedAtUtc = null;

                    if (videoWriter != null)
                    {
                        try
                        {
                            videoWriter.Close();
                        }
                        catch
                        {
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
                UpdateUiState("Loop recording stopped: " + ex.Message);
            }
        }

        private static string NormalizeRecordedFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(filePath.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedRecordedVideoPath(string filePath)
        {
            var extension = Path.GetExtension(filePath);
            return string.Equals(extension, ".avi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".wmv", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> LoadRecordedFileHistory()
        {
            var rawFilePaths = Properties.Settings.Default.RecordedFilePaths;
            if (string.IsNullOrWhiteSpace(rawFilePaths))
            {
                return Array.Empty<string>();
            }

            return rawFilePaths
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeRecordedFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && IsSupportedRecordedVideoPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void SaveRecordedFileHistory(IEnumerable<string> filePaths)
        {
            var normalizedFilePaths = filePaths
                .Select(NormalizeRecordedFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && IsSupportedRecordedVideoPath(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Properties.Settings.Default.RecordedFilePaths = string.Join(Environment.NewLine, normalizedFilePaths);
            Properties.Settings.Default.Save();
        }

        private void RememberRecordedFile(string filePath)
        {
            SaveRecordedFileHistory(LoadRecordedFileHistory().Concat(new[] { filePath }));
        }

        private void ForgetRecordedFiles(IEnumerable<string> filePaths)
        {
            var filePathSet = new HashSet<string>(filePaths.Select(NormalizeRecordedFilePath).Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
            SaveRecordedFileHistory(LoadRecordedFileHistory().Where(path => !filePathSet.Contains(path)));
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
            RotateLoopRecordingSegmentIfNeeded();
        }

        private void RecordingsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selectedFile = RecordingsListBox.SelectedItem as RecordedVideoItem;
            SelectedFileTextBlock.Text = selectedFile != null ? selectedFile.FilePath : "Select a recording to play.";
            UpdateUiState(StatusTextBlock.Text);
        }

        private void RecordedFileCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSelectAllCheckBox();
            UpdateUiState(StatusTextBlock.Text);
        }

        private void SelectAllRecordingsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (isUpdatingSelectAllCheckBox)
            {
                return;
            }

            var isChecked = SelectAllRecordingsCheckBox.IsChecked == true;
            foreach (var item in recordedVideos)
            {
                item.IsChecked = isChecked;
            }

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

        private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedFile = RecordingsListBox.SelectedItem as RecordedVideoItem;
            if (selectedFile == null)
            {
                UpdateUiState("Select a recorded file to delete.");
                return;
            }

            DeleteRecordedFiles(new[] { selectedFile });
        }

        private void DeleteCheckedButton_Click(object sender, RoutedEventArgs e)
        {
            var checkedFiles = recordedVideos.Where(item => item.IsChecked).ToList();
            if (checkedFiles.Count == 0)
            {
                UpdateUiState("Check one or more recorded files to delete.");
                return;
            }

            DeleteRecordedFiles(checkedFiles);
        }

        private void DeleteRecordedFiles(IReadOnlyCollection<RecordedVideoItem> filesToDelete)
        {
            if (filesToDelete == null || filesToDelete.Count == 0)
            {
                return;
            }

            var uniqueFiles = filesToDelete
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FilePath))
                .GroupBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (uniqueFiles.Count == 0)
            {
                return;
            }

            var confirmationMessage = uniqueFiles.Count == 1
                ? "Delete the selected recording?"
                : string.Format("Delete {0} selected recordings?", uniqueFiles.Count);

            if (MessageBox.Show(this, confirmationMessage, "Delete Recordings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            var deletedFilePaths = new List<string>();
            var failedFileNames = new List<string>();

            foreach (var file in uniqueFiles)
            {
                try
                {
                    if (File.Exists(file.FilePath))
                    {
                        File.Delete(file.FilePath);
                    }

                    deletedFilePaths.Add(file.FilePath);
                }
                catch
                {
                    failedFileNames.Add(Path.GetFileName(file.FilePath));
                }
            }

            ForgetRecordedFiles(deletedFilePaths);
            LoadRecordedVideos();
            RecordingsListBox.SelectedItem = null;
            SelectedFileTextBlock.Text = recordedVideos.Count == 0 ? "No recordings yet." : "Select a recording to play.";

            if (failedFileNames.Count == 0)
            {
                UpdateUiState(uniqueFiles.Count == 1 ? "Recording deleted." : string.Format("{0} recordings deleted.", deletedFilePaths.Count));
                return;
            }

            UpdateUiState(string.Format("Deleted {0} recording(s). Could not delete: {1}", deletedFilePaths.Count, string.Join(", ", failedFileNames)));
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
            EnsureRecordingFolderExists();
            var nextRecordingFilePath = GenerateRecordingFilePath();

            try
            {
                OpenRecordingSegment(nextRecordingFilePath);

                recordingStartedAtUtc = DateTime.UtcNow;
                UpdateRecordingTimeDisplay();
                recordingTimer.Start();
                DeleteExpiredLoopRecordings();

                UpdateUiState((IsLoopRecordingEnabled() ? "Loop recording to " : "Recording to ") + Path.GetFileName(currentRecordingFilePath));
            }
            catch (Exception ex)
            {
                lock (syncRoot)
                {
                    isRecording = false;
                    currentRecordingFilePath = null;
                    currentLoopSegmentStartedAtUtc = null;
                    if (videoWriter != null)
                    {
                        videoWriter.Dispose();
                        videoWriter = null;
                    }
                }

                recordingStartedAtUtc = null;
                recordingTimer.Stop();
                UpdateRecordingTimeDisplay();
                UpdateUiState("Could not start recording: " + ex.Message);
            }
        }

        private void StopRecording()
        {
            var savedFilePath = CloseCurrentRecordingSegment(true);
            if (savedFilePath == null && recordingStartedAtUtc == null)
            {
                return;
            }

            recordingTimer.Stop();
            recordingStartedAtUtc = null;
            UpdateRecordingTimeDisplay();
            RememberRecordedFile(savedFilePath);
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
            EnsureRecordingFolderExists();

            var files = LoadRecordedFileHistory()
                .Concat(Directory.EnumerateFiles(recordingsFolder).Where(IsSupportedRecordedVideoPath))
                .Select(NormalizeRecordedFilePath)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsSupportedRecordedVideoPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .Select(file => new RecordedVideoItem(file.FullName, string.Format("{0}    {1:yyyy-MM-dd HH:mm:ss}", file.Name, file.CreationTime)))
                .ToList();

            SaveRecordedFileHistory(files.Select(file => file.FilePath));

            recordedVideos.Clear();
            foreach (var file in files)
            {
                recordedVideos.Add(file);
            }

            UpdateSelectAllCheckBox();

            if (recordedVideos.Count == 0)
            {
                SelectedFileTextBlock.Text = "No recordings yet.";
            }
        }

        private void UpdateSelectAllCheckBox()
        {
            isUpdatingSelectAllCheckBox = true;
            try
            {
                if (recordedVideos.Count == 0)
                {
                    SelectAllRecordingsCheckBox.IsChecked = false;
                    return;
                }

                SelectAllRecordingsCheckBox.IsChecked = recordedVideos.All(item => item.IsChecked);
            }
            finally
            {
                isUpdatingSelectAllCheckBox = false;
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
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = statusText;
            }

            if (StartRecordingButton != null)
            {
                StartRecordingButton.IsEnabled = !isRecording && videoSource != null && videoSource.IsRunning;
            }

            if (StopRecordingButton != null)
            {
                StopRecordingButton.IsEnabled = isRecording;
            }

            if (RefreshCamerasButton != null)
            {
                RefreshCamerasButton.IsEnabled = !isRecording;
            }

            if (CameraComboBox != null)
            {
                CameraComboBox.IsEnabled = !isRecording && CameraComboBox.Items.Count > 0;
            }

            if (QualityComboBox != null)
            {
                QualityComboBox.IsEnabled = !isRecording && QualityComboBox.Items.Count > 0;
            }

            if (RegularRecordingRadioButton != null)
            {
                RegularRecordingRadioButton.IsEnabled = !isRecording;
            }

            if (LoopRecordingRadioButton != null)
            {
                LoopRecordingRadioButton.IsEnabled = !isRecording;
            }

            if (LoopHoursComboBox != null)
            {
                LoopHoursComboBox.IsEnabled = !isRecording && IsLoopRecordingEnabled();
            }

            if (PlaySelectedButton != null)
            {
                PlaySelectedButton.IsEnabled = RecordingsListBox != null && RecordingsListBox.SelectedItem != null;
            }

            if (DeleteSelectedButton != null)
            {
                DeleteSelectedButton.IsEnabled = RecordingsListBox != null && RecordingsListBox.SelectedItem != null;
            }

            if (DeleteCheckedButton != null)
            {
                DeleteCheckedButton.IsEnabled = recordedVideos.Any(item => item.IsChecked);
            }

            if (SelectAllRecordingsCheckBox != null)
            {
                SelectAllRecordingsCheckBox.IsEnabled = recordedVideos.Count > 0;
            }

            if (OpenFolderButton != null)
            {
                OpenFolderButton.IsEnabled = true;
            }

            if (ChangeFolderButton != null)
            {
                ChangeFolderButton.IsEnabled = !isRecording;
            }

            if (HelpButton != null)
            {
                HelpButton.IsEnabled = true;
            }
        }

        private RecordingQualityPreset GetSelectedQualityPreset()
        {
            if (QualityComboBox != null && QualityComboBox.SelectedItem is RecordingQualityPreset selected)
            {
                return selected;
            }

            return recordingQualityPresets != null ? recordingQualityPresets[1] : null;
        }

        private void UpdateQualityInformation()
        {
            var selectedQualityPreset = GetSelectedQualityPreset();
            if (selectedQualityPreset == null)
            {
                return;
            }

            if (RecordingQualityTextBlock != null)
            {
                RecordingQualityTextBlock.Text = string.Format("{0} ({1}x{2}, {3} FPS, {4:0.0} Mbps)", selectedQualityPreset.Name, selectedQualityPreset.Width, selectedQualityPreset.Height, selectedQualityPreset.FrameRate, selectedQualityPreset.Bitrate / 1000000d);
            }

            if (EstimatedSizeTextBlock == null)
            {
                return;
            }

            var estimatedMegabytesPerMinute = CalculateEstimatedMegabytesPerMinute(selectedQualityPreset.Bitrate);
            if (IsLoopRecordingEnabled())
            {
                var loopHours = GetSelectedLoopRecordingHours();
                var estimatedLoopMegabytes = estimatedMegabytesPerMinute * 60d * loopHours;
                EstimatedSizeTextBlock.Text = string.Format("Approx. {0} for {1} hour(s) of loop recording.", FormatEstimatedSize(estimatedLoopMegabytes), loopHours);
                return;
            }

            EstimatedSizeTextBlock.Text = string.Format("Approx. {0} per 1 minute of recording.", FormatEstimatedSize(estimatedMegabytesPerMinute));
        }

        private void UpdateRecordingTimeDisplay()
        {
            var elapsed = recordingStartedAtUtc.HasValue ? DateTime.UtcNow - recordingStartedAtUtc.Value : TimeSpan.Zero;
            if (RecordingTimeTextBlock != null)
            {
                RecordingTimeTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
        }

        private static double CalculateEstimatedMegabytesPerMinute(int bitrate)
        {
            return bitrate * 60d / 8d / 1024d / 1024d;
        }

        private static string FormatEstimatedSize(double sizeInMegabytes)
        {
            if (sizeInMegabytes >= 1024d)
            {
                return string.Format("{0:0.0} GB", sizeInMegabytes / 1024d);
            }

            return string.Format("{0:0.0} MB", sizeInMegabytes);
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

        private sealed class RecordedVideoItem : INotifyPropertyChanged
        {
            public RecordedVideoItem(string filePath, string displayName)
            {
                FilePath = filePath;
                DisplayName = displayName;
            }

            public string FilePath { get; private set; }

            public string DisplayName { get; private set; }

            private bool isChecked;

            public bool IsChecked
            {
                get
                {
                    return isChecked;
                }
                set
                {
                    if (isChecked == value)
                    {
                        return;
                    }

                    isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
