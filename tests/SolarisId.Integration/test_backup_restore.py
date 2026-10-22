#!/usr/bin/env python3
"""Exercise actual PostgreSQL custom-format backup/restore on owned test databases.

SQL fixture markers prove preservation, not successful Identity authentication.
Real account/password/token behavior belongs to test_solaris_id.py. No existing
database is restored over, altered or deleted by this test.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
spec = importlib.util.spec_from_file_location('local_runner', HERE / 'run_local.py')
local = importlib.util.module_from_spec(spec)
spec.loader.exec_module(local)

# Execute only after restore, with API and all bridges stopped. This statement
# expires replayable restored state without deleting accounts/history/MFA/skins.
REVOKE_RESTORED_STATE = '''
BEGIN;
UPDATE "AspNetUsers" SET "SecurityStamp" = gen_random_uuid()::text;
UPDATE "AuthSessions" SET "RevokedUtc" = COALESCE("RevokedUtc", CURRENT_TIMESTAMP);
UPDATE "AuthRefreshTokens" SET "ConsumedUtc" = COALESCE("ConsumedUtc", CURRENT_TIMESTAMP),
  "ExpiresUtc" = LEAST("ExpiresUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisGameTicket" SET "ConsumedUtc" = COALESCE("ConsumedUtc", CURRENT_TIMESTAMP),
  "ExpiresUtc" = LEAST("ExpiresUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisGameConnection" SET "DeniedUtc" = COALESCE("DeniedUtc", CURRENT_TIMESTAMP);
UPDATE "SolarisTrustedGameSession" SET "ClosedUtc" = COALESCE("ClosedUtc", CURRENT_TIMESTAMP);
COMMIT;
'''


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--ef', default='dotnet-ef')
    args = parser.parse_args()
    c = json.loads(args.config.read_text())
    if not re.fullmatch(r'solaris-id-test-[A-Za-z0-9-]+', c['db_container']):
        parser.error('Only the isolated Solaris ID test container is supported')
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', c['db_user']):
        parser.error('Unexpected test database role name')
    output = args.output.resolve()
    if output == ROOT or ROOT in output.parents:
        parser.error('--output must be outside the repository (private database backup)')
    os.umask(0o077)
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    suffix = uuid.uuid4().hex[:12]
    source = 'solaris_backup_' + suffix
    restored = 'solaris_restored_' + suffix
    role = 'solaris_restore_app_' + suffix
    created: list[str] = []
    role_created = False
    results: list[dict] = []
    failed = False

    def check(value: bool, label: str) -> None:
        results.append({'test': label, 'passed': bool(value)})
        if not value:
            raise RuntimeError(label)

    def sql(query: str, database: str = 'postgres', *, success: bool = True) -> str | int:
        result = subprocess.run(['docker', 'exec', '-i', c['db_container'],
            'psql', '-v', 'ON_ERROR_STOP=1', '-U', c['db_user'], '-d', database, '-At'],
            input=query, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if not success:
            return result.returncode
        if result.returncode:
            raise RuntimeError('SQL fixture operation failed; SQL/credentials are not printed')
        return result.stdout.strip()

    def snapshot(database: str) -> str:
        # Row serialization is consumed only in memory, never emitted to logs.
        tables = ['__EFMigrationsHistory', 'AspNetUsers', 'AspNetUserTokens',
            'NicknameReservations', 'Skins', 'PlayerSessions', 'LegacyImports',
            'AuthSessions', 'AuthRefreshTokens', 'SolarisGameTicket',
            'SolarisGameConnection', 'SolarisTrustedGameSession']
        return '\n'.join(str(sql('SELECT COALESCE(jsonb_agg(j ORDER BY j::text),\'[]\'::jsonb)::text '
            f'FROM (SELECT to_jsonb(t) j FROM "{table}" t) q;', database)) for table in tables)

    try:
        image = subprocess.check_output(['docker', 'inspect', '--format', '{{.Config.Image}}',
            c['db_container']], text=True).strip()
        check(image.startswith('postgres:16-alpine'), 'isolated_postgresql_16_fixture_only')
        for database in (source, restored):
            assert re.fullmatch(r'solaris_(?:backup|restored)_[a-f0-9]{12}', database)
            sql('CREATE DATABASE ' + database + ';')
            created.append(database)
        env = local.api_environment(dict(c, db_name=source), output)
        with (output / 'migrations.log').open('wb') as log:
            migration = subprocess.run([args.ef, 'database', 'update', '--project', str(local.PROJECT),
                '--startup-project', str(local.PROJECT), '--configuration', 'Release', '--no-build'],
                cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
        check(migration.returncode == 0 and sql('SELECT count(*) FROM "__EFMigrationsHistory";', source) == '4',
            'source_has_all_four_real_migrations')
        sql('''
INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Nickname","NormalizedNickname",
 "Email","NormalizedEmail","EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp",
 "PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","CreatedUtc","IsBanned","LastActiveUtc")
VALUES ('11111111-2222-4333-8444-555555555555','BackupHero','BACKUPHERO','BackupHero','BACKUPHERO',
 'restore@fixture.invalid','RESTORE@FIXTURE.INVALID',true,'backup-password-hash-column-marker',
 'backup-stamp-marker','backup-concurrency-marker',false,true,true,0,NOW(),false,NOW());
INSERT INTO "AspNetUserTokens" ("UserId","LoginProvider","Name","Value")
VALUES ('11111111-2222-4333-8444-555555555555','[AspNetUserStore]','AuthenticatorKey','backup-mfa-fixture-marker');
INSERT INTO "NicknameReservations" ("NormalizedNickname","UserId","ReservedUtc")
VALUES ('BACKUPHERO','11111111-2222-4333-8444-555555555555',NOW());
INSERT INTO "Skins" ("UserId","Png","Sha256","UpdatedUtc")
VALUES ('11111111-2222-4333-8444-555555555555',decode('01020304','hex'),repeat('A',64),NOW());
INSERT INTO "PlayerSessions" ("UserId","Id","Mode","StartedUtc","Seconds")
VALUES ('11111111-2222-4333-8444-555555555555','10000000-0000-4000-8000-000000000001','vanilla',NOW(),120);
INSERT INTO "LegacyImports" ("UserId","SourceId","VanillaSeconds","ModdedSeconds","ImportedUtc")
VALUES ('11111111-2222-4333-8444-555555555555','backup-history',12,34,NOW());
INSERT INTO "AuthSessions" ("Id","UserId","AccessTokenHash","AccessExpiresUtc","RefreshExpiresUtc", "SecurityStamp",
 "MfaVerified","CreatedUtc","LastSeenUtc","RevokedUtc") VALUES
 ('20000000-0000-4000-8000-000000000001','11111111-2222-4333-8444-555555555555',repeat('B',64),
 NOW()+interval '15 minutes',NOW()+interval '30 days','backup-stamp-marker',true,NOW(),NOW(),NULL);
INSERT INTO "AuthRefreshTokens" ("Id","SessionId","TokenHash","CreatedUtc","ExpiresUtc","ConsumedUtc")
VALUES ('30000000-0000-4000-8000-000000000001','20000000-0000-4000-8000-000000000001',repeat('C',64),NOW(),NOW()+interval '30 days',NULL);
INSERT INTO "SolarisGameTicket" ("Id","TokenHash","UserId","AuthSessionId","ServerId","Nickname","CreatedUtc","ExpiresUtc","ConsumedUtc")
VALUES ('40000000-0000-4000-8000-000000000001',repeat('D',64),'11111111-2222-4333-8444-555555555555',
 '20000000-0000-4000-8000-000000000001','vanilla-local','BackupHero',NOW(),NOW()+interval '2 minutes',NULL);
INSERT INTO "SolarisTrustedGameSession" ("Id","UserId","AuthSessionId","ServerId","Mode","Nickname",
 "ConnectionId","StartedUtc","LastHeartbeatUtc","ClosedUtc","LastSequence","PlayedTicks")
VALUES ('50000000-0000-4000-8000-000000000001','11111111-2222-4333-8444-555555555555','20000000-0000-4000-8000-000000000001',
 'vanilla-local','vanilla','BackupHero','60000000-0000-4000-8000-000000000001',NOW(),NOW(),NULL,7,5000000000);
INSERT INTO "SolarisGameConnection" ("ServerId","ConnectionId","Code","Nickname","CreatedUtc","ExpiresUtc","DeniedUtc","GameSessionId")
VALUES ('vanilla-local','60000000-0000-4000-8000-000000000001','ABCD234567','BackupHero',NOW(),NOW()+interval '90 seconds',NULL,'50000000-0000-4000-8000-000000000001');
''', source)
        before = snapshot(source)
        archive = output / 'isolated-postgresql.dump'
        with archive.open('wb') as dump, (output / 'pg-dump.log').open('wb') as log:
            dump_result = subprocess.run(['docker', 'exec', c['db_container'], 'pg_dump',
                '-U', c['db_user'], '-Fc', '--no-owner', '--no-privileges', '-d', source], stdout=dump, stderr=log)
        check(dump_result.returncode == 0 and archive.read_bytes()[:5] == b'PGDMP', 'real_custom_format_pg_dump')
        check(archive.stat().st_mode & 0o077 == 0, 'backup_file_not_group_or_world_readable')
        with archive.open('rb') as dump, (output / 'archive-list.log').open('wb') as log:
            listed = subprocess.run(['docker', 'exec', '-i', c['db_container'], 'pg_restore', '--list'],
                stdin=dump, stdout=log, stderr=subprocess.STDOUT)
        check(listed.returncode == 0, 'pg_restore_can_read_archive_contents')
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        (output / 'isolated-postgresql.dump.sha256').write_text(digest + '  isolated-postgresql.dump\n')
        check(sql("SELECT count(*) FROM information_schema.tables WHERE table_schema='public';", restored) == '0',
            'restore_target_is_a_new_empty_database')
        with archive.open('rb') as dump, (output / 'pg-restore.log').open('wb') as log:
            restore_result = subprocess.run(['docker', 'exec', '-i', c['db_container'], 'pg_restore',
                '-U', c['db_user'], '--exit-on-error', '--single-transaction', '--no-owner', '--no-privileges',
                '-d', restored], stdin=dump, stdout=log, stderr=subprocess.STDOUT)
        check(restore_result.returncode == 0, 'real_pg_restore_to_new_database')
        check(snapshot(restored) == before, 'all_fixture_rows_columns_and_migration_history_preserved_byte_for_byte')
        check(sql('SELECT "PasswordHash" FROM "AspNetUsers";', restored) == 'backup-password-hash-column-marker',
            'password_hash_column_preserved_not_reset')
        check(sql('SELECT "Value" FROM "AspNetUserTokens";', restored) == 'backup-mfa-fixture-marker',
            'mfa_store_preserved_not_reset')
        check(sql('SELECT "PlayedTicks" FROM "SolarisTrustedGameSession";', restored) == '5000000000',
            'trusted_time_not_double_counted_during_restore')
        check(sql('SELECT count(*) FROM "AuthSessions" WHERE "RevokedUtc" IS NULL;', restored) == '1',
            'restore_old_live_session_risk_demonstrated')
        sql(REVOKE_RESTORED_STATE, restored)
        check(sql('SELECT count(*) FROM "AuthSessions" WHERE "RevokedUtc" IS NULL;', restored) == '0',
            'restored_access_device_sessions_revoked')
        check(sql('SELECT count(*) FROM "AuthRefreshTokens" WHERE "ConsumedUtc" IS NULL OR "ExpiresUtc">NOW();', restored) == '0',
            'restored_refresh_tokens_consumed_and_expired')
        check(sql('SELECT count(*) FROM "SolarisGameTicket" WHERE "ConsumedUtc" IS NULL OR "ExpiresUtc">NOW();', restored) == '0',
            'restored_game_tickets_consumed_and_expired')
        check(sql('SELECT count(*) FROM "SolarisGameConnection" WHERE "DeniedUtc" IS NULL;', restored) == '0' and
            sql('SELECT count(*) FROM "SolarisTrustedGameSession" WHERE "ClosedUtc" IS NULL;', restored) == '0',
            'restored_connections_denied_and_game_sessions_closed')
        check(sql('SELECT "SecurityStamp" FROM "AspNetUsers";', restored) != 'backup-stamp-marker',
            'restored_security_stamp_rotated_for_identity_token_invalidation')
        check(sql('SELECT "PasswordHash" FROM "AspNetUsers";', restored) == 'backup-password-hash-column-marker' and
            sql('SELECT "PlayedTicks" FROM "SolarisTrustedGameSession";', restored) == '5000000000' and
            sql('SELECT encode("Png",\'hex\') FROM "Skins";', restored) == '01020304',
            'revocation_preserves_password_columns_skin_and_playtime')
        sql('CREATE ROLE ' + role + ' NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;')
        role_created = True
        sql('GRANT CONNECT ON DATABASE ' + restored + ' TO ' + role + ';')
        sql('GRANT USAGE ON SCHEMA public TO ' + role + '; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO ' + role + '; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO ' + role + ';', restored)
        check(sql('SET ROLE ' + role + '; SELECT count(*) FROM "AspNetUsers";', restored).endswith('1'),
            'restricted_runtime_role_can_read_restored_identity_data')
        check(sql('SET ROLE ' + role + '; BEGIN; UPDATE "AspNetUsers" SET "LastActiveUtc"=NOW(); ROLLBACK;', restored, success=False) == 0,
            'restricted_runtime_role_can_perform_transactional_dml')
        check(sql('SET ROLE ' + role + '; CREATE TABLE forbidden_schema_write (id int);', restored, success=False) != 0,
            'restricted_runtime_role_cannot_create_schema_objects')
        check(sql('SET ROLE ' + role + '; CREATE ROLE forbidden_global_role;', restored, success=False) != 0,
            'restricted_runtime_role_cannot_grant_roles')
        check(snapshot(source) == before, 'source_database_untouched_by_restore_revocation_and_permissions_tests')
    except (RuntimeError, subprocess.CalledProcessError, OSError):
        failed = True
        if not results or results[-1]['passed']:
            results.append({'test': 'execution_error_see_private_evidence', 'passed': False})
    finally:
        # Remove ONLY database names generated above and successfully created by us.
        for database in reversed(created):
            try:
                sql('DROP DATABASE ' + database + ';')
            except RuntimeError:
                failed = True
                results.append({'test': 'owned_test_database_cleanup', 'passed': False})
        if role_created:
            try:
                sql('DROP ROLE ' + role + ';')
            except RuntimeError:
                failed = True
                results.append({'test': 'owned_test_role_cleanup', 'passed': False})
        receipt = {'tests': len(results), 'failures': sum(not r['passed'] for r in results),
            'results': results, 'real_postgresql': True, 'real_pg_dump_and_restore': True,
            'fixture_rows_only': True, 'production_database_touched': False,
            'native_identity_login_tested_here': False}
        (output / 'results.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({k: receipt[k] for k in ['tests', 'failures', 'real_postgresql', 'real_pg_dump_and_restore']}))
    if failed:
        print('Backup/restore check failed; inspect private evidence outside the checkout.')
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
