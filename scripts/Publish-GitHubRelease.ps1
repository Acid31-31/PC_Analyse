#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$sourceRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $env:TEMP "PCAnalyse-github-release"
$version = (Select-Xml -Path (Join-Path $sourceRoot "Directory.Build.props") -XPath "//Version").Node.InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.1" }

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Publish-Zip {
    param([string]$Project, [string]$OutName, [string]$ZipName)

    $out = Join-Path $staging $OutName
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & dotnet publish (Join-Path $sourceRoot $Project) -c Release -r win-x64 --self-contained true -o $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Publish fehlgeschlagen: $Project" }

    $zip = Join-Path $staging $ZipName
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{ Path = $zip; Hash = $hash; Name = $ZipName }
}

$mein = Publish-Zip "src\PCAnalyse\PCAnalyse.csproj" "MeinPC" "PCAnalyse-MeinPC.zip"
$zweiter = Publish-Zip "src\PCAnalyse.Verbindung\PCAnalyse.Verbindung.csproj" "ZweiterPC" "PCAnalyse-ZweiterPC.zip"

$notes = @"
PC Analyse $version für beide Rechner.

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
gh release create $tag $mein.Path $zweiter.Path --title "PC Analyse $version" --notes $notes
Write-Host "Release $tag erstellt."
