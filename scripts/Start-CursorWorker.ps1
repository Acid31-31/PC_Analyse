#Requires -Version 5.1
param(
    [string]$Name = $env:COMPUTERNAME,
    [string]$WorkerDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command agent -ErrorAction SilentlyContinue)) {
    irm "https://cursor.com/install?win32=true" | iex
}
agent worker start --name $Name --worker-dir $WorkerDir --computer-use
