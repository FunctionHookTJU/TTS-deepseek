using System.Windows;
using System.Windows.Controls;

namespace DeepSeekTTS;

public partial class SettingsWindow : Window
{
    public string ApiKey { get; private set; } = "";
    public string VoiceId { get; private set; } = "stella";
    public string Format { get; private set; } = "wav";

    public SettingsWindow(AppSettings current, string effectiveKey)
    {
        InitializeComponent();

        // 已保存的 Key 不回显明文：输入框留空表示"不修改"
        KeyPasswordBox.Password = "";
        KeyTextBox.Text = "";

        SelectComboByTag(VoiceCombo, current.VoiceId);
        SelectComboByTag(FormatCombo, current.Format);
        ApiKey = current.ApiKey;

        DataDirText.Text = $"配置目录：{AppSettings.DataDir}";

        if (!string.IsNullOrEmpty(current.ApiKey))
        {
            KeyHintText.Text =
                $"当前已保存：{AppSettings.Mask(current.ApiKey)}。留空保存不会改动已有的 Key。";
        }
        else if (!string.IsNullOrEmpty(effectiveKey))
        {
            KeyHintText.Text =
                $"当前使用环境变量中的 Key：{AppSettings.Mask(effectiveKey)}。在此填写的 Key 会被优先使用。";
        }
        else
        {
            KeyHintText.Text =
                "尚未配置。请填入 DeepSeek API Key（以 sk- 开头），保存在本机配置文件中，仅用于调用接口。";
        }

        Loaded += (_, _) => KeyPasswordBox.Focus();
    }

    private bool _passwordVisible;

    private string CurrentInput =>
        _passwordVisible ? KeyTextBox.Text : KeyPasswordBox.Password;

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            KeyTextBox.Text = KeyPasswordBox.Password;
            KeyTextBox.Visibility = Visibility.Visible;
            KeyPasswordBox.Visibility = Visibility.Collapsed;
            ToggleButton.Content = "隐藏";
            KeyTextBox.Focus();
            KeyTextBox.CaretIndex = KeyTextBox.Text.Length;
        }
        else
        {
            KeyPasswordBox.Password = KeyTextBox.Text;
            KeyPasswordBox.Visibility = Visibility.Visible;
            KeyTextBox.Visibility = Visibility.Collapsed;
            ToggleButton.Content = "显示";
            KeyPasswordBox.Focus();
        }
    }

    private void KeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 切换显示状态时保持两框内容一致
        if (_passwordVisible) KeyPasswordBox.Password = KeyTextBox.Text;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var input = CurrentInput.Trim();

        // 留空 = 不修改已保存的 Key
        if (!string.IsNullOrEmpty(input))
        {
            if (!input.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                var r = MessageBox.Show(this,
                    "DeepSeek API Key 通常以 “sk-” 开头，当前输入看起来不像标准格式。\n\n仍要保存吗？",
                    "确认格式", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            ApiKey = input;
        }

        VoiceId = (VoiceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "stella";
        Format = (FormatCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wav";

        DialogResult = true;
        Close();
    }

    private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "确定要清除已保存的 API Key 吗？清除后需要重新填写才能继续使用。",
            "清除 API Key", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        ApiKey = "";
        DialogResult = true;
        Close();
    }

    private void CancelSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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
}
