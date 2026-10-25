param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $project 'src/PeripheralBattery.Native'
$version = ([xml](Get-Content (Join-Path $project 'src/PeripheralBattery.App/PeripheralBattery.App.csproj') -Raw)).Project.PropertyGroup.Version
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'dist/win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$nativeBuild = Join-Path $project 'src/PeripheralBattery.Native/obj'
New-Item -ItemType Directory -Force -Path $nativeBuild | Out-Null
Set-Content -LiteralPath (Join-Path $nativeBuild 'version.h') -Value ('#define APP_VERSION "' + $version + '"') -Encoding ascii
$parts = ($version.Split('.') + @('0'))[0..3] -join ','
Copy-Item -LiteralPath (Join-Path $project 'src/PeripheralBattery.App/Assets/App.ico') -Destination (Join-Path $nativeBuild 'app.ico')
Copy-Item -LiteralPath (Join-Path $project 'src/PeripheralBattery.App/app.manifest') -Destination (Join-Path $nativeBuild 'app.manifest')
$ico = 'app.ico'
$manifest = 'app.manifest'
$resource = @"
#include <windows.h>
1 ICON "$ico"
1 RT_MANIFEST "$manifest"
1 VERSIONINFO
FILEVERSION $parts
PRODUCTVERSION $parts
FILEOS VOS_NT_WINDOWS32
FILETYPE VFT_APP
BEGIN
 BLOCK "StringFileInfo" BEGIN
  BLOCK "040904b0" BEGIN
   VALUE "CompanyName", "wgen95\0"
   VALUE "FileDescription", "Peripheral Battery\0"
   VALUE "ProductName", "PeripheralBattery\0"
   VALUE "FileVersion", "$version\0"
   VALUE "ProductVersion", "$version\0"
  END
 END
 BLOCK "VarFileInfo" BEGIN VALUE "Translation", 0x409, 1200 END
END
"@
$rc = Join-Path $nativeBuild 'app.rc'; Set-Content -LiteralPath $rc -Value $resource -Encoding utf8
$gpp = Get-Command g++.exe -ErrorAction SilentlyContinue
if ($gpp) {
    $windres = Join-Path (Split-Path $gpp.Source -Parent) 'windres.exe'
    $res = Join-Path $nativeBuild 'app.res'
    Push-Location $nativeBuild
    try {
        & $windres -i app.rc -O coff -o app.res
        if ($LASTEXITCODE -ne 0) { throw 'Native resource compilation failed' }
        $relativeExe = [IO.Path]::GetRelativePath($nativeBuild, (Join-Path $OutputDirectory 'PeripheralBattery.exe'))
        & $gpp.Source -std=c++17 -Os -s -static -municode -mwindows '-D_WIN32_WINNT=0x0A00' -I. ../main.cpp app.res -o $relativeExe -lgdiplus -ldwmapi -lshell32 -lole32 -luuid -lgdi32 -luser32
    } finally { Pop-Location }
}
else {
    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'C++ compiler missing. Use MSVC developer shell or MinGW-w64.' }
    Push-Location $nativeBuild
    try {
        & rc.exe /nologo /foapp.res app.rc
        if ($LASTEXITCODE -ne 0) { throw 'Native resource compilation failed' }
        $relativeExe = [IO.Path]::GetRelativePath($nativeBuild, (Join-Path $OutputDirectory 'PeripheralBattery.exe'))
        & cl.exe /nologo /std:c++17 /utf-8 /O1 /MT /EHsc /D_WIN32_WINNT=0x0A00 /I. ../main.cpp /Fomain.obj ("/Fe$relativeExe") app.res /link /SUBSYSTEM:WINDOWS gdiplus.lib dwmapi.lib shell32.lib ole32.lib uuid.lib gdi32.lib user32.lib
    } finally { Pop-Location }
}
if ($LASTEXITCODE -ne 0) { throw 'Native frontend compilation failed' }
Write-Output "Built native frontend: $OutputDirectory/PeripheralBattery.exe"
