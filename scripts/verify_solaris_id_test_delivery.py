#!/usr/bin/env python3
"""Verify a GitHub-downloaded test ZIP against the runner's original package."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import zipfile

from package_solaris_id_test import ARCHIVE, BASE_COMMIT, FOLDER, NATIVE_FILES, sha256


def verify(download: Path, original_info: Path, commit: str) -> dict:
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Expected source commit must be a full lowercase SHA')
    download, original_info = download.resolve(), original_info.resolve()
    if download == original_info.parent:
        raise ValueError('Downloaded artifact must be separate from original package')
    expected_files = {ARCHIVE, 'PACKAGE-INFO.json', 'SHA256SUMS.txt'}
    if {p.name for p in download.iterdir()} != expected_files:
        raise ValueError('Downloaded artifact has missing or unexpected files')
    if any(not p.is_file() or p.is_symlink() for p in download.iterdir()):
        raise ValueError('Downloaded artifact contains a directory or symlink')
    if original_info.stat().st_size > 131072:
        raise ValueError('Original package metadata is oversized')
    original_bytes = original_info.read_bytes()
    if (download/'PACKAGE-INFO.json').read_bytes() != original_bytes:
        raise ValueError('Downloaded package metadata differs from original runner metadata')
    info = json.loads(original_bytes)
    if (info.get('file') != ARCHIVE or info.get('source_commit') != commit
            or info.get('control_commit') != BASE_COMMIT
            or info.get('zip_crc_verified') is not True
            or info.get('production_release_created') is not False
            or not re.fullmatch(r'[0-9a-f]{64}', info.get('sha256', ''))
            or type(info.get('bytes')) is not int or info['bytes'] <= 0):
        raise ValueError('Original package identity or verification flags are invalid')
    for archive in [original_info.parent/ARCHIVE, download/ARCHIVE]:
        if archive.stat().st_size != info['bytes'] or sha256(archive) != info['sha256']:
            raise ValueError('ZIP bytes/hash differ from original runner package')
    checksum = info['sha256']+'  '+ARCHIVE+'\n'
    if (download/'SHA256SUMS.txt').read_text(encoding='ascii') != checksum:
        raise ValueError('Downloaded checksum file differs from verified ZIP')
    binaries = info.get('binaries', {})
    if {n.lower() for n in binaries} != NATIVE_FILES | {'solarislauncher.exe'} or len(binaries) != 6:
        raise ValueError('Unexpected binary inventory')
    documents = {'README-TEST.txt', 'BUILD-INFO.txt', 'config/solaris-id.example.json',
                 'TEST-VERIFICATION.json', 'RUNNER-ISOLATION.json'}
    expected_entries = {FOLDER+'/'+name for name in binaries.keys() | documents}
    with zipfile.ZipFile(download/ARCHIVE) as archive:
        entries = archive.infolist()
        if len(entries) != len(expected_entries) or {e.filename for e in entries} != expected_entries:
            raise ValueError('ZIP contains missing, duplicate or unexpected entries')
        if sum(e.file_size for e in entries) > 512*1024*1024:
            raise ValueError('ZIP uncompressed contents exceed package bounds')
        for entry in entries:
            if entry.is_dir() or entry.flag_bits & 1 or stat.S_ISLNK(entry.external_attr >> 16):
                raise ValueError('ZIP contains directory, encrypted entry or symlink')
        if archive.testzip() is not None:
            raise ValueError('Downloaded ZIP CRC validation failed')
        for name, metadata in binaries.items():
            entry = archive.getinfo(FOLDER+'/'+name)
            with archive.open(entry) as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            if entry.file_size != metadata['bytes'] or digest != metadata['sha256']:
                raise ValueError('Downloaded binary differs from original manifest: '+name)
        def document(name: str) -> bytes:
            entry = archive.getinfo(FOLDER+'/'+name)
            if entry.file_size > 131072:
                raise ValueError('Package document is oversized')
            return archive.read(entry)
        if json.loads(document('config/solaris-id.example.json')) != {
                'ApiOrigin': '', 'VanillaServerId': '', 'ModdedServerId': ''}:
            raise ValueError('ZIP contains a configured API/server audience')
        for document_name, key in [('TEST-VERIFICATION.json', 'real_windows_runner_gui_smoke'),
                                   ('RUNNER-ISOLATION.json', 'runner_isolation')]:
            if json.loads(document(document_name)) != info[key]:
                raise ValueError('Embedded verification differs from original package')
        build_info = document('BUILD-INFO.txt').decode('utf-8-sig').splitlines()
        if (build_info.count('Source commit: '+commit) != 1
                or build_info.count('Control commit: '+BASE_COMMIT) != 1):
            raise ValueError('Embedded build source identity differs')
    return {'file': ARCHIVE, 'bytes': info['bytes'], 'sha256': info['sha256'],
            'source_commit': commit, 'github_artifact_download_verified': True,
            'matches_original_runner_package': True, 'zip_crc_verified': True,
            'binary_manifest_verified': True}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--download-dir', required=True, type=Path)
    parser.add_argument('--expected-package-info', required=True, type=Path)
    parser.add_argument('--expected-commit', required=True)
    parser.add_argument('--summary-file', type=Path)
    args = parser.parse_args()
    receipt = verify(args.download_dir, args.expected_package_info, args.expected_commit)
    print('SOLARIS_TEST_DELIVERY_VERIFIED '+json.dumps(receipt, sort_keys=True))
    if args.summary_file:
        with args.summary_file.open('a', encoding='utf-8') as summary:
            summary.write('\n## Downloaded TEST ZIP verified\n\n'
                          'GitHub artifact was downloaded back and matched the original runner package.\n\n'
                          f"- File: `{receipt['file']}`\n"
                          f"- Size: **{receipt['bytes']} bytes**\n"
                          f"- SHA-256: `{receipt['sha256']}`\n"
                          f"- Source: `{receipt['source_commit']}`\n"
                          '- ZIP CRC, exact file inventory and every binary hash: **PASS**\n'
                          '- No production release was created.\n')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
