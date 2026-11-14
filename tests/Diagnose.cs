using System.IO;
using System.Text;
using NAudio.Wave;
using DeepSeekTTS;

namespace TtsTests;

/// <summary>
/// WAV 头诊断工具：验证各种容器变体能否被 NAudio 正确解析。
///
/// 用途：当真实接口返回的音频无法播放时，用这个快速定位是哪种头部字段异常。
/// 当初正是靠它确认了 data 长度字段为 0xFFFFFFFF 是播放失败的根因。
///
/// 运行：dotnet run --project tests/TtsTests.csproj -- --diagnose
/// </summary>
internal static class Diagnose
{
    private const uint OK = 0xFFFFFFFE; // 哨兵：该字段使用正确的真实值

    public static void Run()
    {
        Console.WriteLine("=== WAV 头变体诊断 ===\n");

        Try("标准头（长度字段正确）", BuildWav(OK, OK, OK, OK));
        Try("RIFF 长度 = 0xFFFFFFFF", BuildWav(0xFFFFFFFF, OK, 0xFFFFFFFF, OK));
        Try("data 长度 = 0xFFFFFFFF", BuildWav(OK, OK, OK, 0xFFFFFFFF));
        Try("两者都 = 0xFFFFFFFF", BuildWav(0xFFFFFFFF, OK, 0xFFFFFFFF, 0xFFFFFFFF));
        Try("长度字段 = 0（流式未回填）", BuildWav(0, OK, 0, 0));
        Try("data 长度 = 0", BuildWav(OK, OK, OK, 0));
        Try("RIFF 长度 = 0x7FFFFFFF", BuildWav(0x7FFFFFFF, OK, 0x7FFFFFFF, OK));

        Console.WriteLine("\n=== 其他容器变体 ===");
        Try("fmt + LIST + data（标准）", BuildWavWithList());
        Try("带 LIST 且 data 长度 = 0xFFFFFFFF", BuildWavWithList(0xFFFFFFFF));

        Console.WriteLine("\n=== 修复后（MakePlayable）===");
        Try("0xFFFFFFFF 头部经修复", WavHelper.MakePlayable(BuildWav(OK, OK, OK, 0xFFFFFFFF)));
    }

    private static void Try(string name, byte[] wav)
    {
        try
        {
            using var ms = new MemoryStream(wav, writable: false);
            using var reader = new WaveFileReader(ms);
            Console.WriteLine($"  OK   {name,-40} len={reader.Length} dur={reader.TotalTime.TotalSeconds:F2}s");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name,-40} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static byte[] BuildWav(uint riffSize, uint fmtSize, uint riffSize2, uint dataSize)
    {
        var pcm = new byte[40000];
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(riffSize == OK ? 36u + (uint)pcm.Length : riffSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(fmtSize == OK ? 16u : fmtSize);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(24000);
        w.Write(24000 * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize == OK ? (uint)pcm.Length : dataSize);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>带 LIST 块（真实编码器常带元数据块）</summary>
    private static byte[] BuildWavWithList(uint dataSizeOverride = OK)
    {
        var pcm = new byte[40000];
        var listBody = Encoding.ASCII.GetBytes("INFOISFT" + (char)6 + (char)0 + (char)0 + (char)0 + "test");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var total = 4 + (8 + 16) + (8 + listBody.Length) + (8 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)total);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16u);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(24000);
        w.Write(24000 * 2);
        w.Write((short)2);
        w.Write((short)16);

        w.Write(Encoding.ASCII.GetBytes("LIST"));
        w.Write((uint)listBody.Length);
        w.Write(listBody);

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSizeOverride == OK ? (uint)pcm.Length : dataSizeOverride);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
