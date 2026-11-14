using System.IO;
using System.Text.Json;

namespace DeepSeekTTS;

/// <summary>应用配置：API Key 与默认选项，持久化到用户配置目录。</summary>
public sealed class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string VoiceId { get; set; } = "stella";
    public string Format { get; set; } = "wav";
    public bool FilterMarkdown { get; set; } = true;

    private static string Dir =>
        Environment.GetEnvironmentVariable("DEEPSEEK_TTS_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekTTS");

    public static string DataDir => Dir;
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s is not null)
                {
                    if (s.VoiceId is not ("stella" or "tide")) s.VoiceId = "stella";
                    if (s.Format is not ("wav" or "pcm")) s.Format = "wav";
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不影响使用
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(tmp, FilePath);
    }

    /// <summary>脱敏显示：sk-abcd…wxyz</summary>
    public static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 10) return key[..2] + "…";
        return $"{key[..5]}…{key[^4..]}";
    }
}
