param([int]$Seconds = 75)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$registration = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PeripheralBatteryTray_is1'
$installed = Join-Path $registration.InstallLocation 'PeripheralBattery.exe'
$backend = Join-Path $registration.InstallLocation 'PeripheralBattery.Backend.exe'
$resident = @(Get-Process -Name PeripheralBattery -ErrorAction SilentlyContinue | Where-Object Path -eq $installed)
if ($resident.Count -eq 0) { $resident = @(Start-Process -FilePath $installed -WindowStyle Hidden -PassThru) }
if ($resident.Count -ne 1) { throw 'Cannot uniquely identify installed resident process.' }
$resident = $resident[0]
$samples = [Collections.Generic.List[object]]::new()
$childPids = [Collections.Generic.HashSet[int]]::new()
$deadline = [DateTimeOffset]::Now.AddSeconds($Seconds)
while ([DateTimeOffset]::Now -lt $deadline) {
    $resident.Refresh()
    if ($resident.HasExited) { throw 'Resident process exited during memory test.' }
    $children = @(Get-Process -Name PeripheralBattery.Backend -ErrorAction SilentlyContinue | Where-Object Path -eq $backend)
    $childWorking = 0L
    foreach ($child in $children) { try { $childWorking += $child.WorkingSet64; $null = $childPids.Add($child.Id) } catch [InvalidOperationException] { } }
    $samples.Add([pscustomobject]@{ Time=[DateTimeOffset]::Now.ToString('O'); Working=$resident.WorkingSet64; Private=$resident.PrivateMemorySize64; ChildWorking=$childWorking; ChildCount=$children.Count; CombinedWorking=$resident.WorkingSet64+$childWorking })
    Start-Sleep -Milliseconds 100
}
$idle = @($samples | Where-Object ChildCount -eq 0)
if ($idle.Count -eq 0) { throw 'No resident-only samples; a helper may be stuck.' }
$sorted = @($idle.Working | Sort-Object)
$privateSorted = @($idle.Private | Sort-Object)
$report = [pscustomobject]@{
    Version=$registration.DisplayVersion; DurationSeconds=$Seconds; Samples=$samples.Count; WorkerProcessesObserved=$childPids.Count
    IdleWorkingMedianMiB=[math]::Round($sorted[[int]($sorted.Count/2)]/1MB,2)
    IdleWorkingMinMiB=[math]::Round(($idle.Working | Measure-Object -Minimum).Minimum/1MB,2)
    IdleWorkingMaxMiB=[math]::Round(($idle.Working | Measure-Object -Maximum).Maximum/1MB,2)
    IdlePrivateMedianMiB=[math]::Round($privateSorted[[int]($privateSorted.Count/2)]/1MB,2)
    ObservedCombinedWorkingPeakMiB=[math]::Round(($samples.CombinedWorking | Measure-Object -Maximum).Maximum/1MB,2)
    ClrLoadedInResident=@($resident.Modules | Where-Object ModuleName -eq 'coreclr.dll').Count -gt 0
    RemainingBackendProcesses=@(Get-Process -Name PeripheralBattery.Backend -ErrorAction SilentlyContinue | Where-Object Path -eq $backend).Count
    Note='100 ms sampling of installed application. Transient peaks shorter than sampling interval may be missed. No working-set trimming.'
}
New-Item -ItemType Directory -Force -Path (Join-Path $project 'evidence') | Out-Null
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $project 'evidence/memory-2.0.0.json') -Encoding utf8
if ($report.ClrLoadedInResident) { throw 'Resident unexpectedly loaded CLR.' }
$report | ConvertTo-Json -Compress
