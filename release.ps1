param(
  [string]$Version = "v1.0.0"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^v\d+\.\d+\.\d+$') {
  throw "Version must look like v1.0.0"
}

git add .
git commit -m "Release Solaris Launcher $Version"
git push origin main
git tag $Version
git push origin $Version

Write-Host "Release $Version pushed. GitHub Actions will build and publish it." -ForegroundColor Green
