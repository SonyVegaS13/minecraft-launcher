#!/usr/bin/env python3
"""Package only a rendered, compile-time-isolated Solaris ID Windows test build.

No installed launcher/profile, active operator configuration, test-data, source
checkout, API/server secret or game client is copied into the archive.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASE_COMMIT = '4b38eb717baa4da95c583d934f0f7e0b699560a0'
FOLDER = 'Solaris-Launcher-ID-TEST'
ARCHIVE = FOLDER + '-win-x64.zip'
TEMPLATES = ROOT / 'docs/solaris-id/test-build'

# WindowsDesktop win-x64 native assets. The .NET 8 Windows single-file superhost
# statically includes CoreCLR; managed dependencies/artwork are also in the EXE.
# WPF's five native files stay beside it, avoiding shared native extraction.
NATIVE_FILES = {
    'penimc_cor3.dll',
    'presentationnative_cor3.dll', 'wpfgfx_cor3.dll',
    'd3dcompiler_47_cor3.dll', 'vcruntime140_cor3.dll',
}
REQUIRED_NATIVE = NATIVE_FILES


def sha256(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def pe_machine(path: Path) -> int:
    with path.open('rb') as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:2] != b'MZ':
            raise ValueError(f'{path.name}: not a PE file')
        stream.seek(struct.unpack_from('<I', header, 0x3c)[0])
        pe = stream.read(6)
        if len(pe) != 6 or pe[:4] != b'PE\0\0':
            raise ValueError(f'{path.name}: invalid PE header')
        return struct.unpack_from('<H', pe, 4)[0]


def git(*args: str) -> str:
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


def verify_smoke(path: Path, publish: Path, executable_hash: str) -> dict:
    if not path.is_file() or path.stat().st_size > 65536:
        raise ValueError('Missing or oversized runtime smoke report')
    report = json.loads(path.read_text(encoding='utf-8-sig'))
    for name in ['testBuild', 'windowLoaded', 'contentRendered', 'smokePassed',
                 'dpapiRoundTrip', 'previousDpapiProbeRestored', 'nativeModulesInTestDirectory']:
        if report.get(name) is not True:
            raise ValueError(f'Runtime smoke did not confirm {name}')
    for name in ['productionUpdaterEnabled', 'productionServerConnectionsEnabled', 'nativeExtractionEnabled']:
        if report.get(name) is not False:
            raise ValueError(f'Runtime smoke did not disable {name}')
    if report.get('executableSha256', '').lower() != executable_hash:
        raise ValueError('Smoke report belongs to a different executable')
    expected_root = (publish/'test-data').resolve()
    test_root = Path(report.get('testRoot', '')).resolve()
    state = Path(report.get('stateDirectory', '')).resolve()
    config = Path(report.get('configFile', '')).resolve()
    if test_root != expected_root or not state.is_relative_to(expected_root):
        raise ValueError('Runtime smoke reported a shared/outside test profile')
    if config != (publish/'config/solaris-id.test.json').resolve():
        raise ValueError('Runtime smoke reported the production configuration path')
    # No absolute runner paths or local profile contents are shipped.
    return {name: report[name] for name in [
        'testBuild', 'windowLoaded', 'contentRendered', 'smokePassed',
        'dpapiRoundTrip', 'previousDpapiProbeRestored', 'nativeModulesInTestDirectory',
        'productionUpdaterEnabled', 'productionServerConnectionsEnabled',
        'nativeExtractionEnabled',
        'executableSha256',
    ]} | {'testRootRelative': 'test-data',
         'stateDirectoryRelative': state.relative_to(publish).as_posix(),
         'configFileRelative': 'config/solaris-id.test.json',
         'human_visual_review_performed': False,
         'real_solaris_id_api_tested_by_this_smoke': False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish-dir', required=True, type=Path)
    parser.add_argument('--smoke-report', required=True, type=Path)
    parser.add_argument('--runner-isolation-report', required=True, type=Path)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--commit', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.commit) or git('rev-parse', 'HEAD') != args.commit:
        parser.error('--commit must be the exact checked-out source commit')
    subprocess.run(['git', 'merge-base', '--is-ancestor', BASE_COMMIT, args.commit], cwd=ROOT, check=True)
    if git('status', '--porcelain', '--untracked-files=no'):
        parser.error('Tracked source files changed since the supplied commit')
    publish = args.publish_dir.resolve()
    output = args.output_dir.resolve()
    if output == publish or output.is_relative_to(publish):
        parser.error('Delivery directory must be outside the publish directory')
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        parser.error('Delivery directory must be empty; existing artifacts are not overwritten')
    executable = publish/'SolarisLauncher.exe'
    if not executable.is_file() or executable.is_symlink() or pe_machine(executable) != 0x8664:
        parser.error('SolarisLauncher.exe must be a real AMD64 Windows PE executable')
    binary_hash = sha256(executable)
    verification = verify_smoke(args.smoke_report, publish, binary_hash)
    runner = json.loads(args.runner_isolation_report.read_text(encoding='utf-8-sig'))
    for field in ['protectedProfileSentinelsUnchanged', 'unsafeUpdateArgumentsRejected',
                  'normalGuiOpened', 'normalGuiClosed']:
        if runner.get(field) is not True:
            parser.error('Windows isolation check did not confirm '+field)
    if runner.get('executableSha256', '').lower() != binary_hash:
        parser.error('Windows isolation report belongs to a different executable')
    runner = {k: runner[k] for k in ['protectedProfileSentinelsUnchanged',
        'unsafeUpdateArgumentsRejected', 'normalGuiOpened', 'normalGuiClosed', 'executableSha256']}
    binaries = []
    for path in sorted(publish.iterdir(), key=lambda p: p.name.lower()):
        if path.is_symlink():
            parser.error('Publish directory must not contain symlinks')
        if path.is_dir():
            if path.name not in {'test-data', 'config'}:
                parser.error('Unexpected directory in publish output: '+path.name)
            continue
        name = path.name.lower()
        native = name in NATIVE_FILES
        if path.name != 'SolarisLauncher.exe' and not native:
            parser.error('Unexpected file in publish output: '+path.name)
        if name.endswith(('.dll', '.exe')):
            machine = pe_machine(path)
            if machine != 0x8664:
                parser.error('Unsupported runtime PE machine: '+path.name)
        binaries.append(path)
    names = {p.name.lower() for p in binaries}
    if not REQUIRED_NATIVE.issubset(names):
        parser.error('Required self-contained native runtime files missing: '+', '.join(sorted(REQUIRED_NATIVE-names)))
    example = json.loads((TEMPLATES/'config/solaris-id.example.json').read_text(encoding='utf-8'))
    if example != {'ApiOrigin': '', 'VanillaServerId': '', 'ModdedServerId': ''}:
        parser.error('Test configuration example must have no configured API or server audience')
    files = {p.name: {'bytes': p.stat().st_size, 'sha256': sha256(p)} for p in binaries}
    commit_time = int(git('show', '-s', '--format=%ct', args.commit))
    created = datetime.fromtimestamp(commit_time, timezone.utc)
    build_info = '\n'.join([
        'SOLARIS LAUNCHER — SOLARIS ID — TEST BUILD',
        'Source commit: '+args.commit,
        'Control commit: '+BASE_COMMIT,
        'Source repository: https://github.com/SonyVegaS13/minecraft-launcher',
        'Target: Windows x64, self-contained .NET 8 / WPF',
        'Edition: SolarisTestBuild=true; production updater/self-install disabled',
        'Profile/game clients: test-data beside the extracted executable',
        'Active config: config/solaris-id.test.json (not included; API disabled by default)',
        'Core runtime: statically linked .NET 8 Windows single-file host',
        'Native WPF: five DLLs supplied beside EXE; no shared native extraction cache',
        'Windows runner: WPF Loaded + ContentRendered + bounded clean shutdown passed',
        'Windows runner: no-argument GUI start/title/close and unsafe updater arguments checked',
        'Windows runner: synthetic stable-profile/install sentinel trees remained unchanged',
        'Windows DPAPI: synthetic CurrentUser probe round-trip and process restart passed',
        'DPAPI probe does not prove a real online-account session without an API',
        'Human visual review: NOT PERFORMED',
        'Live Solaris ID API / Minecraft client testing in this GUI smoke: NOT PERFORMED',
        'No production release, updater manifest or server deployment is created.',
        '', 'PACKAGED BINARIES (SHA-256 / bytes):',
        *[f"{v['sha256']}  {v['bytes']}  {k}" for k, v in files.items()], '',
    ])
    with tempfile.TemporaryDirectory(prefix='solaris-id-test-package-') as temp:
        stage = Path(temp)/FOLDER
        (stage/'config').mkdir(parents=True)
        for path in binaries:
            shutil.copyfile(path, stage/path.name)
        shutil.copyfile(TEMPLATES/'README-TEST.txt', stage/'README-TEST.txt')
        shutil.copyfile(TEMPLATES/'config/solaris-id.example.json', stage/'config/solaris-id.example.json')
        (stage/'BUILD-INFO.txt').write_text(build_info, encoding='utf-8-sig')
        (stage/'TEST-VERIFICATION.json').write_text(json.dumps(verification, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
        (stage/'RUNNER-ISOLATION.json').write_text(json.dumps(runner, indent=2)+'\n', encoding='utf-8')
        archive = output/ARCHIVE
        with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zip_file:
            for path in sorted(stage.rglob('*')):
                if path.is_file():
                    info = zipfile.ZipInfo(path.relative_to(stage.parent).as_posix(), created.timetuple()[:6])
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.external_attr = 0o100644 << 16
                    zip_file.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
        with zipfile.ZipFile(archive) as zip_file:
            if zip_file.testzip() is not None:
                raise ValueError('Archive CRC verification failed')
            if any('/test-data/' in n or n.endswith('/solaris-id.test.json') for n in zip_file.namelist()):
                raise ValueError('Active profile/configuration leaked into the ZIP')
    archive_hash = sha256(archive)
    (output/'SHA256SUMS.txt').write_text(archive_hash+'  '+ARCHIVE+'\n', encoding='ascii')
    receipt = {'file': ARCHIVE, 'bytes': archive.stat().st_size, 'sha256': archive_hash,
        'source_commit': args.commit, 'control_commit': BASE_COMMIT,
        'zip_crc_verified': True, 'production_release_created': False,
        'real_windows_runner_gui_smoke': verification, 'runner_isolation': runner, 'binaries': files}
    (output/'PACKAGE-INFO.json').write_text(json.dumps(receipt, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k:receipt[k] for k in ['file', 'bytes', 'sha256', 'source_commit', 'zip_crc_verified']}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
