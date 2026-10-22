#!/usr/bin/env python3
"""Exercise non-destructive upgrade of an actual legacy PostgreSQL schema.

The two explicit SQL fixture rows test schema preservation, not authentication.
All real account/password/session behavior is covered by test_solaris_id.py.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import uuid

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
spec=importlib.util.spec_from_file_location('local_runner',HERE/'run_local.py')
local=importlib.util.module_from_spec(spec);spec.loader.exec_module(local)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--ef',default='dotnet-ef')
    args=parser.parse_args()
    c=json.loads(args.config.read_text());assert c['db_container'].startswith('solaris-id-test-')
    args.output.mkdir(parents=True,exist_ok=False,mode=0o700)
    name='solaris_upgrade_'+uuid.uuid4().hex[:12];assert re.fullmatch('[a-z0-9_]+',name)
    def sql(query,database=name):
        result=subprocess.run(['docker','exec','-i',c['db_container'],'psql','-v','ON_ERROR_STOP=1','-U',c['db_user'],'-d',database,'-At'],input=query,text=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        if result.returncode:raise RuntimeError('SQL fixture command failed; no SQL/credential dump')
        return result.stdout.strip()
    def migrate(target,label):
        env=local.api_environment(dict(c,db_name=name),args.output)
        command=[args.ef,'database','update']+([target] if target else [])+['--project',str(local.PROJECT),'--startup-project',str(local.PROJECT),'--configuration','Release','--no-build']
        with (args.output/(label+'.log')).open('wb') as log:r=subprocess.run(command,cwd=ROOT,env=env,stdout=log,stderr=subprocess.STDOUT)
        return r.returncode
    results=[]
    def check(value,label):
        results.append({'test':label,'passed':bool(value)})
        if not value:raise RuntimeError(label)
    created=False
    try:
        sql('CREATE DATABASE '+name+';',database='postgres');created=True
        check(migrate('20261010000813_LegacyImports','old-schema')==0,'apply_existing_legacy_migrations')
        sql('''INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Nickname","NormalizedNickname","Email","NormalizedEmail","EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","CreatedUtc","IsBanned") VALUES
        ('legacy-one','OldOne','OLDONE','OldOne','OLDONE','same@fixture.invalid','SAME@FIXTURE.INVALID',false,'migration-preservation-marker','stamp-one','concurrency-one',false,false,true,0,NOW(),false),
        ('legacy-two','OldTwo','OLDTWO','OldTwo','OLDTWO','same@fixture.invalid','SAME@FIXTURE.INVALID',false,'migration-preservation-marker','stamp-two','concurrency-two',false,false,true,0,NOW(),false);''')
        check(migrate(None,'duplicate-email-rejected')!=0,'duplicate_email_preflight_aborts_upgrade')
        check(sql('SELECT count(*) FROM "AspNetUsers";')=='2','failed_upgrade_preserves_all_existing_users')
        check(sql('SELECT count(*) FROM "__EFMigrationsHistory";')=='2','failed_upgrade_rolls_back_new_schema')
        check(sql("SELECT count(*) FROM \"AspNetUsers\" WHERE \"PasswordHash\"='migration-preservation-marker';")=='2','failed_upgrade_preserves_password_columns')
        # Only this artificial, newly created fixture database is modified.
        sql('''UPDATE "AspNetUsers" SET "Email"='other@fixture.invalid',"NormalizedEmail"='OTHER@FIXTURE.INVALID' WHERE "Id"='legacy-two';''')
        check(migrate(None,'upgrade')==0,'upgrade_after_explicit_fixture_duplicate_resolution')
        check(sql('SELECT count(*) FROM "AspNetUsers";')=='2','successful_upgrade_preserves_both_ids')
        check(sql("SELECT count(*) FROM \"AspNetUsers\" WHERE \"PasswordHash\"='migration-preservation-marker';")=='2','successful_upgrade_preserves_password_columns')
        check(sql('''SELECT count(*) FROM "NicknameReservations" r JOIN "AspNetUsers" u ON u."Id"=r."UserId" AND u."NormalizedNickname"=r."NormalizedNickname";''')=='2','permanent_nickname_ownership_backfills_existing_accounts')
        check(sql('SELECT count(*) FROM "__EFMigrationsHistory";')=='4','all_four_migrations_recorded')
    finally:
        if created:sql('DROP DATABASE '+name+';',database='postgres')
        (args.output/'results.json').write_text(json.dumps(dict(tests=len(results),failures=sum(not x['passed'] for x in results),results=results,real_postgresql=True,fixture_rows_only=True),indent=2)+'\n')
    print(json.dumps({'tests':len(results),'failures':sum(not x['passed'] for x in results),'real_postgresql':True}))

if __name__=='__main__':main()
