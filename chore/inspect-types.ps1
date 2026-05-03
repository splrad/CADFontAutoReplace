param([string]$Path)
$bytes = [System.IO.File]::ReadAllBytes($Path)
$asm = [System.Reflection.Assembly]::Load($bytes)
$t = $asm.GetTypes() | Where-Object { $_.Name -eq 'AppInitializer' }
if (-not $t) { Write-Host 'AppInitializer not found'; exit 1 }
Write-Host "Type: $($t.FullName)"
$bf = [System.Reflection.BindingFlags] 'NonPublic, Public, Static, Instance, DeclaredOnly'
$t.GetMethods($bf) | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" }
