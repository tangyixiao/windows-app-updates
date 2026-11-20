using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsAppUpdates;

public partial class MainWindow : Window
{
    private readonly IUpdateProvider provider;
    private readonly HistoryStore historyStore;
    private readonly ObservableCollection<UpdateEntry> history = [];
    private readonly CancellationTokenSource lifetime = new();
    private UpdateRow[] rows = [];
    private ICollectionView? visibleRows;
    private string scanDetails = "";
    private string historyProblem = "";
    private bool historyReadFailed;
    private bool checking;
    private bool upgrading;
    private bool stopRequested;
    private bool loaded;
    private bool scanFailed;
    private int omitted;
    private bool changingSelection;

    public MainWindow(IUpdateProvider provider, HistoryStore historyStore)
    {
        this.provider = provider;
        this.historyStore = historyStore;
        InitializeComponent();
        HistoryGrid.ItemsSource = history;
        try
        {
            foreach (var entry in historyStore.Read())
                history.Add(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            historyReadFailed = true;
            historyProblem = $"无法读取本机更新记录。{ex.Message}";
            HistoryEmpty.Text = "无法读取本机记录";
        }

        SyncControls();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded)
            return;
        loaded = true;
        await RefreshAsync();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (upgrading)
        {
            e.Cancel = true;
            MessageBox.Show(this, "安装仍在进行。可以点「本项完成后停止」，等当前安装结束再关闭。", Title);
            return;
        }
        lifetime.Cancel();
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? batchSummary = null)
    {
        if (checking || upgrading)
            return;

        checking = true;
        scanFailed = false;
        scanDetails = "";
        SetStatus(batchSummary is null ? "正在检查 WinGet 社区源…" : $"{batchSummary}。正在重新检查…");
        SyncControls();
        try
        {
            var result = await provider.ScanAsync(lifetime.Token);
            omitted = result.Omitted;
            SetRows(result.Packages);
            scanDetails = result.Output;
            LastScan.Text = $"上次检查 {DateTime.Now:HH:mm:ss}";
            string summary = $"可更新 {rows.Length} 项";
            if (result.Omitted > 0)
                summary += $"，另有 {result.Omitted} 条需查看检查详情";
            SetStatus(batchSummary is null ? summary : $"{batchSummary}。{summary}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            scanFailed = true;
            SetRows([]);
            scanDetails = ex.Message;
            LastScan.Text = "检查失败";
            SetStatus(batchSummary is null ? "检查失败，请查看检查详情。" : $"{batchSummary}。重新检查失败。");
        }
        finally
        {
            checking = false;
            SyncControls();
        }
    }

    private void SetRows(IReadOnlyList<PackageUpdate> packages)
    {
        rows = packages.Select(package => new UpdateRow(package)).ToArray();
        foreach (var row in rows)
            row.PropertyChanged += (_, _) => { if (!changingSelection) SyncControls(); };
        visibleRows = new ListCollectionView(rows) { Filter = item => MatchesSearch((UpdateRow)item) };
        UpdatesGrid.ItemsSource = visibleRows;
    }

    private bool MatchesSearch(UpdateRow row)
    {
        string query = Search.Text.Trim();
        return row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        visibleRows?.Refresh();
        SyncControls();
    }

    private void PageChanged(object sender, RoutedEventArgs e)
    {
        if (HistoryPage is null || UpdatesPage is null)
            return;
        bool showHistory = HistoryTab.IsChecked == true;
        HistoryPage.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPage.Visibility = showHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SelectVisibleClicked(object sender, RoutedEventArgs e) =>
        ChangeSelection(visibleRows!.Cast<UpdateRow>(), true);

    private void ClearClicked(object sender, RoutedEventArgs e) => ChangeSelection(rows, false);

    private void ChangeSelection(IEnumerable<UpdateRow> items, bool selected)
    {
        changingSelection = true;
        foreach (var row in items)
            row.Selected = selected;
        changingSelection = false;
        SyncControls();
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (checking || upgrading || scanFailed)
            return;

        var selected = rows.Where(row => row.Selected).Select(row => row.Package).ToArray();
        if (selected.Length == 0 || !ConfirmUpdates(selected))
            return;

        upgrading = true;
        stopRequested = false;
        SyncControls();
        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        int reviewCount = 0;
        try
        {
            foreach (var package in selected)
            {
                if (stopRequested)
                    break;

                SetStatus($"正在处理 {succeeded + failed + skipped + 1}/{selected.Length}　{package.Name}");
                CommandResult result;
                try
                {
                    result = await provider.UpgradeAsync(package);
                }
                catch (Exception ex)
                {
                    result = new CommandResult(ex.HResult, ex.Message);
                }

                if (result.ExitCode is null)
                    skipped++;
                else if (result.ExitCode == 0)
                    succeeded++;
                else
                    failed++;
                if (result.ExitCode == 0 && result.NeedsReview)
                    reviewCount++;
                RecordAttempt(package, result);
            }
        }
        finally
        {
            upgrading = false;
            SyncControls();
        }

        int remaining = selected.Length - succeeded - failed - skipped;
        string summary = $"WinGet 报告成功 {succeeded} 项，失败 {failed} 项";
        if (skipped > 0)
            summary += $"，跳过 {skipped} 项";
        if (reviewCount > 0)
            summary += $"，{reviewCount} 项需检查详情";
        if (remaining > 0)
            summary += $"，未执行 {remaining} 项";
        if (failed + skipped + reviewCount > 0)
            HistoryTab.IsChecked = true;
        await RefreshAsync(summary);
    }

    private void StopClicked(object sender, RoutedEventArgs e)
    {
        stopRequested = true;
        SetStatus("当前安装结束后停止，剩余应用将跳过。");
        SyncControls();
    }

    private void RecordAttempt(PackageUpdate package, CommandResult result)
    {
        string log = result.Output.Length > 8000 ? $"{result.Output[..8000]}\n（其余输出已省略）" : result.Output;
        string outcome = result.ExitCode switch
        {
            null => "已跳过",
            0 when result.NeedsReview => "需检查详情",
            0 => "WinGet 报告成功",
            _ => "失败"
        };
        var entry = new UpdateEntry(DateTimeOffset.Now, package.Name, package.Id, package.Installed,
            package.Available, outcome,
            $"WinGet 退出代码 {result.ExitCodeText}\n\n{log}");
        history.Insert(0, entry);
        if (history.Count > 100)
            history.RemoveAt(history.Count - 1);
        try
        {
            if (!historyReadFailed)
            {
                historyStore.Save(history);
                historyProblem = "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            historyProblem = $"更新记录暂时只在窗口中可见。{ex.Message}";
        }
        SyncControls();
    }

    private void SetStatus(string status) => Status.Text = string.IsNullOrEmpty(historyProblem)
        ? status : $"{status}　·　{historyProblem}";

    private void SyncControls()
    {
        if (ScanDetailsButton is null)
            return;

        bool busy = checking || upgrading;
        int selection = rows.Count(row => row.Selected);
        int visibleCount = UpdatesGrid.Items.Count;
        int hiddenCount = rows.Count(row => row.Selected && !MatchesSearch(row));
        RefreshButton.IsEnabled = !busy;
        SelectVisibleButton.IsEnabled = !busy && visibleCount > 0;
        ClearButton.IsEnabled = !busy && selection > 0;
        InstallButton.IsEnabled = !busy && !scanFailed && selection > 0;
        UpdatesGrid.IsEnabled = !busy;
        StopButton.Visibility = upgrading ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = upgrading && !stopRequested;
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanDetailsButton.IsEnabled = !string.IsNullOrWhiteSpace(scanDetails);

        SelectionInfo.Text = $"显示 {visibleCount}/{rows.Length} 项　·　已选 {selection} 项";
        if (hiddenCount > 0)
            SelectionInfo.Text += $"（含筛选隐藏的 {hiddenCount} 项）";

        UpdatesEmpty.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesEmpty.Text = checking ? "正在检查应用更新…" : scanFailed ? "检查失败，点击「检查详情」查看原因。" :
            rows.Length > 0 ? "没有匹配的应用" : omitted > 0 ? "没有可直接更新的应用，请查看检查详情。" : "当前没有可更新的应用";
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        HistoryDetailsButton.IsEnabled = HistoryGrid.SelectedItem is UpdateEntry;

    private void HistoryDetailsClicked(object sender, RoutedEventArgs e) => ShowSelectedHistory();

    private void HistoryDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(HistoryGrid, source) is DataGridRow)
            ShowSelectedHistory();
    }

    private void ShowSelectedHistory()
    {
        if (HistoryGrid.SelectedItem is UpdateEntry entry)
            ShowDetails(entry.Name, $"{entry.Id}\n{entry.Installed} → {entry.Available}\n{entry.TimeText}　{entry.Result}\n\n{entry.Details}");
    }

    private void ScanDetailsClicked(object sender, RoutedEventArgs e) => ShowDetails("检查详情", scanDetails);

    private void ShowDetails(string title, string details)
    {
        var window = CreateDialog(title, 700, 470);
        window.Content = new TextBox
        {
            Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(12), Margin = new Thickness(16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        window.ShowDialog();
    }

    private bool ConfirmUpdates(PackageUpdate[] selected)
    {
        var window = CreateDialog("确认应用更新", 610, 400);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var intro = new TextBlock
        {
            Text = $"将依次请求静默更新 {selected.Length} 个应用，并指定原安装目录。无法确认目录或安装器支持时会跳过。安装器可能要求管理员权限。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)FindResource(typeof(Button)) };
        var confirm = new Button
        {
            Content = "开始更新", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0)
        };
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBox
        {
            Text = string.Join("\r\n\r\n", selected.Select(package =>
                $"{package.Name}  ({package.Id})\r\n{package.Installed} → {package.Available}")),
            Padding = new Thickness(10), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        window.Content = panel;
        window.Loaded += (_, _) => cancel.Focus();
        return window.ShowDialog() == true;
    }

    private Window CreateDialog(string title, double width, double height) => new()
    {
        Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Width = width, Height = height, MinWidth = 420, MinHeight = 260,
        ShowInTaskbar = false, FontFamily = FontFamily, FontSize = FontSize,
        Foreground = Foreground, Background = new SolidColorBrush(Color.FromRgb(242, 245, 242))
    };

    private sealed class UpdateRow(PackageUpdate package) : INotifyPropertyChanged
    {
        private bool selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public PackageUpdate Package { get; } = package;
        public string Name => Package.Name;
        public string Id => Package.Id;
        public string Installed => Package.Installed;
        public string Available => Package.Available;

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value)
                    return;
                selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }
}
