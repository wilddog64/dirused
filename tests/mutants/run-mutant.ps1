param([Parameter(Mandatory)][string]$Id)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
Set-Location $root
$project = 'tests/Dirused.Tests'
$filter = 'FullyQualifiedName~Dirused.Tests.ScheduleWindowsTests|FullyQualifiedName~Dirused.Tests.ProgramScheduleTests'
if ($Id -ne 'control') {
  $all = Get-Content -Raw -Encoding utf8 'tests/mutants/mutants.json' | ConvertFrom-Json
  $m = @($all | Where-Object { $_.id -eq $Id })
  if ($m.Count -ne 1) { Write-Output "ERROR: no single mutant with id '$Id'"; exit 2 }
  $m = $m[0]
  $filter = if ($m.PSObject.Properties['filter']) { $m.filter } else { 'FullyQualifiedName~Dirused.Tests.ScheduleWindowsTests' }
  $text = [IO.File]::ReadAllText((Join-Path $root $m.file))
  $count = ([regex]::Matches($text, [regex]::Escape($m.find))).Count
  if ($count -ne 1) { Write-Output "ERROR: mutant '$Id' find text occurs $count times in $($m.file) (expected 1); update mutants.json"; exit 2 }
  [IO.File]::WriteAllText((Join-Path $root $m.file), $text.Replace($m.find, $m.replace), (New-Object Text.UTF8Encoding($false)))
  Write-Output "applied mutant '$Id' ($($m.proves)) to $($m.file)"
}
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR: build failed with mutant '$Id'; a mutant that does not compile proves nothing"; exit 2 }
dotnet test $project -c Release --no-build --blame-hang-timeout 120s --filter $filter
$rc = $LASTEXITCODE
if ($Id -eq 'control') {
  if ($rc -eq 0) { Write-Output 'CONTROL PASS: unmutated suite is green'; exit 0 }
  Write-Output "CONTROL FAIL: unmutated suite exit $rc"; exit 1
}
if ($rc -ne 0) { Write-Output "KILLED: mutant '$Id' turns the suite red (exit $rc)"; exit 0 }
Write-Output "SURVIVED: mutant '$Id' leaves the suite green; a test is missing for $($m.proves)"; exit 1
