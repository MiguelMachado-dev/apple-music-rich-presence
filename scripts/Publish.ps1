param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\out\AppleMusicPresence-win-x64'),
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot '..\src\AppleMusicPresence\AppleMusicPresence.csproj'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
# No compression: a compressed bundle keeps a decompressed private copy of every loaded assembly
# in memory (~90 MB for this app). Uncompressed assemblies are mapped straight from the exe.
dotnet publish $projectPath -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }
$exe = Join-Path $outputPath 'AppleMusicPresence.exe'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$exe.sha256" -Value "$hash  AppleMusicPresence.exe" -Encoding ascii
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination (Join-Path $outputPath 'LEIA-ME.md')
Write-Output "Executável: $exe"
Write-Output "SHA-256: $hash"
