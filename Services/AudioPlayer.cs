using System.IO;
using NAudio.Wave;

namespace DeepSeekTTS;

/// <summary>
/// 音频播放与临时缓存管理。
///
/// 关于 PCM：官方返回的 pcm 是 24 kHz / 16 bit / 单声道裸流，没有容器头，无法直接播放。
/// 这里只在内存中补一个 44 字节 WAV 头喂给播放器（采样数据一个字节都不改）；
/// 而落盘的临时文件与用户下载的文件，始终是接口返回的原始字节。
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public TimeSpan? Duration => _reader?.TotalTime;

    public event EventHandler? PlaybackStopped;

    /// <summary>当前时间位置（用于进度显示）。</summary>
    public TimeSpan Position
    {
        get
        {
            if (_reader is null) return TimeSpan.Zero;
            var pos = _reader.CurrentTime;
            // 播放结束后 CurrentTime 可能略超总时长
            return pos > _reader.TotalTime ? _reader.TotalTime : pos;
        }
    }

    /// <summary>加载音频（自动修复 WAV 长度字段 / 为 PCM 补头，均只作用于播放副本）。</summary>
    public void Load(byte[] audio, string format)
    {
        Stop();

        // PCM: 裸流需要补容器头才能播放
        // WAV:  流式返回的 data 长度可能是 0xFFFFFFFF 占位值，必须回填实际长度
        var playable = format == "pcm" ? WavHelper.WrapPcm(audio) : WavHelper.MakePlayable(audio);

        Stream source = new MemoryStream(playable, writable: false);
        _reader = new WaveFileReader(source);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.PlaybackStopped += (_, e) => PlaybackStopped?.Invoke(this, e);
    }

    public void Play()
    {
        if (_output is null) return;
        // 播放到结尾后再次点击播放：回到开头
        if (_reader is not null && _reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(50))
        {
            _reader.CurrentTime = TimeSpan.Zero;
        }
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _reader.TotalTime) position = _reader.TotalTime;
        _reader.CurrentTime = position;
    }

    public void Stop()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
            _reader?.Dispose();
        }
        catch
        {
            // 释放失败不影响后续加载
        }
        finally
        {
            _output = null;
            _reader = null;
        }
    }

    public void Dispose() => Stop();
}
