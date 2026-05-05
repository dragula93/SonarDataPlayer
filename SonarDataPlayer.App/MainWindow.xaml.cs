using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SonarDataPlayer.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Line = System.Windows.Shapes.Line;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using WpfImage = System.Windows.Controls.Image;

namespace SonarDataPlayer.App;

public partial class MainWindow : Window
{
    private const string DefaultContrastPreset = "balanced";

    private readonly ObservableCollection<ChannelViewModel> _channels = new();
    private readonly PlaybackState _playback = new();
    private readonly DispatcherTimer _timer;
    private readonly List<WpfImage> _sonarImages = new();
    private readonly List<Canvas> _depthGrids = new();
    private readonly List<Rectangle> _timeCursors = new();
    private readonly List<TextBlock> _cursorTimeLabels = new();
    private TextBlock? _viewerDepthReadout;
    private TextBlock? _viewerTempReadout;
    private SonarRecording? _recording;
    private IReadOnlyDictionary<int, BitmapSource> _rawChannelImages = new Dictionary<int, BitmapSource>();
    private double? _manualMaxDepthMeters;
    private double _manualDepthOffsetMeters;
    private bool _isDepthAutoRange = true;
    private double _autoMaxDepthMeters;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private bool _isUpdatingSeek;
    private bool _isUpdatingDepthPanScrollBar;
    private DepthUnit _depthUnit = DepthUnit.Meters;
    private SpeedUnit _speedUnit = SpeedUnit.Mph;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;
    private double _zoomWindowSeconds;
    private int _utcOffsetHours = -4;
    private AppSettings _settings = AppSettings.Load();
    private bool _isRefreshingPaletteSelector;
    private double _alongTrackStretch = 1.0;
    private BitmapSource? _sideScanImage;
    private double _sideScanMaxRangeMeters;
    private double[]? _frameRawTimes;
    private int _frameCount;
    private readonly DispatcherTimer _depthRebuildDebounceTimer;
    private bool _isDepthRebuildPending;

    public MainWindow()
    {
        InitializeComponent();
        _isRefreshingPaletteSelector = true;
        FullPaletteListCheckBox.IsChecked = _settings.ShowFullPaletteList;
        RefreshPaletteOptions(_settings.PaletteName);
        RefreshContrastPresetOptions(_settings.ContrastPreset);
        RefreshCustomContrastControls();
        ContrastLockCheckBox.IsChecked = _settings.ContrastLockAcrossChannels;
        RefreshSideScanBoostControl();
        _isRefreshingPaletteSelector = false;
        UpdateAlongTrackStretchReadout();

        ChannelControls.ItemsSource = _channels;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _depthRebuildDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _depthRebuildDebounceTimer.Tick += DepthRebuildDebounceTimer_Tick;
    }

