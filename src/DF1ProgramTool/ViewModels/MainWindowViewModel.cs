using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using DF1Comm;
using DF1ProgramTool.Models;
using DF1ProgramTool.Services;
using ReactiveUI;

namespace DF1ProgramTool.ViewModels;

public class MainWindowViewModel : ReactiveObject, IDisposable
{
    // ─── Private state ────────────────────────────────────────────────────────
    private global::DF1Comm.DF1Comm? _df1;
    private CancellationTokenSource? _cts;
    private PlcInfo _currentPlcInfo = new(0, "Unknown", false, "Unknown");
    private bool _disposed;

    // ─── Backing fields ───────────────────────────────────────────────────────
    private bool   _isBusy;
    private bool   _isConnected;
    private string _statusText     = "Not connected";
    private double _progressValue;
    private string _progressMessage = string.Empty;
    private string _selectedPort    = string.Empty;
    private int    _selectedBaud    = 19200;
    private string _selectedParity  = "None";
    private decimal _targetNode     = 1;   // decimal for NumericUpDown two-way binding
    private decimal _myNode         = 0;

    // ─── Observable collections ───────────────────────────────────────────────
    public ObservableCollection<string> AvailablePorts  { get; } = new();
    public ObservableCollection<int>    BaudRates        { get; } = new() { 9600, 19200, 38400 };
    public ObservableCollection<string> ParityOptions    { get; } = new() { "None", "Even", "Odd" };

    // ─── Properties ───────────────────────────────────────────────────────────
    public string SelectedPort
    {
        get => _selectedPort;
        set => this.RaiseAndSetIfChanged(ref _selectedPort, value);
    }

    public int SelectedBaud
    {
        get => _selectedBaud;
        set => this.RaiseAndSetIfChanged(ref _selectedBaud, value);
    }

    public string SelectedParity
    {
        get => _selectedParity;
        set => this.RaiseAndSetIfChanged(ref _selectedParity, value);
    }

    /// <summary>NumericUpDown requires decimal? binding; clamped to 0–31 in the view.</summary>
    public decimal TargetNode
    {
        get => _targetNode;
        set => this.RaiseAndSetIfChanged(ref _targetNode, value);
    }

    public decimal MyNode
    {
        get => _myNode;
        set => this.RaiseAndSetIfChanged(ref _myNode, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            // Force re-evaluation of derived reactive properties
            this.RaisePropertyChanged(nameof(CanUpload));
            this.RaisePropertyChanged(nameof(CanDownload));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(CanUpload));
            this.RaisePropertyChanged(nameof(CanDownload));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => this.RaiseAndSetIfChanged(ref _progressMessage, value);
    }

    // Computed – no backing field needed; notified manually from IsBusy / IsConnected setters
    public bool CanUpload   => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;
    public bool CanDownload => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;

    // ─── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand      { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand   { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand       { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand     { get; }

