# 一键打包：编译 Release → 生成便携版 zip → 生成安装包
# 用法（在项目根目录）：powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.0.1]
param([string]$Version = "1.0.1")

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appName = "声记"

# ---- 工具路径（SDK / Inno Setup 均为用户目录安装） ----
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$iscc = Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# ---- 1. 编译 ----
Write-Host "== 编译 Release $Version ==" -ForegroundColor Cyan
& $dotnet build (Join-Path $root "src\ShengJi\ShengJi.csproj") -c Release --nologo `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" "-p:AssemblyVersion=$Version.0"
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
$exe = Join-Path $root "src\ShengJi\bin\Release\ShengJi.exe"

# ---- 2. 整理输出 ----
$dist = Join-Path $root "dist"
$build = Join-Path $dist "build"
$portableDir = Join-Path $dist "portable\$appName"
foreach ($d in @($build, (Join-Path $dist "portable"))) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
}
New-Item -ItemType Directory -Path $build, $portableDir -Force | Out-Null

Copy-Item $exe (Join-Path $build "$appName.exe")
Copy-Item (Join-Path $root "docs\使用说明.txt") $build

# ---- 3. 便携版 ----
Write-Host "== 便携版 ==" -ForegroundColor Cyan
Copy-Item (Join-Path $build "$appName.exe") $portableDir
Copy-Item (Join-Path $root "docs\使用说明.txt") $portableDir
Copy-Item (Join-Path $root "docs\portable.txt") $portableDir
$zip = Join-Path $dist "${appName}_v${Version}_便携版.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# 手动写入条目，确保压缩包内路径使用标准的 “/” 分隔符，解压后是一个“声记”文件夹
$zs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$za = New-Object System.IO.Compression.ZipArchive($zs, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
foreach ($f in Get-ChildItem $portableDir -File) {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, "$appName/$($f.Name)",
        [System.IO.Compression.CompressionLevel]::Optimal)
}
$za.Dispose(); $zs.Dispose()
Write-Host "  $zip"

# ---- 4. 安装版 ----
Write-Host "== 安装版 ==" -ForegroundColor Cyan
if (-not (Test-Path $iscc)) { throw "找不到 Inno Setup：$iscc" }
& $iscc /Q "/DAppVersion=$Version" (Join-Path $root "installer\ShengJi.iss")
if ($LASTEXITCODE -ne 0) { throw "生成安装包失败" }
Write-Host "  $(Join-Path $dist "${appName}_v${Version}_安装版.exe")"

# 清理中间目录，只保留最终产物
Remove-Item (Join-Path $dist "portable") -Recurse -Force
Write-Host "== 完成 ==" -ForegroundColor Green
Get-ChildItem $dist -File | Format-Table Name, @{n = 'Size(KB)'; e = { [int]($_.Length / 1KB) } } -AutoSize
