#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$sourceRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$copyRoot = "Z:\PC_Analyse"
$meinPc = Join-Path $copyRoot "Mein PC"
$zweiterPc = Join-Path $copyRoot "Zweiter PC"
$staging = Join-Path $env:TEMP "PCAnalyse-publish"

function Publish-Package {
    param(
        [string]$Project,
        [string]$Destination,
        [string]$ExeName
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $out = Join-Path $staging ([IO.Path]::GetFileName($Destination))
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    dotnet publish $Project -c Release -r win-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish fehlgeschlagen: $Project" }

    & robocopy $out $Destination /E /IS /IT /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Kopieren fehlgeschlagen: $Destination" }
    Copy-Item (Join-Path $out $ExeName) (Join-Path $Destination "Programm installieren.exe") -Force
    Copy-Item (Join-Path $out $ExeName) (Join-Path $Destination "Programm deinstallieren.exe") -Force

    Set-Content -Path (Join-Path $Destination "STARTEN.bat") -Encoding ASCII -Value @"
@echo off
cd /d "%~dp0"
start "" "Programm installieren.exe"
"@
    Set-Content -Path (Join-Path $Destination "DEINSTALLIEREN.bat") -Encoding ASCII -Value @"
@echo off
cd /d "%~dp0"
start "" "Programm deinstallieren.exe"
"@
}

New-Item -ItemType Directory -Force -Path $staging | Out-Null
Publish-Package -Project (Join-Path $sourceRoot "src\PCAnalyse\PCAnalyse.csproj") -Destination $meinPc -ExeName "PCAnalyse.exe"
Publish-Package -Project (Join-Path $sourceRoot "src\PCAnalyse.Verbindung\PCAnalyse.Verbindung.csproj") -Destination $zweiterPc -ExeName "PCAnalyse.Verbindung.exe"

function Write-NetworkZip {
    param([string]$Folder, [string]$ZipName)
    $zipTmp = Join-Path $staging $ZipName
    $zipDest = Join-Path $copyRoot $ZipName
    if (Test-Path $zipTmp) { Remove-Item $zipTmp -Force }
    Compress-Archive -Path (Join-Path $Folder "*") -DestinationPath $zipTmp -CompressionLevel Fastest -Force
    Copy-Item $zipTmp $zipDest -Force
}

Write-NetworkZip -Folder $zweiterPc -ZipName "Zweiter PC.zip"
Write-NetworkZip -Folder $meinPc -ZipName "Mein PC.zip"

$releases = Join-Path $copyRoot "Releases"
New-Item -ItemType Directory -Force -Path $releases | Out-Null
$version = (Select-Xml -Path (Join-Path $sourceRoot "Directory.Build.props") -XPath "//Version").Node.InnerText.Trim()
Set-Content -Path (Join-Path $releases "version.txt") -Value $version -Encoding ASCII

function Write-AppZip {
    param([string]$PublishFolder, [string]$ZipName)
    $zip = Join-Path $releases $ZipName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $PublishFolder "*") -DestinationPath $zip -CompressionLevel Optimal -Force
}

Write-AppZip -PublishFolder (Join-Path $staging "Mein PC") -ZipName "PCAnalyse-MeinPC-app.zip"
Write-AppZip -PublishFolder (Join-Path $staging "Zweiter PC") -ZipName "PCAnalyse-ZweiterPC-app.zip"

Write-Host "Kopierordner:"
Write-Host "  $meinPc"
Write-Host "  $zweiterPc"
Write-Host "Netzwerk (eine Datei):"
Write-Host "  $(Join-Path $copyRoot 'Zweiter PC.zip')"
Write-Host "  $(Join-Path $copyRoot 'Mein PC.zip')"
Write-Host "Kleine Updates:"
Write-Host "  $releases"