    // ─── Constructor ─────────────────────────────────────────────────────────
    public MainWindowViewModel()
    {
        // canExecute observables derived from reactive properties
        var notBusy      = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canConnect   = this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected,
                               (busy, connected) => !busy && !connected);
        var canTransfer  = this.WhenAnyValue(x => x.CanUpload); // re-notified on IsBusy/IsConnected

        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts, notBusy);
        ConnectCommand      = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        DisconnectCommand   = ReactiveCommand.Create(Disconnect,
                                  this.WhenAnyValue(x => x.IsConnected));

        // Upload/Download use their own canExecute observables
        var canUpload   = this.WhenAnyValue(x => x.CanUpload);
        var canDownload = this.WhenAnyValue(x => x.CanDownload);
        UploadCommand   = ReactiveCommand.CreateFromTask(UploadAsync,   canUpload);
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAsync, canDownload);

        RefreshPorts();
    }

    // ─── Port management ─────────────────────────────────────────────────────
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var p in SerialPort.GetPortNames())
            AvailablePorts.Add(p);
        if (AvailablePorts.Count > 0)
            SelectedPort = AvailablePorts[0];
    }

    // ─── Connect / Disconnect ────────────────────────────────────────────────
    private async Task ConnectAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort))
        {
            await ShowMessageAsync("Error", "Select a COM port first.");
            return;
        }

        IsBusy = true;
        StatusText = "Connecting…";

        try
        {
            Parity parity = SelectedParity switch
            {
                "Even" => Parity.Even,
                "Odd"  => Parity.Odd,
                _      => Parity.None
            };

            // Dispose any previous instance
            DisposeDF1();

            _df1 = new global::DF1Comm.DF1Comm(SelectedPort, SelectedBaud, parity)
            {
                TargetNode = (int)TargetNode,
                MyNode     = (int)MyNode,
                CheckSum   = CheckSumOptions.Crc,
                Protocol   = "DF1"
            };

            await Task.Run(() => _df1.OpenComms());

            var plcInfo = await PlcIdentifier.IdentifyAsync(_df1);
            _currentPlcInfo = plcInfo;

            if (plcInfo.SupportsUploadDownload)
            {
                int mode = await Task.Run(() => _df1.GetRunMode());
                string modeStr = mode == 1 ? "RUN" : "PROG";
                StatusText = $"Connected | {plcInfo.Name} | {modeStr}";
            }
            else
            {
                StatusText = $"Connected | {plcInfo.Name} (upload/download not supported)";
            }

            IsConnected = true;
        }
        catch (Exception ex)
        {
            DisposeDF1();
            StatusText = "Connection failed";
            await ShowMessageAsync("Connection Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Disconnect()
    {
        DisposeDF1();
        _currentPlcInfo = new(0, "Unknown", false, "Unknown");
        IsConnected     = false;
        StatusText      = "Disconnected";
    }

    // ─── Upload ──────────────────────────────────────────────────────────────
    private async Task UploadAsync()
    {
        if (_df1 == null) return;

        var topLevel = GetMainWindow();
        if (topLevel == null) return;

        // Resolve mode string before building the filename
        string modeStr;
        try   { modeStr = await Task.Run(() => _df1.GetRunMode()) == 1 ? "RUN" : "PROG"; }
        catch { modeStr = "UNKNOWN"; }

        string defaultFileName = _currentPlcInfo.GetDefaultFileName(modeStr);

        var saveFile = await topLevel.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title              = "Save PLC Program",
                SuggestedFileName  = defaultFileName,
                DefaultExtension   = "bin",
                FileTypeChoices    = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Binary file")
                        { Patterns = new[] { "*.bin" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files")
                        { Patterns = new[] { "*.*" } }
                }
            });

        if (saveFile == null) return;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct);
            await svc.UploadToFileAsync(saveFile.Path.LocalPath);
        }, "Upload");
    }

    // ─── Download ────────────────────────────────────────────────────────────
    private async Task DownloadAsync()
    {
        if (_df1 == null) return;

        var topLevel = GetMainWindow();
        if (topLevel == null) return;

        var openFiles = await topLevel.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title         = "Select PLC Program File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Binary file")
                        { Patterns = new[] { "*.bin" } },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files")
                        { Patterns = new[] { "*.*" } }
                }
            });

        if (openFiles == null || openFiles.Count == 0) return;
        string filePath = openFiles[0].Path.LocalPath;

        bool confirmed = await ShowConfirmAsync(
            "Confirm Download",
            "This will overwrite the PLC program with the file contents.\nContinue?");
        if (!confirmed) return;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct);
            await svc.DownloadFromFileAsync(filePath);
        }, "Download");
    }

    // ─── Shared transfer runner ───────────────────────────────────────────────
    private async Task RunTransferAsync(
        Func<IProgress<string>, IProgress<double>, CancellationToken, Task> work,
        string operationName)
    {
        IsBusy        = true;
        ProgressValue = 0;
        ProgressMessage = string.Empty;

        _cts = new CancellationTokenSource();

        var progressMsg = new Progress<string>(msg => ProgressMessage = msg);
        var progressPct = new Progress<double>(pct => ProgressValue   = pct);

        try
        {
            await work(progressMsg, progressPct, _cts.Token);
            await ShowMessageAsync($"{operationName} Complete",
                $"{operationName} finished successfully.");
        }
        catch (OperationCanceledException)
        {
            await ShowMessageAsync("Cancelled", $"{operationName} was cancelled.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync($"{operationName} Error", ex.Message);
        }
        finally
        {
            IsBusy          = false;
            ProgressValue   = 0;
            ProgressMessage = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ─── Dialog helpers (native Avalonia – no external package needed) ───────
    private static async Task ShowMessageAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource();
        var okButton = new Button { Content = "OK", Width = 80,
            HorizontalAlignment = HorizontalAlignment.Center };

        var dialog = BuildDialog(title, message, okButton);
        okButton.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult();

        var owner = GetMainWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();

        await tcs.Task;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();
        var yesButton = new Button { Content = "Yes", Width = 70 };
        var noButton  = new Button { Content = "No",  Width = 70 };

        var buttonRow = new StackPanel
        {
            Orientation           = Orientation.Horizontal,
            HorizontalAlignment   = HorizontalAlignment.Center,
            Spacing               = 16,
            Children              = { yesButton, noButton }
        };

        var dialog = BuildDialog(title, message, buttonRow);
        
        // Set result when user clicks a button
        yesButton.Click += (_, _) => { dialog.Close(); tcs.TrySetResult(true);  };
        noButton .Click += (_, _) => { dialog.Close(); tcs.TrySetResult(false); };
        
        // DO NOT add a Closed handler here – it would overwrite the button result.

        var owner = GetMainWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show(); // Fallback, but may not block properly

        return await tcs.Task;
    }

    private static Window BuildDialog(string title, string message, Control actionControl)
    {
        return new Window
        {
            Title                   = title,
            Width                   = 340,
            SizeToContent           = SizeToContent.Height,
            CanResize               = false,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
            Content                 = new StackPanel
            {
                Margin  = new Thickness(20, 16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text                = message,
                        TextWrapping        = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment       = TextAlignment.Center
                    },
                    actionControl
                }
            }
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────
    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as Avalonia.Controls.Window;
        return null;
    }

    private void DisposeDF1()
    {
        try { _df1?.CloseComms(); } catch { /* ignore */ }
        _df1?.Dispose();
        _df1 = null;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        DisposeDF1();
    }
}
