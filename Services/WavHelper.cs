using System.IO;
using System.Text;
using NAudio.Wave;

namespace DeepSeekTTS;

/// <summary>
/// 音频容器修复与封装。
///
/// 背景：官方整段合成接口在流式返回时，WAV 的 data 块长度字段可能是
/// 0xFFFFFFFF（长度未知的占位值）。直接交给播放器会抛
/// "Stream length must be non-negative and less than 2^31 - 1 - origin"。
///
/// 因此播放前把长度字段按实际字节数回填。这只影响**用于播放的内存副本**，
/// 落盘与下载始终使用接口返回的原始字节。
/// </summary>
public static class WavHelper
{
    /// <summary>
    /// 返回一个可以安全交给播放器的 WAV 副本：
    /// 若长度字段异常（0xFFFFFFFF / 0 / 超出实际大小），按实际数据重写 RIFF 与 data 长度。
    /// 若头部本就正确，则原样返回。
    /// </summary>
    public static byte[] MakePlayable(byte[] wav)
    {
        if (!TryLocate(wav, out var dataOffset, out var dataSize, out var needsFix))
            return wav; // 结构无法识别：交给播放器自行判断，避免破坏数据

        if (!needsFix) return wav;

        var playable = new byte[wav.Length];
        Buffer.BlockCopy(wav, 0, playable, 0, wav.Length);

        var actualData = (uint)(wav.Length - dataOffset);
        BitConverter.GetBytes(actualData).CopyTo(playable, dataOffset - 4); // data 块长度
        BitConverter.GetBytes((uint)(wav.Length - 8)).CopyTo(playable, 4); // RIFF 长度

        return playable;
    }

    /// <summary>
    /// 解析 RIFF 结构，定位 data 块。
    /// 返回 false 表示不是可识别的 RIFF/WAVE。
    /// </summary>
    private static bool TryLocate(byte[] wav, out int dataOffset, out uint declaredSize, out bool needsFix)
    {
        dataOffset = 0;
        declaredSize = 0;
        needsFix = false;

        if (wav.Length < 44) return false;
        if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
        if (Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;

        // 遍历 chunk，找到 data（真实文件可能带 LIST/fact 等附加块）
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToUInt32(wav, pos + 4);
            var bodyStart = pos + 8;

            if (id == "data")
            {
                dataOffset = bodyStart;
                declaredSize = size;

                var actual = wav.Length - bodyStart;
                // 长度字段不可信时修复：占位值、0、或声明超过实际
                needsFix = size == 0xFFFFFFFF || size == 0 || size > actual;
                return true;
            }

            // 块长度异常则无法继续遍历
            if (size == 0xFFFFFFFF || size > (uint)(wav.Length - bodyStart)) return false;

            // chunk 按偶数字节对齐
            pos = bodyStart + (int)size + (int)(size % 2);
        }

        return false;
    }

    /// <summary>把 24 kHz / 16 bit / 单声道 PCM 裸流包成标准 WAV（仅用于播放，不改变采样数据）。</summary>
    public static byte[] WrapPcm(byte[] pcm)
    {
        const int sampleRate = 24000;
        const short channels = 1;
        const short bitsPerSample = 16;

        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataSize = pcm.Length;

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);              // fmt 块长度
        w.Write((short)1);        // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(pcm);
        w.Flush();

        return ms.ToArray();
    }
}
