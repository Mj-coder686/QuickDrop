using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using QuickDrop.Core.Models;
using QuickDrop.Network.Discovery;
using QuickDrop.Network.Transfer;

namespace QuickDrop.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _operationCancellation;
    private SenderService? _activeSender;
    private string _pairingCode = "------";
    private string _receiveCode = string.Empty;
    private string _destinationFolder;
    private string _statusText = "准备就绪";
    private string _detailText = "选择发送或接收即可开始，无需账号。";
    private string _currentFile = "—";
    private string _speedText = "0 B/s";
    private string _etaText = "—";
    private string _transferredText = "0 B / 0 B";
    private string _routeText = "等待连接";
    private double _progressPercent;
    private bool _isBusy;
    private bool _canRetry;
    private OperationKind _lastOperation;

    public MainViewModel()
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _destinationFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "QuickDrop");
    }

    public ObservableCollection<string> SelectedItems { get; } = [];

    public Func<DeviceIdentity, Task<bool>>? ConfirmDeviceAsync { get; set; }

    public string PairingCode
    {
        get => _pairingCode;
        private set => SetProperty(ref _pairingCode, value);
    }

    public string ReceiveCode
    {
        get => _receiveCode;
        set => SetProperty(ref _receiveCode, new string((value ?? string.Empty).Where(char.IsAsciiDigit).Take(6).ToArray()));
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set => SetProperty(ref _destinationFolder, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        private set => SetProperty(ref _currentFile, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    public string TransferredText
    {
        get => _transferredText;
        private set => SetProperty(ref _transferredText, value);
    }

    public string RouteText
    {
        get => _routeText;
        private set => SetProperty(ref _routeText, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanStart));
            }
        }
    }

    public bool CanStart => !IsBusy;

    public bool CanRetry
    {
        get => _canRetry;
        private set => SetProperty(ref _canRetry, value);
    }

    public string SelectionSummary => SelectedItems.Count == 0
        ? "还没有选择内容"
        : $"已选择 {SelectedItems.Count} 项";

    public void AddSelectedItems(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !SelectedItems.Contains(path, StringComparer.OrdinalIgnoreCase)))
        {
            SelectedItems.Add(path);
        }

        RaisePropertyChanged(nameof(SelectionSummary));
    }

    public void ClearSelectedItems()
    {
        SelectedItems.Clear();
        RaisePropertyChanged(nameof(SelectionSummary));
    }

    public async Task StartSendingAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedItems.Count == 0)
        {
            SetFriendlyError("请先选择要发送的文件或文件夹。", false);
            return;
        }

        _lastOperation = OperationKind.Send;
        await RunOperationAsync(async cancellationToken =>
        {
            UpdateStatus("正在整理文件清单", "大文件不会一次性读入内存。", "准备中");
            _activeSender = await SenderService.CreateAsync(SelectedItems.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
            _activeSender.ProgressChanged += OnProgressChanged;
            _activeSender.StatusChanged += status => PostToUi(() => StatusText = TranslateStatus(status));
            _activeSender.ConfirmDeviceAsync = ConfirmOnUiAsync;
            PostToUi(() =>
            {
                PairingCode = FormatPairingCode(_activeSender.PairingCode);
                StatusText = "等待对方输入配对码";
                DetailText = "把这 6 位数字告诉接收方；有效期 10 分钟。";
                RouteText = "正在广播局域网会话";
            });

            var result = await _activeSender.RunAsync(cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                StatusText = "发送完成";
                DetailText = "所有文件都已由接收端通过 SHA-256 校验。";
                RouteText = TranslateRoute(result.RouteDescription);
                CanRetry = false;
            });
        }).ConfigureAwait(false);
    }

    public async Task StartReceivingAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (ReceiveCode.Length != 6)
        {
            SetFriendlyError("请输入完整的 6 位配对码。", false);
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            SetFriendlyError("请选择保存位置。", false);
            return;
        }

        _lastOperation = OperationKind.Receive;
        await RunOperationAsync(async cancellationToken =>
        {
            var receiver = new ReceiverService(new LanRouteProvider());
            receiver.ProgressChanged += OnProgressChanged;
            receiver.StatusChanged += status => PostToUi(() => StatusText = TranslateStatus(status));
            receiver.SenderFound += sender => PostToUi(() =>
            {
                DetailText = $"已找到 {sender.Name}，正在等待发送方确认。";
                RouteText = "局域网直连";
            });

            var result = await receiver.ReceiveAsync(ReceiveCode, DestinationFolder, cancellationToken).ConfigureAwait(false);
            PostToUi(() =>
            {
                StatusText = "接收完成";
                DetailText = $"文件已保存到 {DestinationFolder}";
                RouteText = TranslateRoute(result.RouteDescription);
                CanRetry = false;
            });
        }).ConfigureAwait(false);
    }

    public Task RetryAsync() => _lastOperation switch
    {
        OperationKind.Send => StartSendingAsync(),
        OperationKind.Receive => StartReceivingAsync(),
        _ => Task.CompletedTask
    };

    public void Cancel()
    {
        _operationCancellation?.Cancel();
        StatusText = "正在取消";
        DetailText = "已接收的部分会保留，下次可从断点继续。";
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        ResetProgress();
        IsBusy = true;
        CanRetry = false;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            await operation(_operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PostToUi(() =>
            {
                StatusText = "已取消";
                DetailText = "接收端的临时文件已保留，可点击重试继续。";
                CanRetry = true;
            });
        }
        catch (TimeoutException)
        {
            PostToUi(() => SetFriendlyError("没有找到对应设备。请确认两台电脑连接到同一网络，并检查配对码。", true));
        }
        catch (UnauthorizedAccessException exception)
        {
            PostToUi(() => SetFriendlyError($"设备确认未通过：{exception.Message}", true));
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or InvalidDataException)
        {
            PostToUi(() => SetFriendlyError($"传输中断：{exception.Message}", true));
        }
        finally
        {
            if (_activeSender is not null)
            {
                await _activeSender.DisposeAsync().ConfigureAwait(false);
                _activeSender = null;
            }

            _operationCancellation.Dispose();
            _operationCancellation = null;
            PostToUi(() => IsBusy = false);
        }
    }

    private Task<bool> ConfirmOnUiAsync(DeviceIdentity device, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(async _ =>
        {
            try
            {
                var callback = ConfirmDeviceAsync;
                completion.TrySetResult(callback is not null && await callback(device));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void OnProgressChanged(TransferProgress progress) => PostToUi(() =>
    {
        ProgressPercent = progress.Fraction * 100;
        CurrentFile = string.IsNullOrWhiteSpace(progress.CurrentFile) ? "—" : progress.CurrentFile;
        SpeedText = FormatBytes(progress.BytesPerSecond) + "/s";
        EtaText = FormatEta(progress.EstimatedRemaining);
        TransferredText = $"{FormatBytes(progress.TransferredBytes)} / {FormatBytes(progress.TotalBytes)}";
        StatusText = progress.State == "Completed" ? "正在完成校验" : "正在传输";
    });

    private void ResetProgress()
    {
        ProgressPercent = 0;
        CurrentFile = "—";
        SpeedText = "0 B/s";
        EtaText = "—";
        TransferredText = "0 B / 0 B";
        RouteText = "正在选择最佳路径";
    }

    private void UpdateStatus(string status, string detail, string route)
    {
        StatusText = status;
        DetailText = detail;
        RouteText = route;
    }

    private void SetFriendlyError(string message, bool retryable)
    {
        StatusText = "需要处理";
        DetailText = message;
        CanRetry = retryable;
    }

    private void PostToUi(Action action)
    {
        if (SynchronizationContext.Current == _uiContext)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private static string FormatPairingCode(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta is null || eta.Value.TotalDays > 1) return "—";
        if (eta.Value.TotalHours >= 1) return $"约 {eta.Value.Hours} 小时 {eta.Value.Minutes} 分";
        if (eta.Value.TotalMinutes >= 1) return $"约 {eta.Value.Minutes} 分 {eta.Value.Seconds} 秒";
        return $"约 {Math.Max(1, eta.Value.Seconds)} 秒";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, unit == 0 ? "{0:0} {1}" : "{0:0.0} {1}", value, units[unit]);
    }

    private static string TranslateStatus(string status)
    {
        if (status.StartsWith("Connection interrupted", StringComparison.Ordinal)) return "连接中断，等待对方重试";
        if (status.StartsWith("Confirmation required", StringComparison.Ordinal)) return "请确认接收设备";
        return status switch
        {
            "Waiting for a receiver" => "等待接收设备",
            "Transfer complete" => "传输完成",
            "Receiver rejected" => "已拒绝接收设备",
            "Searching the local network" => "正在局域网中查找设备",
            "Device confirmed; preparing files" => "设备已确认，正在准备文件",
            "All files verified" => "全部文件校验完成",
            _ => status
        };
    }

    private static string TranslateRoute(string route) => route switch
    {
        "LAN direct" => "局域网直连",
        "Internet P2P" => "公网点对点",
        "Relay" => "服务器中继",
        _ => route
    };

    private enum OperationKind
    {
        None,
        Send,
        Receive
    }
}
