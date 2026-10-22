#!/usr/bin/env python3
"""Prove working updater/install/modpack code is unchanged from audited DEV base."""
from pathlib import Path
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
BASE='efb37d9596b4c73b9d24a38372035225658e6f5b'
files=subprocess.check_output(['git','ls-tree','-r','--name-only',BASE],cwd=ROOT,text=True).splitlines()
protected=[p for p in files if ('Update' in p and p.endswith('.cs')) or p in ['App.xaml.cs','LauncherInstall.cs','SolarisBootstrapper.cs','SolarisJavaRuntime.cs','SolarisPackIntegrity.cs','SolarisFileVerification.cs','SolarisGameInstallation.cs','update.json','release.yml','tools_prepare_release.ps1','.github/workflows/release.yml','.github/workflows/solaris-id-schema.yml'] or p.startswith('vendor/')]
failed=[]
for name in protected:
    original=subprocess.check_output(['git','show',BASE+':'+name],cwd=ROOT)
    if hashlib.sha256(original).digest()!=hashlib.sha256((ROOT/name).read_bytes()).digest():failed.append(name)
# Adding a test-source exclusion must not alter version, channel or packages.
old=ET.fromstring(subprocess.check_output(['git','show',BASE+':SolarisLauncher.csproj'],cwd=ROOT))
new=ET.parse(ROOT/'SolarisLauncher.csproj').getroot()
for section in ['PropertyGroup','ItemGroup/PackageReference','ItemGroup/ProjectReference']:
    before=[ET.tostring(x) for x in old.findall(section)]
    after=[ET.tostring(x) for x in new.findall(section)]
    if before!=after:failed.append('SolarisLauncher.csproj:'+section)
print(json.dumps({'base':BASE,'protected_files':len(protected),'unchanged':len(protected)-sum(p in protected for p in failed),'failures':failed,'launcher_version_channels_packages_unchanged':not any(x.startswith('SolarisLauncher.csproj:') for x in failed)}))
raise SystemExit(1 if failed else 0)
