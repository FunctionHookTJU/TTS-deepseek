/**
 * 原生组件级测试（不需要 UI 线程）：
 *   1. TtsClient 对接 mock 上游，验证 WAV/PCM 合成与错误处理
 *   2. PcmWrapper 补头正确性
 *   3. TempAudioStore 生命周期：写入 / 删除 / 启动清空 / 容量淘汰
 *   4. AppSettings 持久化与脱敏
 *
 * 运行：dotnet run --project tests/TtsTests.csproj
 */
