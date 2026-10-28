param([string]$SetupPath = '', [string]$TestDirectory = '')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content (Join-Path $project 'src/PeripheralBattery.App/PeripheralBattery.App.csproj') -Raw)).Project.PropertyGroup.Version
if (-not $SetupPath) { $SetupPath = Join-Path $project "dist/PeripheralBattery-$version-Setup-x64.exe" }
if (-not $TestDirectory) { $TestDirectory = Join-Path $project 'evidence/install-smoke' }
$target = [IO.Path]::GetFullPath($TestDirectory)
$safePrefix = [IO.Path]::GetFullPath((Join-Path $project 'evidence')) + [IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer test directory must be inside project/evidence.' }
New-Item -ItemType Directory -Force -Path (Join-Path $project 'evidence') | Out-Null
if (Test-Path -LiteralPath $target) { throw 'Test target already exists; refusing to overwrite it.' }
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PeripheralBatteryTray_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs/无线外设电量.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '无线外设电量.lnk'
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$dataDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'PeripheralBattery'))
$defaultCache = [IO.Path]::GetFullPath((Join-Path $env:TEMP '.net/PeripheralBattery'))
$backendDefaultCache = [IO.Path]::GetFullPath((Join-Path $env:TEMP '.net/PeripheralBattery.Backend'))
$customCacheRoot = Join-Path $project ('evidence/bundle-smoke-' + [Guid]::NewGuid().ToString('N'))
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $customCacheRoot
$customCache = Join-Path $customCacheRoot 'PeripheralBattery'
$backendCustomCache = Join-Path $customCacheRoot 'PeripheralBattery.Backend'
$siblingSentinel = Join-Path $customCacheRoot 'UnrelatedApp/keep.txt'
New-Item -ItemType Directory -Force -Path (Split-Path $siblingSentinel -Parent) | Out-Null
Set-Content -LiteralPath $siblingSentinel -Value 'Must survive app uninstall.'
# These exact resolved product directories are the only data/cache deletion targets.
if ((Split-Path $dataDirectory -Leaf) -ne 'PeripheralBattery' -or (Split-Path $defaultCache -Leaf) -ne 'PeripheralBattery') { throw 'Unexpected cleanup target.' }
if ((Test-Path $registry) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $desktop)) { throw 'Existing installation or shortcut found; run this test in a clean user profile.' }
if (Get-ItemProperty -LiteralPath $runPath -Name PeripheralBattery -ErrorAction SilentlyContinue) { throw 'An existing startup setting must be preserved; use a clean test profile.' }
function Run-AndWait([string]$Exe, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Timed out: $([IO.Path]::GetFileName($Exe))" }
    if ($process.ExitCode -ne 0) { throw "Exit code $($process.ExitCode): $([IO.Path]::GetFileName($Exe))" }
}
function Assert-NoProductProcesses([string]$Directory) {
    $prefix = $Directory.TrimEnd('\') + '\'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $remaining = @(Get-Process -Name 'PeripheralBattery','PeripheralBattery.Backend' -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Installed application left product processes running: $($remaining.Id -join ', ')"
}
$setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $target + '"'), '/TASKS="desktopicon"', ('/LOG="' + (Join-Path $project 'evidence/installer-smoke.log') + '"'))
Run-AndWait $SetupPath $setupArgs
$installed = Join-Path $target 'PeripheralBattery.exe'
$uninstaller = Join-Path $target 'unins000.exe'
$registration = Get-ItemProperty -LiteralPath $registry
if ($registration.DisplayVersion -ne $version -or $registration.InstallLocation.TrimEnd('\') -ne $target) { throw 'Incorrect application registration.' }
if (-not (Test-Path -LiteralPath $installed) -or -not (Test-Path -LiteralPath $shortcut) -or -not (Test-Path -LiteralPath $desktop)) { throw 'Missing installed app or shortcuts.' }
# Check the actual MS-SHLLINK payload. WScript.Shell returns empty shortcut
# properties on the hosted Server image even though the .lnk file exists.
foreach ($linkPath in @($shortcut, $desktop)) {
    $bytes = [IO.File]::ReadAllBytes($linkPath)
    if ($bytes.Length -lt 76 -or [BitConverter]::ToUInt32($bytes, 0) -ne 76 -or [BitConverter]::ToString($bytes[4..19]) -ne '01-14-02-00-00-00-00-00-C0-00-00-00-00-00-00-46') { throw 'Invalid shell link header.' }
    $flags = [BitConverter]::ToUInt32($bytes, 20)
    if (($flags -band 0x22) -ne 0x22) { throw 'Shell link has no local target or arguments field.' }
    [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
    $ansi = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
    $offset = 76
    if ($flags -band 1) { $offset += 2 + [BitConverter]::ToUInt16($bytes, $offset) }
    $linkInfoSize = [BitConverter]::ToUInt32($bytes, $offset)
    $localOffset = $offset + [BitConverter]::ToUInt32($bytes, $offset + 16)
    $end = $localOffset
    while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
    $linkedTarget = $ansi.GetString($bytes, $localOffset, $end - $localOffset)
    $offset += $linkInfoSize
    $encoding = if ($flags -band 0x80) { [Text.Encoding]::Unicode } else { $ansi }
    $characterBytes = if ($flags -band 0x80) { 2 } else { 1 }
    $arguments = ''
    foreach ($fieldFlag in @(4, 8, 16, 32, 64)) {
        if ($flags -band $fieldFlag) {
            $length = [BitConverter]::ToUInt16($bytes, $offset) * $characterBytes
            $offset += 2
            $value = $encoding.GetString($bytes, $offset, $length)
            if ($fieldFlag -eq 32) { $arguments = $value }
            $offset += $length
        }
    }
    if ($linkedTarget -ne $installed -or $arguments -ne '--show') { throw "Incorrect shell link target or argument: $linkedTarget / $arguments" }
}
$diagnostic = Join-Path $project 'evidence/installed-probe.json'
Run-AndWait $installed @('--diagnose', ('"' + $diagnostic + '"'))
if (-not (Test-Path -LiteralPath $diagnostic)) { throw 'Installed app failed to generate device diagnostics.' }
Assert-NoProductProcesses $target
# Legacy settings deliberately omit new optional fields and contain an unknown
# future field. Installing an update must not rewrite either preference data.
$settingsPath = Join-Path $dataDirectory 'settings.json'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
[IO.File]::WriteAllText($settingsPath, '{"PollSeconds":90,"LowThreshold":15,"HoverPanel":false,"KeyboardOuterRing":false,"ColorTheme":"Purple","FutureSetting":{"Keep":true}}')
$settingsHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
if (-not (Test-Path -LiteralPath $runPath)) { New-Item -Path $runPath -Force | Out-Null }
$startupValue = '"' + $installed + '"'
New-ItemProperty -LiteralPath $runPath -Name PeripheralBattery -Value $startupValue -PropertyType String | Out-Null
$main = Start-Process -FilePath $installed -WindowStyle Hidden -PassThru
if ($main.WaitForExit(4000)) { throw 'Installed app did not stay running.' }
# Cover installing over a running version, using the application's normal quit command.
Run-AndWait $SetupPath $setupArgs
if (-not $main.WaitForExit(10000) -or $main.ExitCode -ne 0) { throw 'Upgrade failed to close the existing app normally.' }
Assert-NoProductProcesses $target
if ((Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash -ne $settingsHash) { throw 'Upgrade rewrote legacy settings or removed an unknown preference.' }
if ((Get-ItemProperty -LiteralPath $runPath -Name PeripheralBattery).PeripheralBattery -ne $startupValue) { throw 'Upgrade changed the startup preference.' }
$main = Start-Process -FilePath $installed -WindowStyle Hidden -PassThru
if ($main.WaitForExit(4000)) { throw 'Upgraded app did not stay running.' }
New-Item -ItemType Directory -Force -Path (Join-Path $dataDirectory 'uninstall-test/nested') | Out-Null
Set-Content -LiteralPath (Join-Path $dataDirectory 'uninstall-test/nested/cache.txt') -Value 'Must be removed.'
# The resolved uninstall target was verified inside project/evidence before installation.
Run-AndWait $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $project 'evidence/uninstaller-smoke.log') + '"'))
if (-not $main.WaitForExit(10000) -or $main.ExitCode -ne 0) { throw 'Uninstall did not close the running app normally.' }
Assert-NoProductProcesses $target
# Inno's final self-delete helper can finish just after its launcher exits.
$cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
while ((Test-Path -LiteralPath $target) -and [DateTimeOffset]::UtcNow -lt $cleanupDeadline) { Start-Sleep -Milliseconds 200 }
if ((Test-Path -LiteralPath $installed) -or (Test-Path $registry) -or (Test-Path -LiteralPath $shortcut) -or (Test-Path -LiteralPath $desktop)) { throw 'App files, registration or shortcuts were left behind.' }
if (Get-ItemProperty -LiteralPath $runPath -Name PeripheralBattery -ErrorAction SilentlyContinue) { throw 'Startup setting was left behind.' }
foreach ($directory in @($target, $dataDirectory, $defaultCache, $customCache, $backendDefaultCache, $backendCustomCache)) {
    if (Test-Path -LiteralPath $directory) { throw "Application directory left behind: $directory" }
}
if (-not (Test-Path -LiteralPath $siblingSentinel)) { throw 'Uninstall touched another application directory.' }
Remove-Item -LiteralPath $siblingSentinel
Remove-Item -LiteralPath (Split-Path $siblingSentinel -Parent)
Remove-Item -LiteralPath $customCacheRoot
Write-Output 'PASS: installation, app registration and shortcuts, real diagnostic, legacy settings/startup preserved across running upgrade; no orphan app/helper processes, app files, startup, configuration, logs or bundle caches remain after uninstall; unrelated sibling directory preserved.'
exit 0
