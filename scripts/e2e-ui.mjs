/**
 * 端到端 UI 验证：启动 WPF 应用 → 自动配置 Key → 触发合成 → 截图结果区
 * 依赖：本地 mock 上游 (scripts/mock-upstream.js) 监听 4300
 * 用法: node scripts/e2e-ui.mjs
 */
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';

const ROOT = process.cwd();
const EXE = path.join(ROOT, 'publish', 'DeepSeekTTS.exe');
const DATA = path.join(ROOT, '.e2e-data');
const OUT = path.join(ROOT, 'e2e-shots');

await fs.rm(DATA, { recursive: true, force: true });
await fs.mkdir(OUT, { recursive: true });
// 预置配置：直接写好 Key，跳过设置弹窗，便于验证主流程
await fs.mkdir(DATA, { recursive: true });
await fs.writeFile(path.join(DATA, 'settings.json'), JSON.stringify({
  ApiKey: 'sk-e2e-mock-key-123456',
  VoiceId: 'stella',
  Format: 'wav',
  FilterMarkdown: true,
}, null, 2));

const ps = `
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-MainHandle([int]$procId) {
  $found = [IntPtr]::Zero
  $cb = [W+EnumProc]{
    param($h, $l)
    $pid2 = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $procId -and [W]::IsWindowVisible($h)) {
      $sb = New-Object System.Text.StringBuilder 256
      [void][W]::GetWindowTextW($h, $sb, 256)
      if ($sb.ToString() -eq 'DeepSeek 语音合成') { $script:found = $h; return $false }
    }
    return $true
  }
  [void][W]::EnumWindows($cb, [IntPtr]::Zero)
  return $script:found
}

function Shot([IntPtr]$h, [string]$name) {
  $r = New-Object W+RECT
  [void][W]::GetWindowRect($h, [ref]$r)
  $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
  if ($w -le 0 -or $ht -le 0) { return $false }
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][W]::PrintWindow($h, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $bmp.Save((Join-Path '${OUT.replace(/\\/g, '\\\\')}' $name), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  return $true
}

$env:DEEPSEEK_TTS_DATA = '${DATA.replace(/\\/g, '\\\\')}'
$env:DEEPSEEK_TTS_BASE = 'http://127.0.0.1:4300'
$proc = Start-Process -FilePath '${EXE.replace(/\\/g, '\\\\')}' -PassThru
Start-Sleep -Seconds 4

$h = Get-MainHandle $proc.Id
if ($h -eq [IntPtr]::Zero) { Write-Output '{"error":"main window not found"}'; exit 1 }
[void][W]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 500

$root = $A::FromHandle($h)
$out = @{}

# 1. 初始截图
[void](Shot $h 'e2e-1-initial.png')

# 2. 填入文本（ValuePattern）
$editCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
$edits = $root.FindAll($TS::Descendants, $editCond)
$out.editCount = $edits.Count
if ($edits.Count -gt 0) {
  $vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $vp.SetValue('你好，我是 DeepSeek。这是一段用于验证原生界面播放与下载的测试语音。')
  $out.textFilled = $true
}
Start-Sleep -Milliseconds 600
[void](Shot $h 'e2e-2-text.png')

# 3. 点击「生成语音」（自定义模板按钮不支持 Invoke，用鼠标模拟）
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
}
"@
$btnCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, '生成语音')
$genBtn = $root.FindFirst($TS::Descendants, $btnCond)
if ($genBtn -ne $null) {
  $b = $genBtn.Current.BoundingRectangle
  $cx = [int]($b.X + $b.Width/2); $cy = [int]($b.Y + $b.Height/2)
  [void][M]::SetCursorPos($cx, $cy)
  Start-Sleep -Milliseconds 250
  [M]::mouse_event(0x02,0,0,0,[IntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [M]::mouse_event(0x04,0,0,0,[IntPtr]::Zero)
  $out.generateClicked = $true
} else {
  $out.generateClicked = $false
}

Start-Sleep -Seconds 12
[void](Shot $h 'e2e-3-result.png')

# 4. 读取结果区文本，确认已生成
$all = $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
$texts = @()
foreach ($t in $all) { if ($t.Current.Name) { $texts += $t.Current.Name } }
$out.texts = $texts
$out.hasResult = ($texts -join ' ') -match 'WAV|KB|临时缓存'

$out | ConvertTo-Json -Depth 5

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
`;

const p = spawn('pwsh', ['-NoProfile', '-Command', ps], { stdio: 'inherit' });
p.on('exit', (code) => process.exit(code ?? 0));
