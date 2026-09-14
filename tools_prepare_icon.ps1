$ErrorActionPreference = 'Stop'

if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    choco install imagemagick.app -y --no-progress
}

New-Item -ItemType Directory -Force -Path Assets | Out-Null
magick Assets/Solaris.svg -background none -define icon:auto-resize=256,128,64,48,32,16 Assets/Solaris.ico

if (-not (Test-Path Assets/Solaris.ico)) {
    throw 'Solaris.ico was not generated.'
}

Write-Host 'Generated Assets/Solaris.ico'
