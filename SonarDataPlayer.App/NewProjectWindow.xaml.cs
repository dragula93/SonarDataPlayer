using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Color = System.Windows.Media.Color;

namespace SonarDataPlayer.App;

public partial class NewProjectWindow : Window
{
    private AppSettings _settings;
    private bool _processed;

    public NewProjectWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        var pingverterRoot = ResolvePingverterRoot(settings);
        PingverterRootText.Text = pingverterRoot ?? "Not configured. Set this in Python settings.";
        ProjectsRootText.Text = settings.ProjectsRoot ?? DefaultProjectFolder();
        ProjectsRootText.TextChanged += (_, _) => UpdateGeneratedFolder();
        UpdateGeneratedFolder();
        AppendOutput("Select a recording and projects root, then click Process Recording.");
    }

    public string ManifestPath { get; private set; } = "";

    public bool OpenProjectAfterProcessing => OpenProjectCheck.IsChecked == true;

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select sonar recording",
            Filter = "Sonar recordings (*.dat;*.DAT;*.rsd;*.RSD;*.sl2;*.SL2;*.sl3;*.SL3;*.svlog;*.SVLOG;*.jsf;*.JSF;*.xtf;*.XTF)|*.dat;*.DAT;*.rsd;*.RSD;*.sl2;*.SL2;*.sl3;*.SL3;*.svlog;*.SVLOG;*.jsf;*.JSF;*.xtf;*.XTF|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputFileText.Text = dialog.FileName;
            UpdateGeneratedFolder();
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose or create the SonarDataPlayer project folder",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(ProjectsRootText.Text))
        {
            dialog.SelectedPath = ProjectsRootText.Text;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            ProjectsRootText.Text = dialog.SelectedPath;
            SaveProjectsRoot();
            UpdateGeneratedFolder();
        }
    }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var pythonPath, out var pingverterRoot, out var inputFile, out var outputFolder))
        {
            return;
        }

        ProcessButton.IsEnabled = false;
        ProcessButton.Content = "Processing...";
        OutputText.Clear();
        AppendOutput($"Python: {pythonPath}");
        AppendOutput($"PINGverter: {pingverterRoot}");
        AppendOutput($"Input: {inputFile}");
        AppendOutput($"Output: {outputFolder}");
        AppendOutput("");

        Directory.CreateDirectory(outputFolder);
        var exitCode = await RunPingverterAsync(pythonPath, pingverterRoot, inputFile, outputFolder);
        ManifestPath = Path.Combine(outputFolder, "manifest.json");

        if (exitCode == 0 && File.Exists(ManifestPath))
        {
            _processed = true;
            ProcessButton.Content = "Complete";
            ProcessButton.Background = new SolidColorBrush(Color.FromRgb(46, 138, 87));
            ProcessButton.BorderBrush = new SolidColorBrush(Color.FromRgb(92, 220, 139));
            AppendOutput("");
            AppendOutput($"Done. Wrote {ManifestPath}");
            if (OpenProjectAfterProcessing)
            {
                DialogResult = true;
            }
        }
        else
        {
            ProcessButton.IsEnabled = true;
            ProcessButton.Content = "Process Recording";
            AppendOutput("");
            AppendOutput(exitCode == 0
                ? "Conversion finished, but manifest.json was not created."
                : $"Conversion failed with exit code {exitCode}.");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _processed;
    }

    private bool ValidateInputs(out string pythonPath, out string pingverterRoot, out string inputFile, out string outputFolder)
    {
        pythonPath = MainWindow.FindPythonExecutable(_settings) ?? "";
        pingverterRoot = ResolvePingverterRoot(_settings) ?? "";
        inputFile = InputFileText.Text.Trim();
        outputFolder = GeneratedProjectFolder(inputFile);

        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            AppendOutput("No usable Python was found. Configure Python settings first.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(pingverterRoot) || !Directory.Exists(Path.Combine(pingverterRoot, "pingverter")))
        {
            AppendOutput("PINGverter root is not configured or does not contain a pingverter folder.");
            return false;
        }

        if (!File.Exists(inputFile))
        {
            AppendOutput("Input file does not exist.");
            return false;
        }

        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dat", ".rsd", ".sl2", ".sl3", ".svlog", ".jsf", ".xtf"
        };

        if (!supportedExtensions.Contains(ext))
        {
            AppendOutput("Input must be one of: .dat, .rsd, .sl2, .sl3, .svlog, .jsf, .xtf.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            AppendOutput("Choose a projects root folder.");
            return false;
        }

        SaveProjectsRoot();
        return true;
    }

    private async Task<int> RunPingverterAsync(string pythonPath, string pingverterRoot, string inputFile, string outputFolder)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                WorkingDirectory = pingverterRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(PingverterRunnerCode);
        process.StartInfo.ArgumentList.Add(pingverterRoot);
        process.StartInfo.ArgumentList.Add(inputFile);
        process.StartInfo.ArgumentList.Add(outputFolder);

        var done = new TaskCompletionSource<int>();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(args.Data));
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                Dispatcher.Invoke(() => AppendOutput(args.Data));
            }
        };
        process.Exited += (_, _) => done.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return await done.Task;
        }
        catch (Exception ex)
        {
            AppendOutput($"Failed to start Python: {ex.Message}");
            return -1;
        }
    }

    private void AppendOutput(string text)
    {
        OutputText.AppendText(text + Environment.NewLine);
        OutputText.ScrollToEnd();
    }

    private void UpdateGeneratedFolder()
    {
        var input = InputFileText.Text.Trim();
        GeneratedFolderText.Text = string.IsNullOrWhiteSpace(input)
            ? Path.Combine(ProjectsRootText.Text.Trim(), "<recording name>")
            : GeneratedProjectFolder(input);
    }

    private string GeneratedProjectFolder(string inputFile)
    {
        var root = ProjectsRootText.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(inputFile))
        {
            return "";
        }

        return Path.Combine(root, SafeProjectFolderName(Path.GetFileNameWithoutExtension(inputFile)));
    }

    private void SaveProjectsRoot()
    {
        var root = ProjectsRootText.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, _settings.ProjectsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings = _settings with { ProjectsRoot = root };
        _settings.Save();
    }

    private static string SafeProjectFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "SonarProject" : safe;
    }

    private static string DefaultProjectFolder()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ProcessedRecordings"));
    }

    private static string? ResolvePingverterRoot(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PingverterRoot) &&
            Directory.Exists(Path.Combine(settings.PingverterRoot, "pingverter")))
        {
            return settings.PingverterRoot;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "PINGverter")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "PINGverter"))
        };

        return candidates.FirstOrDefault(path => Directory.Exists(Path.Combine(path, "pingverter")));
    }

    private const string PingverterRunnerCode = """
import os
import sys

root, source, output = sys.argv[1], sys.argv[2], sys.argv[3]
if not os.path.isdir(os.path.join(root, "pingverter")):
    raise FileNotFoundError(f"PINGverter package folder not found: {root}")

sys.path.insert(0, root)

print(f"Loading PINGverter from {root}", flush=True)
print(f"Detected input format: {os.path.splitext(source)[1].lower()}", flush=True)

import pingverter

print("Parsing recording and writing project...", flush=True)
manifest = pingverter.export_sonar_data_player_project(
    source,
    output,
    include_pngs=True,
    nchunk=500,
    exportUnknown=True,
)
print(f"Manifest: {manifest}", flush=True)
print("Conversion complete.", flush=True)
""";
}
