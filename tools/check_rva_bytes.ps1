# 检查 acdb25.dll 指定 RVA 的入口字节
param(
    [Parameter(Mandatory=$true)]
    [string]$RvaHex  # 例如: "6CEED4"
)

$dllPath = 'C:\Apps\Autodesk\AutoCAD 2025\acdb25.dll'
if (-not (Test-Path $dllPath)) {
    Write-Host "错误: 找不到 $dllPath" -ForegroundColor Red
    exit 1
}

$rva = [Convert]::ToInt32($RvaHex, 16)
Write-Host "目标 RVA: 0x$($rva.ToString('X'))" -ForegroundColor Cyan

# 读取 PE 头
$bytes = [System.IO.File]::ReadAllBytes($dllPath)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$numSections = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
$optHeaderSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
$sectionBase = $peOffset + 24 + $optHeaderSize

# RVA → 文件偏移
$fileOffset = $null
for ($i = 0; $i -lt $numSections; $i++) {
    $off = $sectionBase + $i * 40
    $vAddr = [BitConverter]::ToUInt32($bytes, $off + 12)
    $vSize = [BitConverter]::ToUInt32($bytes, $off + 16)
    $rawOff = [BitConverter]::ToUInt32($bytes, $off + 20)

    if ($rva -ge $vAddr -and $rva -lt ($vAddr + $vSize)) {
        $fileOffset = $rawOff + ($rva - $vAddr)
        break
    }
}

if ($null -eq $fileOffset) {
    Write-Host "错误: RVA 未找到对应的节" -ForegroundColor Red
    exit 1
}

Write-Host "文件偏移: 0x$($fileOffset.ToString('X8'))" -ForegroundColor Green

# 读取前 32 字节
$dump = $bytes[$fileOffset..($fileOffset + 31)] | ForEach-Object { $_.ToString('X2') }
Write-Host "`n入口字节 (前32字节):" -ForegroundColor Yellow
Write-Host ($dump -join ' ')

# 分析 Prologue
Write-Host "`n分析:" -ForegroundColor Cyan
$b0 = $bytes[$fileOffset]
$b1 = $bytes[$fileOffset + 1]
$b2 = $bytes[$fileOffset + 2]

if ($b0 -eq 0x48 -and $b1 -eq 0x89) {
    Write-Host "✓ 开始于 REX.W mov 指令 (48 89)" -ForegroundColor Green
} elseif ($b0 -eq 0x48 -and $b1 -eq 0x83) {
    Write-Host "✓ 开始于 REX.W sub rsp (48 83)" -ForegroundColor Green
} elseif ($b0 -eq 0x48 -and $b1 -eq 0x8B) {
    Write-Host "✓ 开始于 REX.W mov reg (48 8B)" -ForegroundColor Green
} else {
    Write-Host "⚠ 非典型序言开始: $($b0.ToString('X2')) $($b1.ToString('X2'))" -ForegroundColor Yellow
}

# 检查是否有 RIP-relative 寻址
$hasRipRelative = $false
for ($i = 0; $i -lt 20; $i++) {
    $curr = $bytes[$fileOffset + $i]
    $next = $bytes[$fileOffset + $i + 1]

    # 检查 lea reg, [rip+disp32] 或 mov reg, [rip+disp32]
    if (($curr -eq 0x48 -or $curr -eq 0x4C) -and 
        ($next -eq 0x8D -or $next -eq 0x8B)) {
        $modrm = $bytes[$fileOffset + $i + 2]
        if (($modrm -band 0xC7) -eq 0x05) {
            $hasRipRelative = $true
            Write-Host "⚠ 检测到 RIP-relative 寻址 at offset +$i" -ForegroundColor Red
            break
        }
    }
}

if (-not $hasRipRelative) {
    Write-Host "✓ 未检测到 RIP-relative 寻址" -ForegroundColor Green
}

Write-Host "`n建议:" -ForegroundColor Cyan
if (-not $hasRipRelative) {
    Write-Host "  该函数适合使用 inline hook (trampoline)" -ForegroundColor Green
} else {
    Write-Host "  该函数包含 RIP-relative 寻址,需要重定位" -ForegroundColor Yellow
}
