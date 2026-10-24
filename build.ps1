param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $Dotnet build src/PeripheralBattery.App -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & $Dotnet run --project tests/PeripheralBattery.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    & $Dotnet publish src/PeripheralBattery.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination dist/win-x64
    Copy-Item docs -Destination dist/win-x64 -Recurse -Force
    Compress-Archive -Path dist/win-x64/* -DestinationPath dist/PeripheralBattery-1.0.0-win-x64.zip -Force
    $asset = Get-Item dist/PeripheralBattery-1.0.0-win-x64.zip
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)" | Set-Content dist/SHA256SUMS.txt -Encoding ascii
    Write-Output "Published: $($asset.FullName)"
} finally { Pop-Location }
