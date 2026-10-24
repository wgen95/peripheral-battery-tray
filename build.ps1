param([string]$Dotnet = 'dotnet', [switch]$Installer, [string]$Iscc = '')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $Dotnet build src/PeripheralBattery.App -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & $Dotnet run --project tests/PeripheralBattery.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $Dotnet run --project tests/PeripheralBattery.IconTests -c Release -- evidence/tray-rings-preview.png
    if ($LASTEXITCODE -ne 0) { throw 'Tray icon tests failed' }
    & $Dotnet publish src/PeripheralBattery.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination dist/win-x64
    Copy-Item docs -Destination dist/win-x64 -Recurse -Force
    $version = ([xml](Get-Content src/PeripheralBattery.App/PeripheralBattery.App.csproj -Raw)).Project.PropertyGroup.Version
    $assets = @()
    if ($Installer) {
        if (-not $Iscc) {
            $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            if ($found) { $Iscc = $found.Source }
            else {
                $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe", "$env:LOCALAPPDATA\PeripheralBatteryTools\InnoSetup\ISCC.exe")
                $Iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            }
        }
        if (-not $Iscc) { throw 'Inno Setup compiler not found. Pass -Iscc with the path to ISCC.exe.' }
        & $Iscc "/DAppVersion=$version" installer/PeripheralBattery.iss
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
        $assets += Get-Item "dist/PeripheralBattery-$version-Setup-x64.exe"
    }
    else {
        Write-Output 'Build output is staging only. Use -Installer for the installable application.'
    }
    if ($assets.Count -eq 0) { return }
    $assets | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content dist/SHA256SUMS.txt -Encoding ascii
    $assets | ForEach-Object { Write-Output "Published: $($_.FullName)" }
} finally { Pop-Location }
