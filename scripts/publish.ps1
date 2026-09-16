$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot 'work\.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$publishDirectory = Join-Path $projectRoot 'outputs\QuickDrop-win-x64'
$zipPath = Join-Path $projectRoot 'outputs\QuickDrop-win-x64.zip'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet publish (Join-Path $projectRoot 'src\QuickDrop.App\QuickDrop.App.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\使用说明.txt') -Destination (Join-Path $publishDirectory '使用说明.txt') -Force
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
Write-Host "Published: $publishDirectory"
Write-Host "Archive:   $zipPath"
