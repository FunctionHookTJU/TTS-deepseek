using System.IO;
using System.Text;
using DeepSeekTTS;

namespace TtsTests;

/// <summary>轻量测试运行器（不引入测试框架依赖）。</summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Contains("--diagnose"))
        {
            Diagnose.Run();
            return 0;
        }

        Console.WriteLine("=== DeepSeek TTS 原生组件测试 ===\n");

        await Run("WavHelper.WrapPcm 生成合法 WAV 头", TestPcmWrapperHeader);
        await Run("WavHelper.WrapPcm 不改变采样数据", TestPcmWrapperPayload);
        await Run("WavHelper.MakePlayable 修复 0xFFFFFFFF 的 data 长度", TestFixDataSize);
        await Run("WavHelper.MakePlayable 修复后 NAudio 可正常读取", TestFixedWavReadable);
        await Run("WavHelper.MakePlayable 修复时不改动采样数据", TestFixKeepsPayload);
        await Run("WavHelper.MakePlayable 合法头原样返回", TestFixNoopOnValidWav);
        await Run("WavHelper.MakePlayable 处理带 LIST 块的 WAV", TestFixWithList);
        await Run("计费字符：汉字算 2，其他算 1", TestBillingChars);
        await Run("AppSettings 脱敏", TestMask);
        await Run("AppSettings 保存与读取", TestSettingsRoundtrip);
        await Run("TempAudioStore 写入与删除", TestStoreSaveDelete);
        await Run("TempAudioStore 启动清空残留", TestStoreClearsOnStart);
        await Run("TtsClient 拒绝空文本", TestClientRejectsEmpty);
        await Run("TtsClient 拒绝超长文本", TestClientRejectsTooLong);
        await Run("TtsClient 无 Key 时报错", TestClientRequiresKey);

        await RunWithMock("TtsClient 合成 WAV（含 RIFF/WAVE 头）", TestSynthWav);
        await RunWithMock("TtsClient 合成 PCM（原始裸流）", TestSynthPcm);
        await RunWithMock("流式 WAV（data 长度占位）可通过播放器加载", TestStreamingWavPlayable);
        await RunWithMock("TtsClient 上游 503 转可读错误", TestSynthError);

        Console.WriteLine($"\n=== 结果：通过 {_passed}，失败 {_failed} ===");
        if (Failures.Count > 0)
        {
            Console.WriteLine("\n失败详情：");
            foreach (var f in Failures) Console.WriteLine($"  - {f}");
        }
        return _failed == 0 ? 0 : 1;
    }

    // ===== 断言 =====

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}：期望 {expected}，实际 {actual}");
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"  ✔ {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name} → {ex.Message}");
            Console.WriteLine($"  ✖ {name}");
            Console.WriteLine($"      {ex.Message}");
        }
    }

    private static Task Run(string name, Action test) => Run(name, () => { test(); return Task.CompletedTask; });

    private static async Task RunWithMock(string name, Func<Task> test)
    {
        await Run(name, test);
    }

    // ===== 测试用例 =====

    private static void TestPcmWrapperHeader()
    {
        var pcm = new byte[4800];
        var wav = WavHelper.WrapPcm(pcm);

        AssertEqual(wav.Length, pcm.Length + 44, "总长度");
        AssertEqual(Encoding.ASCII.GetString(wav, 0, 4), "RIFF", "RIFF 标识");
        AssertEqual(Encoding.ASCII.GetString(wav, 8, 12 - 8), "WAVE", "WAVE 标识");
        AssertEqual(Encoding.ASCII.GetString(wav, 12, 4), "fmt ", "fmt 块");
        AssertEqual(BitConverter.ToInt16(wav, 20), (short)1, "音频格式 PCM");
        AssertEqual(BitConverter.ToInt16(wav, 22), (short)1, "声道数");
        AssertEqual(BitConverter.ToInt32(wav, 24), 24000, "采样率");
        AssertEqual(BitConverter.ToInt16(wav, 34), (short)16, "位深");
        AssertEqual(BitConverter.ToInt32(wav, 40), pcm.Length, "data 长度");
        AssertEqual(BitConverter.ToInt32(wav, 4), 36 + pcm.Length, "RIFF 长度");
    }

    private static void TestPcmWrapperPayload()
    {
        var pcm = new byte[1000];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);

        var wav = WavHelper.WrapPcm(pcm);
        var payload = wav.AsSpan(44).ToArray();

        AssertEqual(payload.Length, pcm.Length, "payload 长度");
        Assert(payload.SequenceEqual(pcm), "采样数据必须逐字节一致");
    }

    // ===== WAV 长度字段修复（真实接口返回的流式 WAV 长度是 0xFFFFFFFF）=====

    /// <summary>构造一个 data 长度字段异常的 WAV（模拟流式返回的占位值）。</summary>
    private static byte[] BuildWavWithBadDataSize(uint badSize, int pcmLen = 40000, bool withList = false)
    {
        var pcm = new byte[pcmLen];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 253);

        var listBody = Encoding.ASCII.GetBytes("INFOISFT" + (char)6 + (char)0 + (char)0 + (char)0 + "test");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var bodyLen = (8 + 16) + (withList ? 8 + listBody.Length : 0) + (8 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(4 + bodyLen));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16u);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(24000);
        w.Write(24000 * 2);
        w.Write((short)2);
        w.Write((short)16);

        if (withList)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write((uint)listBody.Length);
            w.Write(listBody);
        }

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(badSize);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    private static void TestFixDataSize()
    {
        var bad = BuildWavWithBadDataSize(0xFFFFFFFF);
        var fixedWav = WavHelper.MakePlayable(bad);

        // data 长度应被改为实际数据量
        var dataPos = fixedWav.Length - 40000 - 4;
        var newSize = BitConverter.ToUInt32(fixedWav, dataPos);
        AssertEqual(newSize, 40000u, "data 长度应回填为实际字节数");

        // RIFF 长度也应修正
        AssertEqual(BitConverter.ToUInt32(fixedWav, 4), (uint)(fixedWav.Length - 8), "RIFF 长度应正确");
    }

    private static void TestFixedWavReadable()
    {
        var bad = BuildWavWithBadDataSize(0xFFFFFFFF);

        // 修复前：NAudio 读取会抛异常（这正是用户看到的报错）
        var threw = false;
        try
        {
            using var raw = new MemoryStream(bad, false);
            using var r = new NAudio.Wave.WaveFileReader(raw);
            _ = r.TotalTime;
        }
        catch
        {
            threw = true;
        }
        Assert(threw, "未修复的 WAV 应当无法被 NAudio 读取（复现用户报错）");

        // 修复后：能正常读取
        var fixedWav = WavHelper.MakePlayable(bad);
        using var ms = new MemoryStream(fixedWav, false);
        using var reader = new NAudio.Wave.WaveFileReader(ms);
        AssertEqual(reader.Length, 40000L, "可读数据长度");
        Assert(Math.Abs(reader.TotalTime.TotalSeconds - 40000.0 / (24000 * 2)) < 0.01, "时长应正确");
    }

    private static void TestFixKeepsPayload()
    {
        var bad = BuildWavWithBadDataSize(0xFFFFFFFF);
        var fixedWav = WavHelper.MakePlayable(bad);

        AssertEqual(fixedWav.Length, bad.Length, "修复不应改变文件大小");

        // 采样区（最后 40000 字节）必须逐字节一致
        var payloadBefore = bad.AsSpan(bad.Length - 40000).ToArray();
        var payloadAfter = fixedWav.AsSpan(fixedWav.Length - 40000).ToArray();
        Assert(payloadBefore.SequenceEqual(payloadAfter), "采样数据必须逐字节一致");
    }

    private static void TestFixNoopOnValidWav()
    {
        // 构造一个头部完全正确的小 WAV
        var pcm = new byte[1000];
        var valid = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(valid, 0);
        BitConverter.GetBytes((uint)(36 + pcm.Length)).CopyTo(valid, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(valid, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(valid, 12);
        BitConverter.GetBytes(16u).CopyTo(valid, 16);
        BitConverter.GetBytes((short)1).CopyTo(valid, 20);
        BitConverter.GetBytes((short)1).CopyTo(valid, 22);
        BitConverter.GetBytes(24000).CopyTo(valid, 24);
        BitConverter.GetBytes(48000).CopyTo(valid, 28);
        BitConverter.GetBytes((short)2).CopyTo(valid, 32);
        BitConverter.GetBytes((short)16).CopyTo(valid, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(valid, 36);
        BitConverter.GetBytes((uint)pcm.Length).CopyTo(valid, 40);
        pcm.CopyTo(valid, 44);

        var result = WavHelper.MakePlayable(valid);
        Assert(ReferenceEquals(result, valid), "合法 WAV 应原样返回，不做多余拷贝");
    }

    private static void TestFixWithList()
    {
        var bad = BuildWavWithBadDataSize(0xFFFFFFFF, withList: true);
        var fixedWav = WavHelper.MakePlayable(bad);

        using var ms = new MemoryStream(fixedWav, false);
        using var reader = new NAudio.Wave.WaveFileReader(ms);
        AssertEqual(reader.Length, 40000L, "带 LIST 块时也应正确修复");

        var payloadBefore = bad.AsSpan(bad.Length - 40000).ToArray();
        var payloadAfter = fixedWav.AsSpan(fixedWav.Length - 40000).ToArray();
        Assert(payloadBefore.SequenceEqual(payloadAfter), "带 LIST 块时采样数据也不能变");
    }

    private static void TestBillingChars()
    {
        AssertEqual(TtsClient.CountBillingChars("你好"), 4, "两个汉字");
        AssertEqual(TtsClient.CountBillingChars("abc"), 3, "三个字母");
        AssertEqual(TtsClient.CountBillingChars("你好，我是 DeepSeek。"), 19, "混合文本");
        AssertEqual(TtsClient.CountBillingChars(""), 0, "空文本");
    }

    private static void TestMask()
    {
        AssertEqual(AppSettings.Mask("sk-abcdef123456789"), "sk-ab…6789", "长 Key 脱敏");
        AssertEqual(AppSettings.Mask(""), "", "空 Key");
        AssertEqual(AppSettings.Mask("short"), "sh…", "短 Key");
        Assert(!AppSettings.Mask("sk-abcdef123456789").Contains("cdef"), "不能泄露中间部分");
    }

    private static void TestSettingsRoundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tts-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DEEPSEEK_TTS_DATA", dir);
        try
        {
            var s = new AppSettings { ApiKey = "sk-test-123456", VoiceId = "tide", Format = "pcm", FilterMarkdown = false };
            s.Save();

            var loaded = AppSettings.Load();
            AssertEqual(loaded.ApiKey, "sk-test-123456", "Key 持久化");
            AssertEqual(loaded.VoiceId, "tide", "音色持久化");
            AssertEqual(loaded.Format, "pcm", "格式持久化");
            AssertEqual(loaded.FilterMarkdown, false, "Markdown 选项持久化");

            Assert(File.Exists(Path.Combine(dir, "settings.json")), "配置文件应存在");
        }
        finally
        {
            Directory.Delete(dir, true);
            Environment.SetEnvironmentVariable("DEEPSEEK_TTS_DATA", null);
        }
    }

    private static void TestStoreSaveDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tts-store-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DEEPSEEK_TTS_DATA", dir);
        try
        {
            using var store = new TempAudioStore();
            var audio = new byte[1000];
            var path = store.Save("wav", audio);

            Assert(File.Exists(path), "临时文件应已写入");
            AssertEqual(new FileInfo(path).Length, 1000L, "文件大小");
            AssertEqual(Path.GetExtension(path), ".wav", "扩展名");
            Assert(!File.Exists(path + ".part"), "不应残留 .part");

            Assert(store.Delete(path), "删除应成功");
            Assert(!File.Exists(path), "文件应已删除");
            Assert(!store.Delete(path), "重复删除应返回 false");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void TestStoreClearsOnStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tts-store2-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DEEPSEEK_TTS_DATA", dir);
        try
        {
            var tmpDir = Path.Combine(dir, "tmp");
            Directory.CreateDirectory(tmpDir);
            // 模拟上次崩溃残留
            File.WriteAllBytes(Path.Combine(tmpDir, "orphan.wav"), new byte[100]);
            File.WriteAllBytes(Path.Combine(tmpDir, "orphan.wav.part"), new byte[50]);

            using (var store = new TempAudioStore())
            {
                var remains = Directory.GetFiles(tmpDir);
                AssertEqual(remains.Length, 0, "启动后 tmp 应为空");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task TestClientRejectsEmpty()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("   ", "stella", "wav", true, "sk-x", null, CancellationToken.None);
        Assert(!r.Success, "空文本应失败");
        Assert(r.Error!.Contains("文本"), "错误信息应提到文本");
    }

    private static async Task TestClientRejectsTooLong()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync(new string('汉', 5001), "stella", "wav", true, "sk-x", null, CancellationToken.None);
        Assert(!r.Success, "超长文本应失败");
        Assert(r.Error!.Contains("超长") || r.Error.Contains("上限"), "错误信息应提示超长");
    }

    private static async Task TestClientRequiresKey()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("测试", "stella", "wav", true, "", null, CancellationToken.None);
        Assert(!r.Success, "无 Key 应失败");
        Assert(r.Error!.Contains("API Key"), "错误信息应提到 API Key");
    }

    // ===== 对接 mock 上游 =====

    private static async Task TestSynthWav()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("你好，我是 DeepSeek。", "stella", "wav", true, "sk-test", null, CancellationToken.None);

        Assert(r.Success, $"合成应成功，实际：{r.Error}");
        AssertEqual(r.Format!, "wav", "格式");
        var audio = r.Audio!;
        Assert(audio.Length > 44, "音频应有内容");
        AssertEqual(Encoding.ASCII.GetString(audio, 0, 4), "RIFF", "RIFF 头");
        AssertEqual(Encoding.ASCII.GetString(audio, 8, 4), "WAVE", "WAVE 头");
        AssertEqual(BitConverter.ToInt32(audio, 24), 24000, "采样率");
    }

    private static async Task TestSynthPcm()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("PCM 测试", "stella", "pcm", true, "sk-test", null, CancellationToken.None);

        Assert(r.Success, $"合成应成功，实际：{r.Error}");
        var audio = r.Audio!;
        Assert(audio.Length >= 960, "PCM 应有内容");
        AssertEqual(audio.Length % 2, 0, "16bit 对齐（字节数为偶数）");
        // 裸流不应带 RIFF 头
        var head = Encoding.ASCII.GetString(audio, 0, Math.Min(4, audio.Length));
        Assert(head != "RIFF", "PCM 不应包含 WAV 容器头");
    }

    /// <summary>
    /// 关键回归：真实接口的流式 WAV（data 长度 = 0xFFFFFFFF）
    /// 必须能通过 AudioPlayer 加载，且加载后时长正确。
    /// </summary>
    private static async Task TestStreamingWavPlayable()
    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("流式 WAV 播放测试", "stella", "wav", true, "sk-test", null, CancellationToken.None);
        Assert(r.Success, $"合成应成功，实际：{r.Error}");

        var audio = r.Audio!;
        var declared = BitConverter.ToUInt32(audio, 40);
        Console.WriteLine($"       （mock 返回的 data 长度字段：0x{declared:X8}）");

        // 通过 WavHelper 修复后应可被 NAudio 读取
        var playable = WavHelper.MakePlayable(audio);
        using var ms = new MemoryStream(playable, false);
        using var reader = new NAudio.Wave.WaveFileReader(ms);
        Assert(reader.Length > 0, "修复后应能读出音频数据");
        Assert(reader.TotalTime.TotalSeconds > 0.5, $"时长应大于 0.5s，实际 {reader.TotalTime.TotalSeconds:F2}s");

        // 若 mock 处于流式模式，额外确认原始文件确实带占位长度，且修复有效
        if (declared == 0xFFFFFFFF)
        {
            Assert(!ReferenceEquals(playable, audio), "流式 WAV 应被修复（返回新副本）");
            // 采样数据必须完全一致
            var before = audio.AsSpan(audio.Length - (audio.Length - 44)).ToArray();
            var after = playable.AsSpan(playable.Length - (playable.Length - 44)).ToArray();
            Assert(before.SequenceEqual(after), "修复不能改动采样数据");
        }
    }

    private static async Task TestSynthError()    {
        using var client = new TtsClient();
        var r = await client.SynthesizeAsync("__503__ 触发服务繁忙", "stella", "wav", true, "sk-test", null, CancellationToken.None);

        Assert(!r.Success, "应失败");
        Assert(r.Error!.Contains("服务繁忙") || r.Error.Contains("503"), $"错误应可读，实际：{r.Error}");
    }
}
