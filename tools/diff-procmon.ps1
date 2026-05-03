# 比对三个 ProcMon 筛选 CSV，找出关键差异
$f1 = 'E:\Software Plugin Project\01\第一次-设置忽略弹窗.CSV'
$f2 = 'E:\Software Plugin Project\01\第二次-设置忽略弹窗.CSV'
$f3 = 'E:\Software Plugin Project\01\第三次-取消忽略弹窗设置.CSV'

$d1 = Import-Csv $f1
$d2 = Import-Csv $f2
$d3 = Import-Csv $f3

$s1 = @{}; $d1 | ForEach-Object { $s1[$_.Value] = [int]$_.Count }
$s2 = @{}; $d2 | ForEach-Object { $s2[$_.Value] = [int]$_.Count }
$s3 = @{}; $d3 | ForEach-Object { $s3[$_.Value] = [int]$_.Count }

Write-Host "=== 总条目 ===" -ForegroundColor Cyan
Write-Host "f1=$($s1.Count)  f2=$($s2.Count)  f3=$($s3.Count)"

Write-Host "`n=== 仅在 f1+f2(设置忽略) 出现，f3(取消) 没有 ===" -ForegroundColor Yellow
$onlySet = $s1.Keys | Where-Object { $s2.ContainsKey($_) -and -not $s3.ContainsKey($_) }
$onlySet | Sort-Object | ForEach-Object { "  $_  (f1=$($s1[$_]) f2=$($s2[$_]))" }

Write-Host "`n=== 仅在 f3(取消) 出现，f1/f2 没有 ===" -ForegroundColor Yellow
$onlyCancel = $s3.Keys | Where-Object { -not $s1.ContainsKey($_) -and -not $s2.ContainsKey($_) }
$onlyCancel | Sort-Object | ForEach-Object { "  $_  (f3=$($s3[$_]))" }

Write-Host "`n=== 三次都有，但 f3 比 f1/f2 多很多 (Count差>=3) ===" -ForegroundColor Yellow
$common = $s1.Keys | Where-Object { $s2.ContainsKey($_) -and $s3.ContainsKey($_) }
$common | ForEach-Object {
    $diff = $s3[$_] - [Math]::Max($s1[$_], $s2[$_])
    if ($diff -ge 3) { "  +$diff  $_  (f1=$($s1[$_]) f2=$($s2[$_]) f3=$($s3[$_]))" }
} | Sort-Object

Write-Host "`n=== 包含关键字 'Dialog'/'Hideable'/'Hidden'/'Font'/'SHX'/'Filedia'/'Suppress' 的所有路径 ===" -ForegroundColor Magenta
$kw = 'Dialog','Hideable','Hidden','Font','SHX','Filedia','Suppress','MsgBox','Notification'
$all = New-Object System.Collections.Generic.HashSet[string]
$s1.Keys + $s2.Keys + $s3.Keys | ForEach-Object { [void]$all.Add($_) }
foreach ($k in ($all | Sort-Object)) {
    foreach ($w in $kw) {
        if ($k -match $w) {
            $c1 = if ($s1.ContainsKey($k)) { $s1[$k] } else { '-' }
            $c2 = if ($s2.ContainsKey($k)) { $s2[$k] } else { '-' }
            $c3 = if ($s3.ContainsKey($k)) { $s3[$k] } else { '-' }
            "  [$w] f1=$c1 f2=$c2 f3=$c3  $k"
            break
        }
    }
}
