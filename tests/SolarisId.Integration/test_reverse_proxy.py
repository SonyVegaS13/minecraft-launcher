#!/usr/bin/env python3
"""Real HAProxy -> verified TLS -> Production API checks on isolated loopback."""
from __future__ import annotations
import argparse
import importlib.util
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import subprocess
import time
import urllib.error
import urllib.request
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('solaris_local', HERE/'run_local.py')
local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(local)
PROXY_IMAGE = 'haproxy:3.2-alpine@sha256:4e4d428de823155c52f1957fd3208f2b99b1dcc92dc36321752cb4edb8366301'


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    c = json.loads(args.config.read_text())
    if not c['db_container'].startswith('solaris-id-test-') or not c['base_url'].startswith('https://localhost:'):
        parser.error('Only isolated loopback fixtures are permitted')
    out = args.output.resolve()
    if out == ROOT or ROOT in out.parents:
        parser.error('--output must be outside the repository (private TLS fixture)')
    os.umask(0o077)
    out.mkdir(parents=True, exist_ok=False, mode=0o700)
    api_port, front_port, plain_port = local.free_port(), local.free_port(), local.free_port()
    api_url, front_url = f'https://localhost:{api_port}', f'https://localhost:{front_port}'
    context = ssl.create_default_context(cafile=c['ca_file'])
    env = local.api_environment(c, out)
    env.update(ASPNETCORE_ENVIRONMENT='Production', ASPNETCORE_URLS=api_url,
        SOLARIS_ID_PUBLIC_ORIGIN=front_url, SOLARIS_ID_DATA_PROTECTION_KEYS=str(out/'keys'))
    log = (out/'api.log').open('wb')
    api = None
    containers = []
    records = []
    framing_result = None
    canaries = [secrets.token_hex(24) for _ in range(4)]

    def check(value, name):
        records.append(dict(test=name, passed=bool(value)))
        if not value: raise RuntimeError(name)

    def request(url, payload=None, headers=None):
        req = urllib.request.Request(url, data=payload, headers=headers or {})
        try:
            with urllib.request.urlopen(req, context=context, timeout=5) as r:
                return r.status, r.headers, r.read()
        except urllib.error.HTTPError as r:
            return r.code, r.headers, r.read()

    def proxy_config(upstream_hostname='localhost'):
        text = (ROOT/'deploy/solaris-id/haproxy.cfg.example').read_text()
        text = text.replace('bind :80', f'bind 127.0.0.1:{plain_port}')
        text = text.replace('bind :443', f'bind 127.0.0.1:{front_port}')
        text = text.replace('auth.example.invalid', 'localhost')
        text = text.replace('/etc/solaris-id/proxy/frontend.pem', '/fixture/frontend.pem')
        text = text.replace('/etc/ssl/certs/ca-certificates.crt', '/fixture/ca.crt')
        text = text.replace('127.0.0.1:8443', f'127.0.0.1:{api_port}')
        text = text.replace('verifyhost localhost', 'verifyhost '+upstream_hostname)
        return text

    def start_proxy(text, suffix):
        config_path = out/('haproxy-'+suffix+'.cfg')
        config_path.write_text(text)
        name = 'solaris-id-test-proxy-'+uuid.uuid4().hex[:12]
        common = ['docker','run','--user',f'{os.getuid()}:{os.getgid()}','--cap-drop','ALL','--security-opt','no-new-privileges',
            '--read-only','--network','host','--mount',f'type=bind,source={out},target=/fixture,readonly']
        local.run(common+['--rm',PROXY_IMAGE,'haproxy','-c','-f','/fixture/'+config_path.name], out/('config-'+suffix+'.log'))
        local.run(common+['--detach','--name',name,PROXY_IMAGE,'haproxy','-W','-db','-f','/fixture/'+config_path.name], out/('start-'+suffix+'.log'))
        containers.append(name)
        return name

    def stop_proxy(name):
        subprocess.run(['docker','stop','--time','3',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        with (out/(name+'.log')).open('wb') as stream:
            subprocess.run(['docker','logs',name],stdout=stream,stderr=subprocess.STDOUT)
        subprocess.run(['docker','rm',name],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        containers.remove(name)

    def raw_request(payload):
        with socket.create_connection(('localhost',front_port),timeout=5) as tcp:
            with context.wrap_socket(tcp,server_hostname='localhost') as tls:
                tls.sendall(payload)
                return tls.recv(4096).split(b'\r\n',1)[0]

    code = 0
    try:
        (out/'frontend.pem').write_bytes(Path(c['tls_cert']).read_bytes()+Path(c['tls_key']).read_bytes())
        (out/'ca.crt').write_bytes(Path(c['ca_file']).read_bytes())
        api = subprocess.Popen(['dotnet',str(ROOT/'backend/Solaris.Id.Api/bin/Release/net8.0/Solaris.Id.Api.dll')],env=env,stdout=log,stderr=subprocess.STDOUT)
        local.wait_until(lambda: request(api_url+'/health')[0] == 200, 'private Production HTTPS API', seconds=25)
        proxy = start_proxy(proxy_config(), 'valid')
        local.wait_until(lambda: request(front_url+'/health')[0] == 200, 'HAProxy HTTPS readiness', seconds=25)
        status, headers, body = request(front_url+'/health')
        check(status==200 and json.loads(body)['status']=='ok', 'real_proxy_serves_database_health_over_verified_frontend_and_upstream_tls')
        check(headers.get('Cache-Control')=='no-store' and headers.get('X-Content-Type-Options')=='nosniff' and headers.get('Strict-Transport-Security')=='max-age=31536000', 'proxy_preserves_security_headers_and_sets_hsts')
        check(request(f'http://localhost:{plain_port}/health',headers={'X-Forwarded-Proto':'https'})[0]==400, 'plaintext_cannot_spoof_https')
        check(request(front_url+'/health',headers={'Host':'attacker.invalid'})[0]==400, 'unknown_host_is_rejected')
        check(request(front_url+'/api/v1/me',headers={'Authorization':'Bearer '+canaries[0]})[0]==401, 'invalid_authentication_is_denied_through_proxy')
        # Unknown user avoids account lockout; spoofed forwarded IPs must NOT
        # split the actual client's Production rate-limit bucket.
        statuses=[]
        for i in range(12):
            statuses.append(request(front_url+'/api/v1/identity/login',
                json.dumps(dict(email='proxy-fixture@integration.invalid',password=canaries[1])).encode(),
                {'Content-Type':'application/json','X-Forwarded-For':f'198.51.100.{i+1}',
                 'X-Forwarded-Proto':'http','Forwarded':f'for=198.51.100.{i+1};proto=http'})[0])
        check(statuses==[401]*10+[429]*2, 'client_forwarded_headers_cannot_bypass_production_ip_rate_limit')
        framing = raw_request(b'POST /health HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1\r\nContent-Length: 2\r\nConnection: close\r\n\r\nxx')
        # HAProxy may close malformed-framing connections without an HTTP body.
        # Both an explicit 400 and immediate EOF reject the request; accepting
        # or forwarding a normal success/error route response would fail here.
        framing_result = 'connection_closed' if framing == b'' else framing.decode('ascii',errors='replace')
        check(framing==b'' or framing.startswith((b'HTTP/1.1 400 ',b'HTTP/1.0 400 ')), 'conflicting_content_lengths_are_rejected')
        request(front_url+'/missing?code='+canaries[2],headers={'Cookie':'session='+canaries[3]})
        api.terminate(); api.wait(timeout=10)
        local.wait_until(lambda: request(front_url+'/health')[0]==503, 'failed upstream closes access', seconds=15)
        check(request(front_url+'/health')[0]==503, 'upstream_outage_fails_closed')
        stop_proxy(proxy)
        api = subprocess.Popen(['dotnet',str(ROOT/'backend/Solaris.Id.Api/bin/Release/net8.0/Solaris.Id.Api.dll')],env=env,stdout=log,stderr=subprocess.STDOUT)
        local.wait_until(lambda: request(api_url+'/health')[0]==200, 'upstream restart', seconds=20)
        proxy = start_proxy(proxy_config('wrong-hostname.invalid'), 'wrong-hostname')
        local.wait_until(lambda: request(front_url+'/health')[0]==503, 'upstream hostname verification failure', seconds=15)
        check(request(front_url+'/health')[0]==503, 'invalid_upstream_certificate_hostname_is_not_trusted')
        stop_proxy(proxy)
        log.flush()
        texts=[p.read_text(errors='replace') for p in out.glob('*.log')]
        check(all(not any(secret in text for secret in canaries) for text in texts), 'proxy_and_api_logs_do_not_contain_password_token_cookie_or_query_canaries')
        check(any('status=503' in text for text in texts), 'upstream_failures_remain_observable_in_sanitized_logs')
    except Exception as error:
        code=1
        print('FAIL:',type(error).__name__,str(error) if isinstance(error,RuntimeError) else 'see private fixture logs')
    finally:
        for name in list(containers): stop_proxy(name)
        if api is not None and api.poll() is None:
            api.terminate()
            try: api.wait(timeout=10)
            except subprocess.TimeoutExpired: api.kill();api.wait()
        log.close()
        passed={x['test'] for x in records if x['passed']}
        receipt=dict(tests=len(records),failures=sum(not x['passed'] for x in records),completed=code==0,
            errors=int(code!=0 and all(x['passed'] for x in records)),
            real_haproxy='real_proxy_serves_database_health_over_verified_frontend_and_upstream_tls' in passed,
            real_production_api='real_proxy_serves_database_health_over_verified_frontend_and_upstream_tls' in passed,
            frontend_and_upstream_tls_verified='invalid_upstream_certificate_hostname_is_not_trusted' in passed,
            malformed_framing_result=framing_result,docker_image=PROXY_IMAGE,results=records)
        (out/'results.json').write_text(json.dumps(receipt,indent=2)+'\n')
        print(json.dumps({k:receipt[k] for k in ['tests','failures','completed','real_haproxy','real_production_api']}))
    return code

if __name__=='__main__':
    raise SystemExit(main())
