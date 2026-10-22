#!/usr/bin/env python3
"""Reject changes to production updater/install logic in the isolated test edition.

This is a source regression guard, not proof of native Windows execution. The
linked C# tests and bounded native WPF smoke check cover the actual test helper.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BASE = '4b38eb717baa4da95c583d934f0f7e0b699560a0'
PROTECTED = [
    'SolarisSafeUpdate.cs', 'LauncherUpdates.cs', 'LauncherInstall.cs',
    'SolarisJavaRuntime.cs', 'SolarisGameInstallation.cs',
    'SolarisPackIntegrity.cs', 'SolarisFileVerification.cs',
    '.github/workflows/release.yml', '.github/workflows/solaris-id-schema.yml',
    'update.json', 'tools_prepare_release.ps1',
]
PRODUCTION_PROJECTIONS = [
    'App.xaml.cs', 'MainWindow.xaml.cs', 'SolarisDirectories.cs',
    'NeonArtwork.cs', 'SolarisCloudClient.cs',
]


def original(name: str) -> bytes:
    return subprocess.check_output(['git', 'show', BASE + ':' + name], cwd=ROOT)


def condition(source: str, defines: set[str]) -> bool:
    tokens = re.findall(r'[A-Za-z_][A-Za-z0-9_]*|&&|\|\||!|\(|\)', source)
    if ''.join(tokens) != re.sub(r'\s+', '', source):
        raise ValueError('Unsupported preprocessor expression: ' + source)
    operators = {'&&': 'and', '||': 'or', '!': 'not', '(': '(', ')': ')'}
    expression = ' '.join(operators.get(t, str(t in defines)) for t in tokens)
    # Every identifier has been converted to a bool; no source expression or
    # Python name survives this restricted transformation.
    return bool(eval(expression, {'__builtins__': {}}, {}))


def preprocess(source: str, defines: set[str]) -> str:
    output: list[str] = []
    stack: list[list[bool]] = []  # parent active, any branch taken, branch active
    active = True
    for line in source.splitlines(keepends=True):
        match = re.match(r'\s*#(if|elif|else|endif)\b(.*)', line)
        if not match:
            if active:
                output.append(line)
            continue
        directive, expression = match.groups()
        if directive == 'if':
            enabled = condition(expression.strip(), defines)
            stack.append([active, enabled, enabled])
        elif directive == 'elif':
            if not stack: raise ValueError('Unmatched #elif')
            parent, taken, _ = stack[-1]
            enabled = not taken and condition(expression.strip(), defines)
            stack[-1] = [parent, taken or enabled, enabled]
        elif directive == 'else':
            if not stack: raise ValueError('Unmatched #else')
            parent, taken, _ = stack[-1]
            stack[-1] = [parent, True, not taken]
        else:
            if not stack: raise ValueError('Unmatched #endif')
            stack.pop()
        active = all(parent and branch for parent, _, branch in stack)
    if stack: raise ValueError('Unclosed #if')
    return ''.join(output)


TOKEN = re.compile(
    r'(?P<string>(?:\$@|@\$|@|\$)?"(?:\\.|""|[^"\\])*"|\'(?:\\.|[^\'\\])*\')'
    r'|(?P<comment>//[^\n]*|/\*.*?\*/)'
    r'|(?P<token>[A-Za-z_][A-Za-z0-9_]*|[0-9]+|[^\s])', re.DOTALL)


def tokens(source: str) -> list[str]:
    # Extra imports do not change production behavior. Executable tokens and
    # literal contents remain exact, including whitespace inside string literals.
    source = re.sub(r'^using\s+[A-Za-z0-9_.]+\s*;\s*$', '', source, flags=re.MULTILINE)
    return [m.group(0) for m in TOKEN.finditer(source) if m.lastgroup != 'comment']


def body(source_tokens: list[str], name: str) -> list[str]:
    opening = None
    for start, token in enumerate(source_tokens[:-1]):
        if token != name:
            continue
        after = start + 1
        if source_tokens[after] == '(':
            parens = 1
            after += 1
            while parens and after < len(source_tokens):
                parens += (source_tokens[after] == '(') - (source_tokens[after] == ')')
                after += 1
        if after < len(source_tokens) and source_tokens[after] == '{':
            opening = after
            break
    if opening is None:
        raise ValueError('Declaration body not found: ' + name)
    depth = 1
    for end in range(opening + 1, len(source_tokens)):
        depth += (source_tokens[end] == '{') - (source_tokens[end] == '}')
        if depth == 0:
            return source_tokens[opening + 1:end]
    raise ValueError('Unclosed body: ' + name)


def main() -> int:
    failures: list[str] = []
    checked = 0
    baseline_names = set(subprocess.check_output(
        ['git', 'ls-tree', '-r', '--name-only', BASE], cwd=ROOT, text=True).splitlines())
    for name in PROTECTED + sorted(p for p in baseline_names if p.startswith('vendor/')):
        if name not in baseline_names:
            continue
        checked += 1
        current = ROOT / name
        if not current.is_file() or hashlib.sha256(current.read_bytes()).digest() != hashlib.sha256(original(name)).digest():
            failures.append('protected:' + name)

    projection_checks = 0
    for name in PRODUCTION_PROJECTIONS:
        for dev in (False, True):
            defines = {'SOLARIS_DEV_DISTRIBUTION'} if dev else set()
            before = tokens(preprocess(original(name).decode(), defines))
            after = tokens(preprocess((ROOT/name).read_text(), defines))
            projection_checks += 1
            if before != after:
                failures.append(f'production-projection:{name}:dev={dev}')

    old = ET.fromstring(original('SolarisLauncher.csproj'))
    new = ET.parse(ROOT/'SolarisLauncher.csproj').getroot()
    for path in ['PropertyGroup', 'ItemGroup/PackageReference', 'ItemGroup/ProjectReference']:
        before = [ET.tostring(x) for x in old.findall(path)]
        elements = new.findall(path)
        if path == 'PropertyGroup':
            elements = [x for x in elements if 'SolarisTestBuild' not in x.attrib.get('Condition', '')]
        after = [ET.tostring(x) for x in elements]
        if before != after:
            failures.append('project-production-properties:' + path)
    test_groups = [x for x in new.findall('PropertyGroup')
                   if 'SolarisTestBuild' in x.attrib.get('Condition', '')]
    if not test_groups or not any('SOLARIS_ID_TEST_BUILD' in ''.join(x.itertext()) for x in test_groups):
        failures.append('missing-explicit-test-build-define')

    test_checks = 0
    def test_check(value: bool, name: str) -> None:
        nonlocal test_checks
        test_checks += 1
        if not value:
            failures.append('test-edition:' + name)
    define = {'SOLARIS_ID_TEST_BUILD'}
    app = tokens(preprocess((ROOT/'App.xaml.cs').read_text(), define))
    startup = body(app, 'OnStartup')
    test_check('StartIsolatedTestEdition' in startup and
               not any(t in startup for t in ['RunSelfUpdateAsync', 'ApplyUpdateAsync', 'RedirectToPermanentInstallation']),
               'startup-bypasses-updater-installer')
    window = tokens(preprocess((ROOT/'MainWindow.xaml.cs').read_text(), define))
    loaded = body(window, 'MainWindow_Loaded')
    test_check(not any(t in loaded for t in ['PublishReady', 'CheckForUpdatesAsync', 'CompleteLegacyUpdateMigrationAsync']),
               'loaded-does-not-ack-or-install-updates')
    test_check('ServerIp' not in window and 'PingMinecraftServerAsync' not in body(window, 'UpdateServerStatusAsync'),
               'no-production-server-autojoin-or-poll')
    cloud = preprocess((ROOT/'SolarisCloudClient.cs').read_text(), define)
    test_check('SOLARIS_ID_API_ORIGIN' not in cloud and 'SolarisTestEdition.ConfigFile' in cloud,
               'cloud-origin-is-explicit-test-config-only')
    directories = tokens(preprocess((ROOT/'SolarisDirectories.cs').read_text(), define))
    test_check('StateDirectory' in body(directories, 'StateDir') and 'SaveProfile' not in body(directories, 'StateDir'),
               'state-root-does-not-read-or-migrate-installed-profile')
    native = {x.tag: x.text for g in test_groups for x in g}
    test_check(native.get('IncludeNativeLibrariesForSelfExtract') == 'false' and
               native.get('IncludeAllContentForSelfExtract') == 'false',
               'native-dlls-are-adjacent-not-shared-extraction')
    test_check(native.get('AssemblyName') != old.findtext('PropertyGroup/AssemblyName'),
               'test-assembly-is-distinct')
    ui_file = ROOT/'SolarisTestWindow.cs'
    test_check(ui_file.is_file() and not tokens(preprocess(ui_file.read_text(), set())),
               'smoke-hooks-absent-from-production-compile')

    print(json.dumps({
        'base': BASE, 'protected_files': checked, 'production_projection_checks': projection_checks,
        'test_compile_guards': test_checks,
        'production_versions_channels_dependencies_unchanged': not any(x.startswith('project-') for x in failures),
        'failures': failures, 'native_windows_execution_tested_here': False,
    }, indent=2))
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
