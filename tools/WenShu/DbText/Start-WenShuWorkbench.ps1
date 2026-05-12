#Requires -Version 5.1
param(
    [string]$Package = "",
    [string]$Python = "",
    [string]$DatasetRoot = "",
    [string]$ModelRoot = "",
    [int]$Port = 0,
    [switch]$NoBrowser,
    [switch]$NoInstallDeps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
    Write-Host $Message -ForegroundColor Cyan
}

function Write-Warn([string]$Message) {
    Write-Host $Message -ForegroundColor Yellow
}

function Invoke-CommandLine([string[]]$CommandLine, [string[]]$ExtraArgs) {
    if ($CommandLine.Count -eq 0) {
        throw "No command was provided."
    }

    $exe = $CommandLine[0]
    $args = @()
    if ($CommandLine.Count -gt 1) {
        $args += $CommandLine[1..($CommandLine.Count - 1)]
    }
    $args += $ExtraArgs
    & $exe @args 2>&1 | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

function Resolve-BasePythonCommand {
    if (Get-Command python -ErrorAction SilentlyContinue) {
        return @("python")
    }
    if (Get-Command py -ErrorAction SilentlyContinue) {
        return @("py", "-3")
    }
    return @()
}

function Ensure-PythonEnvironment([string]$ToolRoot, [string]$RequestedPython, [switch]$SkipInstall) {
    $venvRoot = Join-Path $ToolRoot ".venv"
    $venvPython = Join-Path $venvRoot "Scripts\python.exe"
    $requirements = Join-Path $ToolRoot "requirements.txt"

    if (-not [string]::IsNullOrWhiteSpace($RequestedPython)) {
        if (-not (Test-Path -LiteralPath $RequestedPython) -and -not (Get-Command $RequestedPython -ErrorAction SilentlyContinue)) {
            throw "Requested Python was not found: $RequestedPython"
        }
        return $RequestedPython
    }

    if (-not (Test-Path -LiteralPath $venvPython)) {
        $basePython = @(Resolve-BasePythonCommand)
        if ($basePython.Count -eq 0) {
            throw "Python was not found. Install Python 3.11+ or pass -Python with a full python.exe path."
        }

        Write-Info "Local .venv not found. Creating: $venvRoot"
        $code = Invoke-CommandLine $basePython @("-m", "venv", $venvRoot)
        if ($code -ne 0) {
            throw "Failed to create Python virtual environment. Exit code: $code"
        }
    }

    if (-not $SkipInstall) {
        $check = "import importlib.util, sys; mods=['numpy','pandas','lightgbm','onnx','onnxmltools','packaging']; missing=[m for m in mods if importlib.util.find_spec(m) is None]; print('missing=' + ','.join(missing) if missing else 'dependencies-ok'); sys.exit(1 if missing else 0)"
        & $venvPython -c $check *> $null
        if ($LASTEXITCODE -ne 0) {
            if (-not (Test-Path -LiteralPath $requirements)) {
                throw "Missing requirements.txt: $requirements"
            }

            Write-Warn "Training dependencies are missing. Installing requirements.txt. First run may take a few minutes."
            & $venvPython -m pip install -r $requirements 2>&1 | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to install training dependencies. Exit code: $LASTEXITCODE"
            }
        }
    }

    return $venvPython
}

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8

    $ToolRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $ToolRoot "..\..\..")).Path

    if ([string]::IsNullOrWhiteSpace($DatasetRoot)) {
        $DatasetRoot = Join-Path $RepoRoot "datasets\WenShu\DbText"
    }
    if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
        $ModelRoot = Join-Path $RepoRoot "models\WenShu\DbText\Current"
    }

    New-Item -ItemType Directory -Force -Path $DatasetRoot, $ModelRoot | Out-Null

    $Python = Ensure-PythonEnvironment $ToolRoot $Python $NoInstallDeps

    $Server = Join-Path $ToolRoot "workbench\server.py"
    if (-not (Test-Path -LiteralPath $Server)) {
        throw "Workbench server script was not found: $Server"
    }

    $Arguments = @(
        $Server,
        "--tool-root", $ToolRoot,
        "--dataset-root", $DatasetRoot,
        "--model-root", $ModelRoot,
        "--port", $Port
    )
    if (-not [string]::IsNullOrWhiteSpace($Package)) {
        $Arguments += @("--package", $Package)
    }
    if ($NoBrowser) {
        $Arguments += "--no-open"
    }

    Write-Info "Starting AFR WenShu training workbench..."
    Write-Info "Tool root: $ToolRoot"
    Write-Info "Dataset root: $DatasetRoot"
    Write-Info "Model root: $ModelRoot"
    Write-Info "Python: $Python"
    if (-not [string]::IsNullOrWhiteSpace($Package)) {
        Write-Info "Package: $Package"
    }

    & $Python @Arguments
    exit $LASTEXITCODE
}
catch {
    Write-Host ""
    Write-Host "AFR WenShu workbench failed to start:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    Write-Host "Suggested fixes:" -ForegroundColor Yellow
    Write-Host "1. Install Python 3.11+, or pass -Python with a full python.exe path." -ForegroundColor Yellow
    Write-Host "2. If dependency installation failed, retry with network access or run:" -ForegroundColor Yellow
    Write-Host "   .\tools\WenShu\DbText\.venv\Scripts\python.exe -m pip install -r .\tools\WenShu\DbText\requirements.txt" -ForegroundColor Yellow
    Write-Host "3. If script execution policy blocks this file, run:" -ForegroundColor Yellow
    Write-Host "   powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Yellow
    Write-Host ""
    Read-Host "Press Enter to close"
    exit 1
}
