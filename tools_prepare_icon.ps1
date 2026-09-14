$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path Assets | Out-Null

if (-not (Test-Path Assets/Solaris.ico)) {
    throw 'Assets/Solaris.ico is missing. The committed Solaris icon is the launcher icon source.'
}

Write-Host 'Using committed Assets/Solaris.ico as the launcher icon.'
