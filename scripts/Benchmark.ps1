param(
    # Each entry is "label=path\to\AppleMusicPresence.exe".
    [Parameter(Mandatory)][string[]]$Variant,
    [int]$Rounds = 5,
    [string]$Out = (Join-Path $PSScriptRoot '..\out\perf\runs'),
    [string]$Counters = (Join-Path $PSScriptRoot '..\out\tools\dotnet-counters.exe'),
    [int]$Warmup = 20,
    [int]$Steady = 60,
    [int]$Hidden = 20
)
# Runs the published app under the real saved settings, one instance at a time, in alternating
# order per round. Close the tray app first. Install the counters tool with:
#   dotnet tool install dotnet-counters --tool-path out/tools
$ErrorActionPreference = 'Stop'
if (Get-Process AppleMusicPresence -ErrorAction SilentlyContinue) { throw 'Feche o Apple Music Presence antes do benchmark.' }
$benchProject = Join-Path $PSScriptRoot '..\tests\AppleMusicPresence.Bench'
dotnet build $benchProject -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o benchmark.' }
$bench = Join-Path $benchProject 'bin\Release\net8.0-windows10.0.19041.0\AppleMusicPresence.Bench.exe'
$variants = @($Variant | ForEach-Object {
    $label, $path = $_ -split '=', 2
    [pscustomobject]@{ Label = $label; Path = (Resolve-Path $path).Path }
})
$countersArgs = if (Test-Path $Counters) { @('--counters', (Resolve-Path $Counters).Path) } else { @() }
New-Item -ItemType Directory -Force $Out | Out-Null

# Prime each binary once (file cache and native library extraction) without touching Discord.
foreach ($v in $variants) {
    $diagnostics = Join-Path ([IO.Path]::GetTempPath()) "amp-prime-$([guid]::NewGuid().ToString('N')).json"
    Start-Process -FilePath $v.Path -ArgumentList '--diagnose', $diagnostics -Wait
    Remove-Item $diagnostics -ErrorAction SilentlyContinue
}

for ($round = 1; $round -le $Rounds; $round++) {
    $order = if ($round % 2 -eq 1) { $variants } else { $variants[($variants.Count - 1)..0] }
    foreach ($v in $order) {
        Write-Output "Rodada ${round}/${Rounds}: $($v.Label)"
        & $bench scenario --exe $v.Path --label $v.Label --out $Out --warmup $Warmup --steady $Steady --hidden $Hidden @countersArgs
        if ($LASTEXITCODE -ne 0) { throw "Falha no cenário $($v.Label)." }
    }
}
& $bench summarize $Out
