#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$sourceRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $env:TEMP "PCAnalyse-github-release"
$version = (Select-Xml -Path (Join-Path $sourceRoot "Directory.Build.props") -XPath "//Version").Node.InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.1" }
$localReleases = Join-Path $staging "local-releases"
if (Test-Path "Z:\PC_Analyse") {
    $localReleases = "Z:\PC_Analyse\Releases"
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
New-Item -ItemType Directory -Force -Path $localReleases | Out-Null

function New-AppZip {
    param([string]$PublishDir, [string]$ZipName)

    # 1.0.7/1.0.8 starten die EXE aus dem entpackten Update-Ordner.
    # Ein Paket nur mit DLLs ergibt „keine gültige PCAnalyse.Verbindung.exe“.
    $zip = Join-Path $staging $ZipName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $zip -CompressionLevel Optimal -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item $zip (Join-Path $localReleases $ZipName) -Force
    return @{ Path = $zip; Hash = $hash; Name = $ZipName }
}

function Publish-Zip {
    param([string]$Project, [string]$OutName, [string]$ZipName, [string]$AppZipName)

    $out = Join-Path $staging $OutName
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish (Join-Path $sourceRoot $Project) -c Release -r win-x64 --self-contained true -o $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Publish fehlgeschlagen: $Project" }

    $zip = Join-Path $staging $ZipName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item $zip (Join-Path $localReleases $ZipName) -Force
    $app = New-AppZip -PublishDir $out -ZipName $AppZipName
    return @{ Path = $zip; Hash = $hash; Name = $ZipName; App = $app }
}

$mein = Publish-Zip "src\PCAnalyse\PCAnalyse.csproj" "MeinPC" "PCAnalyse-MeinPC.zip" "PCAnalyse-MeinPC-app.zip"
$zweiter = Publish-Zip "src\PCAnalyse.Verbindung\PCAnalyse.Verbindung.csproj" "ZweiterPC" "PCAnalyse-ZweiterPC.zip" "PCAnalyse-ZweiterPC-app.zip"

Set-Content -Path (Join-Path $localReleases "version.txt") -Value $version -Encoding ASCII

$notes = @"
PC Analyse $version für beide Rechner.

Dieses Update enthält die Runtime, damit der zweite PC (ältere Version) das Paket starten kann.

SHA256 $($mein.App.Name): $($mein.App.Hash)
SHA256 $($zweiter.App.Name): $($zweiter.App.Hash)
SHA256 $($mein.Name): $($mein.Hash)
SHA256 $($zweiter.Name): $($zweiter.Hash)
"@

$tag = "v$version"
$ErrorActionPreference = "Continue"
gh release view $tag --json tagName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    gh release delete $tag --yes
}
$ErrorActionPreference = "Stop"
gh release create $tag $mein.App.Path $zweiter.App.Path $mein.Path $zweiter.Path --title "PC Analyse $version" --notes $notes
Write-Host "Release $tag erstellt."
Write-Host "Lokale App-Pakete: $localReleases"
