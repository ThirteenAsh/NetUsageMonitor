using System.Windows.Media;
using NetUsageMonitor.Common;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;

namespace NetUsageMonitor.ViewModels;

public enum ProcessStatus
{
    Idle,
    Active,
    Kept,
    KeptActive,
    Ignored,
    NotTracked,
    Blocked,
    Capped
}

/// <summary>One row in the apps grid; updated in place each tick so sorting/selection stay stable.</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    public string GroupKey { get; }

    public ProcessRowViewModel(string groupKey)
    {
        GroupKey = groupKey;
    }

    private string _displayName = "";
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }

    private string? _exePath;
    public string? ExePath { get => _exePath; private set => SetProperty(ref _exePath, value); }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; private set => SetProperty(ref _icon, value); }

    private double _downRate;
    public double DownRate { get => _downRate; private set => SetProperty(ref _downRate, value); }

    private double _upRate;
    public double UpRate { get => _upRate; private set => SetProperty(ref _upRate, value); }

    private long _downHour;
    public long DownHour { get => _downHour; private set => SetProperty(ref _downHour, value); }

    private long _upHour;
    public long UpHour { get => _upHour; private set => SetProperty(ref _upHour, value); }

    private long _totalHour;
    public long TotalHour { get => _totalHour; private set => SetProperty(ref _totalHour, value); }

    private long _sessionDown;
    public long SessionDown { get => _sessionDown; private set => SetProperty(ref _sessionDown, value); }

    private long _sessionUp;
    public long SessionUp { get => _sessionUp; private set => SetProperty(ref _sessionUp, value); }

    private bool _isIgnored;
    public bool IsIgnored { get => _isIgnored; set => SetProperty(ref _isIgnored, value); }

    private bool _isKept;
    public bool IsKept { get => _isKept; set => SetProperty(ref _isKept, value); }

    private bool _isRecorded;
    public bool IsRecorded { get => _isRecorded; private set => SetProperty(ref _isRecorded, value); }

    private bool _isBlocked;
    public bool IsBlocked { get => _isBlocked; set => SetProperty(ref _isBlocked, value); }

    private bool _isRecordingConnections;
    public bool IsRecordingConnections { get => _isRecordingConnections; set => SetProperty(ref _isRecordingConnections, value); }

    private bool _hasCap;
    public bool HasCap { get => _hasCap; set => SetProperty(ref _hasCap, value); }

    private long _capLimit;
    public long CapLimit { get => _capLimit; set => SetProperty(ref _capLimit, value); }

    private long _capUsed;
    public long CapUsed { get => _capUsed; set => SetProperty(ref _capUsed, value); }

    private bool _capTripped;
    public bool CapTripped { get => _capTripped; set => SetProperty(ref _capTripped, value); }

    private ProcessStatus _status;
    public ProcessStatus Status
    {
        get => _status;
        private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => Status switch
    {
        ProcessStatus.Active => "活跃",
        ProcessStatus.Kept => "永久保留",
        ProcessStatus.KeptActive => "永久保留 · 活跃",
        ProcessStatus.Ignored => "已忽略",
        ProcessStatus.NotTracked => "未跟踪",
        ProcessStatus.Blocked => "已禁止联网",
        ProcessStatus.Capped => "已达限额",
        _ => "空闲"
    };

    // ---- Derived (for the detail panel) ----
    private int _retentionMinutes = 60;
    public double CapPercent => HasCap && CapLimit > 0 ? Math.Min(100.0, CapUsed * 100.0 / CapLimit) : 0;
    public string CapText => HasCap ? $"{ByteFormatter.Bytes(CapUsed)} / {ByteFormatter.Bytes(CapLimit)}" : "未设置限额";
    public string AvgPerMinDownText => ByteFormatter.Bytes(DownHour / _retentionMinutes) + "/分钟";
    public string AvgPerMinUpText => ByteFormatter.Bytes(UpHour / _retentionMinutes) + "/分钟";
    public string BlockText => IsBlocked ? "恢复联网" : "禁止联网";
    public string RecordConnText => IsRecordingConnections ? "停止记录连接" : "记录连接";

    public void Update(GroupUsage g, IconProvider icons, int retentionMinutes)
    {
        DisplayName = g.DisplayName;
        if (ExePath != g.ExePath)
        {
            ExePath = g.ExePath;
            Icon = icons.GetIcon(g.ExePath);
        }
        DownRate = g.RecvRate;
        UpRate = g.SentRate;
        DownHour = g.HourReceived;
        UpHour = g.HourSent;
        TotalHour = g.HourReceived + g.HourSent;
        SessionDown = g.SessionReceived;
        SessionUp = g.SessionSent;
        IsIgnored = g.IsIgnored;
        IsKept = g.IsKept;
        IsRecorded = g.IsRecorded;
        IsBlocked = g.IsBlocked;
        IsRecordingConnections = g.IsRecordingConnections;
        HasCap = g.HasCap;
        CapLimit = g.CapLimitBytes;
        CapUsed = g.CapUsedBytes;
        CapTripped = g.CapTripped;
        _retentionMinutes = Math.Max(1, retentionMinutes);
        Status = ComputeStatus(g);

        // Refresh derived values shown in the detail panel.
        OnPropertyChanged(nameof(CapPercent));
        OnPropertyChanged(nameof(CapText));
        OnPropertyChanged(nameof(AvgPerMinDownText));
        OnPropertyChanged(nameof(AvgPerMinUpText));
        OnPropertyChanged(nameof(BlockText));
        OnPropertyChanged(nameof(RecordConnText));
    }

    private static ProcessStatus ComputeStatus(GroupUsage g)
    {
        if (g.IsBlocked) return ProcessStatus.Blocked;
        if (g.IsIgnored) return ProcessStatus.Ignored;
        if (!g.IsRecorded) return ProcessStatus.NotTracked;
        bool active = g.RecvRate >= 1 || g.SentRate >= 1;
        if (g.HasCap && g.CapTripped) return ProcessStatus.Capped;
        if (g.IsKept) return active ? ProcessStatus.KeptActive : ProcessStatus.Kept;
        return active ? ProcessStatus.Active : ProcessStatus.Idle;
    }
}
