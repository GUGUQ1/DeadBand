# -*- coding: utf-8 -*-
<#
组发布包。

  1. 跑 build.cmd（失败就停）
  2. 把 exe / 图标 / 浏览器脚本 / 教程 PDF / 许可证 / 版本说明 摆成 release\DeadBand-v<版本>-win64\
  3. 压成 release\DeadBand-v<版本>-win64.zip

版本号来自 src\AssemblyInfo.cs 里的 AssemblyVersion，别在别处写第二份。

注意：本文件必须存成 UTF-8 **带 BOM**。Windows PowerShell 5.1 遇到没有 BOM 的
.ps1 会按系统 ANSI（简体中文是 GBK）解码，中文字符串直接变乱码、整个脚本解析失败。
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---------- 版本号 ----------
$asm = Join-Path $root 'src\AssemblyInfo.cs'
$m = Select-String -Path $asm -Pattern 'AssemblyVersion\("([0-9]+\.[0-9]+\.[0-9]+)' | Select-Object -First 1
if (-not $m) { throw "没能从 $asm 里读出 AssemblyVersion" }
$ver = $m.Matches[0].Groups[1].Value
Write-Host "版本 $ver" -ForegroundColor Cyan

# ---------- 编译 ----------
# exe 正在运行的话编译器写不进去（CS0016），先收掉
$running = Get-Process DeadBand -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "先退出正在运行的 DeadBand（pid $($running.Id -join ',')）" -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

Write-Host "编译..." -ForegroundColor Cyan
& cmd.exe /c "`"$root\build.cmd`""
if ($LASTEXITCODE -ne 0) { throw "build.cmd 失败（exit $LASTEXITCODE）" }

$dist = Join-Path $root 'dist'
if (-not (Test-Path (Join-Path $dist 'DeadBand.exe'))) { throw "dist\DeadBand.exe 不存在" }

# ---------- 摆目录 ----------
$name  = "DeadBand-v$ver-win64"
$stage = Join-Path $root "release\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'tools') -Force | Out-Null

Copy-Item (Join-Path $dist 'DeadBand.exe')   $stage -Force
if (Test-Path (Join-Path $dist 'deadband.ico')) { Copy-Item (Join-Path $dist 'deadband.ico') $stage -Force }
Get-ChildItem (Join-Path $dist 'tools') -Filter *.user.js -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $stage 'tools') -Force

# 教程 PDF：还没生成就提示，但不阻断（先能出包）
$pdf = Join-Path $root 'docs\DeadBand使用教程.pdf'
if (Test-Path $pdf) {
    Copy-Item $pdf $stage -Force
} else {
    Write-Host "提醒：docs\DeadBand使用教程.pdf 还没有，包里会缺教程" -ForegroundColor Yellow
}

if (Test-Path (Join-Path $root 'LICENSE')) {
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage '许可证.txt') -Force
}

# ---------- 版本说明 ----------
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("DeadBand v$ver")
[void]$sb.AppendLine("打包日期：" + (Get-Date -Format 'yyyy-MM-dd'))
[void]$sb.AppendLine("项目主页：https://github.com/GUGUQ1/DeadBand")
[void]$sb.AppendLine("")
$changelog = Join-Path $root 'CHANGELOG.md'
if (Test-Path $changelog) {
    [void]$sb.AppendLine("---- 更新日志 ----")
    [void]$sb.AppendLine("")
    Get-Content $changelog -Encoding UTF8 | Select-Object -First 40 | ForEach-Object { [void]$sb.AppendLine($_) }
} else {
    [void]$sb.AppendLine("首次发布。")
}
[System.IO.File]::WriteAllText((Join-Path $stage '版本说明.txt'), $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))

# ---------- 压缩 ----------
<#
不用 Compress-Archive：它（.NET Framework 版）把非 ASCII 文件名按**本地 ANSI（GBK）**
写进 zip 且不置 UTF-8 标志，换个语言的 Windows 打开就是一串乱码。
ZipFile.CreateFromDirectory 的最后一个参数可以指定 entryNameEncoding，
传 UTF-8 才会既写 UTF-8 字节、又置第 11 位标志。
#>
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $root "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false,
    [System.Text.Encoding]::UTF8)

$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host ""
Write-Host "包好了：release\$name.zip（$kb KB）" -ForegroundColor Green
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    Write-Host ("  " + $_.FullName.Substring($stage.Length + 1) + "  " + [math]::Round($_.Length / 1KB) + " KB")
}
