param(
    [string]$Exe = '',
    [string]$Targets = ''
)
$ErrorActionPreference = 'Continue'
$env:DIRUSED_NO_UPDATE_CHECK = '1'
$exe = $Exe
if (-not $exe) {
    $nextToScript = Join-Path $PSScriptRoot 'dirused.exe'
    if (Test-Path -LiteralPath $nextToScript) { $exe = $nextToScript } else { $exe = 'C:\src\dirused\src\Dirused.Cli\bin\Release\net10.0-windows\dirused.exe' }
}
$script:failed = 0
$script:passed = 0

function Invoke-Dirused([string[]]$argList) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $exe @argList 2>&1 | ForEach-Object { "$_" }
    $rc = $LASTEXITCODE
    $sw.Stop()
    [pscustomobject]@{ rc = $rc; out = ($out -join "`n"); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); cmd = "dirused " + (($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ') }
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
if (-not (Test-Path -LiteralPath $exe)) { "FAIL setup: $exe not found (run make vm-build, or pass -Exe)"; exit 1 }
"exe: $exe  built: $((Get-Item -LiteralPath $exe).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
""

$codes = '(51|53|64|67|1231|1707)'

$r = Invoke-Dirused @('\\nosuchhost-xyz\c$')
Test-Check 'L1' 'unreachable share prints UNREACHABLE with a known code and exits 3' (($r.rc -eq 3) -and ($r.out -match "UNREACHABLE: \\\\nosuchhost-xyz\\c\$ \($codes\)")) $r

$r = Invoke-Dirused @('\\nosuchhost-xyz')
Test-Check 'L2' 'unreachable bare node prints UNREACHABLE with a known code and exits 3' (($r.rc -eq 3) -and ($r.out -match "UNREACHABLE: \\\\nosuchhost-xyz.* \($codes\)")) $r

$letters = @(Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' } | ForEach-Object { "$($_.DriveLetter)" } | Sort-Object)
$r = Invoke-Dirused @('\\localhost', '/summary', '/json', '/warn', '99', '/crit', '100')
$j = ConvertFrom-DirusedJson $r
$got = @()
if ($j) { $got = @($j.targets | Where-Object { $_.complete } | ForEach-Object { $_.target }) }
$want = @($letters | ForEach-Object { "\\localhost\$_`$\" })
Test-Check 'L3' "\\localhost /summary lists exactly the fixed drives ($($letters -join ',')) as admin shares, all complete, exit 0" (($r.rc -eq 0) -and ($got.Count -eq $want.Count) -and (-not (Compare-Object $got $want))) $r

$volOk = $true
if ($j) {
    foreach ($t in $j.targets) {
        if ("$($t.target)" -notmatch '^\\\\localhost\\([A-Za-z])\$\\$') { $volOk = $false; "    unexpected target: $($t.target)"; continue }
        $letter = $Matches[1]
        $v = Get-Volume -DriveLetter $letter -ErrorAction SilentlyContinue
        if (-not $v) { $volOk = $false; "    no local volume for drive $letter"; continue }
        if ([int64]$t.volume.totalBytes -ne [int64]$v.Size -or [int64]$t.volume.freeBytes -ne [int64]$v.SizeRemaining) { $volOk = $false; "    drive $($letter): dirused total=$($t.volume.totalBytes) free=$($t.volume.freeBytes)  Get-Volume total=$($v.Size) free=$($v.SizeRemaining)" }
    }
} else { $volOk = $false }
Test-Check 'L4' '\\localhost /summary totals and free space equal Get-Volume byte for byte' $volOk $r

$dir = Join-Path $env:TEMP 'dirused-live'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file = Join-Path $dir 'targets.txt'
[IO.File]::WriteAllLines($file, [string[]]@('# fleet list with a BOM, a comment and a blank line', '', 'C:\Windows\Web', '\\localhost\C$\Windows\Web', '\\nosuchhost-xyz\c$'), (New-Object System.Text.UTF8Encoding $true))
$head = [IO.File]::ReadAllBytes($file)[0..2]
$bomOk = ($head[0] -eq 0xEF) -and ($head[1] -eq 0xBB) -and ($head[2] -eq 0xBF)
$r = Invoke-Dirused @('/file', $file, '/parallel', '8', '/timeout', '60s', '/top', '1', '/json')
$j = ConvertFrom-DirusedJson $r
$order = @()
if ($j) { $order = @($j.targets | ForEach-Object { $_.target }) }
$fileOk = ($r.rc -eq 3) -and $j -and ($order.Count -eq 3) -and ($order[0] -like 'C:\Windows\Web*') -and ($order[1] -like '\\localhost\C$\Windows\Web*') -and ($order[2] -like '\\nosuchhost-xyz*') -and $j.targets[0].complete -and $j.targets[1].complete -and (-not $j.targets[2].complete)
Test-Check 'L5' '/file with BOM+comment+blank, /parallel 8: 3 targets in input order, unreachable one fails alone, exit 3' ($fileOk -and $bomOk) $r
if (-not $bomOk) { '    fixture: targets.txt does not start with a UTF-8 BOM' }

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

$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/timeout', '30s', '/warn', '99', '/crit', '100')
Test-Check 'L8' 'positive control: /summary of a local folder with a generous /timeout completes, exit 0' (($r.rc -eq 0) -and ($r.out -notmatch 'TIMED OUT|CANCELLED')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/parallel', '65')
Test-Check 'L9' '/parallel 65 is rejected as a usage error, exit 1' (($r.rc -eq 1) -and ($r.out -match '/parallel requires an integer between 1 and 64')) $r

$r = Invoke-Dirused @('/help')
Test-Check 'L10' '/help prints switches and exit codes, exit 0' (($r.rc -eq 0) -and ($r.out -match 'Exit codes') -and ($r.out -match '/parallel')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/warn', '1', '/crit', '2')
Test-Check 'L11' '/summary with /warn 1 /crit 2 tags the volume [CRIT], prints the footer, exit 4' (($r.rc -eq 4) -and ($r.out -match '\[CRIT\]') -and ($r.out -match '1 volume\(s\): 1 at or above 2%, 0 at or above 1%')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/warn', '95')
Test-Check 'L12' '/warn at or above /crit is rejected as a usage error, exit 1' (($r.rc -eq 1) -and ($r.out -match '/warn must be below /crit')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/warn', '1', '/crit', '2')
Test-Check 'L13' '/summary lists the top folders of a [CRIT] volume' (($r.rc -eq 4) -and ($r.out -match '(?m)^    \S.*  C:\\Windows\\Web\\')) $r

$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/crit', '/warn')
Test-Check 'L14' 'bare /warn and /crit use defaults 85 and 90' (($r.rc -in 0,4) -and ($r.out -match '1 volume\(s\): \d+ at or above 90%, \d+ at or above 85%')) $r

$htmlDir = Join-Path $env:TEMP 'dirused-l15'
Remove-Item -LiteralPath $htmlDir -Recurse -Force -ErrorAction SilentlyContinue
$htmlPath = Join-Path $htmlDir 'report.html'
$r = Invoke-Dirused @('C:\Windows\Web', '/summary', '/html', '/output', $htmlPath)
$htmlOk = (Test-Path -LiteralPath $htmlPath) -and ((Get-Content -LiteralPath $htmlPath -Raw) -match '<table class="volumes">')
Test-Check 'L15' '/summary /html writes a volumes table report' (($r.rc -in 0,4) -and $htmlOk -and ($r.out -match 'HTML report: ')) $r

Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue

if ($Targets) {
    ""
    "fleet: $Targets"
    $r = Invoke-Dirused @('/file', $Targets, '/summary', '/timeout', '60s', '/parallel', '8', '/json')
    $j = ConvertFrom-DirusedJson $r
    $raw = @()
    $labelled = 0
    $complete = 0
    if ($j) {
        foreach ($t in $j.targets) {
            if ($t.complete) {
                $complete++
                "    OK      $($t.target)  total=$($t.volume.totalBytes) free=$($t.volume.freeBytes) used=$($t.volume.percentUsed)%"
            } else {
                foreach ($e in $t.errors) {
                    if ($e.reason -match '^(UNREACHABLE|ACCESS DENIED|TIMED OUT|CANCELLED)') { $labelled++ } else { $raw += $e.reason }
                    "    FAILED  $($t.target)  $($e.reason)"
                }
            }
        }
    }
    Test-Check 'F1' "fleet /summary: $complete complete, $labelled labelled failures, no unlabelled error codes" (($r.rc -eq 0 -or $r.rc -eq 3 -or $r.rc -eq 4) -and $j -and $raw.Count -eq 0) $r
    $r = Invoke-Dirused @('/file', $Targets, '/timeout', '120s', '/parallel', '8', '/top', '3')
    "    full scan of the fleet: rc=$($r.rc) in $($r.seconds)s"
    $r.out -split "`n" | Select-Object -First 60 | ForEach-Object { "    $_" }
    Test-Check 'F2' 'fleet full scan with /parallel 8 and /timeout 120s finishes, exit 0, 3 or 4' (($r.rc -eq 0) -or ($r.rc -eq 3) -or ($r.rc -eq 4)) $r
}
""
"RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
exit 0