    private void OpenManifest_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open processed sonar project",
            Filter = "Sonar project manifest (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadRecording(dialog.FileName);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.OpenProjectAfterProcessing && File.Exists(dialog.ManifestPath))
        {
            LoadRecording(dialog.ManifestPath);
        }
    }

    private void PythonSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PythonSettingsWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _settings = dialog.Settings;
            ProjectStatusText.Text = "Python settings saved";
            ProjectStatusText.Foreground = new SolidColorBrush(Color.FromRgb(88, 214, 141));
        }
    }

    internal static string? FindPythonExecutable(AppSettings settings)
    {
        var configured = Environment.GetEnvironmentVariable("SONAR_DATA_PLAYER_PYTHON");
        var candidates = new List<string>();

        if (settings.UseEnvironmentPython)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            if (!string.IsNullOrWhiteSpace(settings.PythonPath))
            {
                candidates.Add(settings.PythonPath);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.PythonPath))
            {
                candidates.Add(settings.PythonPath);
            }

            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }
        }

        candidates.AddRange(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".venv", "Scripts", "python.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PINGverter", ".venv", "Scripts", "python.exe")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache",
                "codex-runtimes",
                "codex-primary-runtime",
                "dependencies",
                "python",
                "python.exe"),
            "python",
            "py"
        });

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(PythonHasParserDependencies);
    }

    internal static bool PythonHasParserDependencies(string pythonPath)
    {
        if (Path.IsPathFullyQualified(pythonPath) && !File.Exists(pythonPath))
        {
            return false;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("import numpy, pandas, PIL");

        try
        {
            process.Start();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void LoadRecording(string manifestPath)
    {
        _recording = ProcessedProjectLoader.Load(manifestPath);
        _manualMaxDepthMeters = GetFileMaxRangeMeters();
        _manualDepthOffsetMeters = 0;
        _isDepthAutoRange = false;
        BuildFrameTimelineModel();
        _autoMaxDepthMeters = GetAutoMaxRangeMeters();
        ClampManualDepthOffset();
        RenderRawChannelImages();
        _channels.Clear();

        foreach (var channel in _recording.Channels)
        {
            _rawChannelImages.TryGetValue(channel.ChannelId, out var rawImage);
            var vm = new ChannelViewModel(channel, rawImage);
            vm.PropertyChanged += Channel_PropertyChanged;
            _channels.Add(vm);
        }

        var title = string.IsNullOrWhiteSpace(_recording.SourcePath)
            ? Path.GetFileNameWithoutExtension(manifestPath)
            : Path.GetFileName(_recording.SourcePath);

        RecordingTitle.Text = _rawChannelImages.Count > 0
            ? $"{title}  | raw samples"
            : $"{title}  | preview PNGs";
        EmptyViewerText.Visibility = _channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var playbackDuration = GetPlaybackDurationSeconds();
        SeekSlider.Maximum = playbackDuration;
        _playback.Seek(0, playbackDuration);
        RenderChannels();
        UpdateReadouts();
        UpdateDepthAutoButtonState();
        UpdateDepthPanScrollBarState();
        RefreshDepthRangeInputs();
    }

    private void Channel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.IsVisible) or nameof(ChannelViewModel.Opacity))
        {
            RenderChannels();
        }
    }

    private bool IsSideScanMode() => SideScanMode.IsChecked == true;

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RebuildDepthScaledView();
        }
    }

    private void ViewerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateImageViewports();
        UpdateCursorPositions();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _playback.Toggle();
        PlayPauseButton.Content = _playback.IsPlaying ? "Pause" : "Play";
        _lastTick = DateTimeOffset.Now;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_recording is null || _isUpdatingSeek)
        {
            return;
        }

        _playback.Seek(e.NewValue, GetPlaybackDurationSeconds());
        UpdateReadouts();
    }

    private void RateSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RateSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            double.TryParse(tag, out var rate))
        {
            _playback.SetRate(rate);
        }

        UpdateReadouts();
    }

    private void AlongTrackStretchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _alongTrackStretch = Math.Clamp(e.NewValue, 1.0, 4.0);
        UpdateAlongTrackStretchReadout();

        if (_recording is null)
        {
            return;
        }

        UpdateImageViewports();
        UpdateCursorPositions();
    }

    private void UnitSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _depthUnit = SelectedDepthUnit();
        _speedUnit = SelectedSpeedUnit();
        _temperatureUnit = SelectedTemperatureUnit();
        _utcOffsetHours = SelectedUtcOffsetHours();

        if (_recording is not null && _isDepthAutoRange)
        {
            _autoMaxDepthMeters = GetAutoMaxRangeMeters();
            RebuildDepthScaledView();
            return;
        }

        RenderChannels();
        UpdateReadouts();
        RefreshDepthRangeInputs();
    }

    private void ZoomSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _zoomWindowSeconds = SelectedZoomWindowSeconds();
        UpdateImageViewports();
        UpdateCursorPositions();
    }

    private void PaletteSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var selectedPalette = SelectedPaletteName();
        var normalizedPalette = SonarPaletteCatalog.NormalizeName(selectedPalette);
        if (string.Equals(_settings.PaletteName, normalizedPalette, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings = _settings with { PaletteName = normalizedPalette };
        _settings.Save();

        if (_recording is not null)
        {
            RebuildDepthScaledView();
        }
    }

    private void PaletteListVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var showFullPaletteList = FullPaletteListCheckBox.IsChecked == true;
        if (_settings.ShowFullPaletteList == showFullPaletteList)
        {
            RefreshPaletteOptions(_settings.PaletteName);
            return;
        }

        _settings = _settings with { ShowFullPaletteList = showFullPaletteList };
        _settings.Save();
        RefreshPaletteOptions(_settings.PaletteName);
    }

    private void ContrastSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var selectedPreset = SelectedContrastPreset();
        if (string.Equals(_settings.ContrastPreset, selectedPreset, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings = _settings with { ContrastPreset = selectedPreset };
        _settings.Save();
        UpdateCustomContrastControlState();

        if (_recording is not null)
        {
            RebuildDepthScaledView();
        }
    }

    private void ContrastCustomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var lowPercentile = Math.Clamp(ContrastLowClipSlider.Value / 100.0, 0, 0.999);
        var highPercentile = Math.Clamp(ContrastHighClipSlider.Value / 100.0, 0, 0.9999);

        if (highPercentile <= lowPercentile + 0.001)
        {
            if (ReferenceEquals(sender, ContrastLowClipSlider))
            {
                highPercentile = Math.Min(0.9999, lowPercentile + 0.001);
            }
            else
            {
                lowPercentile = Math.Max(0, highPercentile - 0.001);
            }

            _isRefreshingPaletteSelector = true;
            ContrastLowClipSlider.Value = lowPercentile * 100.0;
            ContrastHighClipSlider.Value = highPercentile * 100.0;
            _isRefreshingPaletteSelector = false;
        }

        UpdateCustomContrastLabels(lowPercentile, highPercentile);

        if (Math.Abs(_settings.CustomContrastLowPercentile - lowPercentile) < 0.00001 &&
            Math.Abs(_settings.CustomContrastHighPercentile - highPercentile) < 0.00001)
        {
            return;
        }

        _settings = _settings with
        {
            CustomContrastLowPercentile = lowPercentile,
            CustomContrastHighPercentile = highPercentile
        };
        _settings.Save();

        if (_recording is not null && string.Equals(SelectedContrastPreset(), "custom", StringComparison.OrdinalIgnoreCase))
        {
            RebuildDepthScaledView();
        }
    }

    private void ContrastLockCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var lockAcrossChannels = IsContrastLockedAcrossChannels();
        if (_settings.ContrastLockAcrossChannels == lockAcrossChannels)
        {
            return;
        }

        _settings = _settings with { ContrastLockAcrossChannels = lockAcrossChannels };
        _settings.Save();

        if (_recording is not null)
        {
            RebuildDepthScaledView();
        }
    }

    private void SideScanBoostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isRefreshingPaletteSelector)
        {
            return;
        }

        var boost = Math.Clamp(SideScanBoostSlider.Value, 0, 2.0);
        UpdateSideScanBoostLabel(boost);
        if (Math.Abs(_settings.SideScanContrastBoost - boost) < 0.001)
        {
            return;
        }

        _settings = _settings with { SideScanContrastBoost = boost };
        _settings.Save();

        if (_recording is not null)
        {
            RebuildDepthScaledView();
        }
    }

    private void DepthZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        var current = GetDisplayDepthSpanMeters();
        _isDepthAutoRange = false;
        _manualMaxDepthMeters = Math.Max(3.0, current * 0.8);
        ClampManualDepthOffset();
        RebuildDepthScaledView();
        RefreshDepthRangeInputs();
    }

    private void DepthZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        var auto = GetAutoMaxRangeMeters();
        var current = GetDisplayDepthSpanMeters();
        _isDepthAutoRange = false;
        _manualMaxDepthMeters = Math.Min(GetFileMaxRangeMeters(), Math.Max(auto, current * 1.25));
        ClampManualDepthOffset();
        RebuildDepthScaledView();
        RefreshDepthRangeInputs();
    }

    private void DepthZoomAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null)
        {
            return;
        }

        _isDepthAutoRange = true;
        _manualMaxDepthMeters = null;
        _manualDepthOffsetMeters = 0;
        _autoMaxDepthMeters = GetAutoMaxRangeMeters();
        RebuildDepthScaledView();
        RefreshDepthRangeInputs();
    }

    private void DepthRangeApply_Click(object sender, RoutedEventArgs e)
    {
        ApplyDepthRangeFromInputs();
    }

    private void DepthRangeTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyDepthRangeFromInputs();
        e.Handled = true;
    }

    private void DepthRangeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.IsKeyboardFocusWithin)
        {
            return;
        }

        if (DepthMinRangeTextBox?.IsKeyboardFocusWithin == true || DepthMaxRangeTextBox?.IsKeyboardFocusWithin == true)
        {
            return;
        }

        ApplyDepthRangeFromInputs();
    }

    private void ApplyDepthRangeFromInputs()
    {
        if (_recording is null || DepthMinRangeTextBox is null || DepthMaxRangeTextBox is null)
        {
            return;
        }

        if (!TryParseDepthDisplayValue(DepthMaxRangeTextBox.Text, out var maxDisplay))
        {
            RefreshDepthRangeInputs();
            return;
        }

        var fileMaxMeters = GetFileMaxRangeMeters();

        if (IsSideScanMode())
        {
            // In side-scan mode only the Max (swath half-width from nadir) matters.
            var rangeMeters = Math.Clamp(DisplayDepthToMeters(maxDisplay), 0.5, fileMaxMeters);
            _isDepthAutoRange = false;
            _manualDepthOffsetMeters = 0;
            _manualMaxDepthMeters = rangeMeters;
            ClampManualDepthOffset();
            RebuildDepthScaledView();
            RefreshDepthRangeInputs();
            return;
        }

        if (!TryParseDepthDisplayValue(DepthMinRangeTextBox.Text, out var minDisplay))
        {
            RefreshDepthRangeInputs();
            return;
        }

        var minMeters = Math.Clamp(DisplayDepthToMeters(minDisplay), 0, fileMaxMeters);
        var maxMeters = Math.Clamp(DisplayDepthToMeters(maxDisplay), 0, fileMaxMeters);
        if (maxMeters <= minMeters)
        {
            var minSpan = Math.Max(0.25, GetDepthGridIntervalMeters() * 0.5);
            maxMeters = Math.Min(fileMaxMeters, minMeters + minSpan);
            if (maxMeters <= minMeters)
            {
                minMeters = Math.Max(0, maxMeters - minSpan);
            }
        }

        _isDepthAutoRange = false;
        _manualDepthOffsetMeters = minMeters;
        _manualMaxDepthMeters = Math.Max(0.25, maxMeters - minMeters);
        ClampManualDepthOffset();
        RebuildDepthScaledView();
        RefreshDepthRangeInputs();
    }

    private void ViewerHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_recording is null || _isDepthAutoRange)
        {
            return;
        }

        var span = GetDisplayDepthSpanMeters();
        var maxOffset = Math.Max(0, GetFileMaxRangeMeters() - span);
        if (maxOffset <= 0)
        {
            return;
        }

        var panStep = Math.Max(GetDepthGridIntervalMeters(), span * 0.05);
        var direction = e.Delta > 0 ? -1.0 : 1.0;
        var nextOffset = Math.Clamp(_manualDepthOffsetMeters + (direction * panStep), 0, maxOffset);
        if (Math.Abs(nextOffset - _manualDepthOffsetMeters) < 0.001)
        {
            return;
        }

        _manualDepthOffsetMeters = nextOffset;
        RequestDepthScaledRebuild();
        e.Handled = true;
    }

    private void DepthPanScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingDepthPanScrollBar || _recording is null || _isDepthAutoRange)
        {
            return;
        }

        var span = GetDisplayDepthSpanMeters();
        var maxOffset = Math.Max(0, GetFileMaxRangeMeters() - span);
        if (maxOffset <= 0)
        {
            return;
        }

        var nextOffset = Math.Clamp(e.NewValue, 0, maxOffset);
        if (Math.Abs(nextOffset - _manualDepthOffsetMeters) < 0.001)
        {
            return;
        }

        _manualDepthOffsetMeters = nextOffset;
        RequestDepthScaledRebuild();
    }

    private void RequestDepthScaledRebuild(bool immediate = false)
    {
        if (_recording is null)
        {
            return;
        }

        if (immediate)
        {
            _isDepthRebuildPending = false;
            _depthRebuildDebounceTimer.Stop();
            RebuildDepthScaledView();
            return;
        }

        _isDepthRebuildPending = true;
        _depthRebuildDebounceTimer.Stop();
        _depthRebuildDebounceTimer.Start();
    }

    private void DepthRebuildDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _depthRebuildDebounceTimer.Stop();
        if (!_isDepthRebuildPending)
        {
            return;
        }

        _isDepthRebuildPending = false;
        RebuildDepthScaledView();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (_recording is null)
        {
            return;
        }

        _playback.Advance(elapsed, GetPlaybackDurationSeconds());
        PlayPauseButton.Content = _playback.IsPlaying ? "Pause" : "Play";
        UpdateReadouts();
    }

    private void UpdateReadouts()
    {
        if (_recording is null)
        {
            return;
        }

        _isUpdatingSeek = true;
        SeekSlider.Value = _playback.CurrentTimeSeconds;
        _isUpdatingSeek = false;

        var playbackDuration = GetPlaybackDurationSeconds();
        TimeReadout.Text = $"{_playback.CurrentTimeSeconds:0.0} / {playbackDuration:0.0} s";
        UpdateImageViewports();
        UpdateCursorPositions();

        var ping = _recording.FindNearestTelemetry(_playback.CurrentTimeSeconds);
        if (ping is null)
        {
            return;
        }

        if (_isDepthAutoRange && UpdateAutoRangeFromDepth(ping.DepthMeters))
        {
            RebuildDepthScaledView();
        }

        DepthReadout.Text = $"Depth: {FormatDepth(ping.DepthMeters)}";
        RangeReadout.Text = $"Range: {FormatDepth(ping.MinimumRangeMeters)} - {FormatDepth(ping.MaximumRangeMeters)}";
        PositionReadout.Text = $"Position: {Format(ping.Latitude, "0.000000")}, {Format(ping.Longitude, "0.000000")}";
        SpeedReadout.Text = $"Speed: {FormatSpeed(ping.SpeedMetersPerSecond)}";
        HeadingReadout.Text = $"Heading: {Format(ping.HeadingDegrees, "0")} deg";
        TempReadout.Text = $"Water Temp: {FormatTemperature(ping.TemperatureCelsius)}";
        PingReadout.Text = $"Ping: {ping.RecordNumber}  Ch: {ping.ChannelId}  Samples: {ping.SampleCount}";
        UpdateViewerTelemetry(ping);
    }

    private static string Format(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format) : "-";
    }

    private void UpdateAlongTrackStretchReadout()
    {
        if (AlongTrackStretchValueText is not null)
        {
            AlongTrackStretchValueText.Text = $"{_alongTrackStretch:0.00}x";
        }
    }

    private string FormatDepth(double? meters)
    {
        if (!meters.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (meters.Value * 3.280839895, "ft"),
            DepthUnit.Fathoms => (meters.Value / 1.8288, "fm"),
            _ => (meters.Value, "m")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatSpeed(double? metersPerSecond)
    {
        if (!metersPerSecond.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _speedUnit switch
        {
            SpeedUnit.Knots => (metersPerSecond.Value * 1.943844492, "kt"),
            _ => (metersPerSecond.Value * 2.236936292, "mph")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatTemperature(double? celsius)
    {
        if (!celsius.HasValue)
        {
            return "-";
        }

        var (value, suffix) = _temperatureUnit switch
        {
            TemperatureUnit.Fahrenheit => ((celsius.Value * 9.0 / 5.0) + 32.0, "F"),
            _ => (celsius.Value, "C")
        };

        return $"{value:0.0} {suffix}";
    }

    private string FormatLocalTime(DateTime? utc)
    {
        if (!utc.HasValue)
        {
            return "-";
        }

        var local = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).AddHours(_utcOffsetHours);
        return $"{local:yyyy-MM-dd HH:mm:ss} UTC{_utcOffsetHours:+0;-0;+0}";
    }

    private string FormatCursorLocalTime()
    {
        if (_recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds)?.TimestampUtc is not { } utc)
        {
            return string.Empty;
        }

        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(_utcOffsetHours);
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private DepthUnit SelectedDepthUnit()
    {
        return DepthUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<DepthUnit>(tag, out var unit)
            ? unit
            : DepthUnit.Meters;
    }

    private SpeedUnit SelectedSpeedUnit()
    {
        return SpeedUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<SpeedUnit>(tag, out var unit)
            ? unit
            : SpeedUnit.Mph;
    }

    private TemperatureUnit SelectedTemperatureUnit()
    {
        return TemperatureUnitSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               Enum.TryParse<TemperatureUnit>(tag, out var unit)
            ? unit
            : TemperatureUnit.Celsius;
    }

    private int SelectedUtcOffsetHours()
    {
        return UtcOffsetSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               int.TryParse(tag, out var offset)
            ? offset
            : -4;
    }

    private double SelectedZoomWindowSeconds()
    {
        return ZoomSelector?.SelectedItem is ComboBoxItem item &&
               item.Tag is string tag &&
               double.TryParse(tag, out var seconds)
            ? seconds
            : 0;
    }

    private string SelectedPaletteName()
    {
        return PaletteSelector?.SelectedItem is string paletteName
            ? SonarPaletteCatalog.NormalizeName(paletteName)
            : SonarPaletteCatalog.DefaultName;
    }

    private string SelectedContrastPreset()
    {
        return ContrastSelector?.SelectedItem is ComboBoxItem item
            ? NormalizeContrastPreset(item.Tag as string)
            : NormalizeContrastPreset(_settings.ContrastPreset);
    }

    private bool IsContrastLockedAcrossChannels()
    {
        return ContrastLockCheckBox?.IsChecked == true;
    }

    private bool TryParseDepthDisplayValue(string? text, out double value)
    {
        text = text?.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private double MetersToDisplayDepth(double meters)
    {
        return _depthUnit switch
        {
            DepthUnit.Feet => meters * 3.280839895,
            DepthUnit.Fathoms => meters / 1.8288,
            _ => meters
        };
    }

    private double DisplayDepthToMeters(double value)
    {
        return _depthUnit switch
        {
            DepthUnit.Feet => value / 3.280839895,
            DepthUnit.Fathoms => value * 1.8288,
            _ => value
        };
    }

    private void RefreshDepthRangeInputs()
    {
        if (DepthMinRangeTextBox is null || DepthMaxRangeTextBox is null)
        {
            return;
        }

        if (DepthMinRangeTextBox.IsKeyboardFocused || DepthMaxRangeTextBox.IsKeyboardFocused)
        {
            return;
        }

        if (IsSideScanMode())
        {
            // In side-scan mode the range controls set cross-track swath ± from nadir.
            // Min is always 0 (nadir); only Max (swath half-width) is meaningful.
            if (RangeMinLabel is not null)
            {
                RangeMinLabel.Text = "Near";
            }
            if (RangeMaxLabel is not null)
            {
                RangeMaxLabel.Text = "Range";
            }
            DepthMinRangeTextBox.Text = "0";
            DepthMinRangeTextBox.IsEnabled = false;
            var maxDisplay = MetersToDisplayDepth(GetDisplayMaxRangeMeters());
            DepthMaxRangeTextBox.Text = maxDisplay.ToString("0.##", CultureInfo.CurrentCulture);
            DepthMaxRangeTextBox.IsEnabled = true;
        }
        else
        {
            if (RangeMinLabel is not null)
            {
                RangeMinLabel.Text = "Min";
            }
            if (RangeMaxLabel is not null)
            {
                RangeMaxLabel.Text = "Max";
            }
            DepthMinRangeTextBox.IsEnabled = true;
            DepthMaxRangeTextBox.IsEnabled = true;
            var minDisplay = MetersToDisplayDepth(GetDisplayMinRangeMeters());
            var maxDisplay = MetersToDisplayDepth(GetDisplayMaxRangeMeters());
            DepthMinRangeTextBox.Text = minDisplay.ToString("0.##", CultureInfo.CurrentCulture);
            DepthMaxRangeTextBox.Text = maxDisplay.ToString("0.##", CultureInfo.CurrentCulture);
        }
    }

    private void RefreshPaletteOptions(string? preferredPalette = null)
    {
        var normalizedPalette = SonarPaletteCatalog.NormalizeName(preferredPalette ?? _settings.PaletteName);
        var options = SonarPaletteCatalog.GetSelectableNames(_settings.ShowFullPaletteList, normalizedPalette)
            .ToArray();

        _isRefreshingPaletteSelector = true;
        PaletteSelector.ItemsSource = options;
        PaletteSelector.SelectedItem = options.FirstOrDefault(name =>
            string.Equals(name, normalizedPalette, StringComparison.OrdinalIgnoreCase))
            ?? SonarPaletteCatalog.DefaultName;
        _isRefreshingPaletteSelector = false;
    }

    private void RefreshContrastPresetOptions(string? preferredPreset = null)
    {
        var normalizedPreset = NormalizeContrastPreset(preferredPreset ?? _settings.ContrastPreset);
        _isRefreshingPaletteSelector = true;

        ComboBoxItem? selectedItem = null;
        foreach (var item in ContrastSelector.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag as string, normalizedPreset, StringComparison.OrdinalIgnoreCase))
            {
                selectedItem = comboItem;
                break;
            }
        }

        ContrastSelector.SelectedItem = selectedItem ?? ContrastSelector.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _isRefreshingPaletteSelector = false;
        UpdateCustomContrastControlState();
    }

    private void RefreshCustomContrastControls()
    {
        var lowPercentile = Math.Clamp(_settings.CustomContrastLowPercentile, 0, 0.999);
        var highPercentile = Math.Clamp(_settings.CustomContrastHighPercentile, lowPercentile + 0.001, 0.9999);

        _isRefreshingPaletteSelector = true;
        ContrastLowClipSlider.Value = lowPercentile * 100.0;
        ContrastHighClipSlider.Value = highPercentile * 100.0;
        _isRefreshingPaletteSelector = false;

        UpdateCustomContrastLabels(lowPercentile, highPercentile);
        UpdateCustomContrastControlState();
    }

    private void UpdateCustomContrastLabels(double lowPercentile, double highPercentile)
    {
        ContrastLowClipLabel.Text = $"Low clip: {lowPercentile * 100.0:0.00}%";
        ContrastHighClipLabel.Text = $"High clip: {highPercentile * 100.0:0.00}%";
    }

    private void UpdateCustomContrastControlState()
    {
        var isCustom = string.Equals(SelectedContrastPreset(), "custom", StringComparison.OrdinalIgnoreCase);
        ContrastLowClipSlider.IsEnabled = isCustom;
        ContrastHighClipSlider.IsEnabled = isCustom;
        ContrastLowClipLabel.Opacity = isCustom ? 1.0 : 0.55;
        ContrastHighClipLabel.Opacity = isCustom ? 1.0 : 0.55;
    }

    private void RefreshSideScanBoostControl()
    {
        var boost = Math.Clamp(_settings.SideScanContrastBoost, 0, 2.0);
        _isRefreshingPaletteSelector = true;
        SideScanBoostSlider.Value = boost;
        _isRefreshingPaletteSelector = false;
        UpdateSideScanBoostLabel(boost);
    }

    private void UpdateSideScanBoostLabel(double boost)
    {
        SideScanBoostLabel.Text = $"Side scan boost: {boost:0.00}";
    }

    private static string NormalizeContrastPreset(string? preset)
    {
        return preset?.Trim().ToLowerInvariant() switch
        {
            "soft" => "soft",
            "custom" => "custom",
            "strong" => "strong",
            _ => DefaultContrastPreset
        };
    }

    private (double LowPercentile, double HighPercentile) GetContrastPercentiles(string? preset)
    {
        return NormalizeContrastPreset(preset) switch
        {
            "soft" => (0.005, 0.999),
            "custom" =>
            (
                Math.Clamp(_settings.CustomContrastLowPercentile, 0, 0.999),
                Math.Clamp(_settings.CustomContrastHighPercentile, Math.Clamp(_settings.CustomContrastLowPercentile, 0, 0.999) + 0.001, 0.9999)
            ),
            "strong" => (0.02, 0.99),
            _ => (0.01, 0.995)
        };
    }

    private void RenderChannels()
    {
        ViewerHost.Children.Clear();
        ViewerHost.RowDefinitions.Clear();
        _sonarImages.Clear();
        _depthGrids.Clear();
        _timeCursors.Clear();
        _cursorTimeLabels.Clear();
        _viewerDepthReadout = null;
        _viewerTempReadout = null;

        if (_recording is null)
        {
            return;
        }

        var visibleChannels = _channels.Where(c => c.IsVisible && c.Image is not null).ToArray();
        EmptyViewerText.Visibility = visibleChannels.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visibleChannels.Length == 0)
        {
            return;
        }

        if (SideScanMode.IsChecked == true)
        {
            RenderSideScanView();
        }
        else if (OverlayMode.IsChecked == true)
        {
            RenderOverlay(visibleChannels);
        }
        else
        {
            RenderStacked(visibleChannels);
        }

        AddViewerTelemetryOverlay();
        UpdateCursorPositions();
        UpdateDepthPanScrollBarState();
    }

    private void RebuildDepthScaledView()
    {
        ClampManualDepthOffset();
        RenderRawChannelImages();

        // Build the combined side-scan bitmap when that mode is active and initialise range.
        if (IsSideScanMode() && _recording is not null)
        {
            var (lowP, highP) = GetContrastPercentiles(_settings.ContrastPreset);
            _sideScanImage = BinaryWaterfallRenderer.RenderSideScan(
                _recording,
                _settings.PaletteName,
                lowP, highP,
                Math.Clamp(_settings.SideScanContrastBoost, 0, 2.0));

            // If the SS max range hasn't been set yet (or changed), initialise it and reset
            // the range controls to show the full swath.
            var ssMax = BinaryWaterfallRenderer.GetSideScanMaxRangeMeters(_recording);
            if (ssMax > 0 && Math.Abs(ssMax - _sideScanMaxRangeMeters) > 0.001)
            {
                _sideScanMaxRangeMeters = ssMax;
                _isDepthAutoRange = false;
                _manualDepthOffsetMeters = 0;
                _manualMaxDepthMeters = ssMax;
            }
        }
        else
        {
            _sideScanImage = null;
            _sideScanMaxRangeMeters = 0;
        }

        foreach (var channel in _channels)
        {
            _rawChannelImages.TryGetValue(channel.Channel.ChannelId, out var rawImage);
            channel.SetImage(rawImage ?? ChannelViewModel.LoadRotatedPreviewImage(channel.Channel.WaterfallPath));
        }
        RenderChannels();
        UpdateReadouts();
        UpdateDepthAutoButtonState();
        UpdateDepthPanScrollBarState();
        RefreshDepthRangeInputs();
    }

    private void RenderRawChannelImages()
    {
        var displayMinRange = GetDisplayMinRangeMeters();
        var displayMaxRange = GetDisplayMaxRangeMeters();
        var (lowPercentile, highPercentile) = GetContrastPercentiles(_settings.ContrastPreset);
        var lockAcrossChannels = IsContrastLockedAcrossChannels();
        var sideScanBoost = Math.Clamp(_settings.SideScanContrastBoost, 0, 2.0);
        _rawChannelImages = _recording is null
            ? new Dictionary<int, BitmapSource>()
            : BinaryWaterfallRenderer.Render(
                _recording,
                displayMinRange,
                displayMaxRange > 0 ? displayMaxRange : null,
                _settings.PaletteName,
                lowPercentile,
                highPercentile,
            lockAcrossChannels,
            sideScanBoost);
    }

    private void RenderStacked(IReadOnlyList<ChannelViewModel> channels)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var panel = CreateChannelPanel(channels[i]);
            panel.Margin = new Thickness(0, 0, 0, i == channels.Count - 1 ? 0 : 8);
            Grid.SetRow(panel, i);
            ViewerHost.Children.Add(panel);
        }
    }

    private void RenderOverlay(IReadOnlyList<ChannelViewModel> channels)
    {
        ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        foreach (var channel in channels)
        {
            panel.Children.Add(CreateSonarImage(channel));
        }

        panel.Children.Add(CreateDepthGrid(null));

        panel.Children.Add(CreateCursor());
        panel.Children.Add(CreateCursorTimeLabel());
        ViewerHost.Children.Add(panel);
    }

    private void RenderSideScanView()
    {
        if (_sideScanImage is null)
        {
            return;
        }

        ViewerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        // The image fills the full width; height is managed by UpdateImageViewports.
        var image = new WpfImage
        {
            Source = _sideScanImage,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
        _sonarImages.Add(image);
        panel.Children.Add(image);

        // Nadir centre line.
        panel.Children.Add(new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
            IsHitTestVisible = false
        });

        // Horizontal time cursor (2 px tall, full width).
        var cursor = new Rectangle
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new SolidColorBrush(Color.FromRgb(255, 239, 132))
        };
        _timeCursors.Add(cursor);
        panel.Children.Add(cursor);

        panel.Children.Add(CreateCursorTimeLabel());
        ViewerHost.Children.Add(panel);
    }

    private Grid CreateChannelPanel(ChannelViewModel channel)
    {
        var panel = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Black
        };

        panel.Children.Add(CreateSonarImage(channel));

        panel.Children.Add(CreateDepthGrid(channel.Channel.ChannelId));

        panel.Children.Add(CreateCursor());
        panel.Children.Add(CreateCursorTimeLabel());
        return panel;
    }

    private void AddViewerTelemetryOverlay()
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(198, 255, 255, 244)),
            Padding = new Thickness(10, 6, 12, 8),
            Margin = new Thickness(10),
            Child = new StackPanel
            {
                Children =
                {
                    (_viewerDepthReadout = new TextBlock
                    {
                        Foreground = Brushes.Black,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 46,
                        FontWeight = FontWeights.Bold,
                        LineHeight = 44,
                        Text = "--.-"
                    }),
                    (_viewerTempReadout = new TextBlock
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(1, 0, 0, 0),
                        Text = "--.-"
                    })
                }
            }
        };

        Grid.SetRowSpan(border, Math.Max(1, ViewerHost.RowDefinitions.Count));
        Panel.SetZIndex(border, 20);
        ViewerHost.Children.Add(border);
        UpdateViewerTelemetry(_recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds));
    }

    private void UpdateViewerTelemetry(PingTelemetry? ping)
    {
        if (_viewerDepthReadout is null || _viewerTempReadout is null)
        {
            return;
        }

        _viewerDepthReadout.Text = FormatDigitalDepth(ping?.DepthMeters);
        _viewerTempReadout.Text = FormatTemperature(ping?.TemperatureCelsius);
    }

    private string FormatDigitalDepth(double? meters)
    {
        if (!meters.HasValue)
        {
            return "--.-";
        }

        var (value, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (meters.Value * 3.280839895, "ft"),
            DepthUnit.Fathoms => (meters.Value / 1.8288, "fm"),
            _ => (meters.Value, "m")
        };

        return $"{value:0.0}{suffix}";
    }

    private WpfImage CreateSonarImage(ChannelViewModel channel)
    {
        var image = new WpfImage
        {
            Source = channel.Image,
            Stretch = Stretch.Fill,
            Opacity = channel.Opacity,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
        _sonarImages.Add(image);
        return image;
    }

    private Rectangle CreateCursor()
    {
        var cursor = new Rectangle
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new SolidColorBrush(Color.FromRgb(255, 239, 132))
        };

        _timeCursors.Add(cursor);
        return cursor;
    }

    private TextBlock CreateCursorTimeLabel()
    {
        var label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(Color.FromRgb(255, 239, 132)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 0, 4)
        };

        _cursorTimeLabels.Add(label);
        return label;
    }

    private Canvas CreateDepthGrid(int? channelId)
    {
        var canvas = new Canvas
        {
            IsHitTestVisible = false,
            Tag = channelId
        };
        canvas.SizeChanged += DepthGrid_SizeChanged;
        _depthGrids.Add(canvas);
        return canvas;
    }

    private void DepthGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Canvas canvas)
        {
            DrawDepthGrid(canvas);
        }
    }

    private void DrawDepthGrid(Canvas canvas)
    {
        canvas.Children.Clear();
        if (_recording is null || canvas.ActualHeight <= 0 || canvas.ActualWidth <= 0)
        {
            return;
        }

        var channelId = canvas.Tag as int?;
        var minDepthMeters = GetDisplayMinRangeMeters();
        var maxDepthMeters = GetDisplayMaxRangeMeters();
        var visibleDepthSpanMeters = maxDepthMeters - minDepthMeters;
        if (visibleDepthSpanMeters <= 0)
        {
            return;
        }

        var (intervalMeters, suffix) = _depthUnit switch
        {
            DepthUnit.Feet => (10.0 / 3.280839895, "ft"),
            DepthUnit.Fathoms => (1.8288, "fm"),
            _ => (3.0, "m")
        };

        var intervalDisplay = _depthUnit switch
        {
            DepthUnit.Feet => 10.0,
            DepthUnit.Fathoms => 1.0,
            _ => 3.0
        };

        var stroke = new SolidColorBrush(Color.FromArgb(92, 255, 255, 255));
        var textBrush = new SolidColorBrush(Color.FromArgb(210, 238, 242, 247));

        var firstDepthLine = Math.Floor(minDepthMeters / intervalMeters) * intervalMeters;
        if (firstDepthLine <= minDepthMeters)
        {
            firstDepthLine += intervalMeters;
        }

        for (var depthMeters = firstDepthLine; depthMeters < maxDepthMeters; depthMeters += intervalMeters)
        {
            var y = ((depthMeters - minDepthMeters) / visibleDepthSpanMeters) * canvas.ActualHeight;
            var line = new Line
            {
                X1 = 0,
                X2 = canvas.ActualWidth,
                Y1 = y,
                Y2 = y,
                Stroke = stroke,
                StrokeThickness = 1
            };
            canvas.Children.Add(line);

            var displayValue = depthMeters * intervalDisplay / intervalMeters;
            var label = new TextBlock
            {
                Text = $"{displayValue:0} {suffix}",
                Foreground = textBrush,
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                FontSize = 11,
                Padding = new Thickness(4, 1, 4, 1)
            };
            Canvas.SetLeft(label, 6);
            Canvas.SetTop(label, Math.Max(0, y - 10));
            canvas.Children.Add(label);
        }

        DrawBottomTrace(canvas, channelId, minDepthMeters, maxDepthMeters);
    }

    private void DrawBottomTrace(Canvas canvas, int? channelId, double minDepthMeters, double maxDepthMeters)
    {
        if (_recording is null || channelId is null || _recording.Frames.Count < 2)
        {
            return;
        }

        var visibleDepthSpan = maxDepthMeters - minDepthMeters;
        if (visibleDepthSpan <= 0)
        {
            return;
        }

        var points = new PointCollection();
        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        var (startFraction, endFraction, windowFraction) = GetVisibleRenderFractionWindow(visibleStart, visibleDuration);
        for (var i = 0; i < _recording.Frames.Count; i++)
        {
            var frame = _recording.Frames[i];
            var frameFraction = GetFrameRenderFraction(i, frame.TimeSeconds);
            if (frameFraction < startFraction || frameFraction > endFraction)
            {
                continue;
            }

            var block = _recording.Frames[i].Channels.FirstOrDefault(c => c.ChannelId == channelId.Value);
            if (block?.BottomDepthMeters is not { } bottom || bottom <= 0)
            {
                continue;
            }

            var x = ((frameFraction - startFraction) / windowFraction) * canvas.ActualWidth;
            var y = Math.Clamp(((bottom - minDepthMeters) / visibleDepthSpan) * canvas.ActualHeight, 0, canvas.ActualHeight);
            points.Add(new Point(x, y));
        }

        if (points.Count < 2)
        {
            return;
        }

        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromArgb(210, 255, 210, 86)),
            StrokeThickness = 1.5
        });
    }

    private double GetDisplayDepthSpanMeters()
    {
        return _isDepthAutoRange
            ? _autoMaxDepthMeters
            : _manualMaxDepthMeters ?? _autoMaxDepthMeters;
    }

    private double GetDisplayMinRangeMeters()
    {
        if (_isDepthAutoRange)
        {
            return 0;
        }

        var span = GetDisplayDepthSpanMeters();
        var maxOffset = Math.Max(0, GetFileMaxRangeMeters() - span);
        return Math.Clamp(_manualDepthOffsetMeters, 0, maxOffset);
    }

    private double GetDisplayMaxRangeMeters()
    {
        var span = GetDisplayDepthSpanMeters();
        if (span <= 0)
        {
            return 0;
        }

        return GetDisplayMinRangeMeters() + span;
    }

    private void ClampManualDepthOffset()
    {
        if (_isDepthAutoRange)
        {
            _manualDepthOffsetMeters = 0;
            return;
        }

        var span = GetDisplayDepthSpanMeters();
        var maxOffset = Math.Max(0, GetFileMaxRangeMeters() - span);
        _manualDepthOffsetMeters = Math.Clamp(_manualDepthOffsetMeters, 0, maxOffset);
    }

    private void UpdateDepthPanScrollBarState()
    {
        if (DepthPanScrollBar is null)
        {
            return;
        }

        _isUpdatingDepthPanScrollBar = true;
        try
        {
            if (_recording is null || _isDepthAutoRange)
            {
                DepthPanScrollBar.Minimum = 0;
                DepthPanScrollBar.Maximum = 0;
                DepthPanScrollBar.SmallChange = 0;
                DepthPanScrollBar.LargeChange = 0;
                DepthPanScrollBar.Value = 0;
                DepthPanScrollBar.IsEnabled = false;
                return;
            }

            var span = GetDisplayDepthSpanMeters();
            var maxOffset = Math.Max(0, GetFileMaxRangeMeters() - span);
            var canPan = maxOffset > 0;

            DepthPanScrollBar.Minimum = 0;
            DepthPanScrollBar.Maximum = maxOffset;
            DepthPanScrollBar.SmallChange = Math.Max(0.1, GetDepthGridIntervalMeters());
            DepthPanScrollBar.LargeChange = Math.Max(0.5, span * 0.2);
            DepthPanScrollBar.Value = Math.Clamp(_manualDepthOffsetMeters, 0, maxOffset);
            DepthPanScrollBar.IsEnabled = canPan;
        }
        finally
        {
            _isUpdatingDepthPanScrollBar = false;
        }
    }

    private double GetAutoMaxRangeMeters()
    {
        var ping = _recording?.FindNearestTelemetry(_playback.CurrentTimeSeconds);
        return GetAutoMaxRangeMeters(ping?.DepthMeters);
    }

    private double GetAutoMaxRangeMeters(double? currentDepthMeters)
    {
        if (currentDepthMeters.HasValue && currentDepthMeters.Value > 0)
        {
            return CalculateAutoDepthRangeMeters(currentDepthMeters.Value);
        }

        return GetFileMaxRangeMeters();
    }

    private double GetFileMaxRangeMeters()
    {
        if (_recording is null)
        {
            return 0;
        }

        if (IsSideScanMode() && _sideScanMaxRangeMeters > 0)
        {
            return _sideScanMaxRangeMeters;
        }

        return _recording.Frames
            .SelectMany(frame => frame.Channels)
            .Select(channel => channel.MaximumRangeMeters ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private bool UpdateAutoRangeFromDepth(double? currentDepthMeters)
    {
        if (!_isDepthAutoRange)
        {
            return false;
        }

        if (!currentDepthMeters.HasValue || currentDepthMeters.Value <= 0)
        {
            return false;
        }

        var target = GetAutoDepthTargetMeters(currentDepthMeters.Value);
        var next = CalculateAutoDepthRangeMeters(currentDepthMeters.Value);
        var gridLine = GetDepthGridIntervalMeters();

        if (_autoMaxDepthMeters > 0 && next < _autoMaxDepthMeters)
        {
            var shrinkThreshold = _autoMaxDepthMeters - (3 * gridLine);
            if (target >= shrinkThreshold)
            {
                return false;
            }
        }

        if (next <= 0 || Math.Abs(next - _autoMaxDepthMeters) < 0.001)
        {
            return false;
        }

        _autoMaxDepthMeters = next;
        return true;
    }

    private double CalculateAutoDepthRangeMeters(double depthMeters)
    {
        var targetMeters = GetAutoDepthTargetMeters(depthMeters);
        var quantumMeters = GetDepthGridIntervalMeters() * 2.0;
        var minMeters = 10.0 / 3.280839895;

        if (targetMeters <= minMeters)
        {
            return minMeters;
        }

        return Math.Ceiling(targetMeters / quantumMeters) * quantumMeters;
    }

    private double GetAutoDepthTargetMeters(double depthMeters)
    {
        var depthFeet = depthMeters * 3.280839895;
        if (depthFeet < 10)
        {
            return 10.0 / 3.280839895;
        }

        var factor = depthFeet > 1000 ? 1.10 : 1.20;
        return depthMeters * factor;
    }

    private double GetDepthGridIntervalMeters()
    {
        return _depthUnit switch
        {
            DepthUnit.Feet => 10.0 / 3.280839895,
            DepthUnit.Fathoms => 1.8288,
            _ => 3.0
        };
    }

    private void UpdateDepthAutoButtonState()
    {
        if (DepthZoomAutoButton is null)
        {
            return;
        }

        if (_isDepthAutoRange)
        {
            DepthZoomAutoButton.Background = new SolidColorBrush(Color.FromRgb(46, 138, 87));
            DepthZoomAutoButton.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 220, 139));
            DepthZoomAutoButton.Foreground = Brushes.White;
        }
        else
        {
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            DepthZoomAutoButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
    }

    private void UpdateCursorPositions()
    {
        if (_recording is null || GetPlaybackDurationSeconds() <= 0)
        {
            return;
        }

        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        var (startFraction, endFraction, windowFraction) = GetVisibleRenderFractionWindow(visibleStart, visibleDuration);
        var currentFraction = GetPlaybackRenderFraction();
        var t = windowFraction <= 0
            ? 0
            : Math.Clamp((currentFraction - startFraction) / windowFraction, 0, 1);

        if (IsSideScanMode())
        {
            // In side-scan mode the current ping is always at panel y=0 (top of
            // the flipped image).  Pin both the cursor bar and the time label to
            // the top of the panel so they are always co-located with the newest ping.
            foreach (var cursor in _timeCursors)
            {
                if (cursor.Parent is not FrameworkElement parent || parent.ActualHeight <= 0)
                {
                    continue;
                }

                cursor.Margin = new Thickness(0, 0, 0, 0);
            }

            var labelText = FormatCursorLocalTime();
            foreach (var label in _cursorTimeLabels)
            {
                if (label.Parent is not FrameworkElement parent || parent.ActualHeight <= 0)
                {
                    continue;
                }

                label.Text = labelText;
                label.Margin = new Thickness(0, 2, 8, 0);
                label.HorizontalAlignment = HorizontalAlignment.Right;
                label.VerticalAlignment = VerticalAlignment.Top;
            }
        }
        else
        {
            foreach (var cursor in _timeCursors)
            {
                if (cursor.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
                {
                    continue;
                }

                cursor.Margin = new Thickness((parent.ActualWidth - cursor.Width) * t, 0, 0, 0);
            }

            var labelText = FormatCursorLocalTime();
            foreach (var label in _cursorTimeLabels)
            {
                if (label.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
                {
                    continue;
                }

                label.Text = labelText;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var left = (parent.ActualWidth - label.DesiredSize.Width) / 2.0;
                var maxLeft = Math.Max(0, parent.ActualWidth - label.DesiredSize.Width);
                label.Margin = new Thickness(Math.Clamp(left, 0, maxLeft), 0, 0, 4);
            }
        }
    }

    private void UpdateImageViewports()
    {
        if (_recording is null || GetDisplayAxisDurationSeconds() <= 0)
        {
            return;
        }

        var (visibleStart, visibleDuration) = GetVisibleTimeWindow();
        var (startFraction, endFraction, windowFraction) = GetVisibleRenderFractionWindow(visibleStart, visibleDuration);

        if (IsSideScanMode())
        {
            // Side-scan: time axis is vertical; cross-track axis is horizontal.
            // The depth range controls act on cross-track metres from nadir.
            foreach (var image in _sonarImages)
            {
                if (image.Parent is not FrameworkElement parent || parent.ActualHeight <= 0)
                {
                    continue;
                }

                var imageHeight = parent.ActualHeight / windowFraction;
                var unclampedTop = -(imageHeight * (1.0 - endFraction));
                var minTop = parent.ActualHeight - imageHeight;
                var top = Math.Clamp(unclampedTop, minTop, 0);
                image.Height = imageHeight;
                // ScaleTransform(1,-1) is applied once at image creation; do not
                // recreate it here to avoid triggering layout on every frame.
                image.Margin = new Thickness(0, top, 0, 0);

                // Cross-track crop: show [displayMin .. displayMax] metres centred on nadir.
                // _sideScanImage.PixelWidth = portSamples + starSamples; both halves are equal.
                if (_sideScanImage is not null && _sideScanMaxRangeMeters > 0)
                {
                    var displayMin = GetDisplayMinRangeMeters();   // always 0 for now
                    var displayMax = GetDisplayMaxRangeMeters();   // metres from nadir
                    var halfBitmap = _sideScanImage.PixelWidth / 2.0;
                    // fraction of one side (port or star) that is visible
                    var visibleFrac = Math.Clamp(displayMax / _sideScanMaxRangeMeters, 0.001, 1.0);
                    var skipFrac   = Math.Clamp(displayMin / _sideScanMaxRangeMeters, 0.0,   1.0);
                    // total bitmap width scaled so that visibleFrac fills the panel
                    var totalScaledWidth = parent.ActualWidth / ((visibleFrac - skipFrac) * 2.0);
                    image.Width = totalScaledWidth;
                    // centre of bitmap should land at centre of panel
                    // left edge offset: panel_centre - (halfBitmap_fraction * scaledWidth)
                    var bitmapCentreX = (halfBitmap / _sideScanImage.PixelWidth) * totalScaledWidth;
                    var panelCentreX  = parent.ActualWidth / 2.0;
                    // shift inward by the skipFrac (nadir offset) amount
                    var skipPixels = skipFrac * (totalScaledWidth / 2.0);
                    image.Margin = new Thickness(panelCentreX - bitmapCentreX + skipPixels, top, 0, 0);
                }
            }
        }
        else
        {
            // Stacked / overlay: time axis is horizontal; clear any SS flip transform.
            foreach (var image in _sonarImages)
            {
                if (image.Parent is not FrameworkElement parent || parent.ActualWidth <= 0)
                {
                    continue;
                }

                var imageWidth = parent.ActualWidth / windowFraction;
                var left = -(startFraction * imageWidth);
                image.Width = imageWidth;
                image.Margin = new Thickness(left, 0, 0, 0);
            }

            foreach (var grid in _depthGrids)
            {
                DrawDepthGrid(grid);
            }
        }
    }

    private (double Start, double Duration) GetVisibleTimeWindow()
    {
        var axisDuration = GetDisplayAxisDurationSeconds();
        if (_recording is null || axisDuration <= 0)
        {
            return (0, 1);
        }

        var baseDuration = _zoomWindowSeconds <= 0
            ? axisDuration
            : Math.Min(_zoomWindowSeconds, axisDuration);
        var duration = Math.Clamp(baseDuration / _alongTrackStretch, 0.1, axisDuration);
        var axisCurrent = _playback.CurrentTimeSeconds;

        // In side-scan mode the window covers [axisCurrent-duration, axisCurrent].
        // The image is rendered flipped so the newest ping stays at the top of the
        // panel and history trails downward.
        if (IsSideScanMode())
        {
            var ssStart = Math.Max(0, axisCurrent - duration);
            return (ssStart, duration);
        }

        var start = Math.Clamp(
            axisCurrent - (duration / 2.0),
            0,
            Math.Max(0, axisDuration - duration));
        return (start, duration);
    }

    private double GetDisplayAxisDurationSeconds()
    {
        if (_frameRawTimes is not null && _frameRawTimes.Length > 0)
        {
            return _frameRawTimes[^1];
        }

        return _recording?.DurationSeconds ?? 0;
    }

    private double GetPlaybackDurationSeconds()
    {
        return Math.Max(0, GetDisplayAxisDurationSeconds());
    }

    private void BuildFrameTimelineModel()
    {
        _frameRawTimes = null;
        _frameCount = 0;

        if (_recording is null || _recording.Frames.Count == 0)
        {
            return;
        }

        var frames = _recording.Frames.OrderBy(f => f.FrameIndex).ToArray();
        _frameCount = frames.Length;
        var rawTimes = frames.Select(f => f.TimeSeconds).ToArray();
        var firstRaw = rawTimes[0];
        for (var i = 0; i < rawTimes.Length; i++)
        {
            rawTimes[i] = Math.Max(0, rawTimes[i] - firstRaw);
            if (i > 0 && rawTimes[i] < rawTimes[i - 1])
            {
                rawTimes[i] = rawTimes[i - 1];
            }
        }

        _frameRawTimes = rawTimes;
    }

    private double GetPlaybackRenderFraction()
    {
        return TimeToRenderFraction(_playback.CurrentTimeSeconds);
    }

    private (double StartFraction, double EndFraction, double WindowFraction) GetVisibleRenderFractionWindow(double visibleStart, double visibleDuration)
    {
        var start = TimeToRenderFraction(visibleStart);
        var end = TimeToRenderFraction(visibleStart + visibleDuration);
        if (end <= start)
        {
            end = Math.Min(1.0, start + 1e-6);
        }

        return (start, end, Math.Max(1e-6, end - start));
    }

    private double GetFrameRenderFraction(int frameIndex, double frameRawTimeSeconds)
    {
        if (_frameCount <= 1)
        {
            return 0;
        }

        // Bitmap columns are laid out by frame index, so overlays in stacked/overlay
        // mode must use the same index-based fraction to stay aligned.
        return Math.Clamp(frameIndex / (double)(_frameCount - 1), 0, 1);
    }

    private double TimeToRenderFraction(double rawTimeSeconds)
    {
        if (_frameRawTimes is null || _frameRawTimes.Length == 0)
        {
            var duration = _recording?.DurationSeconds ?? 0;
            return duration > 0 ? Math.Clamp(rawTimeSeconds / duration, 0, 1) : 0;
        }

        if (_frameRawTimes.Length == 1)
        {
            return 0;
        }

        if (rawTimeSeconds <= _frameRawTimes[0])
        {
            return 0;
        }

        var last = _frameRawTimes.Length - 1;
        if (rawTimeSeconds >= _frameRawTimes[last])
        {
            return 1;
        }

        var hi = Array.BinarySearch(_frameRawTimes, rawTimeSeconds);
        if (hi >= 0)
        {
            return hi / (double)last;
        }

        hi = ~hi;
        var lo = hi - 1;
        var span = _frameRawTimes[hi] - _frameRawTimes[lo];
        if (span <= 0)
        {
            return lo / (double)last;
        }

        var t = (rawTimeSeconds - _frameRawTimes[lo]) / span;
        var index = lo + t;
        return Math.Clamp(index / last, 0, 1);
    }
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private bool _isVisible = true;
    private double _opacity = 1.0;
    private BitmapSource? _image;

    public ChannelViewModel(ChannelTrack channel, BitmapSource? rawImage)
    {
        Channel = channel;
        Label = $"{channel.Label} ({channel.ChannelId})";
        _image = rawImage ?? LoadRotatedPreviewImage(channel.WaterfallPath);
    }

    public ChannelTrack Channel { get; }

    public string Label { get; }

    public BitmapSource? Image
    {
        get => _image;
        private set
        {
            _image = value;
            OnPropertyChanged();
        }
    }

    public void SetImage(BitmapSource? image)
    {
        Image = image;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Opacity));
        }
    }

    public double Opacity
    {
        get => IsVisible ? _opacity : 0;
        set
        {
            var next = Math.Clamp(value, 0, 1);
            if (Math.Abs(_opacity - next) < 0.001)
            {
                return;
            }

            _opacity = next;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public static BitmapSource? LoadRotatedPreviewImage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.Rotation = Rotation.Rotate90;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

public enum DepthUnit
{
    Feet,
    Meters,
    Fathoms
}

public enum SpeedUnit
{
    Mph,
    Knots
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit
}

public sealed record AppSettings(
    string? PythonPath = null,
    bool UseEnvironmentPython = true,
    string? PingverterRoot = null,
    string? ProjectsRoot = null,
    string PaletteName = SonarPaletteCatalog.DefaultName,
    bool ShowFullPaletteList = false,
    string ContrastPreset = "balanced",
    bool ContrastLockAcrossChannels = false,
    double CustomContrastLowPercentile = 0.01,
    double CustomContrastHighPercentile = 0.995,
    double SideScanContrastBoost = 0.6)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SonarDataPlayer",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings(PythonPath: null, PaletteName: SonarPaletteCatalog.DefaultName, ShowFullPaletteList: false);
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                ?? new AppSettings(PythonPath: null, PaletteName: SonarPaletteCatalog.DefaultName, ShowFullPaletteList: false);
        }
        catch
        {
            return new AppSettings(PythonPath: null, PaletteName: SonarPaletteCatalog.DefaultName, ShowFullPaletteList: false);
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
