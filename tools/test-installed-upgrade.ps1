param([string]$SetupPath = '')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content (Join-Path $project 'src/PeripheralBattery.App/PeripheralBattery.App.csproj') -Raw)).Project.PropertyGroup.Version
if (-not $SetupPath) { $SetupPath = Join-Path $project "dist/PeripheralBattery-$version-Setup-x64.exe" }
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PeripheralBatteryTray_is1'
$before = Get-ItemProperty -LiteralPath $registry
$installedDirectory = [IO.Path]::GetFullPath($before.InstallLocation).TrimEnd('\')
$installedExe = Join-Path $installedDirectory 'PeripheralBattery.exe'
if (-not (Test-Path -LiteralPath $installedExe) -or -not (Test-Path -LiteralPath (Join-Path $installedDirectory 'unins000.exe'))) {
    throw 'No registered installed application to upgrade. Do not test a portable copy.'
}
$settingsPath = Join-Path $env:LOCALAPPDATA 'PeripheralBattery/settings.json'
$beforeHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$beforeStartup = (Get-ItemProperty -LiteralPath $runPath -Name PeripheralBattery -ErrorAction SilentlyContinue).PeripheralBattery
$setup = Start-Process -FilePath (Resolve-Path -LiteralPath $SetupPath).Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-') -WindowStyle Hidden -PassThru
if (-not $setup.WaitForExit(120000)) { throw 'Installer timed out.' }
if ($setup.ExitCode -ne 0) { throw "Installer exit code: $($setup.ExitCode)" }
$after = Get-ItemProperty -LiteralPath $registry
if ($after.DisplayVersion -ne $version -or [IO.Path]::GetFullPath($after.InstallLocation).TrimEnd('\') -ne $installedDirectory) {
    throw 'Upgrade version or installation directory was not preserved.'
}
$fileVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
if (-not $fileVersion.StartsWith($version + '+') -and $fileVersion -ne $version) { throw 'Installed binary version is incorrect.' }
$afterHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } else { $null }
$afterStartup = (Get-ItemProperty -LiteralPath $runPath -Name PeripheralBattery -ErrorAction SilentlyContinue).PeripheralBattery
if ($beforeHash -ne $afterHash -or $beforeStartup -ne $afterStartup) { throw 'Upgrade changed existing settings or startup preference.' }
$installedPrefix = $installedDirectory + '\'
$processDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
do {
    $remaining = @(Get-Process -Name 'PeripheralBattery','PeripheralBattery.Backend' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($installedPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($remaining.Count -eq 0) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTimeOffset]::UtcNow -lt $processDeadline)
if ($remaining.Count -ne 0) { throw "Upgrade left an installed app or helper running: $($remaining.Id -join ', ')" }
Write-Output "PASS: installed application upgraded from $($before.DisplayVersion) to $version at the original location. Settings and startup preference preserved."
Write-Output 'No installed application or helper processes remain from the previous version.'
Write-Output 'Application was not launched by this test. Open it manually from the Start menu.'
exit 0
