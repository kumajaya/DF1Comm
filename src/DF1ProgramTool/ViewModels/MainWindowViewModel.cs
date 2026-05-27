using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private PlcInfo _currentPlcInfo = new PlcInfo(0, "Unknown", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");

    private bool _disposed;

    // Log buffer – capped at MaxLogLines
    private readonly StringBuilder _logBuffer = new();
    private int _logLineCount;
    private const int MaxLogLines = 500;

    // ─── Backing fields ───────────────────────────────────────────────────────
    private bool    _isBusy;
    private bool    _isConnected;
    private string  _statusText      = "Not connected";
    private double  _progressValue;
    private string  _progressMessage = string.Empty;
    private string  _selectedPort    = string.Empty;
    private int     _selectedBaud    = 19200;
    private string  _selectedParity  = "None";
    private string  _selectedChecksum  = "Crc";
    private decimal _targetNode      = 1;
    private decimal _myNode          = 0;
    private string  _logText         = string.Empty;

    // ─── Observable collections ───────────────────────────────────────────────
    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<int>    BaudRates      { get; } = new() { 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public ObservableCollection<string> ParityOptions  { get; } = new() { "None", "Even", "Odd" };
    public ObservableCollection<string> ChecksumOptions { get; } = new() { "Crc", "Bcc" };

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

    public string SelectedChecksum
    {
        get => _selectedChecksum;
        set => this.RaiseAndSetIfChanged(ref _selectedChecksum, value);
    }

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

    /// <summary>
    /// Append-only log string bound to a read-only TextBox.
    /// Raises PropertyChanged on every append so the view can scroll to end.
    /// </summary>
    public string LogText
    {
        get => _logText;
        private set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    public bool CanUpload   => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;
    public bool CanDownload => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;

    // ─── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand      { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand   { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand       { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand     { get; }
    public ReactiveCommand<Unit, Unit> ClearLogCommand     { get; }

    private void OnRawFrameSent(object? sender, byte[] frame)     => AppendLog($"TX  {FrameDecoder.Hex(frame)}\n    {FrameDecoder.Decode(frame)}");
    private void OnRawFrameReceived(object? sender, byte[] frame) => AppendLog($"RX  {FrameDecoder.Hex(frame)}\n    {FrameDecoder.Decode(frame)}");

    // ─── Constructor ─────────────────────────────────────────────────────────
    public MainWindowViewModel()
    {
        var notBusy     = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canConnect  = this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected,
                              (busy, connected) => !busy && !connected);
        var canUpload   = this.WhenAnyValue(x => x.CanUpload);
        var canDownload = this.WhenAnyValue(x => x.CanDownload);

        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts, notBusy);
        ConnectCommand      = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        DisconnectCommand = ReactiveCommand.Create(Disconnect,
                        this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy,
                            (connected, busy) => connected && !busy));
        UploadCommand       = ReactiveCommand.CreateFromTask(UploadAsync,   canUpload);
        DownloadCommand     = ReactiveCommand.CreateFromTask(DownloadAsync, canDownload);
        ClearLogCommand     = ReactiveCommand.Create(ClearLog);

        RefreshPorts();
    }

    // ─── Logging ─────────────────────────────────────────────────────────────
    private void AppendLog(string line)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Remove oldest line if buffer exceeds max allowed lines
            if (_logLineCount >= MaxLogLines)
            {
                string s = _logBuffer.ToString();
                int nl = s.IndexOf('\n');
                if (nl >= 0)
                    _logBuffer.Remove(0, nl + 1);
                else
                    _logBuffer.Clear();
                _logLineCount--;
            }

            string timeStamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logBuffer.Append(timeStamp);
            _logBuffer.Append("  ");

            // If line contains a newline (TX/RX + decoded payload), indent the second line
            if (line.Contains('\n'))
            {
                int idx = line.IndexOf('\n');
                string firstLine = line.Substring(0, idx);
                string secondLine = line.Substring(idx + 1);
                _logBuffer.AppendLine(firstLine);
                // Indent second line to align with text after timestamp
                _logBuffer.Append(new string(' ', timeStamp.Length + 2));
                _logBuffer.Append(secondLine);
                _logBuffer.AppendLine();
                _logLineCount += 2;
            }
            else
            {
                _logBuffer.AppendLine(line);
                _logLineCount++;
            }

            LogText = _logBuffer.ToString();
        });
    }

    private void ClearLog()
    {
        _logBuffer.Clear();
        _logLineCount = 0;
        LogText = string.Empty;
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
        AppendLog($"Connecting to {SelectedPort} @ {SelectedBaud} baud ({SelectedParity} parity, {SelectedChecksum} checksum)…");

        try
        {
            Parity parity = SelectedParity switch
            {
                "Even" => Parity.Even,
                "Odd"  => Parity.Odd,
                _      => Parity.None
            };

            DisposeDF1();

            _df1 = new global::DF1Comm.DF1Comm(SelectedPort, SelectedBaud, parity)
            {
                TargetNode = (int)TargetNode,
                MyNode     = (int)MyNode,
                CheckSum   = SelectedChecksum == "Crc" ? CheckSumOptions.Crc : CheckSumOptions.Bcc,
                Protocol   = "DF1"
            };

            await Task.Run(() => _df1.OpenComms());
            AppendLog("Port opened.");

            var plcInfo = await PlcIdentifier.IdentifyAsync(_df1);
            _currentPlcInfo = plcInfo;
            AppendLog($"Identified: {plcInfo.Name} (0x{plcInfo.ProcessorType:X2}) " +
                      $"Upload/Download={plcInfo.SupportsUploadDownload}");

            if (plcInfo.SupportsUploadDownload)
            {
                // Mode already read from diagnostic status in PlcIdentifier
                string modeStr = plcInfo.ModeStr;
                StatusText = $"Connected | {plcInfo.Name} (0x{plcInfo.ProcessorType:X2}) | {modeStr}";
                AppendLog($"Mode: {modeStr}");
            }
            else
            {
                StatusText = $"Connected | {plcInfo.Name} (upload/download not supported)";
            }

            IsConnected = true;

            _df1.RawFrameSent     += OnRawFrameSent;
            _df1.RawFrameReceived += OnRawFrameReceived;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
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
        AppendLog("Disconnecting…");
        DisposeDF1();
        _currentPlcInfo = new PlcInfo(0, "Unknown", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");
        IsConnected     = false;
        StatusText      = "Disconnected";
        AppendLog("Disconnected.");
    }

    /// <summary>
    /// Refresh PLC status (mode and processor info) after upload/download.
    /// Updates StatusText and optionally _currentPlcInfo if processor type changed.
    /// </summary>
    private async Task RefreshPlcStatusAsync()
    {
        if (_df1 == null) return;
        try
        {
            byte[]? data = await Task.Run(() => _df1.GetDiagnosticStatusRaw());
            if (data != null && data.Length > 18)
            {
                byte modeByte = data[18];
                string modeStr = (modeByte == 0x06 || modeByte == 0x1E) ? "RUN" : "PROG";
                _currentPlcInfo = _currentPlcInfo with { ModeStr = modeStr };
                StatusText = $"Connected | {_currentPlcInfo.Name} | {modeStr}";
            }
            else
            {
                AppendLog("Failed to refresh PLC status: invalid diagnostic data");
            }
        }
        catch (Exception ex)
        {
            // Log error but don't disrupt UI
            AppendLog($"Failed to refresh PLC status: {ex.Message}");
        }
    }

    // ─── Upload ──────────────────────────────────────────────────────────────
    private async Task UploadAsync()
    {
        if (_df1 == null) return;

        var topLevel = GetMainWindow();
        if (topLevel == null) return;

        // Prefer the mode already read at connect; fall back to a fresh diagnostic read if you want latest mode
        string defaultFileName = _currentPlcInfo.GetDefaultFileName(_currentPlcInfo.ModeStr);

        var saveFile = await topLevel.StorageProvider.SaveFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title             = "Save PLC Program",
                SuggestedFileName = defaultFileName,
                DefaultExtension  = "bin",
                FileTypeChoices   = new[]
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
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct, _currentPlcInfo);
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
                Title          = "Select PLC Program File",
                AllowMultiple  = false,
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

        // Get target PLC info
        int targetProc = _currentPlcInfo.ProcessorType;
        string targetBulletin = _currentPlcInfo.Bulletin;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct);
            await svc.DownloadFromFileAsync(filePath, targetProc, targetBulletin);
        }, "Download");
    }

    // ─── Shared transfer runner ───────────────────────────────────────────────
    private async Task RunTransferAsync(
        Func<IProgress<string>, IProgress<double>, CancellationToken, Task> work,
        string operationName)
    {
        IsBusy          = true;
        ProgressValue   = 0;
        ProgressMessage = string.Empty;
        AppendLog($"--- {operationName} started ---");

        var cts = new CancellationTokenSource();
        var prevCts = Interlocked.Exchange(ref _cts, cts);
        prevCts?.Cancel();
        prevCts?.Dispose();

        var progressMsg = new Progress<string>(msg =>
        {
            ProgressMessage = msg;
            AppendLog(msg);
        });
        var progressPct = new Progress<double>(pct => ProgressValue = pct);

        try
        {
            await work(progressMsg, progressPct, _cts.Token);
            AppendLog($"--- {operationName} complete ---");
            await RefreshPlcStatusAsync();
            await ShowMessageAsync($"{operationName} Complete",
                $"{operationName} finished successfully.");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"--- {operationName} cancelled ---");
            await ShowMessageAsync("Cancelled", $"{operationName} was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            await ShowMessageAsync($"{operationName} Error", ex.Message);
        }
        finally
        {
            IsBusy          = false;
            ProgressValue   = 0;
            ProgressMessage = string.Empty;
            var doneCts = Interlocked.Exchange(ref _cts, null);
            doneCts?.Dispose();
        }
    }

    // ─── Dialog helpers ───────────────────────────────────────────────────────
    private static async Task ShowMessageAsync(string title, string message)
    {
        var tcs      = new TaskCompletionSource();
        var okButton = new Button { Content = "OK", Width = 80,
            HorizontalAlignment = HorizontalAlignment.Center };
        var dialog   = BuildDialog(title, message, okButton);
        okButton.Click += (_, _) => dialog.Close();
        dialog.Closed  += (_, _) => tcs.TrySetResult();
        var owner = GetMainWindow();
        if (owner != null) await dialog.ShowDialog(owner);
        else               dialog.Show();
        await tcs.Task;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message)
    {
        bool result   = false;   // default: No / dismissed
        var yesButton = new Button { Content = "Yes", Width = 70 };
        var noButton  = new Button { Content = "No",  Width = 70 };
        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing             = 16,
            Children            = { yesButton, noButton }
        };
        var dialog = BuildDialog(title, message, buttonRow);

        // Buttons set the result then close.
        // Closing via Alt+F4 / X leaves result = false (treat as cancel).
        yesButton.Click += (_, _) => { result = true;  dialog.Close(); };
        noButton .Click += (_, _) => { result = false; dialog.Close(); };

        var owner = GetMainWindow();
        if (owner != null) await dialog.ShowDialog(owner);
        else               dialog.Show();

        return result;
    }

    private static Window BuildDialog(string title, string message, Control actionControl) =>
        new()
        {
            Title                 = title,
            Width                 = 340,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new StackPanel
            {
                Margin  = new Avalonia.Thickness(20, 16),
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

    // ─── Helpers ─────────────────────────────────────────────────────────────
    private static Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as Window;
        return null;
    }

    private void DisposeDF1()
    {
        if (_df1 == null) return;
        _df1.RawFrameSent     -= OnRawFrameSent;
        _df1.RawFrameReceived -= OnRawFrameReceived;
        try { _df1.CloseComms(); } catch { /* ignore */ }
        _df1.Dispose();
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
