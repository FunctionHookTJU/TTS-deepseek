using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeepSeekTTS;

public partial class MainWindow : Window
{
    private readonly TtsClient _client = new();
    private readonly AudioPlayer _player = new();
    private readonly TempAudioStore _store = new();
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly DispatcherTimer _positionTimer;

    private AppSettings _settings;
    private CancellationTokenSource? _cts;

    /// <summary>当前音频（官方原始字节，wav 含头 / pcm 裸流）。</summary>
    private byte[]? _audio;
    private string _audioFormat = "wav";
    private string? _tempPath;
    private bool _suppressSettingsSave;
    private bool _suppressSeek;
    private bool _isDraggingSeek;

    public sealed record LogEntry(string Time, string Message, string Kind);

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        LogList.ItemsSource = _logs;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _positionTimer.Tick += (_, _) => UpdatePlaybackPosition();

        _player.PlaybackStopped += (_, _) => Dispatcher.Invoke(() =>
        {
            if (!_isDraggingSeek) UpdatePlaybackPosition();
            UpdatePlayButton();
        });

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // ===== 初始化 =====

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        RefreshKeyBadge();
        Log("应用已启动", "info");
        Log($"临时缓存目录：{_store.DirPath}", "info");

        if (string.IsNullOrEmpty(EffectiveKey()))
        {
            Log("尚未配置 API Key，请点击右上角「设置」填写", "warn");
        }
        else
        {
            Log($"API Key 已就绪（{AppSettings.Mask(EffectiveKey())}）", "ok");
        }

        TextInputBox.Focus();

        // 首次使用（无任何 Key）时自动引导配置，等主窗口绘制完成后再弹
        if (string.IsNullOrEmpty(EffectiveKey()))
        {
            Dispatcher.BeginInvoke(new Action(OpenSettingsWindow), DispatcherPriority.ApplicationIdle);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel();
        _positionTimer.Stop();
        _player.Dispose();
        DiscardTemp();
        _store.Dispose();
        _client.Dispose();
        SaveSettingsFromUi();
    }

