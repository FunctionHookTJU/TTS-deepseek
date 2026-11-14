/**
 * 本地 Mock 上游（仅用于离线测试 / 演示，不参与生产链路）
 *
 * 模拟官方整段合成接口 POST /v1/tools/tts 的流式音频响应：
 *   format=wav → 返回合法的 RIFF/WAVE（24 kHz / 16 bit / 单声道）
 *   format=pcm → 返回 24 kHz / 16 bit / 单声道裸流
 *
 * 用法：
 *   node scripts/mock-upstream.js            # 默认监听 4300
 *   MOCK_PORT=4300 node scripts/mock-upstream.js
 * 然后以 DEEPSEEK_TTS_BASE=http://127.0.0.1:4300 启动 server.js
 */

import http from 'node:http';

const PORT = Number(process.env.MOCK_PORT || 4300);
const SAMPLE_RATE = 24000;

/** 生成指定秒数的正弦波 PCM（16 bit 小端、单声道） */
function makePcm(seconds = 1.5, freq = 440, gain = 0.28) {
  const sampleCount = Math.round(SAMPLE_RATE * seconds);
  const buf = Buffer.alloc(sampleCount * 2);
  for (let i = 0; i < sampleCount; i += 1) {
    // 淡入淡出，避免爆音
    const t = i / SAMPLE_RATE;
    const env = Math.min(1, t / 0.05, (seconds - t) / 0.05);
    const v = Math.sin(2 * Math.PI * freq * t) * gain * Math.max(0, env);
    buf.writeInt16LE(Math.round(v * 32767), i * 2);
  }
  return buf;
}

function wavHeader(dataSize) {
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'latin1');
  header.writeUInt32LE(36 + dataSize, 4);
  header.write('WAVE', 8, 'latin1');
  header.write('fmt ', 12, 'latin1');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);            // PCM
  header.writeUInt16LE(1, 22);            // 单声道
  header.writeUInt32LE(SAMPLE_RATE, 24);
  header.writeUInt32LE(SAMPLE_RATE * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'latin1');
  header.writeUInt32LE(dataSize, 40);
  return header;
}

/**
 * 流式 WAV 头：data 长度用 0xFFFFFFFF 占位（长度未知）。
 * 这是真实接口在"边合成边返回"时的行为，客户端必须能处理。
 * 用环境变量 MOCK_STREAMING_WAV=1 启用。
 */
function streamingWavHeader() {
  const header = wavHeader(0);
  header.writeUInt32LE(0xFFFFFFFF, 40); // data 长度 = 未知
  header.writeUInt32LE(0xFFFFFFFF, 4);  // RIFF 长度 = 未知
  return header;
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url || '/', `http://${req.headers.host}`);

  if (url.pathname !== '/v1/tools/tts' || req.method !== 'POST') {
    res.writeHead(404, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: { message: 'not found' } }));
  }

  if (!/^Bearer\s+\S+/.test(req.headers.authorization || '')) {
    res.writeHead(401, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: { message: 'invalid api key' } }));
  }

  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', async () => {
    let payload;
    try {
      payload = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    } catch {
      res.writeHead(400, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ error: { message: 'bad json' } }));
    }

    const text = String(payload.text || '');
    if (!text.trim()) {
      res.writeHead(400, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ error: { message: 'Content Exists Risk' } }));
    }
    // 测试钩子：触发指定错误码
    if (text.includes('__503__')) {
      res.writeHead(503, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ error: { message: 'service busy' } }));
    }

    const format = payload.format === 'pcm' ? 'pcm' : 'wav';
    const seconds = Number(process.env.MOCK_SECONDS || 1.5);
    const audio = makePcm(seconds);

    // MOCK_STREAMING_WAV=1：模拟真实接口的流式 WAV（长度字段为 0xFFFFFFFF 占位）
    const streaming = process.env.MOCK_STREAMING_WAV === '1';
    const header = format === 'wav'
      ? (streaming ? streamingWavHeader() : wavHeader(audio.length))
      : null;
    const body = header ? Buffer.concat([header, audio]) : audio;

    res.writeHead(200, {
      'content-type': format === 'wav' ? 'audio/wav' : 'application/octet-stream',
      // 流式返回时不声明 Content-Length（与真实接口一致）
      ...(streaming && format === 'wav' ? { 'transfer-encoding': 'chunked' } : { 'content-length': body.length }),
    });

    // 分块流式返回，模拟边合成边下发
    const step = 8192;
    for (let i = 0; i < body.length; i += step) {
      if (res.destroyed) return;
      res.write(body.subarray(i, i + step));
      await new Promise((r) => setTimeout(r, 5));
    }
    res.end();
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Mock 上游已启动： http://127.0.0.1:${PORT}/v1/tools/tts`);
});
