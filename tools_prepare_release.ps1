param(
    [Parameter(Mandatory=$true)]
    [string]$MinecraftDir,
    [string]$Output = "SolarisClient.zip"
)

$ErrorActionPreference = "Stop"

$includeDirs = @(
    "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks",
    "kubejs", "journeymap", "tacz", "xaero", "patchouli_books", "scripts"
)
$includeFiles = @("options.txt", "optionsof.txt", "servers.dat", "servers.dat_old")

if (Test-Path $Output) { Remove-Item $Output -Force }
$temp = Join-Path $env:TEMP ("SolarisPack-" + [guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    foreach ($dir in $includeDirs) {
        $src = Join-Path $MinecraftDir $dir
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $temp $dir) -Recurse -Force
        }
    }

    foreach ($file in $includeFiles) {
        $src = Join-Path $MinecraftDir $file
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $temp $file) -Force
        }
    }

    Compress-Archive -Path (Join-Path $temp "*") -DestinationPath $Output -CompressionLevel Optimal
    Write-Host "Created $Output"
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
