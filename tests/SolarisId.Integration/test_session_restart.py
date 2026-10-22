#!/usr/bin/env python3
"""Verify persisted sessions and Data Protection across owned Production API restarts.

Uses an existing isolated loopback PostgreSQL/SMTP/TLS fixture, creates only new
random accounts through HTTPS, and starts/stops only this test's API processes.
Client reconstruction retains credentials in memory; Windows DPAPI and a native
launcher restart are deliberately outside this test.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import urllib.parse
import uuid

import run_local as local
from test_solaris_id import Api, Fixture, Response


ROOT = Path(__file__).resolve().parents[2]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    config_path = args.config.resolve()
    if any(path == ROOT or ROOT in path.parents for path in (output, config_path)):
        parser.error('Config and output must be OUTSIDE the repository')
    config = json.loads(config_path.read_text())
    origin = urllib.parse.urlsplit(config['base_url'])
    if (origin.scheme != 'https' or origin.hostname not in ('localhost', '127.0.0.1', '::1')
            or not config['db_container'].startswith('solaris-id-test-')
            or config['smtp_host'] not in ('localhost', '127.0.0.1', '::1')):
        parser.error('Only isolated loopback HTTPS/PostgreSQL/SMTP fixtures are permitted')
    dll = ROOT / 'backend/Solaris.Id.Api/bin/Release/net8.0/Solaris.Id.Api.dll'
    if not dll.is_file():
        parser.error('Build the real Release API before running this suite')
    os.umask(0o077)
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    # A fresh listener and key ring belong only to this test. The harness API,
    # database container, SMTP process and their key directory are never stopped.
    config = dict(config, base_url=f'https://localhost:{local.free_port()}')
    env = local.api_environment(config, output)
    env['ASPNETCORE_ENVIRONMENT'] = 'Production'
    env['SOLARIS_GAME_SERVERS'] = '[]'
    fixture = Fixture(config)
    results: list[dict] = []
    processes: list[subprocess.Popen] = []
    handles = []
    current: subprocess.Popen | None = None
    phase = 'initialization'
    secret_values = {str(config[name]) for name in ('db_password', 'smtp_password', 'pfx_password')}
    secret_values.update(server['key'] for server in config.get('server_credentials', []))
    observed = dict(real_postgresql=False, real_smtp_starttls_auth=False,
                    email_confirmation_survives_api_restart=False,
                    client_http_objects_recreated=False)

    def check(value: bool, name: str):
        results.append(dict(test=name, passed=bool(value)))
        if not value:
            raise AssertionError(name)

    def expect(response: Response, status: int, name: str) -> Response:
        check(response.status == status, name)
        return response

    def tokens(response: Response) -> dict:
        data = response.data
        if (not isinstance(data, dict) or any(not isinstance(data.get(key), str) or len(data[key]) != 43
                                            for key in ('accessToken', 'refreshToken'))
                or not isinstance(data.get('sessionId'), str)):
            raise RuntimeError('Invalid session response shape')
        uuid.UUID(data['sessionId'])
        secret_values.update(data[key] for key in ('accessToken', 'refreshToken'))
        return data

    def start():
        nonlocal current
        log = (output / f'api-start-{len(processes) + 1}.log').open('wb')
        handles.append(log)
        current = subprocess.Popen(['dotnet', str(dll)], cwd=dll.parent, env=env,
                                   stdout=log, stderr=subprocess.STDOUT)
        processes.append(current)
        health_api = Api(config)

        def healthy():
            if current.poll() is not None:
                raise RuntimeError('Owned API exited before readiness')
            try:
                response = health_api.request('GET', '/health')
            except RuntimeError:
                return False
            return response.status == 200 and response.data.get('status') == 'ok'

        local.wait_until(healthy, 'owned Production HTTPS API', seconds=25)
        observed['real_postgresql'] = True
        check(current.poll() is None, f'production_api_start_{len(processes)}_healthy')

    def stop():
        nonlocal current
        if current is not None and current.poll() is None:
            current.terminate()
            try:
                current.wait(timeout=10)
            except subprocess.TimeoutExpired:
                current.kill()
                current.wait(timeout=10)
        current = None

    def register(api: Api, label: str) -> tuple[dict, str]:
        nickname = 'Restart' + secrets.token_hex(4)
        user = dict(nickname=nickname, email=nickname.lower() + '@example.test',
                    password='Restart!' + secrets.token_urlsafe(18) + '9')
        secret_values.add(user['password'])
        before = fixture.mail_snapshot()
        expect(api.request('POST', '/api/v1/accounts', {
            **user, 'confirmPassword': user['password']}), 202, label + '_registered_over_https')
        message = fixture.message_for(user['email'], before)
        match = re.search(r'https://[^\s<>"\']+', message)
        if match is None:
            raise RuntimeError('Fixture confirmation mail has no HTTPS link')
        link = match.group(0)
        secret_values.add(link)
        secret_values.update(urllib.parse.parse_qs(urllib.parse.urlsplit(link).query).get('code', []))
        observed['real_smtp_starttls_auth'] = True
        return user, link

    def profile(api: Api, session: dict, user_id: str, label: str):
        data = expect(api.request('GET', '/api/v1/me', token=session['accessToken']),
                      200, label + '_access_accepted').data
        check(data.get('id') == user_id and data.get('emailConfirmed') is True,
              label + '_same_confirmed_identity')

    def keyring() -> dict[str, str]:
        # Hashes are used only for an in-memory comparison, never emitted.
        return {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                for path in (output / 'keys').glob('key-*.xml')}

    try:
        phase = 'first_start_and_registration'
        start()
        client_a, client_b = Api(config), Api(config)
        user, link = register(client_a, 'owner')
        expect(client_a.request('GET', link), 200, 'owner_email_confirmed_over_https')
        body = dict(email=user['email'], password=user['password'])
        session_a = tokens(expect(client_a.request('POST', '/api/v1/identity/login', body),
                                  200, 'device_a_password_login'))
        session_b = tokens(expect(client_b.request('POST', '/api/v1/identity/login', body),
                                  200, 'device_b_password_login'))
        check(session_a['sessionId'] != session_b['sessionId'], 'devices_have_independent_persisted_sessions')
        owner = expect(client_a.request('GET', '/api/v1/me', token=session_a['accessToken']),
                       200, 'initial_owner_profile_available').data['id']
        profile(client_b, session_b, owner, 'initial_device_b')
        pending, pending_link = register(client_a, 'pending_owner')
        expect(client_a.request('POST', '/api/v1/identity/login', {
            'email': pending['email'], 'password': pending['password']}),
            401, 'pending_confirmation_is_not_already_confirmed')
        first_keys = keyring()
        check(bool(first_keys), 'production_data_protection_key_ring_persisted')

        phase = 'client_reconstruction'
        # Recreate both HTTP clients, retaining only the serialized token data.
        # This does not simulate Windows DPAPI or execute the WPF launcher.
        session_a, session_b = json.loads(json.dumps([session_a, session_b]))
        client_a, client_b = Api(config), Api(config)
        profile(client_a, session_a, owner, 'reconstructed_device_a')
        profile(client_b, session_b, owner, 'reconstructed_device_b')
        observed['client_http_objects_recreated'] = True

        phase = 'first_api_outage'
        stop()
        unavailable = False
        try:
            client_b.request('GET', '/api/v1/me', token=session_b['accessToken'])
        except RuntimeError:
            unavailable = True
        check(unavailable, 'stopped_api_is_transport_failure_not_success')

        phase = 'first_restart_same_database_and_keys'
        start()
        check(keyring() == first_keys, 'first_restart_reuses_existing_data_protection_keys')
        client_a, client_b = Api(config), Api(config)
        profile(client_a, session_a, owner, 'first_restart_device_a')
        profile(client_b, session_b, owner, 'first_restart_device_b')
        expect(client_a.request('GET', pending_link), 200,
               'pre_restart_email_confirmation_link_valid_after_restart')
        observed['email_confirmation_survives_api_restart'] = True
        pending_session = tokens(expect(client_a.request('POST', '/api/v1/identity/login', {
            'email': pending['email'], 'password': pending['password']}),
            200, 'pre_restart_pending_owner_can_login_after_confirmation'))
        expect(client_a.request('GET', '/api/v1/me', token=pending_session['accessToken']),
               200, 'confirmed_pending_owner_profile_available')
        for label, api, session in (('a', client_a, session_a), ('b', client_b, session_b)):
            refreshed = tokens(expect(api.request('POST', '/api/v1/identity/refresh', {
                'refreshToken': session['refreshToken']}), 200,
                'pre_restart_device_' + label + '_refresh_accepted_after_restart'))
            check(refreshed['sessionId'] == session['sessionId'],
                  'device_' + label + '_refresh_preserves_device_id')
            session.clear()
            session.update(refreshed)
            profile(api, session, owner, 'rotated_device_' + label)
        expect(client_a.request('POST', '/api/v1/identity/logout', {}, token=session_a['accessToken']),
               200, 'device_a_logout_persisted')

        phase = 'second_restart_after_device_logout'
        stop()
        start()
        check(keyring() == first_keys, 'second_restart_reuses_existing_data_protection_keys')
        client_a, client_b = Api(config), Api(config)
        expect(client_a.request('GET', '/api/v1/me', token=session_a['accessToken']),
               401, 'logged_out_device_a_access_stays_revoked_after_restart')
        expect(client_a.request('POST', '/api/v1/identity/refresh', {
            'refreshToken': session_a['refreshToken']}),
            401, 'logged_out_device_a_refresh_stays_revoked_after_restart')
        profile(client_b, session_b, owner, 'second_restart_unaffected_device_b')
        refreshed_b = tokens(expect(client_b.request('POST', '/api/v1/identity/refresh', {
            'refreshToken': session_b['refreshToken']}),
            200, 'unaffected_device_b_refresh_valid_after_second_restart'))
        check(refreshed_b['sessionId'] == session_b['sessionId'],
              'second_restart_device_b_refresh_preserves_device_id')
        profile(client_b, refreshed_b, owner, 'second_restart_rotated_device_b')
        phase = 'secret_log_scan'
        stop()
        for handle in handles:
            handle.flush()
        logs = '\n'.join(path.read_text(errors='replace') for path in output.glob('api-start-*.log'))
        check(all(secret not in logs and urllib.parse.quote(secret, safe='') not in logs
                  for secret in secret_values if len(secret) >= 8),
              'owned_api_logs_do_not_contain_passwords_tokens_credentials_or_confirmation_codes')
    except Exception as error:
        if not isinstance(error, AssertionError):
            results.append(dict(test='unexpected_error_during_' + phase, passed=False))
        # Exception text and HTTP/mail/config payloads can carry credentials.
        print('FAIL session restart:', phase, type(error).__name__)
    finally:
        stop()
        for handle in handles:
            handle.close()
        receipt = dict(tests=len(results), failures=sum(not result['passed'] for result in results),
                       results=results, production_api_process_starts=len(processes),
                       **observed, https_certificate_verification=True,
                       native_windows_dpapi_tested=False, native_launcher_process_restarted=False,
                       native_minecraft_clients_tested=False, production_host_deployed=False)
        (output / 'results.json').write_text(json.dumps(receipt, indent=2) + '\n')
        print(json.dumps({key: value for key, value in receipt.items() if key != 'results'}))
    return 0 if results and not receipt['failures'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
