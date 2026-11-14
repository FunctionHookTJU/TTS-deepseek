# 组件测试

不依赖 UI 线程，直接测试服务层逻辑。

## 覆盖范围

1. `TtsClient` 对接 mock 上游，验证 WAV/PCM 合成与错误处理
2. `WavHelper` PCM 补头、以及流式 WAV 的 `0xFFFFFFFF` 长度字段修复
3. `TempAudioStore` 生命周期：写入 / 删除 / 启动清空 / 容量淘汰
4. `AppSettings` 持久化与脱敏

## 运行

测试需要 mock 上游（模拟官方流式响应，不消耗额度）：

```bash
# 终端 A：启动 mock 上游
node scripts/mock-upstream.js

# 终端 B：运行测试
set DEEPSEEK_TTS_BASE=http://127.0.0.1:4300
dotnet run --project tests/TtsTests.csproj
```

设置 `MOCK_STREAMING_WAV=1` 启动 mock，可复现真实接口的流式 WAV
（`data` 长度字段为 `0xFFFFFFFF`）场景。

诊断模式（排查 WAV 头解析问题）：

```bash
dotnet run --project tests/TtsTests.csproj -- --diagnose
```
