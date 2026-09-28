param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root/windows/Seamlet.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -o "$root/build/windows"
if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
Write-Host "Built $root/build/windows/Seamlet.exe"