    private string EffectiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey)) return _settings.ApiKey.Trim();
        return (Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "").Trim();
    }

    // ===== 设置 =====

    private void ApplySettingsToUi()
    {
        _suppressSettingsSave = true;
        try
        {
            SelectComboByTag(VoiceCombo, _settings.VoiceId);
            SelectComboByTag(FormatCombo, _settings.Format);
            FilterMarkdownCheck.IsChecked = _settings.FilterMarkdown;
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                combo.SelectedItem = ci;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string ComboTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private void RefreshKeyBadge()
    {
        var key = EffectiveKey();
        if (string.IsNullOrEmpty(key))
        {
            KeyBadgeText.Text = "未配置 Key";
            KeyBadgeText.Foreground = (Brush)FindResource("WarnBrush");
            KeyDot.Fill = (Brush)FindResource("WarnBrush");
        }
        else
        {
            KeyBadgeText.Text = AppSettings.Mask(key);
            KeyBadgeText.Foreground = (Brush)FindResource("OkBrush");
            KeyDot.Fill = (Brush)FindResource("OkBrush");
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        var dlg = new SettingsWindow(_settings, EffectiveKey()) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings.ApiKey = dlg.ApiKey;
            _settings.VoiceId = dlg.VoiceId;
            _settings.Format = dlg.Format;
            _settings.Save();

            ApplySettingsToUi();
            RefreshKeyBadge();

            if (string.IsNullOrEmpty(EffectiveKey()))
            {
                Log("已清除 API Key", "warn");
            }
            else
            {
                Log($"API Key 已保存（{AppSettings.Mask(EffectiveKey())}）", "ok");
            }
        }
    }

    private void SaveSettingsFromUi()
    {
        if (_suppressSettingsSave) return;
        try
        {
            _settings.VoiceId = ComboTag(VoiceCombo, "stella");
            _settings.Format = ComboTag(FormatCombo, "wav");
            _settings.FilterMarkdown = FilterMarkdownCheck.IsChecked == true;
            _settings.Save();
        }
        catch
        {
            // 退出时保存失败不影响关闭
        }
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettingsFromUi();
    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveSettingsFromUi();
    private void FilterMarkdownCheck_Changed(object sender, RoutedEventArgs e) => SaveSettingsFromUi();

    // ===== 文本与计数 =====

    private void TextInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var n = TtsClient.CountBillingChars(TextInputBox.Text);
        CounterText.Text = $"{n} / 10000 计费字符";
        CounterText.Foreground = n > 10000
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("MutedBrush");
    }

    private void TextInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            if (GenerateButton.IsEnabled) GenerateButton_Click(sender, new RoutedEventArgs());
        }
    }

    // ===== 生成 =====

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return; // 正在合成

        var text = TextInputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "请先输入要朗读的文本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            TextInputBox.Focus();
            return;
        }

        var billing = TtsClient.CountBillingChars(text);
        if (billing > 10000)
        {
            MessageBox.Show(this, $"文本超过 10,000 计费字符上限（当前约 {billing}）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var key = EffectiveKey();
        if (string.IsNullOrEmpty(key))
        {
            Log("尚未配置 API Key", "error");
            OpenSettingsWindow();
            return;
        }

        // 重新生成前清掉上一段临时文件
        DiscardTemp();

        var voiceId = ComboTag(VoiceCombo, "stella");
        var format = ComboTag(FormatCombo, "wav");
        var filterMd = FilterMarkdownCheck.IsChecked == true;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        Log($"开始合成：{voiceId} / {format.ToUpperInvariant()} / {billing} 计费字符", "info");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<long>(bytes =>
        {
            ResultMeta.Text = $"正在接收… {bytes / 1024.0:F1} KB";
        });

        try
        {
            var result = await _client.SynthesizeAsync(
                text, voiceId, format, filterMd, key, progress, _cts.Token);

            sw.Stop();

            if (!result.Success)
            {
                Log($"合成失败：{result.Error}", "error");
                MessageBox.Show(this, result.Error ?? "合成失败", "合成失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _audio = result.Audio!;
            _audioFormat = result.Format!;

            // 落盘到临时目录（原始字节，不做任何转换）
            try
            {
                _tempPath = _store.Save(_audioFormat, _audio);
            }
            catch (Exception ex)
            {
                Log($"临时文件写入失败（不影响播放与下载）：{ex.Message}", "warn");
                _tempPath = null;
            }

            // 播放器加载（pcm 会自动在内存补 WAV 头）
            _player.Load(_audio, _audioFormat);

            ShowResult();
            Log($"合成完成：{FormatBytes(_audio.Length)}，耗时 {sw.ElapsedMilliseconds} ms", "ok");
            Log(_audioFormat == "pcm"
                ? "PCM 裸流已在内存中临时封装供试听（下载仍为原始 PCM）"
                : "音频已就绪，可直接播放或下载", _audioFormat == "pcm" ? "warn" : "ok");
        }
        catch (Exception ex)
        {
            Log($"合成异常：{ex.Message}", "error");
            MessageBox.Show(this, ex.Message, "合成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("已请求取消合成", "warn");
    }

    private void SetBusy(bool busy)
    {
        GenerateButton.IsEnabled = !busy;
        GenerateButton.Content = busy ? "正在合成…" : "生成语音";
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TextInputBox.IsReadOnly = busy;
    }

    private void ShowResult()
    {
        ResultCard.Visibility = Visibility.Visible;

        var duration = _player.Duration ?? TimeSpan.Zero;
        _suppressSeek = true;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001);
        SeekSlider.Value = 0;
        _suppressSeek = false;

        UpdateTimeText(TimeSpan.Zero, duration);
        ResultMeta.Text = $"{_audioFormat.ToUpperInvariant()} · {FormatBytes(_audio!.Length)} · {ComboTag(VoiceCombo, "stella")}";
        PcmNote.Visibility = _audioFormat == "pcm" ? Visibility.Visible : Visibility.Collapsed;
        TempPathText.Text = _tempPath is null ? "" : $"临时文件：{Path.GetFileName(_tempPath)}";

        UpdatePlayButton();
    }

    // ===== 播放控制 =====

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        try
        {
            if (_player.IsPlaying)
            {
                _player.Pause();
                _positionTimer.Stop();
            }
            else
            {
                _player.Play();
                _positionTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Log($"播放失败：{ex.Message}", "error");
        }
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        PlayIcon.Text = _player.IsPlaying ? "❚❚" : "▶";
        if (!_player.IsPlaying) _positionTimer.Stop();
    }

    private void UpdatePlaybackPosition()
    {
        if (_audio is null || _isDraggingSeek) return;
        var pos = _player.Position;
        var total = _player.Duration ?? TimeSpan.Zero;

        _suppressSeek = true;
        SeekSlider.Value = Math.Clamp(pos.TotalSeconds, 0, SeekSlider.Maximum);
        _suppressSeek = false;

        UpdateTimeText(pos, total);
        UpdatePlayButton();
    }

    private void UpdateTimeText(TimeSpan pos, TimeSpan total)
    {
        TimeText.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || _audio is null || !_isDraggingSeek) return;
        _player.Seek(TimeSpan.FromSeconds(e.NewValue));
        UpdateTimeText(TimeSpan.FromSeconds(e.NewValue), _player.Duration ?? TimeSpan.Zero);
    }

    private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isDraggingSeek = true;
    }

    private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _isDraggingSeek = false;
        if (_audio is null) return;
        _player.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    // ===== 下载与临时文件 =====

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;

        var ext = _audioFormat == "pcm" ? "pcm" : "wav";
        var dlg = new SaveFileDialog
        {
            Title = "保存音频",
            FileName = $"deepseek-tts-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}",
            Filter = _audioFormat == "pcm"
                ? "PCM 裸流 (*.pcm)|*.pcm|所有文件 (*.*)|*.*"
                : "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
            DefaultExt = ext,
            AddExtension = true,
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // 直接写官方原始字节：wav 是 WAV 容器，pcm 是无头裸流，均不做转换
            File.WriteAllBytes(dlg.FileName, _audio);
            Log($"已保存：{dlg.FileName}（{FormatBytes(_audio.Length)}）", "ok");

            var result = MessageBox.Show(this,
                "音频已保存。\n\n是否打开所在文件夹？",
                "保存成功", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
            }
        }
        catch (Exception ex)
        {
            Log($"保存失败：{ex.Message}", "error");
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _positionTimer.Stop();
        DiscardTemp();
        _audio = null;
        ResultCard.Visibility = Visibility.Collapsed;
        ResultMeta.Text = "";
        UpdatePlayButton();
        Log("已删除临时文件并释放音频", "info");
    }

    private void DiscardTemp()
    {
        if (_tempPath is null) return;
        _store.Delete(_tempPath);
        _tempPath = null;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        TextInputBox.Clear();
        TextInputBox.Focus();
        if (_audio is not null)
        {
            _player.Stop();
            _positionTimer.Stop();
            DiscardTemp();
            _audio = null;
            ResultCard.Visibility = Visibility.Collapsed;
            UpdatePlayButton();
            Log("已清空输入并释放音频", "info");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    // ===== 工具 =====

    private void Log(string message, string kind = "info")
    {
        _logs.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, kind));
        // 只保留最近 200 条
        while (_logs.Count > 200) _logs.RemoveAt(0);
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F2} MB";
    }
}
