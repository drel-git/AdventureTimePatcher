using AdventureTimePatcher.Core;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace AdventureTimePatcher.Gui;

public partial class MainWindow : Window
{
    private const string DefaultOwner = "sebbun123";
    private const string DefaultRepo = "Adventuretime";
    private const string DefaultBranch = "main";

    private readonly PatcherService _service = new();
    private readonly RepoRef _repo = new(DefaultOwner, DefaultRepo, DefaultBranch);
    private bool _busy;
    private bool _checked;
    private bool _hasUpdate;
    private string _latestSha = "";
    private string _installedSha = "";
    private string _startupCommand = "";
    private string _selfLatestVersion = "";

    public MainWindow()
    {
        InitializeComponent();
        PatcherVersionText.Text = "Patcher v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev");
        MqDirTextBox.Text = GuessMqRoot();
        ParseStartupArgs();
        AppendLog("Choose your MacroQuest folder, then Check or Update.");
        if (_startupCommand == "update")
        {
            ActionHint.Text = "AdventureTime closed in-game. Click Update to install the latest copy.";
            _ = CheckAsync();
        }
        _ = RefreshSelfUpdateAsync();
    }

    private void ParseStartupArgs()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (i == 0 && !a.StartsWith("--", StringComparison.Ordinal))
            {
                _startupCommand = a.ToLowerInvariant();
                continue;
            }
            if (a == "--mq" && i + 1 < args.Length) MqDirTextBox.Text = args[++i];
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_checked)
        {
            await CheckAsync();
            return;
        }
        await UpdateAsync(false);
    }

    private async Task CheckAsync()
    {
        await RunAsync(async () =>
        {
            AppendLog("Checking GitHub...");
            PatchNotesText.Text = "Checking...";
            var result = await _service.CheckAsync(MqDirTextBox.Text, _repo);
            _checked = true;
            _hasUpdate = result.UpdateAvailable;
            _latestSha = result.LatestSha;
            _installedSha = result.InstalledSha;
            VersionLine.Text = $"Installed {Pretty(result.InstalledSha)}   |   Latest {Pretty(result.LatestSha)}";
            PatchNotesText.Text = result.UpdateAvailable
                ? "A newer AdventureTime copy is available from GitHub. Click Update below to install it."
                : "AdventureTime is current. Reinstall forces a fresh copy from GitHub.";
            if (result.UpdateAvailable)
            {
                StatusLine.Text = "Update ready";
                StatusLine.Foreground = BrushOf(0xFF, 0xD1, 0x66);
                ActionButton.Content = "Update Now";
                ActionButton.Background = BrushOf(0x8F, 0xD3, 0xFF);
                ActionButton.Foreground = BrushOf(0x06, 0x15, 0x22);
                ActionHint.Text = "Click Update Now, then restart AdventureTime in-game.";
                AppendLog("Update available.");
            }
            else
            {
                StatusLine.Text = "Up to date";
                StatusLine.Foreground = BrushOf(0xA7, 0xE2, 0xFF);
                ActionButton.Content = "Reinstall / Recheck";
                ActionButton.Background = BrushOf(0x15, 0x30, 0x44);
                ActionButton.Foreground = BrushOf(0xEA, 0xF6, 0xFF);
                ActionHint.Text = "AdventureTime is current. Reinstall forces a fresh copy from GitHub.";
                AppendLog("AdventureTime is up to date.");
            }
        });
    }

    private async Task UpdateAsync(bool allowLocalNewer)
    {
        await RunAsync(async () =>
        {
            var progress = new Progress<string>(AppendLog);
            try
            {
                var result = await _service.UpdateAsync(MqDirTextBox.Text, _repo, progress, allowLocalNewer: allowLocalNewer);
                _checked = true;
                _hasUpdate = false;
                _latestSha = result.LatestSha;
                _installedSha = result.LatestSha;
                VersionLine.Text = $"Installed {Pretty(result.LatestSha)}   |   Latest {Pretty(result.LatestSha)}";
                StatusLine.Text = "Up to date";
                StatusLine.Foreground = BrushOf(0xA7, 0xE2, 0xFF);
                ActionButton.Content = "Reinstall / Recheck";
                ActionButton.Background = BrushOf(0x15, 0x30, 0x44);
                ActionButton.Foreground = BrushOf(0xEA, 0xF6, 0xFF);
                PatchNotesText.Text = "AdventureTime was installed from GitHub. Restart AdventureTime in-game.";
                if (result.AlreadyCurrent) AppendLog("AdventureTime is already current.");
                else
                {
                    AppendLog($"Installed AdventureTime {PatcherService.ShortSha(result.LatestSha)}.");
                    foreach (var f in result.CopiedFiles) AppendLog("Updated " + f);
                    if (!string.IsNullOrWhiteSpace(result.BackupDir)) AppendLog("Backup: " + result.BackupDir);
                }
                AppendLog("Done. You can start AdventureTime again in MacroQuest.");
            }
            catch (LocalBuildNewerException ex) when (!allowLocalNewer)
            {
                var answer = System.Windows.MessageBox.Show(
                    this,
                    "The installed AdventureTime file appears newer than GitHub.\n\n" +
                    $"Installed: {ex.LocalBuild.Display}\nGitHub: {ex.RemoteBuild.Display}\n\n" +
                    "Updating may replace local test changes. Continue anyway?",
                    "AdventureTime Patcher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                AppendLog(ex.Message);
                if (answer == MessageBoxResult.Yes)
                    await UpdateAsync(true);
            }
        });
    }

    private async Task RefreshSelfUpdateAsync()
    {
        try
        {
            var result = await Task.Run(() => _service.CheckSelfUpdate());
            if (!result.UpdateAvailable)
            {
                SelfUpdateBanner.Visibility = Visibility.Collapsed;
                return;
            }
            _selfLatestVersion = result.LatestVersion;
            SelfUpdateText.Text =
                $"Patcher update available: you have v{result.LocalVersion}, latest is v{result.LatestVersion}. " +
                "Update the patcher first for the newest installer features.";
            SelfUpdateBanner.Visibility = Visibility.Visible;
            AppendLog($"Patcher update available: v{result.LocalVersion} -> v{result.LatestVersion}");
        }
        catch
        {
            SelfUpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void SelfUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SelfUpdateButton.IsEnabled = false;
        SelfUpdateButton.Content = "Updating...";
        ActionHint.Text = "Downloading the new patcher, then restarting quietly...";
        try
        {
            var log = new Progress<string>(AppendLog);
            var downloaded = await _service.DownloadLatestPatcherAsync(log, CancellationToken.None);
            var target = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                throw new InvalidOperationException("Could not resolve this patcher's path.");

            var helper = PatcherService.WriteWindowsSelfUpdateHelper(
                Environment.ProcessId,
                downloaded,
                target,
                Path.GetDirectoryName(target),
                BuildRelaunchArgsForCmd());

            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "AdventureTimePatcher_update", "relaunched.flag"),
                    _selfLatestVersion);
            }
            catch { }

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{helper}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(helper) ?? "",
            });
            AppendLog("Restarting into the new patcher...");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: Self-update failed: {ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = PatcherService.PatcherDownloadUrl,
                    UseShellExecute = true
                });
                AppendLog("Opened browser download as fallback.");
            }
            catch (Exception ex2)
            {
                System.Windows.MessageBox.Show(
                    $"Could not update or open the download link.\n\n{ex.Message}\n{ex2.Message}",
                    "AdventureTime Patcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            SelfUpdateButton.Content = "Update Patcher";
            SelfUpdateButton.IsEnabled = true;
            _busy = false;
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            SetBusy(true);
            await work();
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Update check failed";
            StatusLine.Foreground = BrushOf(0xE8, 0x5F, 0x5F);
            PatchNotesText.Text = "Could not complete the request. See the log below.";
            AppendLog("ERROR: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BrowseButton.IsEnabled = !busy;
        MqDirTextBox.IsEnabled = !busy;
        ActionButton.IsEnabled = !busy;
        if (SelfUpdateButton != null) SelfUpdateButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "Select your MacroQuest root folder" };
        if (Directory.Exists(MqDirTextBox.Text)) dlg.SelectedPath = MqDirTextBox.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            MqDirTextBox.Text = dlg.SelectedPath;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WindowDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MqDirTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _checked = false;
        _hasUpdate = false;
        _latestSha = "";
        _installedSha = "";
        if (ActionButton == null) return;
        ActionButton.Content = "Check for Updates";
        ActionButton.Background = BrushOf(0x15, 0x30, 0x44);
        ActionButton.Foreground = BrushOf(0xEA, 0xF6, 0xFF);
        ActionHint.Text = "Choose your MacroQuest folder, then check for updates.";
    }

    private void GitHubLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }

    private string BuildRelaunchArgsForCmd()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_startupCommand))
            parts.Add(QuoteForCmd(_startupCommand));
        var mq = MqDirTextBox.Text;
        if (!string.IsNullOrWhiteSpace(mq))
        {
            parts.Add("--mq");
            parts.Add(QuoteForCmd(mq));
        }
        return string.Join(" ", parts);
    }

    private static string QuoteForCmd(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {AsciiSafe(message)}\n";
            LogScroll.ScrollToEnd();
        });
    }

    private static string Pretty(string sha) => string.IsNullOrWhiteSpace(sha) ? "none" : PatcherService.ShortSha(sha);

    private static SolidColorBrush BrushOf(byte r, byte g, byte b) => new(System.Windows.Media.Color.FromRgb(r, g, b));

    private static string AsciiSafe(string text) => new(text.Select(ch => ch < 128 ? ch : '?').ToArray());

    private static string GuessMqRoot()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new[]
        {
            baseDir,
            Directory.GetParent(baseDir)?.FullName,
            Directory.GetParent(Directory.GetParent(baseDir)?.FullName ?? string.Empty)?.FullName,
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(Path.Combine(c, "lua")) && Directory.Exists(Path.Combine(c, "config")))
                return c;
        }
        return baseDir;
    }
}
