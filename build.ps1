<#
.SYNOPSIS
    桌面分区管家 —— 一键构建脚本（Windows PowerShell 5.1 / PowerShell 7 均可）

.DESCRIPTION
    默认产出「完全免安装的独立单文件 exe」（约 65 MB），拷到任何 Windows 机器上双击就能跑，
    目标机器不需要预先安装 .NET 运行时。
    加 -Slim 则产出依赖 .NET 8 运行时的精简版（约 0.4 MB），适合本机已装运行时的场景。

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Slim
    .\build.ps1 -Runtime win-arm64
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',

    # 产出精简版（需要目标机器已安装 .NET 8 桌面运行时）
    [switch]$Slim,

    # 独立版额外开启 ReadyToRun 预编译（启动更快，但体积会大一倍）
    [switch]$ReadyToRun,

    # 只编译不发布
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$ProjectDir  = Join-Path $PSScriptRoot 'src/DesktopOrganizer'
$ProjectFile = Join-Path $ProjectDir 'DesktopOrganizer.csproj'

function Write-Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Ok($message) {
    Write-Host "    $message" -ForegroundColor Green
}

function Get-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) {
        throw @"
找不到 dotnet。

请先安装 .NET 8 SDK：
    winget install Microsoft.DotNet.SDK.8
或者访问 https://dotnet.microsoft.com/download 下载安装。
"@
    }
    return $cmd.Source
}

# ---------------------------------------------------------------- 环境检查
Write-Step '检查 .NET SDK'
$dotnet = Get-Dotnet
$version = & $dotnet --version
Write-Ok "dotnet $version  ($dotnet)"

$major = 0
if ($version -match '^(\d+)\.') { $major = [int]$Matches[1] }
if ($major -lt 8) {
    throw "需要 .NET 8 SDK 或更高版本，当前是 $version。"
}

# ---------------------------------------------------------------- 应用图标
# app.ico 是二进制文件，仓库里只存它的 base64 文本（tools/app-icon.b64.txt），
# 这里在编译前还原成真正的 .ico。已存在就跳过。
Write-Step '准备应用图标'

$iconPath = Join-Path $ProjectDir 'Assets/app.ico'
$b64Path  = Join-Path $PSScriptRoot 'tools/app-icon.b64.txt'

if (Test-Path $iconPath) {
    Write-Ok '图标已存在，跳过'
}
elseif (-not (Test-Path $b64Path)) {
    Write-Host '    没有图标也没有 base64 源文件，将使用默认图标继续构建。' -ForegroundColor Yellow
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path $iconPath -Parent) | Out-Null
    $b64 = (Get-Content $b64Path -Raw) -replace '\s', ''
    [System.IO.File]::WriteAllBytes($iconPath, [System.Convert]::FromBase64String($b64))
    Write-Ok '已从 base64 还原 app.ico'
}

# ---------------------------------------------------------------- 还原
Write-Step '还原 NuGet 依赖'
& $dotnet restore $ProjectFile
if ($LASTEXITCODE -ne 0) { throw '还原失败。' }

# ---------------------------------------------------------------- 编译
Write-Step "编译（$Configuration / $Runtime）"
& $dotnet build $ProjectFile -c $Configuration -r $Runtime --no-restore
if ($LASTEXITCODE -ne 0) { throw '编译失败，请看上面的错误信息。' }
Write-Ok '编译通过'

if ($SkipPublish) {
    Write-Host ''
    Write-Host '已按 -SkipPublish 跳过发布步骤。' -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- 发布
Write-Step '发布'

$publishArgs = @(
    'publish', $ProjectFile,
    '-c', $Configuration,
    '-r', $Runtime,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:DebugType=embedded'
)

if ($Slim) {
    $publishArgs += '--self-contained', 'false'
}
else {
    # 自包含：把 .NET 运行时 + WPF 原生库全部打进这一个 exe，
    # 目标机器无需安装任何东西，单文件拷贝过去双击即用。
    $publishArgs += '--self-contained', 'true'

    # 原生库（wpfgfx / D3DCompiler 等）也打进 exe，保证是真正的「一个文件」
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'

    # 压缩 bundle：体积从 ~138MB 降到 ~63MB（首次启动解压一次，之后走缓存）
    $publishArgs += '-p:EnableCompressionInSingleFile=true'

    # 默认关闭 ReadyToRun：它虽能加快启动，但会让体积从 ~63MB 膨胀到 ~146MB，不划算。
    # 需要时加 -ReadyToRun。
    if ($ReadyToRun) {
        $publishArgs += '-p:PublishReadyToRun=true'
    }
}

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw '发布失败，请看上面的错误信息。' }

$outputDir = Join-Path $ProjectDir "bin/$Configuration/net8.0-windows/$Runtime/publish"
$exe = Join-Path $outputDir 'DesktopOrganizer.exe'

if (-not (Test-Path $exe)) {
    throw "没有找到预期的输出文件：$exe"
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 2)

Write-Host ''
Write-Host '────────────────────────────────────────────' -ForegroundColor Green
Write-Host '  构建完成' -ForegroundColor Green
Write-Host '────────────────────────────────────────────' -ForegroundColor Green
Write-Host "  程序： $exe"
Write-Host "  体积： $size MB"
Write-Host "  模式： $(if ($Slim) { '精简版（需目标机器装 .NET 8 运行时）' } else { '独立版（免 .NET 运行时，拷过去即可用）' })"
Write-Host ''
Write-Host '  双击 DesktopOrganizer.exe 即可运行，托盘里会出现图标。' -ForegroundColor Yellow
Write-Host ''

if ((Get-Command explorer -ErrorAction SilentlyContinue)) {
    explorer.exe $outputDir
}
