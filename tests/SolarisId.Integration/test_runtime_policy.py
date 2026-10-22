#!/usr/bin/env python3
"""Real production-mode startup/TLS/rate-limit checks on isolated loopback fixtures."""
from __future__ import annotations
import argparse
import importlib.util
import json
import os
from pathlib import Path
import ssl
import subprocess
import time
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('solaris_local', HERE/'run_local.py')
local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(local)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    config = json.loads(args.config.read_text())
    if not config['db_container'].startswith('solaris-id-test-') or not config['base_url'].startswith('https://localhost:'):
        parser.error('Only isolated loopback fixtures are permitted')
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    env = local.api_environment(config, args.output)
    env['ASPNETCORE_ENVIRONMENT'] = 'Production'
    env['SOLARIS_GAME_SERVERS'] = '[]'
    env['SOLARIS_ID_DATA_PROTECTION_KEYS'] = str(args.output/'keys')
    dll = ROOT/'backend/Solaris.Id.Api/bin/Release/net8.0/Solaris.Id.Api.dll'
    command = ['dotnet', str(dll)]
    results = []
    processes = []
    handles = []
    def check(value: bool, name: str):
        results.append(dict(test=name, passed=bool(value)))
        if not value: raise RuntimeError(name)
    def request(url: str, payload: bytes | None = None):
        req = urllib.request.Request(url, data=payload, headers={'Content-Type':'application/json'} if payload is not None else {})
        try:
            with urllib.request.urlopen(req, context=ssl.create_default_context(cafile=config['ca_file']), timeout=3) as response:
                return response.status, response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.headers
    try:
        missing = dict(env); missing.pop('SOLARIS_ID_DATA_PROTECTION_KEYS', None)
        with (args.output/'missing-keys.log').open('wb') as log:
            result = subprocess.run(command, env=missing, stdout=log, stderr=subprocess.STDOUT, timeout=15)
        check(result.returncode != 0 and 'SOLARIS_ID_DATA_PROTECTION_KEYS' in (args.output/'missing-keys.log').read_text(errors='replace'), 'production_refuses_missing_persistent_keys')
        https_port, http_port = local.free_port(), local.free_port()
        base = f'https://localhost:{https_port}'
        env['ASPNETCORE_URLS'] = base+f';http://127.0.0.1:{http_port}'
        # Both development-only exceptions must be ignored in Production.
        env['SOLARIS_AUTH_RATE_LIMIT'] = '1000'
        env['SOLARIS_ID_ALLOW_HTTP_LOOPBACK'] = 'true'
        log = (args.output/'production.log').open('wb'); handles.append(log)
        proc = subprocess.Popen(command, env=env, stdout=log, stderr=subprocess.STDOUT); processes.append(proc)
        local.wait_until(lambda: request(base+'/health')[0] == 200, 'production HTTPS readiness', seconds=20)
        check(request(base+'/health')[0] == 200, 'production_serves_verified_https')
        status, headers = request(base+'/health')
        check(headers.get('Cache-Control') == 'no-store' and headers.get('X-Content-Type-Options') == 'nosniff', 'production_security_response_headers')
        check(request(f'http://127.0.0.1:{http_port}/health')[0] == 400, 'production_rejects_plaintext_even_when_dev_override_is_set')
        statuses = [request(base+'/api/v1/identity/login', b'{"email":"unregistered@integration.invalid","password":"invalid-test-credential"}')[0] for _ in range(12)]
        check(statuses == [401]*10+[429]*2, 'production_ignores_dev_rate_override_and_limits_credentials_to_10')
        proc.terminate(); proc.wait(timeout=10)
        # Independent API process: no live database access is changed/stopped.
        bad = dict(env); bad['ASPNETCORE_URLS'] = f'https://localhost:{local.free_port()}'
        bad['SOLARIS_ID_CONNECTION_STRING'] = f'Host=127.0.0.1;Port={local.free_port()};Database=unavailable;Username=unavailable;Timeout=1;Command Timeout=1'
        log = (args.output/'database-unavailable.log').open('wb'); handles.append(log)
        proc = subprocess.Popen(command, env=bad, stdout=log, stderr=subprocess.STDOUT); processes.append(proc)
        local.wait_until(lambda: request(bad['ASPNETCORE_URLS']+'/health')[0] == 503, 'unavailable database response', seconds=20)
        check(request(bad['ASPNETCORE_URLS']+'/health')[0] == 503, 'health_returns_503_for_unavailable_database')
        check(all('invalid-test-credential' not in p.read_text(errors='replace') for p in args.output.glob('*.log')), 'request_password_body_is_not_logged')
    except Exception as error:
        print('FAIL:', type(error).__name__, str(error) if isinstance(error,RuntimeError) else 'see private fixture logs')
        return_code = 1
    else:
        return_code = 0
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired: process.kill();process.wait()
        for handle in handles: handle.close()
        receipt=dict(tests=len(results),failures=sum(not x['passed'] for x in results),results=results,real_production_environment=True,tls_verification=True)
        (args.output/'results.json').write_text(json.dumps(receipt,indent=2)+'\n')
        print(json.dumps({k:receipt[k] for k in ['tests','failures','real_production_environment','tls_verification']}))
    return return_code

if __name__ == '__main__':
    raise SystemExit(main())
