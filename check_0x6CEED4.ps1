$dllPath = 'C:\Apps\Autodesk\AutoCAD 2025\acdb25.dll'
$rva = 0x6CEED4

Write-Host "检查 acdb25.dll+0x$($rva.ToString('X'))" -ForegroundColor Cyan

$bytes = [System.IO.File]::ReadAllBytes($dllPath)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$numSections = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
$optHeaderSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
$sectionBase = $peOffset + 24 + $optHeaderSize

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
    Write-Host "错误: RVA 未找到" -ForegroundColor Red
    exit 1
}

Write-Host "文件偏移: 0x$($fileOffset.ToString('X8'))" -ForegroundColor Green

$dump = $bytes[$fileOffset..($fileOffset + 31)] | ForEach-Object { $_.ToString('X2') }
Write-Host "`n前32字节:" -ForegroundColor Yellow
Write-Host ($dump -join ' ')

Write-Host "`n分析:" -ForegroundColor Cyan
$b0 = $bytes[$fileOffset]
$b1 = $bytes[$fileOffset + 1]
$b2 = $bytes[$fileOffset + 2]

Write-Host "b0=0x$($b0.ToString('X2')), b1=0x$($b1.ToString('X2')), b2=0x$($b2.ToString('X2'))"

# 检查常见 prologue 模式
if ($b0 -eq 0x48 -and $b1 -eq 0x89) {
    Write-Host "✓ REX.W mov 指令" -ForegroundColor Green
} elseif ($b0 -eq 0x48 -and $b1 -eq 0x83) {
    Write-Host "✓ REX.W sub rsp" -ForegroundColor Green
} elseif ($b0 -eq 0x48 -and $b1 -eq 0x8B) {
    Write-Host "✓ REX.W mov reg" -ForegroundColor Green
} elseif ($b0 -eq 0x40 -and $b0 -le 0x4F) {
    Write-Host "✓ REX prefix" -ForegroundColor Green
} else {
    Write-Host "⚠ 不匹配已知模式" -ForegroundColor Red
}
