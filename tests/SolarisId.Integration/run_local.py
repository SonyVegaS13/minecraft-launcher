#!/usr/bin/env python3
"""Run real isolated PostgreSQL + SMTP STARTTLS + HTTPS API integration tests.

Requires Docker, OpenSSL, .NET8, dotnet-ef8 and the pinned Python requirements.
Never targets an existing database, production service, or installed launcher.
Secrets and mailbox data stay in a private directory OUTSIDE the repository.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import smtplib
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / 'backend/Solaris.Id.Api/Solaris.Id.Api.csproj'
POSTGRES_IMAGE = 'postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea'


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def run(command: list[str], log: Path, env: dict[str, str] | None = None) -> None:
    with log.open('wb') as stream:
        result = subprocess.run(command, cwd=ROOT, env=env, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'{log.name} failed (exit {result.returncode}); private log: {log}')


def wait_until(check, label: str, seconds: int = 45):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, ssl.SSLError, urllib.error.URLError, smtplib.SMTPException):
            pass
        time.sleep(.25)
    raise RuntimeError(f'{label} did not become ready within {seconds}s')


def certificates(folder: Path, password: str) -> None:
    folder.mkdir(mode=0o700)
    (folder / 'ca.cnf').write_text('[req]\ndistinguished_name=dn\nx509_extensions=ca\nprompt=no\n[dn]\nCN=Solaris ID integration CA\n[ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign,cRLSign\n')
    (folder / 'leaf.ext').write_text('subjectAltName=DNS:localhost,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n')
    commands = [
        ['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2', '-keyout', str(folder/'ca.key'), '-out', str(folder/'localhost.crt'), '-config', str(folder/'ca.cnf')],
        ['openssl', 'req', '-new', '-newkey', 'rsa:2048', '-nodes', '-keyout', str(folder/'server.key'), '-out', str(folder/'server.csr'), '-subj', '/CN=localhost'],
        ['openssl', 'x509', '-req', '-in', str(folder/'server.csr'), '-CA', str(folder/'localhost.crt'), '-CAkey', str(folder/'ca.key'), '-CAcreateserial', '-days', '2', '-out', str(folder/'server.crt'), '-extfile', str(folder/'leaf.ext')],
        ['openssl', 'pkcs12', '-export', '-out', str(folder/'localhost.pfx'), '-inkey', str(folder/'server.key'), '-in', str(folder/'server.crt'), '-certfile', str(folder/'localhost.crt'), '-passout', 'env:SOLARIS_TEST_PFX_PASSWORD'],
    ]
    env = dict(os.environ, SOLARIS_TEST_PFX_PASSWORD=password)
    with (folder/'openssl.log').open('wb') as log:
        for command in commands:
            subprocess.run(command, env=env, check=True, stdout=log, stderr=log)


def api_environment(config: dict, folder: Path) -> dict[str, str]:
    return dict(os.environ,
        ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=config['base_url'],
        Kestrel__Certificates__Default__Path=config['pfx_file'],
        Kestrel__Certificates__Default__Password=config['pfx_password'],
        SOLARIS_ID_CONNECTION_STRING=f"Host=127.0.0.1;Port={config['db_port']};Database={config['db_name']};Username={config['db_user']};Password={config['db_password']}",
        SOLARIS_ID_PUBLIC_ORIGIN=config['base_url'],
        SOLARIS_ID_DATA_PROTECTION_KEYS=str(folder/'keys'),
        SOLARIS_SMTP_HOST=config['smtp_host'], SOLARIS_SMTP_PORT=str(config['smtp_port']),
        SOLARIS_SMTP_FROM='solaris-id@integration.invalid',
        SOLARIS_SMTP_USER=config['smtp_user'], SOLARIS_SMTP_PASSWORD=config['smtp_password'],
        SOLARIS_AUTH_RATE_LIMIT='1000', SOLARIS_GAME_SERVERS=json.dumps(config['server_credentials']),
        SSL_CERT_FILE=config['ca_file'])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work-dir', type=Path)
    parser.add_argument('--ef', default='dotnet-ef', help='Installed dotnet-ef8 command/path')
    args = parser.parse_args()
    for tool in ['docker', 'openssl', 'dotnet']:
        if not shutil.which(tool):
            parser.error(f'{tool} is required; activate the documented development tools first')
    if not shutil.which(args.ef) and not Path(args.ef).is_file():
        parser.error('dotnet-ef8 is required; pass --ef /path/to/dotnet-ef')
    os.umask(0o077)
    if args.work_dir:
        folder = args.work_dir.resolve()
        if folder == ROOT or ROOT in folder.parents:
            parser.error('--work-dir must be OUTSIDE the repository (secrets and test mailbox)')
        folder.mkdir(parents=True, exist_ok=False, mode=0o700)
    else:
        folder = Path(tempfile.mkdtemp(prefix='solaris-id-validation-'))
    smtp_port, api_port, db_port = free_port(), free_port(), free_port()
    pfx_password = secrets.token_urlsafe(24)
    certificates(folder/'tls', pfx_password)
    config = dict(base_url=f'https://localhost:{api_port}', ca_file=str(folder/'tls/localhost.crt'),
        tls_cert=str(folder/'tls/server.crt'), tls_key=str(folder/'tls/server.key'),
        pfx_file=str(folder/'tls/localhost.pfx'), pfx_password=pfx_password,
        smtp_host='localhost', smtp_port=smtp_port, smtp_user='solaris-test', smtp_password=secrets.token_urlsafe(24),
        mailbox_dir=str(folder/'mail'), db_container='solaris-id-test-'+uuid.uuid4().hex[:12],
        db_name='solaris_id', db_user='solaris_test', db_password=secrets.token_urlsafe(24), db_port=db_port,
        server_credentials=[dict(id='vanilla-local', mode='vanilla', key=secrets.token_urlsafe(32)), dict(id='modded-local', mode='modded', key=secrets.token_urlsafe(32))],
        api_log_path=str(folder/'api.log'), artifact_dir=str(folder))
    (folder/'mail').mkdir(mode=0o700)
    config_file = folder/'integration-env.json'
    config_file.write_text(json.dumps(config, indent=2)+'\n')
    pg_env = folder/'postgres.env'
    pg_env.write_text(f"POSTGRES_USER={config['db_user']}\nPOSTGRES_DB={config['db_name']}\nPOSTGRES_PASSWORD={config['db_password']}\n")
    env = api_environment(config, folder)
    processes: list[subprocess.Popen] = []
    handles = []
    created_db = False
    try:
        run(['docker', 'run', '--detach', '--name', config['db_container'], '--env-file', str(pg_env), '--publish', f'127.0.0.1:{db_port}:5432', POSTGRES_IMAGE], folder/'postgres-start.log')
        created_db = True
        wait_until(lambda: subprocess.run(['docker','exec',config['db_container'],'pg_isready','-U',config['db_user'],'-d',config['db_name']], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0, 'isolated PostgreSQL')
        print('PostgreSQL16 ready (new isolated database).', flush=True)
        run(['dotnet','restore',str(PROJECT)], folder/'restore.log', env)
        run(['dotnet','build',str(PROJECT),'-c','Release','--no-restore'], folder/'build.log', env)
        run([args.ef,'database','update','--project',str(PROJECT),'--startup-project',str(PROJECT),'--configuration','Release','--no-build'], folder/'migrations.log', env)
        smtp_log = (folder/'smtp.log').open('wb'); handles.append(smtp_log)
        processes.append(subprocess.Popen([sys.executable,str(Path(__file__).with_name('mail_sink.py')),'--config',str(config_file)],cwd=ROOT,stdout=smtp_log,stderr=subprocess.STDOUT))
        context = ssl.create_default_context(cafile=config['ca_file'])
        def smtp_ready():
            with smtplib.SMTP(config['smtp_host'],smtp_port,timeout=2) as smtp:
                smtp.starttls(context=context)
                smtp.login(config['smtp_user'],config['smtp_password'])
                return True
        wait_until(smtp_ready,'SMTP STARTTLS/AUTH')
        api_log = (folder/'api.log').open('wb'); handles.append(api_log)
        processes.append(subprocess.Popen(['dotnet',str(PROJECT.parent/'bin/Release/net8.0/Solaris.Id.Api.dll')],env=env,cwd=PROJECT.parent,stdout=api_log,stderr=subprocess.STDOUT))
        def health():
            with urllib.request.urlopen(config['base_url']+'/health',context=context,timeout=2) as response:
                return response.status == 200 and json.load(response)['status'] == 'ok'
        wait_until(health,'HTTPS API + database readiness')
        print('Real HTTPS API and authenticated STARTTLS mailbox ready.',flush=True)
        suites = [
            ('test_solaris_id.py', 'tests', []),
            ('test_runtime_policy.py', 'production-policy', []),
            ('test_migration_upgrade.py', 'migration-upgrade', ['--ef', args.ef]),
        ]
        for script, output, extra in suites:
            command = [sys.executable,str(Path(__file__).with_name(script)),
                '--config',str(config_file),'--output',str(folder/output),*extra]
            if subprocess.run(command,cwd=ROOT,env=env).returncode:
                raise RuntimeError(f'{script} failed; private evidence: {folder}')
        print(f'PASS (29 API, 7 Production policy, 10 migration checks). Evidence: {folder}. Keep integration-env.json, keys and mailbox PRIVATE; never upload them.',flush=True)
        return 0
    except (RuntimeError,subprocess.CalledProcessError) as error:
        print(str(error),file=sys.stderr)
        return 1
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try: process.wait(timeout=8)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        for handle in handles: handle.close()
        if created_db:
            subprocess.run(['docker','rm','--force',config['db_container']],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

if __name__ == '__main__':
    raise SystemExit(main())
