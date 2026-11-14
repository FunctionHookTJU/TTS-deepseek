# 发布到 GitHub Release 的 PowerShell 脚本
#
# 前置条件：
#   1. 已安装 GitHub CLI（winget install GitHub.cli）
#   2. 已登录：gh auth login
#   3. 安装包已构建：dist\DeepSeekTTS-Setup-<版本>.exe
#
# 用法：
#   .\scripts\publish-release.ps1 -Repo deepseek-tts -Visibility public
#   .\scripts\publish-release.ps1 -Repo deepseek-tts -Version 2.0.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [string]$Version = "2.0.0",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Owner = "",
    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# 定位 gh
$gh = "C:\Program Files\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $cmd) { throw "未找到 GitHub CLI，请先运行：winget install GitHub.cli" }
    $gh = $cmd.Source
}

# 工作目录 = 项目根
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Step "检查登录状态"
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    throw "尚未登录 GitHub，请先运行：`n  `"$gh`" auth login"
}
$whoami = (& $gh api user --jq .login).Trim()
Write-Ok "已登录：$whoami"

# 检查产物
$setup = Get-ChildItem "dist\DeepSeekTTS-Setup-*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
$portable = "publish\DeepSeekTTS.exe"
if (-not $setup) { throw "未找到安装包，请先运行：ISCC installer.iss" }
Write-Ok "安装包：$($setup.Name) ($([math]::Round($setup.Length/1MB,2)) MB)"

if (-not $SkipPush) {
    Write-Step "创建仓库 $Owner/$Repo"
    $fullName = if ($Owner) { "$Owner/$Repo" } else { $Repo }
    $exists = $false
    & $gh repo view $fullName *> $null
    if ($LASTEXITCODE -eq 0) {
        $exists = $true
        Write-Ok "仓库已存在，跳过创建"
    } else {
        $visFlag = if ($Visibility -eq "public") { "--public" } else { "--private" }
        & $gh repo create $fullName $visFlag --source . --remote origin --push
        if ($LASTEXITCODE -ne 0) { throw "创建仓库失败" }
        Write-Ok "仓库已创建并推送"
    }

    if ($exists) {
        Write-Step "推送代码"
        $hasRemote = (& git remote) -contains "origin"
        if (-not $hasRemote) {
            $cloneUrl = (& $gh repo view $fullName --json url --jq .url).Trim() + ".git"
            & git remote add origin $cloneUrl
        }
        & git push -u origin main
        if ($LASTEXITCODE -ne 0) { throw "推送失败" }
        Write-Ok "代码已推送"
    }

    Write-Step "推送标签 v$Version"
    $tagExists = & git tag -l "v$Version"
    if (-not $tagExists) {
        & git tag "v$Version"
    }
    & git push origin "v$Version" --force
    if ($LASTEXITCODE -ne 0) { Write-Host "    标签推送失败（可能已存在），继续" -ForegroundColor Yellow }
    else { Write-Ok "标签已推送" }
}

Write-Step "创建 Release v$Version"
$files = @($setup.FullName)
if (Test-Path $portable) { $files += (Resolve-Path $portable).Path }

$notes = @"
## DeepSeek 语音合成 v$Version

基于 DeepSeek 官方 TTS 接口的 Windows 原生桌面应用。

### 下载

| 文件 | 说明 |
| --- | --- |
| ``DeepSeekTTS-Setup-$Version.exe`` | 安装包：创建快捷方式与卸载项（推荐） |
| ``DeepSeekTTS.exe`` | 免安装单文件，双击即用 |

自包含 .NET 运行时，无需安装任何环境。

### 功能

- 文本转语音，两种音色（海星 / 暗潮）
- 内置播放器，支持拖动进度
- WAV / PCM 两种格式，下载保持官方原始字节不做转换
- API Key 应用内配置，本地保存并脱敏显示

### 修复

- 修复流式 WAV 的 ``0xFFFFFFFF`` 长度字段导致播放报错
- 修复静默卸载卡死
- 修复安装后中文名称乱码

安装后首次启动会提示填写 API Key。
"@

$existing = & $gh release view "v$Version" --json tagName --jq .tagName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Ok "Release 已存在，上传/覆盖产物"
    & $gh release upload "v$Version" @files --clobber
} else {
    & $gh release create "v$Version" @files --title "DeepSeek 语音合成 v$Version" --notes $notes
}
if ($LASTEXITCODE -ne 0) { throw "创建 Release 失败" }

$url = (& $gh release view "v$Version" --json url --jq .url).Trim()
Write-Host "`n完成！" -ForegroundColor Green
Write-Host "Release: $url" -ForegroundColor Green
