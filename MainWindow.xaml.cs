using NAudio.CoreAudioApi;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AppVolumeBoost;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AudioAppViewModel> _apps = new();
    private readonly AudioBoostEngine _boostEngine = new();
    private readonly ProfileStore _profiles = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly DispatcherTimer _refreshTimer;
    private Dictionary<string, double> _savedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        AppList.ItemsSource = _apps;
        _savedProfiles = _profiles.Load();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_boostEngine.ActiveProcessId is null) RefreshApps();
        };
        Loaded += (_, _) => RefreshApps();
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            _boostEngine.StopAsync().GetAwaiter().GetResult();
            _profiles.Save(_apps);
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshApps();

    private void RefreshApps()
    {
        _refreshing = true;
        try
        {
            var found = EnumerateAudioApps();
            _apps.Clear();
            foreach (var app in found)
            {
                if (_savedProfiles.TryGetValue(app.ProcessName, out var db)) app.BoostDb = db;
                _apps.Add(app);
            }

            AppCountText.Text = $"{_apps.Count} apps";
            StatusText.Text = _apps.Count == 0
                ? "再生中のアプリが見つかりません。音声を再生してから更新してください。"
                : "準備完了 · Voicemeeter経由でスライダーを動かせます";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"音声セッションを取得できませんでした: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
            _refreshTimer.Start();
        }
    }

    private static List<AudioAppViewModel> EnumerateAudioApps()
    {
        var result = new Dictionary<int, AudioAppViewModel>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            try
            {
                if (!session.State.ToString().Equals("AudioSessionStateActive", StringComparison.Ordinal)) continue;
                var pid = checked((int)session.GetProcessID);
                if (pid <= 0 || pid == Environment.ProcessId || session.IsSystemSoundsSession) continue;

                using var process = Process.GetProcessById(pid);
                var processName = process.ProcessName;
                if (processName.Equals("audiodg", StringComparison.OrdinalIgnoreCase)) continue;
                var processPath = GetProcessPath(process);
                var displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                    ? processName
                    : session.DisplayName.Trim();

                // One process can expose multiple sessions. The process loopback API captures the tree,
                // so a single row is enough and avoids making the user set the same boost twice.
                if (!result.ContainsKey(pid))
                {
                    result.Add(pid, new AudioAppViewModel
                    {
                        ProcessId = pid,
                        ProcessName = processName,
                        DisplayName = displayName,
                        Session = session,
                        Icon = ProcessIconLoader.Load(processPath)
                    });
                }
            }
            catch (ArgumentException)
            {
                // The process can disappear between session enumeration and Process.GetProcessById.
            }
            catch (InvalidOperationException)
            {
                // A session may be torn down at the same time as the refresh.
            }
        }

        return result.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? GetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private async void BoostSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || sender is not Slider { Tag: AudioAppViewModel app }) return;
        var db = app.BoostDb;
        _savedProfiles[app.ProcessName] = db;
        _profiles.Save(_apps);

        await _applyGate.WaitAsync();
        try
        {
            if (db < 0.01)
            {
                if (_boostEngine.ActiveProcessId == app.ProcessId)
                    await _boostEngine.StopAsync();
                app.StateLabel = "通常音量";
                StatusText.Text = $"{app.DisplayName} は通常音量です";
            }
            else if (_boostEngine.ActiveProcessId == app.ProcessId)
            {
                _boostEngine.SetGainDb(db);
                app.StateLabel = "ブースト中";
                StatusText.Text = $"{app.DisplayName} を +{db:0.0} dB で増幅中";
            }
            else
            {
                app.StateLabel = "起動中…";
                StatusText.Text = $"{app.DisplayName} の音声処理を起動しています…";
                await _boostEngine.StartAsync(app, db);
                app.StateLabel = "ブースト中";
                StatusText.Text = $"{app.DisplayName} を +{db:0.0} dB で増幅中";
            }
        }
        catch (Exception ex)
        {
            app.StateLabel = "利用できません";
            StatusText.Text = $"開始できませんでした: {ex.Message}";
            app.BoostDb = 0;
        }
        finally
        {
            _applyGate.Release();
        }
    }
}
