using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using NetUsageMonitor.Common;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;

namespace NetUsageMonitor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly UsageTracker _tracker;
    private readonly IconProvider _icons;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ProcessRowViewModel> _rowMap = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    /// <summary>Raised on the UI thread after each snapshot is applied (used to refresh the chart).</summary>
    public event Action? SnapshotApplied;

    public MainViewModel(AppSettings settings, UsageTracker tracker, IconProvider icons, Dispatcher dispatcher)
    {
        _settings = settings;
        _tracker = tracker;
        _icons = icons;
        _dispatcher = dispatcher;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        // Off by default so rows stay put and are easy to click; user can enable "Auto-sort".
        RowsView.IsLiveSorting = _settings.AutoSort;
        foreach (var p in new[]
                 {
                     nameof(ProcessRowViewModel.DownRate), nameof(ProcessRowViewModel.UpRate),
                     nameof(ProcessRowViewModel.DownHour), nameof(ProcessRowViewModel.UpHour),
                     nameof(ProcessRowViewModel.TotalHour), nameof(ProcessRowViewModel.DisplayName),
                     nameof(ProcessRowViewModel.StatusText)
                 })
            RowsView.LiveSortingProperties.Add(p);
        RowsView.Filter = FilterRow;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.DownRate), ListSortDirection.Descending));

        ToggleKeepCommand = new RelayCommand(o => ToggleKeep(AsRow(o)), o => AsRow(o) != null);
        ToggleIgnoreCommand = new RelayCommand(o => ToggleIgnore(AsRow(o)), o => AsRow(o) != null);
        OnlyTrackThisCommand = new RelayCommand(o => OnlyTrackThis(AsRow(o)), o => AsRow(o) != null);
        TrackAllCommand = new RelayCommand(_ => TrackAll());
        DeleteAppRecordsCommand = new RelayCommand(o => DeleteAppRecords(AsRow(o)), o => AsRow(o) != null);
        OpenFileLocationCommand = new RelayCommand(o => OpenFileLocation(AsRow(o)), o => AsRow(o)?.ExePath != null);
        CopyNameCommand = new RelayCommand(o => CopyName(AsRow(o)), o => AsRow(o) != null);
        DeleteAllRecordsCommand = new RelayCommand(_ => DeleteAllRecords());
        ToggleRecordingCommand = new RelayCommand(_ => ToggleRecording());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv());
        TogglePauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
        BlockCommand = new RelayCommand(o => ToggleBlock(AsRow(o)), o => AsRow(o)?.ExePath != null);
        RecordConnectionsCommand = new RelayCommand(o => ToggleRecordConnections(AsRow(o)), o => AsRow(o) != null);
        ClearConnectionsCommand = new RelayCommand(o => ClearConnections(AsRow(o)), o => AsRow(o) != null);
        ClearCapCommand = new RelayCommand(o => ClearCap(AsRow(o)), o => AsRow(o)?.HasCap == true);
        ResetCapCommand = new RelayCommand(o => ResetCap(AsRow(o)), o => AsRow(o)?.HasCap == true);
        KillProcessCommand = new RelayCommand(o => KillProcess(AsRow(o)), o => AsRow(o)?.ExePath != null);

        UpdateStatusStrings();

        _tracker.Updated += OnTrackerUpdated;
    }

    // ---- Commands -----------------------------------------------------------

    public RelayCommand ToggleKeepCommand { get; }
    public RelayCommand ToggleIgnoreCommand { get; }
    public RelayCommand OnlyTrackThisCommand { get; }
    public RelayCommand TrackAllCommand { get; }
    public RelayCommand DeleteAppRecordsCommand { get; }
    public RelayCommand OpenFileLocationCommand { get; }
    public RelayCommand CopyNameCommand { get; }
    public RelayCommand DeleteAllRecordsCommand { get; }
    public RelayCommand ToggleRecordingCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand BlockCommand { get; }
    public RelayCommand RecordConnectionsCommand { get; }
    public RelayCommand ClearConnectionsCommand { get; }
    public RelayCommand ClearCapCommand { get; }
    public RelayCommand ResetCapCommand { get; }
    public RelayCommand KillProcessCommand { get; }

    private ProcessRowViewModel? AsRow(object? o) => (o as ProcessRowViewModel) ?? SelectedRow;

    // ---- Bound state --------------------------------------------------------

    private ProcessRowViewModel? _selectedRow;
    public ProcessRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) RowsView.Refresh(); }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PauseButtonText)); }
    }

    public string PauseButtonText => IsPaused ? "▶ 恢复视图" : "⏸ 暂停视图";

    public bool AutoSort
    {
        get => _settings.AutoSort;
        set
        {
            if (_settings.AutoSort == value) return;
            _settings.AutoSort = value;
            RowsView.IsLiveSorting = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    private string _totalDownText = "—";
    public string TotalDownText { get => _totalDownText; private set => SetProperty(ref _totalDownText, value); }

    private string _totalUpText = "—";
    public string TotalUpText { get => _totalUpText; private set => SetProperty(ref _totalUpText, value); }

    private string _dbSizeText = "";
    public string DbSizeText { get => _dbSizeText; private set => SetProperty(ref _dbSizeText, value); }

    private string _retentionText = "";
    public string RetentionText { get => _retentionText; private set => SetProperty(ref _retentionText, value); }

    private string _folderText = "";
    public string FolderText { get => _folderText; private set => SetProperty(ref _folderText, value); }

    private string _modeText = "";
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }

    private string _appCountText = "";
    public string AppCountText { get => _appCountText; private set => SetProperty(ref _appCountText, value); }

    private bool _isRecording = true;
    public bool IsRecording
    {
        get => _isRecording;
        private set { if (SetProperty(ref _isRecording, value)) OnPropertyChanged(nameof(RecordingText)); }
    }

    public string RecordingText => IsRecording ? "正在记录" : "记录已暂停";

    public AppSettings Settings => _settings;
    public UsageTracker Tracker => _tracker;

    // ---- Snapshot handling --------------------------------------------------

    private void OnTrackerUpdated(UsageSnapshot snapshot)
        => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplySnapshot(snapshot));

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        // Frozen view: keep the table still so the user can read/click. Engine keeps recording.
        if (IsPaused) return;

        double downRate = 0, upRate = 0;
        foreach (var g in snapshot.Groups)
        {
            if (!_rowMap.TryGetValue(g.GroupKey, out var row))
            {
                row = new ProcessRowViewModel(g.GroupKey);
                _rowMap[g.GroupKey] = row;
                Rows.Add(row);
            }
            row.Update(g, _icons, snapshot.RetentionMinutes);
            downRate += g.RecvRate;
            upRate += g.SentRate;
        }

        TotalDownText = ByteFormatter.Rate(downRate);
        TotalUpText = ByteFormatter.Rate(upRate);
        DbSizeText = "记录占用：" + ByteFormatter.Bytes(snapshot.DatabaseSizeBytes);
        AppCountText = $"{Rows.Count} 个应用";
        IsRecording = snapshot.IsRecording;

        SnapshotApplied?.Invoke();
    }

    private bool FilterRow(object o)
    {
        var r = (ProcessRowViewModel)o;
        if (!_settings.ShowIgnoredInList && r.IsIgnored) return false;

        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (r.ExePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ---- Command implementations -------------------------------------------

    private void ToggleKeep(ProcessRowViewModel? row)
    {
        if (row is null) return;
        bool keep = !row.IsKept;
        _settings.SetKept(row.GroupKey, keep);
        row.IsKept = keep;
        _settings.Save();
        UpdateStatusStrings();
    }

    private void ToggleIgnore(ProcessRowViewModel? row)
    {
        if (row is null) return;
        bool ignore = !row.IsIgnored;
        _settings.SetIgnored(row.GroupKey, ignore);
        row.IsIgnored = ignore;
        _settings.Save();
        RowsView.Refresh();
    }

    private void OnlyTrackThis(ProcessRowViewModel? row)
    {
        if (row is null) return;
        _settings.TrackingMode = TrackingMode.OnlyListed;
        _settings.OnlyListedKeys.Clear();
        _settings.SetOnlyListed(row.GroupKey, true);
        _settings.Save();
        UpdateStatusStrings();
        RowsView.Refresh();
    }

    private void TrackAll()
    {
        _settings.TrackingMode = TrackingMode.All;
        _settings.Save();
        UpdateStatusStrings();
        RowsView.Refresh();
    }

    private void DeleteAppRecords(ProcessRowViewModel? row)
    {
        if (row is null) return;
        var answer = MessageBox.Show(
            $"要删除“{row.DisplayName}”的全部已保存记录吗？\n实时监控仍会继续。",
            "删除记录", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _tracker.Database.DeleteApp(row.GroupKey);
    }

    private static void OpenFileLocation(ProcessRowViewModel? row)
    {
        if (row?.ExePath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void CopyName(ProcessRowViewModel? row)
    {
        if (row is null) return;
        try { Clipboard.SetText(row.DisplayName); } catch { /* ignore */ }
    }

    private void DeleteAllRecords()
    {
        var answer = MessageBox.Show(
            "要删除全部已保存的记录吗？\n此操作无法撤销。实时监控仍会继续。",
            "删除全部记录", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _tracker.Database.DeleteAllSamples();
    }

    private void ToggleRecording()
    {
        if (_tracker.IsRecording) _tracker.Stop();
        else _tracker.Start();
        IsRecording = _tracker.IsRecording;
    }

    private void ToggleBlock(ProcessRowViewModel? row)
    {
        if (row?.ExePath is not { } exe) return;
        bool newState = !row.IsBlocked;
        row.IsBlocked = newState; // optimistic
        string key = row.GroupKey;
        Task.Run(() =>
        {
            bool ok = _tracker.SetBlocked(key, exe, newState);
            if (!ok)
                _dispatcher.Invoke(() =>
                {
                    row.IsBlocked = !newState;
                    MessageBox.Show("无法修改防火墙规则。\n请确认程序正在以管理员身份运行。",
                        App.DisplayName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                });
        });
    }

    private void ToggleRecordConnections(ProcessRowViewModel? row)
    {
        if (row is null) return;
        bool on = !row.IsRecordingConnections;
        _settings.SetRecordingConnections(row.GroupKey, on);
        row.IsRecordingConnections = on;
        _settings.Save();
    }

    private void ClearConnections(ProcessRowViewModel? row)
    {
        if (row is null) return;
        var answer = MessageBox.Show(
            $"要清除“{row.DisplayName}”的连接记录吗？",
            "清除连接记录", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _tracker.Database.DeleteConnections(row.GroupKey);
    }

    /// <summary>Sets a data cap on the app (called by the cap dialog).</summary>
    public void ApplyCap(ProcessRowViewModel row, long limitBytes, CapPeriod period)
    {
        var cap = new AppCap
        {
            LimitBytes = limitBytes,
            Period = period,
            ResetUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Tripped = false
        };
        _settings.SetCap(row.GroupKey, cap);
        _settings.Save();
        row.HasCap = true;
        row.CapLimit = limitBytes;
        row.CapTripped = false;
    }

    private void ClearCap(ProcessRowViewModel? row)
    {
        if (row is null) return;
        if (row.CapTripped && row.ExePath is { } exe)
        {
            string key = row.GroupKey;
            Task.Run(() => _tracker.SetBlocked(key, exe, false));
            row.IsBlocked = false;
        }
        _settings.SetCap(row.GroupKey, null);
        _settings.Save();
        row.HasCap = false;
        row.CapTripped = false;
        row.CapLimit = 0;
    }

    private void ResetCap(ProcessRowViewModel? row)
    {
        if (row is null) return;
        string key = row.GroupKey;
        string? exe = row.ExePath;
        Task.Run(() => _tracker.ResetCap(key, exe));
        row.CapTripped = false;
        row.IsBlocked = false;
    }

    private void KillProcess(ProcessRowViewModel? row)
    {
        if (row?.ExePath is not { } exe) return;
        try
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            if (processes.Length == 0)
            {
                MessageBox.Show($"找不到“{row.DisplayName}”正在运行的进程。",
                    "结束进程", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            if (processes.Length == 1)
            {
                processes[0].Kill();
                MessageBox.Show($"已结束“{row.DisplayName}”的进程。",
                    "结束进程", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            else
            {
                var answer = MessageBox.Show(
                    $"找到“{row.DisplayName}”的 {processes.Length} 个进程。要全部结束吗？",
                    "结束进程", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    foreach (var p in processes)
                        p.Kill();
                    MessageBox.Show($"已结束全部 {processes.Length} 个进程。",
                        "结束进程", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法结束进程：{ex.Message}",
                "结束进程", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "将当前列表导出为 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"流量记录-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try { WriteCsv(dialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show("无法写入文件：" + ex.Message, "导出流量记录",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void WriteCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("应用,路径,下载速度（字节/秒）,上传速度（字节/秒）,时段下载（字节）,时段上传（字节）,本次运行下载（字节）,本次运行上传（字节）,状态");
        foreach (ProcessRowViewModel r in RowsView)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.DisplayName), Csv(r.ExePath ?? ""),
                ((long)r.DownRate), ((long)r.UpRate),
                r.DownHour, r.UpHour, r.SessionDown, r.SessionUp, Csv(r.StatusText)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string s)
        => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- Status strings -----------------------------------------------------

    public void UpdateStatusStrings()
    {
        int kept = _settings.GetKeptKeysSnapshot().Length;
        RetentionText = $"保留最近 {_settings.RetentionMinutes} 分钟" + (kept > 0 ? $" · {kept} 个应用永久保留" : "");
        FolderText = _settings.RecordsFolder;
        ModeText = _settings.TrackingMode == TrackingMode.All ? "跟踪所有应用" : "仅跟踪选定应用";
    }
}
