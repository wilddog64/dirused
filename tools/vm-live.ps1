$ErrorActionPreference = 'Continue'
$exe = 'C:\src\dirused\src\Dirused.Cli\bin\Release\net10.0-windows\dirused.exe'
$script:failed = 0
$script:passed = 0

function Invoke-Dirused([string[]]$argList) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $exe @argList 2>&1 | ForEach-Object { "$_" }
    $rc = $LASTEXITCODE
    $sw.Stop()
    [pscustomobject]@{ rc = $rc; out = ($out -join "`n"); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); cmd = "dirused " + ($argList -join ' ') }
}

function Test-Check([string]$id, [string]$what, [bool]$ok, $r) {
    if ($ok) {
        "PASS $id $what  [$($r.cmd) -> rc=$($r.rc), $($r.seconds)s]"
        $script:passed++
    } else {
        "FAIL $id $what  [$($r.cmd) -> rc=$($r.rc), $($r.seconds)s]"
        $r.out -split "`n" | Select-Object -First 20 | ForEach-Object { "    $_" }
        $script:failed++
    }
}

function ConvertFrom-DirusedJson($r) {
    try { $r.out | ConvertFrom-Json } catch { $null }
}

"host: $env:COMPUTERNAME  os: $((Get-CimInstance Win32_OperatingSystem).Caption)  date: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
if (-not (Test-Path -LiteralPath $exe)) { "FAIL setup: $exe not found (run make vm-build)"; exit 1 }
"exe: $exe  built: $((Get-Item -LiteralPath $exe).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
""

$codes = '(51|53|64|67|1231)'

$r = Invoke-Dirused @('\\nosuchhost-xyz\c$')
Test-Check 'L1' 'unreachable share prints UNREACHABLE with a known code and exits 3' (($r.rc -eq 3) -and ($r.out -match "UNREACHABLE: \\\\nosuchhost-xyz\\c\$ \($codes\)")) $r

$r = Invoke-Dirused @('\\nosuchhost-xyz')
Test-Check 'L2' 'unreachable bare node prints UNREACHABLE with a known code and exits 3' (($r.rc -eq 3) -and ($r.out -match "UNREACHABLE: \\\\nosuchhost-xyz.* \($codes\)")) $r

$letters = @(Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | ForEach-Object { "$($_.DriveLetter)" } | Sort-Object)
$r = Invoke-Dirused @('\\localhost', '/summary', '/json')
$j = ConvertFrom-DirusedJson $r
$got = @()
if ($j) { $got = @($j.targets | Where-Object { $_.complete } | ForEach-Object { $_.target }) }
$want = @($letters | ForEach-Object { "\\localhost\$_`$\" })
Test-Check 'L3' "\\localhost /summary lists exactly the fixed drives ($($letters -join ',')) as admin shares, all complete, exit 0" (($r.rc -eq 0) -and ($got.Count -eq $want.Count) -and (-not (Compare-Object $got $want))) $r

$volOk = $true
if ($j) {
    foreach ($t in $j.targets) {
        $letter = $t.target.Substring(12, 1)
        $v = Get-Volume -DriveLetter $letter
        if ([int64]$t.volume.totalBytes -ne [int64]$v.Size -or [int64]$t.volume.freeBytes -ne [int64]$v.SizeRemaining) { $volOk = $false; "    drive $($letter): dirused total=$($t.volume.totalBytes) free=$($t.volume.freeBytes)  Get-Volume total=$($v.Size) free=$($v.SizeRemaining)" }
    }
} else { $volOk = $false }
Test-Check 'L4' '\\localhost /summary totals and free space equal Get-Volume byte for byte' $volOk $r

$dir = Join-Path $env:TEMP 'dirused-live'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file = Join-Path $dir 'targets.txt'
Set-Content -LiteralPath $file -Encoding UTF8 -Value @('# fleet list with a BOM, a comment and a blank line', '', 'C:\Windows\Web', '\\localhost\C$\Windows\Web', '\\nosuchhost-xyz\c$')
$r = Invoke-Dirused @('/file', $file, '/parallel', '8', '/timeout', '60s', '/top', '1', '/json')
$j = ConvertFrom-DirusedJson $r
$order = @()
if ($j) { $order = @($j.targets | ForEach-Object { $_.target }) }
$fileOk = ($r.rc -eq 3) -and $j -and ($order.Count -eq 3) -and ($order[0] -like 'C:\Windows\Web*') -and ($order[1] -like '\\localhost\C$\Windows\Web*') -and ($order[2] -like '\\nosuchhost-xyz*') -and $j.targets[0].complete -and $j.targets[1].complete -and (-not $j.targets[2].complete)
Test-Check 'L5' '/file with BOM+comment+blank, /parallel 8: 3 targets in input order, unreachable one fails alone, exit 3' $fileOk $r

$sameTotals = $false
if ($j -and $order.Count -eq 3) {
    $a = $j.targets[0].directories | Select-Object -First 1
    $b = $j.targets[1].directories | Select-Object -First 1
    $sameTotals = ($a -and $b -and ([int64]$a.onDiskBytes -eq [int64]$b.onDiskBytes) -and ([int64]$a.logicalBytes -eq [int64]$b.logicalBytes))
}
Test-Check 'L6' 'local and UNC scans of C:\Windows\Web report the same top-folder totals' $sameTotals $r

$r = Invoke-Dirused @('C:\', '/timeout', '2s', '/top', '1', '/json')
$j = ConvertFrom-DirusedJson $r
$timedOut = $j -and (@($j.targets[0].errors | Where-Object { $_.reason -eq 'TIMED OUT after 2s' }).Count -eq 1) -and (-not $j.targets[0].complete)
Test-Check 'L7' 'full scan of C:\ with /timeout 2s reports TIMED OUT after 2s within 30s, exit 3' (($r.rc -eq 3) -and $timedOut -and ($r.seconds -lt 30)) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/timeout', '30s')
Test-Check 'L8' 'positive control: /summary of a local folder with a generous /timeout completes, exit 0' (($r.rc -eq 0) -and ($r.out -notmatch 'TIMED OUT|CANCELLED')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/parallel', '65')
Test-Check 'L9' '/parallel 65 is rejected as a usage error, exit 1' (($r.rc -eq 1) -and ($r.out -match '/parallel requires an integer between 1 and 64')) $r

$r = Invoke-Dirused @('/help')
Test-Check 'L10' '/help prints switches and exit codes, exit 0' (($r.rc -eq 0) -and ($r.out -match 'Exit codes') -and ($r.out -match '/parallel')) $r

Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
""
"RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
