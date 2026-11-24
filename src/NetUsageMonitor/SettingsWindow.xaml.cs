using System.Windows;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;

namespace NetUsageMonitor;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UsageTracker _tracker;

    public SettingsWindow(AppSettings settings, UsageTracker tracker)
    {
        InitializeComponent();
        _settings = settings;
        _tracker = tracker;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        FolderBox.Text = _settings.RecordsFolder;
        RetentionBox.Text = _settings.RetentionMinutes.ToString();
        FlushBox.Text = _settings.FlushIntervalSeconds.ToString();
        ModeAll.IsChecked = _settings.TrackingMode == TrackingMode.All;
        ModeOnly.IsChecked = _settings.TrackingMode == TrackingMode.OnlyListed;
        ShowIgnoredBox.IsChecked = _settings.ShowIgnoredInList;
        BackgroundBox.IsChecked = _settings.KeepRecordingInBackground;
        StartupBox.IsChecked = _settings.StartWithWindows;
        StartMinBox.IsChecked = _settings.StartMinimized;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择记录保存文件夹",
            InitialDirectory = System.IO.Directory.Exists(FolderBox.Text) ? FolderBox.Text : ""
        };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private int ParseRetention()
        => int.TryParse(RetentionBox.Text, out int m) && m >= 1 ? m : _settings.RetentionMinutes;

    private void OnDeleteOld(object sender, RoutedEventArgs e)
    {
        if (Confirm("要删除超出保留时长的记录吗？\n标记为“永久保留记录”的应用不受影响。"))
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ParseRetention() * 60L;
            _tracker.Database.PruneExceptKept(cutoff, _settings.GetKeptKeysSnapshot());
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        if (Confirm("要删除全部已保存的记录吗？此操作无法撤销。"))
            _tracker.Database.DeleteAllSamples();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.RecordsFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? AppPaths.DefaultRecordsFolder : FolderBox.Text;
        _settings.RetentionMinutes = ParseRetention();
        if (int.TryParse(FlushBox.Text, out int flush) && flush >= 1)
            _settings.FlushIntervalSeconds = flush;
        _settings.TrackingMode = ModeOnly.IsChecked == true ? TrackingMode.OnlyListed : TrackingMode.All;
        _settings.ShowIgnoredInList = ShowIgnoredBox.IsChecked == true;
        _settings.KeepRecordingInBackground = BackgroundBox.IsChecked == true;
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.StartMinimized = StartMinBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool Confirm(string message)
        => MessageBox.Show(message, App.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;
}
