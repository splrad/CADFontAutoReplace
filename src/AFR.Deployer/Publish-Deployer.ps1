#Requires -Version 5.1
<#
.SYNOPSIS
    自动重生成所有 AutoCAD 插件 DLL，复制到 Resources\ 目录，再发布 AFR.Deployer 单文件 EXE。
.DESCRIPTION
    执行步骤：
      1. Release x64 构建所有 AFR-ACAD20XX 项目
         - Directory.Build.props 的 CopyDllToReleases Target 自动将 DLL 汇聚到
           artifacts\Releases\
      2. 从 artifacts\Releases\ 复制 DLL 到 AFR.Deployer\Resources\
      3. dotnet publish AFR.Deployer -> 自包含 .NET 10 单文件 EXE
         （已内置 .NET 10 运行时；用户仅需额外安装 Windows App Runtime 1.8 (x64)，
           缺失时由 AFR.Deployer 启动期检测并弹原生对话框给出下载链接）
.OUTPUTS
    <RepoRoot>\publish\AFR.Deployer\AFR.Deployer.exe
.EXAMPLE
    .\Publish-Deployer.ps1
    .\Publish-Deployer.ps1 -SkipPluginBuild   # 跳过插件构建，仅重新发布 EXE
#>
param(
    [switch]$SkipPluginBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 强制 UTF-8 控制台编码：dotnet CLI 默认输出 UTF-8，
# 而 PowerShell 在中文 Windows 上默认按 GBK (CP936) 解码外部进程输出，
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8

# ── 路径常量 ──────────────────────────────────────────────────────────────
$RepoRoot       = (Resolve-Path "$PSScriptRoot\..\..").Path
$PluginsRoot    = Join-Path $RepoRoot "src\AutoCAD"
$ReleasesDir    = Join-Path $RepoRoot "artifacts\Releases"   # CopyDllToReleases 的汇聚点
$ResourcesDir   = Join-Path $PSScriptRoot "Resources"
$DeployerCsproj = Join-Path $PSScriptRoot "AFR.Deployer.csproj"
$PublishOutput  = Join-Path $RepoRoot "publish\AFR.Deployer"

# 自动发现 src\AutoCAD\AFR-ACAD*\*.csproj，无需在新增 CAD 版本时手工维护此列表。
# TFM 仅用于日志展示，从 csproj 中解析；解析失败则回落到 "(unknown)"。
function Get-PluginTfm([string]$csprojPath) {
    try {
        [xml]$xml = Get-Content -LiteralPath $csprojPath -Raw
        $tfm = $xml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($tfm)) {
            $tfm = $xml.Project.PropertyGroup.TargetFrameworks | Where-Object { $_ } | Select-Object -First 1
        }
        if ([string]::IsNullOrWhiteSpace($tfm)) { return '(unknown)' }
        return $tfm
    } catch {
        return '(unknown)'
    }
}

$Plugins = @(
    Get-ChildItem -Path $PluginsRoot -Directory -Filter 'AFR-ACAD*' |
        Sort-Object Name |
        ForEach-Object {
            $csproj = Join-Path $_.FullName "$($_.Name).csproj"
            if (Test-Path $csproj) {
                [pscustomobject]@{
                    Name = $_.Name
                    TFM  = Get-PluginTfm $csproj
                }
            }
        }
)

if ($Plugins.Count -eq 0) {
    Write-Host "未在 $PluginsRoot 下发现任何 AFR-ACAD*\*.csproj，终止发布。" -ForegroundColor Red
    exit 1
}

Write-Host "自动发现 $($Plugins.Count) 个插件项目：" -ForegroundColor DarkGray
$Plugins | ForEach-Object { Write-Host "    • $($_.Name) ($($_.TFM))" -ForegroundColor DarkGray }

# ── 工具函数 ──────────────────────────────────────────────────────────────
function Write-Step([string]$msg) { Write-Host "`n── $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) { Write-Host "  ✗ $msg" -ForegroundColor Red }

# ── Step 1：构建所有插件 DLL ──────────────────────────────────────────────
if (-not $SkipPluginBuild) {
    Write-Step "构建所有 AutoCAD 插件 DLL (Release)"

    $buildErrors = @()
    foreach ($p in $Plugins) {
        $csproj = Join-Path $PluginsRoot "$($p.Name)\$($p.Name).csproj"
        Write-Host "  → 构建 $($p.Name) ($($p.TFM))..." -NoNewline

        # 使用 dotnet build；CopyDllToReleases Target 会在构建成功后自动
        # 将 DLL 复制到 artifacts\Releases\
        $output = dotnet build $csproj -c Release --nologo -v quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "失败"
            $buildErrors += $p.Name
            Write-Host ($output | Out-String) -ForegroundColor DarkRed
        } else {
            Write-Ok "完成"
        }
    }

    if ($buildErrors.Count -gt 0) {
        Write-Host "`n以下项目构建失败，终止发布：" -ForegroundColor Red
        $buildErrors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
}

# ── Step 2：从 artifacts\Releases\ 复制 DLL 到 Resources\ ────────────────
Write-Step "复制 DLL 到 Resources\"
New-Item -ItemType Directory -Force -Path $ResourcesDir | Out-Null

$copyErrors = @()
foreach ($p in $Plugins) {
    $srcDll = Join-Path $ReleasesDir "$($p.Name).dll"
    $dstDll = Join-Path $ResourcesDir "$($p.Name).dll"

    if (Test-Path $srcDll) {
        Copy-Item -Path $srcDll -Destination $dstDll -Force
        Write-Ok "$($p.Name).dll → Resources\"
    } else {
        Write-Fail "$($p.Name).dll 未在 artifacts\Releases\ 中找到"
        $copyErrors += $p.Name
    }

    # 方案 B：复制 CAD 元数据 JSON Sidecar（由 EmitCadDescriptorJson 目标生成）
    $srcJson = Join-Path $ReleasesDir "$($p.Name).cad.json"
    $dstJson = Join-Path $ResourcesDir "$($p.Name).cad.json"
    if (Test-Path $srcJson) {
        Copy-Item -Path $srcJson -Destination $dstJson -Force
        Write-Ok "$($p.Name).cad.json → Resources\"
    } else {
        Write-Fail "$($p.Name).cad.json 未生成（请检查 csproj 中的 CadBrand/CadVersion/CadRegistryBasePath 属性）"
        $copyErrors += "$($p.Name).cad.json"
    }
}

if ($copyErrors.Count -gt 0) {
    Write-Host "`n以下 DLL 缺失，终止发布：" -ForegroundColor Red
    $copyErrors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

# ── Step 3：发布 AFR.Deployer 单文件 EXE ─────────────────────────────────
Write-Step "发布 AFR.Deployer (自包含单文件)"
New-Item -ItemType Directory -Force -Path $PublishOutput | Out-Null

dotnet publish $DeployerCsproj -c Release -o $PublishOutput --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Fail "发布失败"
    exit 1
}

$defaultExePath = Join-Path $PublishOutput "AFR.Deployer.exe"
$exePath        = Join-Path $PublishOutput "AFR-Deployer.exe"
if (Test-Path $defaultExePath) {
    Move-Item -Path $defaultExePath -Destination $exePath -Force
}
$sizeMB  = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "`n✓ 发布完成！" -ForegroundColor Green
Write-Host "  输出：$exePath ($sizeMB MB)" -ForegroundColor Green
