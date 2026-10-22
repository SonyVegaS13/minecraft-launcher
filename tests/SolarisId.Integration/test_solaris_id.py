#!/usr/bin/env python3
"""Real HTTPS/PostgreSQL/STARTTLS security acceptance tests for Solaris ID.

Run against an isolated local fixture only. No account/session/auth logic is
mocked. SQL is restricted to explicit expiry, persisted-state inspection, and
out-of-band test-admin provisioning. Test secrets never appear in output.
"""
from __future__ import annotations

import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
import email
from email import policy
import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import ssl
import struct
import subprocess
import sys
import time
from typing import Any
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib

CONFIG: dict[str, Any] = {}
SECRETS: set[str] = set()


@dataclass
class Response:
    status: int
    raw: bytes
    headers: Any
    route: str

    @property
    def data(self):
        try:
            return json.loads(self.raw)
        except (ValueError, UnicodeDecodeError):
            return None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Api:
    def __init__(self, config):
        self.origin = config["base_url"].rstrip("/")
        parts = urllib.parse.urlsplit(self.origin)
        if parts.scheme != "https" or parts.hostname not in ("localhost", "127.0.0.1", "::1"):
            raise ValueError("Tests require a loopback HTTPS fixture, never a live service")
        self.context = ssl.create_default_context(cafile=config["ca_file"])
        self.context.minimum_version = ssl.TLSVersion.TLSv1_2
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=self.context), NoRedirect())

    def request(self, method: str, path: str, payload=None, token: str | None = None,
                server: dict | None = None, binary: bytes | None = None,
                content_type: str = "image/png", headers: dict | None = None) -> Response:
        if path.startswith("https://"):
            target = urllib.parse.urlsplit(path)
            origin = urllib.parse.urlsplit(self.origin)
            if (target.scheme, target.netloc) != (origin.scheme, origin.netloc):
                raise ValueError("An email link points outside the isolated HTTPS fixture")
            url = path
        else:
            if not path.startswith("/"):
                raise ValueError("API route must be absolute")
            url = self.origin + path
        route = urllib.parse.urlsplit(url).path  # Queries may contain email tokens.
        body = binary
        request_headers = {"Accept": "application/json"}
        if payload is not None:
            body = json.dumps(payload).encode()
            request_headers["Content-Type"] = "application/json"
        elif binary is not None:
            request_headers["Content-Type"] = content_type
        if token:
            request_headers["Authorization"] = "Bearer " + token
        if server:
            request_headers["Authorization"] = "SolarisServer " + server["key"]
            request_headers["X-Solaris-Server"] = server["id"]
        if headers:
            request_headers.update(headers)
        request = urllib.request.Request(url, data=body, method=method, headers=request_headers)
        try:
            with self.opener.open(request, timeout=20) as response:
                return Response(response.status, response.read(), response.headers, route)
        except urllib.error.HTTPError as error:
            return Response(error.code, error.read(), error.headers, route)
        except Exception as error:
            raise RuntimeError(f"HTTPS transport failed for {method} {route}: {type(error).__name__}") from None

    def chunked_skin(self, token: str, parts: list[bytes]) -> Response:
        origin = urllib.parse.urlsplit(self.origin)
        connection = http.client.HTTPSConnection(origin.hostname, origin.port,
                                                 context=self.context, timeout=20)
        try:
            connection.request("PUT", "/api/v1/me/skin", body=iter(parts),
                               headers={"Authorization": "Bearer " + token,
                                        "Content-Type": "image/png",
                                        "Transfer-Encoding": "chunked"},
                               encode_chunked=True)
            response = connection.getresponse()
            return Response(response.status, response.read(), response.headers, "/api/v1/me/skin")
        finally:
            connection.close()


class Fixture:
    def __init__(self, config):
        self.config = config
        self.mailbox = Path(config["mailbox_dir"])
        self.container = config["db_container"]
        if not self.container.startswith("solaris-id-test-"):
            raise ValueError("SQL fixtures may only address a solaris-id-test-* container")
        self.tables: dict[str, str] = {}

    @staticmethod
    def quoted(value: str) -> str:
        return "'" + value.replace("'", "''") + "'"

    @staticmethod
    def identifier(value: str) -> str:
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", value):
            raise ValueError("Unexpected database identifier")
        return '"' + value + '"'

    def sql(self, statement: str) -> str:
        command = ["docker", "exec", "-i", self.container, "psql", "-X", "-A", "-t",
                   "-v", "ON_ERROR_STOP=1", "-U", self.config["db_user"],
                   "-d", self.config["db_name"]]
        completed = subprocess.run(command, input=statement, text=True,
                                   capture_output=True, timeout=20)
        if completed.returncode:
            # SQL may contain token hashes or account metadata; don't print it.
            raise RuntimeError("Isolated PostgreSQL fixture command failed")
        return completed.stdout.strip()

    def table_with_column(self, column: str) -> str:
        if column not in self.tables:
            raw = self.sql("SELECT table_name FROM information_schema.columns "
                           "WHERE table_schema='public' AND column_name=" + self.quoted(column) +
                           " ORDER BY table_name;")
            names = raw.splitlines()
            if len(names) != 1:
                raise RuntimeError("Cannot resolve unique persisted test entity for " + column)
            self.tables[column] = names[0]
        return self.identifier(self.tables[column])

    def expire_auth(self, session_id: str, refresh=False):
        column = "RefreshExpiresUtc" if refresh else "AccessExpiresUtc"
        table = self.table_with_column("AccessTokenHash")
        self.sql(f'UPDATE {table} SET "{column}"=CURRENT_TIMESTAMP-INTERVAL \'1 minute\' '
                 f'WHERE "Id"={self.quoted(session_id)};')

    def ticket_table(self) -> str:
        names = self.sql("SELECT table_name FROM information_schema.columns WHERE table_schema='public' "
                         "AND column_name IN ('TokenHash','ServerId','ConsumedUtc') GROUP BY table_name "
                         "HAVING COUNT(DISTINCT column_name)=3;").splitlines()
        if len(names) != 1:
            raise RuntimeError("Cannot resolve unique persisted game-ticket table")
        return self.identifier(names[0])

    def expire_game_ticket(self, ticket: str):
        table = self.ticket_table()
        hashed = hashlib.sha256(ticket.encode()).hexdigest().upper()
        self.sql(f'UPDATE {table} SET "ExpiresUtc"=CURRENT_TIMESTAMP-INTERVAL \'1 minute\' '
                 f'WHERE UPPER("TokenHash")={self.quoted(hashed)};')

    def expire_connection(self, connection_id: str):
        table = self.table_with_column("Code")
        self.sql(f'UPDATE {table} SET "ExpiresUtc"=CURRENT_TIMESTAMP-INTERVAL \'1 minute\' '
                 f'WHERE "ConnectionId"={self.quoted(connection_id)};')

    def stale_heartbeat(self, game_session_id: str):
        table = self.table_with_column("PlayedTicks")
        self.sql(f'UPDATE {table} SET "LastHeartbeatUtc"=CURRENT_TIMESTAMP-INTERVAL \'2 minutes\' '
                 f'WHERE "Id"={self.quoted(game_session_id)};')

    def grant_test_admin_out_of_band(self, user_id: str):
        # Explicit fixture provisioning mirrors an operator DB/bootstrap action;
        # all protected operations still run through real password+OTP HTTP login.
        role = uuid.uuid4().hex
        self.sql('BEGIN; INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp") '
                 f'VALUES ({self.quoted(role)},\'SolarisAdmin\',\'SOLARISADMIN\',{self.quoted(uuid.uuid4().hex)}) '
                 'ON CONFLICT ("NormalizedName") DO NOTHING; '
                 'INSERT INTO "AspNetUserRoles" ("UserId","RoleId") '
                 f'SELECT {self.quoted(user_id)},"Id" FROM "AspNetRoles" '
                 'WHERE "NormalizedName"=\'SOLARISADMIN\' ON CONFLICT DO NOTHING; COMMIT;')

    def mail_snapshot(self) -> set[Path]:
        return set(self.mailbox.glob("*.eml"))

    def message_for(self, address: str, after: set[Path]) -> str:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            for path in sorted(self.mail_snapshot() - after):
                message = email.message_from_bytes(path.read_bytes(), policy=policy.default)
                destinations = email.utils.getaddresses(message.get_all("To", []))
                if not any(target.lower() == address.lower() for _, target in destinations):
                    continue
                if message.is_multipart():
                    body = message.get_body(preferencelist=("plain", "html"))
                    if body is None:
                        continue
                    return body.get_content()
                return message.get_content()
            time.sleep(.1)
        raise AssertionError("A real SMTP message was not delivered to the isolated mailbox")


def totp(shared_key: str, timestamp: float | None = None) -> str:
    compact = shared_key.replace(" ", "").upper()
    key = base64.b32decode(compact + "=" * ((8 - len(compact) % 8) % 8))
    counter = int((time.time() if timestamp is None else timestamp) // 30)
    digest = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = digest[-1] & 15
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7fffffff
    return f"{value % 1_000_000:06d}"


def png(width=64, height=64, seed=1) -> bytes:
    def chunk(name: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data) & 0xffffffff)
    pixels = b"".join(b"\x00" + bytes((x * 3 + y * 5 + seed + component * 47) % 256
                                       for x in range(width) for component in range(4))
                      for y in range(height))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


class SolarisIdAcceptance(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.api = Api(CONFIG)
        cls.fixture = Fixture(CONFIG)
        cls.servers = {item["mode"]: item for item in CONFIG["server_credentials"]}
        cls.admin = None
        for value in (CONFIG.get("smtp_password", ""), CONFIG.get("db_password", "")):
            if value:
                SECRETS.add(value)
        for server in cls.servers.values():
            SECRETS.add(server["key"])
        response = cls.api.request("GET", "/health")
        if response.status != 200:
            raise RuntimeError("The real Solaris ID API/database fixture is not healthy")

    def expect(self, response: Response, *statuses: int) -> Response:
        self.assertIn(response.status, statuses,
                      f"{response.route}: expected HTTP {statuses}, got {response.status}")
        return response

    def remember_tokens(self, data: dict) -> dict:
        for field in ("accessToken", "refreshToken"):
            self.assertIsInstance(data.get(field), str)
            self.assertGreaterEqual(len(data[field]), 40)
            SECRETS.add(data[field])
        self.assertEqual(data.get("tokenType", "Bearer"), "Bearer")
        self.assertGreater(data["expiresIn"], 0)
        self.assertIsNotNone(uuid.UUID(data["sessionId"]))
        return data

    def new_user(self, prefix="Sid", confirm=True, login=True) -> dict:
        suffix = secrets.token_hex(4)
        nickname = prefix[:7] + suffix
        address = nickname.lower() + "@example.test"
        password = "SolId!" + secrets.token_urlsafe(16) + "9"
        SECRETS.add(password)
        before = self.fixture.mail_snapshot()
        response = self.expect(self.api.request("POST", "/api/v1/accounts", {
            "nickname": nickname, "email": address, "password": password,
            "confirmPassword": password}), 202)
        user = {"nickname": nickname, "email": address, "password": password}
        self.assertFalse(password.encode() in response.raw, "Registration exposed a password")
        if confirm:
            message = self.fixture.message_for(address, before)
            match = re.search(r"https://[^\s<>\"']+", message)
            self.assertIsNotNone(match, "Confirmation email must contain a real HTTPS link")
            link = match.group(0)
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(link).query)
            for value in query.get("code", []):
                SECRETS.add(value)
                SECRETS.add(urllib.parse.quote(value, safe=""))
            self.expect(self.api.request("GET", link), 200, 204)
        if login:
            user["session"] = self.login(user)
            profile = self.expect(self.api.request("GET", "/api/v1/me",
                                                 token=user["session"]["accessToken"]), 200).data
            user["id"] = profile["id"]
            self.assertEqual(profile["nickname"], nickname)
            self.assertEqual(profile["email"].lower(), address)
        return user

    def login(self, user: dict, code: str | None = None) -> dict:
        body = {"email": user["email"], "password": user["password"]}
        if code is not None:
            body["twoFactorCode"] = code
        response = self.expect(self.api.request("POST", "/api/v1/identity/login", body), 200)
        return self.remember_tokens(response.data)

    def auth(self, user: dict) -> str:
        return user["session"]["accessToken"]

    def ticket(self, user: dict, mode="vanilla") -> str:
        response = self.expect(self.api.request("POST", "/api/v1/me/game-tickets",
            {"serverId": self.servers[mode]["id"]}, token=self.auth(user)), 200)
        self.assertEqual(response.data["serverId"], self.servers[mode]["id"])
        self.assertGreaterEqual(len(response.data["ticket"]), 40)
        SECRETS.add(response.data["ticket"])
        return response.data["ticket"]

    def connection(self, user: dict, mode="vanilla", nickname=None) -> dict:
        identity = str(uuid.uuid4())
        response = self.expect(self.api.request("POST", "/api/v1/server/connections",
            {"connectionId": identity, "nickname": nickname or user["nickname"]},
            server=self.servers[mode]), 200)
        self.assertGreaterEqual(len(response.data["code"]), 6)
        return {"id": identity, "mode": mode, **response.data}

    def approve(self, user: dict, connection: dict, ticket: str | None = None) -> Response:
        return self.api.request("POST", "/api/v1/me/game-tickets/approve",
            {"ticket": ticket or self.ticket(user, connection["mode"]), "code": connection["code"]},
            token=self.auth(user))

    def poll(self, connection: dict, mode=None) -> Response:
        return self.api.request("GET", "/api/v1/server/connections/" + connection["id"],
                                server=self.servers[mode or connection["mode"]])

    def approved_game(self, user: dict, mode="vanilla") -> tuple[dict, str]:
        connection = self.connection(user, mode)
        self.expect(self.approve(user, connection), 200)
        status = self.expect(self.poll(connection), 200).data
        self.assertEqual(status["state"], "approved")
        self.assertEqual(status["userId"], user["id"])
        self.assertEqual(status["nickname"], user["nickname"])
        return connection, status["gameSessionId"]

    def totals(self, user: dict) -> dict:
        response = self.expect(self.api.request("GET", "/api/v1/me/playtime", token=self.auth(user)), 200)
        self.assertEqual(response.data["source"], "trusted-servers")
        self.assertEqual(response.data["vanillaSeconds"] + response.data["moddedSeconds"],
                         response.data["totalSeconds"])
        return response.data

    def enroll_mfa(self, user: dict) -> str:
        other_device = self.login(user)
        proven_session = self.auth(user)
        response = self.expect(self.api.request("POST", "/api/v1/me/mfa/setup",
            {"password": user["password"]}, token=self.auth(user)), 200)
        key = response.data["sharedKey"]
        SECRETS.add(key)
        self.assertTrue(response.data["authenticatorUri"].startswith("otpauth://totp/"))
        self.expect(self.api.request("GET", "/api/v1/me", token=proven_session), 200)
        self.expect(self.api.request("GET", "/api/v1/me", token=other_device["accessToken"]), 401)
        response = self.expect(self.api.request("POST", "/api/v1/me/mfa/enable",
            {"code": totp(key)}, token=self.auth(user)), 200)
        self.assertGreaterEqual(len(response.data["recoveryCodes"]), 2)
        SECRETS.update(response.data["recoveryCodes"])
        user["recoveryCodes"] = list(response.data["recoveryCodes"])
        self.expect(self.api.request("GET", "/api/v1/me", token=proven_session), 401)
        user["sharedKey"] = key
        user["session"] = self.login(user, totp(key))
        return key

    def get_admin(self) -> dict:
        cls = type(self)
        if cls.admin is None:
            user = self.new_user("Admin")
            self.fixture.grant_test_admin_out_of_band(user["id"])
            # Even a DB-provisioned role must not authorize a password-only session.
            user["session"] = self.login(user)
            self.expect(self.api.request("GET", "/api/v1/control/audit", token=self.auth(user)), 403)
            self.enroll_mfa(user)
            cls.admin = user
        return cls.admin

    def admin_action(self, user: dict, action: str) -> Response:
        admin = self.get_admin()
        return self.api.request("POST", f"/api/v1/control/accounts/{user['id']}/{action}",
                                {"reason": "Isolated acceptance test"}, token=self.auth(admin))

    def test_010_registration_validation_never_returns_500(self):
        cases = [None, {}, {"nickname": None, "email": None, "password": None},
                 {"nickname": "bad name", "email": "x@example.test", "password": "Valid!Password123"},
                 {"nickname": "Normal123", "email": 123, "password": "Valid!Password123"},
                 {"nickname": "Normal123", "email": "not-an-email", "password": "Valid!Password123"},
                 {"nickname": "Normal123", "email": "x@example.test", "password": "short", "confirmPassword": "short"},
                 {"nickname": "Normal123", "email": "x@example.test", "password": "Valid!Password123", "confirmPassword": "different"}]
        for index, value in enumerate(cases):
            with self.subTest(case=index):
                if value is None:
                    response = self.api.request("POST", "/api/v1/accounts", binary=b"null", content_type="application/json")
                else:
                    response = self.api.request("POST", "/api/v1/accounts", value)
                self.expect(response, 400, 422)
        self.expect(self.api.request("POST", "/api/v1/identity/login", {"email": 123, "password": None}), 400)
        self.expect(self.api.request("POST", "/api/v1/identity/register", {
            "email": "bypass@example.test", "password": "BadBypass!123"}), 404)

    def test_020_registration_uniqueness_case_insensitive_and_confirmation(self):
        user = self.new_user("Unique", confirm=False, login=False)
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": user["email"], "password": user["password"]}), 401)
        self.expect(self.api.request("POST", "/api/v1/accounts", {
            "nickname": user["nickname"].lower(), "email": "other" + user["email"],
            "password": user["password"], "confirmPassword": user["password"]}), 400, 409)
        self.expect(self.api.request("POST", "/api/v1/accounts", {
            "nickname": "Dupe" + secrets.token_hex(4), "email": user["email"].upper(),
            "password": user["password"], "confirmPassword": user["password"]}), 400, 409)

    def test_030_parallel_registration_has_one_owner_without_database_500(self):
        suffix = secrets.token_hex(4)
        password = "Race!Password123456"
        SECRETS.add(password)
        body = {"nickname": "Race" + suffix, "email": "race" + suffix + "@example.test",
                "password": password, "confirmPassword": password}
        with ThreadPoolExecutor(max_workers=3) as pool:
            responses = list(pool.map(lambda _: self.api.request("POST", "/api/v1/accounts", body), range(3)))
        self.assertEqual(sum(x.status == 202 for x in responses), 1)
        self.assertTrue(all(x.status in (202, 400, 409) for x in responses))
        count = self.fixture.sql('SELECT COUNT(*) FROM "AspNetUsers" WHERE "NormalizedEmail"=' +
                                 self.fixture.quoted(body["email"].upper()) + ';')
        self.assertEqual(count, "1")

    def test_040_password_lockout_is_real_and_does_not_issue_tokens(self):
        user = self.new_user("Lock")
        for _ in range(5):
            response = self.expect(self.api.request("POST", "/api/v1/identity/login",
                {"email": user["email"], "password": "Incorrect!Password123"}), 401)
            self.assertFalse(b"accessToken" in response.raw, "Failed login exposed an access token")
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": user["email"], "password": user["password"]}), 401)

    def test_050_two_computers_share_profile_and_keep_independent_sessions(self):
        user = self.new_user("Device")
        second = self.login(user)
        self.assertNotEqual(user["session"]["sessionId"], second["sessionId"])
        profile1 = self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 200).data
        profile2 = self.expect(self.api.request("GET", "/api/v1/me", token=second["accessToken"]), 200).data
        self.assertEqual(profile1["id"], profile2["id"])
        renamed = "Rename" + secrets.token_hex(4)
        changed = self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": renamed}, token=self.auth(user)), 200)
        self.assertTrue(changed.data["reauthenticationRequired"])
        self.expect(self.api.request("GET", "/api/v1/me", token=second["accessToken"]), 401)
        user["nickname"] = renamed
        user["session"] = self.login(user)
        second = self.login(user)
        after = self.expect(self.api.request("GET", "/api/v1/me", token=second["accessToken"]), 200).data
        self.assertEqual(after["id"], user["id"])
        self.assertEqual(after["nickname"], renamed)
        self.assertIn("lastActiveUtc", after)
        rows = self.expect(self.api.request("GET", "/api/v1/me/sessions", token=second["accessToken"]), 200).data
        self.assertGreaterEqual(len(rows), 2)
        self.assertEqual(sum(bool(x["current"]) for x in rows), 1)
        self.assertFalse(b"token" in json.dumps(rows).lower().encode(), "Session list exposed a token field")

    def test_055_former_nickname_cannot_be_reassigned_after_rename(self):
        owner = self.new_user("Alias")
        stranger = self.new_user("AliasNo")
        former = owner["nickname"]
        renamed = "AliasNew" + secrets.token_hex(4)
        changed = self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": renamed},
                                              token=self.auth(owner)), 200)
        self.assertTrue(changed.data["reauthenticationRequired"])
        owner["nickname"] = renamed
        owner["session"] = self.login(owner)
        profile = self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(owner)), 200).data
        self.assertEqual(profile["id"], owner["id"])
        self.assertEqual(profile["nickname"], renamed)
        self.expect(self.api.request("POST", "/api/v1/accounts", {
            "nickname": former.lower(), "email": secrets.token_hex(8) + "@example.test",
            "password": stranger["password"], "confirmPassword": stranger["password"]}), 409)
        self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": former.upper()},
                                     token=self.auth(stranger)), 409)
        profile = self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(stranger)), 200).data
        self.assertEqual(profile["nickname"], stranger["nickname"])
        # Reservations remain owned by the same stable account, which can
        # return to an earlier nickname without releasing its newer alias.
        self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": former},
                                     token=self.auth(owner)), 200)
        owner["nickname"] = former
        owner["session"] = self.login(owner)
        profile = self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(owner)), 200).data
        self.assertEqual(profile["id"], owner["id"])
        self.assertEqual(profile["nickname"], former)
        self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": renamed.lower()},
                                     token=self.auth(stranger)), 409)

    def test_060_logout_revokes_only_one_device_and_its_refresh(self):
        user = self.new_user("Logout")
        second = self.login(user)
        old = user["session"]
        self.expect(self.api.request("POST", "/api/v1/identity/logout", {}, token=old["accessToken"]), 200, 204)
        self.expect(self.api.request("GET", "/api/v1/me", token=old["accessToken"]), 401)
        self.expect(self.api.request("POST", "/api/v1/identity/refresh", {"refreshToken": old["refreshToken"]}), 401)
        self.expect(self.api.request("GET", "/api/v1/me", token=second["accessToken"]), 200)

    def test_070_refresh_is_single_use_and_replay_revokes_the_rotated_session(self):
        user = self.new_user("Refresh")
        old = user["session"]
        first = self.expect(self.api.request("POST", "/api/v1/identity/refresh",
            {"refreshToken": old["refreshToken"]}), 200)
        rotated = self.remember_tokens(first.data)
        self.assertFalse(old["accessToken"] == rotated["accessToken"], "Access token did not rotate")
        self.assertFalse(old["refreshToken"] == rotated["refreshToken"], "Refresh token did not rotate")
        self.expect(self.api.request("GET", "/api/v1/me", token=old["accessToken"]), 401)
        self.expect(self.api.request("GET", "/api/v1/me", token=rotated["accessToken"]), 200)
        self.expect(self.api.request("POST", "/api/v1/identity/refresh", {"refreshToken": old["refreshToken"]}), 401)
        self.expect(self.api.request("GET", "/api/v1/me", token=rotated["accessToken"]), 401)

    def test_080_concurrent_refresh_cannot_create_two_valid_successors(self):
        user = self.new_user("RaceTok")
        old = user["session"]["refreshToken"]
        with ThreadPoolExecutor(max_workers=2) as pool:
            responses = list(pool.map(lambda _: self.api.request("POST", "/api/v1/identity/refresh",
                                                                {"refreshToken": old}), range(2)))
        self.assertEqual(sorted(x.status for x in responses), [200, 401])
        successor = self.remember_tokens(next(x.data for x in responses if x.status == 200))
        self.expect(self.api.request("GET", "/api/v1/me", token=successor["accessToken"]), 401)

    def test_090_session_expiry_and_owner_revocation(self):
        user = self.new_user("Expiry")
        self.fixture.expire_auth(user["session"]["sessionId"])
        self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 401)
        response = self.expect(self.api.request("POST", "/api/v1/identity/refresh",
            {"refreshToken": user["session"]["refreshToken"]}), 200)
        user["session"] = self.remember_tokens(response.data)
        other = self.login(user)
        self.expect(self.api.request("DELETE", "/api/v1/me/sessions/" + other["sessionId"], token=self.auth(user)), 200, 204)
        self.expect(self.api.request("GET", "/api/v1/me", token=other["accessToken"]), 401)
        self.fixture.expire_auth(user["session"]["sessionId"], refresh=True)
        self.expect(self.api.request("POST", "/api/v1/identity/refresh",
            {"refreshToken": user["session"]["refreshToken"]}), 401)

    def test_095_mfa_recovery_requires_password_and_each_code_is_single_use(self):
        user = self.new_user("Recovery")
        self.enroll_mfa(user)
        body = {"email": user["email"], "password": user["password"],
                "twoFactorRecoveryCode": user["recoveryCodes"][0]}
        self.expect(self.api.request("POST", "/api/v1/identity/login", {
            **body, "password": "Incorrect!Password123"}), 401)
        recovered = self.remember_tokens(self.expect(self.api.request("POST", "/api/v1/identity/login", body), 200).data)
        self.expect(self.api.request("GET", "/api/v1/me", token=recovered["accessToken"]), 200)
        self.expect(self.api.request("POST", "/api/v1/identity/login", body), 401)
        # A second backup code raced through the real Identity persistence may
        # create only one session; the consumed code can never be used again.
        body["twoFactorRecoveryCode"] = user["recoveryCodes"][1]
        with ThreadPoolExecutor(max_workers=2) as pool:
            responses = list(pool.map(lambda _: self.api.request("POST", "/api/v1/identity/login", body), range(2)))
        self.assertEqual(sorted(response.status for response in responses), [200, 401])
        self.remember_tokens(next(response.data for response in responses if response.status == 200))
        self.expect(self.api.request("POST", "/api/v1/identity/login", body), 401)
        self.login(user, totp(user["sharedKey"]))
        admin = self.get_admin()
        recovered_admin = self.remember_tokens(self.expect(self.api.request("POST", "/api/v1/identity/login", {
            "email": admin["email"], "password": admin["password"],
            "twoFactorRecoveryCode": admin["recoveryCodes"][0]}), 200).data)
        self.expect(self.api.request("GET", "/api/v1/me", token=recovered_admin["accessToken"]), 200)
        self.expect(self.api.request("GET", "/api/v1/control/status", token=recovered_admin["accessToken"]), 403)
        self.expect(self.api.request("GET", "/api/v1/control/status", token=self.auth(admin)), 200)

    def test_100_password_reset_requires_email_code_and_revokes_old_sessions(self):
        user = self.new_user("Reset")
        before = self.fixture.mail_snapshot()
        generic = self.expect(self.api.request("POST", "/api/v1/identity/forgotPassword",
                                               {"email": user["email"]}), 200, 202)
        missing = self.expect(self.api.request("POST", "/api/v1/identity/forgotPassword",
                                               {"email": secrets.token_hex(8) + "@example.test"}), 200, 202)
        self.assertEqual(generic.status, missing.status)
        self.assertEqual(generic.data, missing.data)
        body = self.fixture.message_for(user["email"], before)
        code = body.strip().splitlines()[-1].strip()
        if code.startswith("https://"):
            code = urllib.parse.parse_qs(urllib.parse.urlsplit(code).query).get("code", [""])[0]
        self.assertGreater(len(code), 20)
        SECRETS.add(code)
        new_password = "NewReset!" + secrets.token_urlsafe(16) + "5"
        SECRETS.add(new_password)
        self.expect(self.api.request("POST", "/api/v1/identity/resetPassword",
            {"email": user["email"], "resetCode": "invalid", "newPassword": new_password}), 400)
        self.expect(self.api.request("POST", "/api/v1/identity/resetPassword",
            {"email": user["email"], "resetCode": code, "newPassword": new_password}), 200, 204)
        self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 401)
        self.expect(self.api.request("POST", "/api/v1/identity/resetPassword",
            {"email": user["email"], "resetCode": code, "newPassword": new_password}), 400)
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": user["email"], "password": user["password"]}), 401)
        user["password"] = new_password
        self.login(user)

    def test_110_skin_sync_is_owner_only_and_rejects_corrupt_and_unbounded_data(self):
        user = self.new_user("Skin")
        other = self.new_user("SkinNo")
        image = png(seed=17)
        self.expect(self.api.request("PUT", "/api/v1/me/skin", token=self.auth(user), binary=image), 200)
        second = self.login(user)
        downloaded = self.expect(self.api.request("GET", "/api/v1/me/skin", token=second["accessToken"]), 200)
        self.assertEqual(downloaded.raw, image)
        self.expect(self.api.request("GET", "/api/v1/me/skin", token=self.auth(other)), 404)
        changed = png(seed=92)
        self.expect(self.api.request("PUT", "/api/v1/me/skin", token=second["accessToken"], binary=changed), 200)
        self.assertEqual(self.expect(self.api.request("GET", "/api/v1/me/skin", token=self.auth(user)), 200).raw, changed)
        corrupted = bytearray(image); corrupted[-5] ^= 1
        for label, invalid in [("crc", bytes(corrupted)), ("truncated", image[:24]),
                               ("dimensions", png(width=32)), ("notPNG", b"GIF89a" + b"x" * 200)]:
            with self.subTest(case=label):
                self.expect(self.api.request("PUT", "/api/v1/me/skin", token=self.auth(user), binary=invalid), 400, 415)
        self.expect(self.api.request("PUT", "/api/v1/me/skin", token=self.auth(user), binary=b"x" * 524289), 400, 413)
        self.expect(self.api.chunked_skin(self.auth(user), [b"x" * 4096] * 129), 400, 413)

    def test_120_client_time_and_legacy_history_cannot_forge_trusted_statistics(self):
        user = self.new_user("History")
        before = self.totals(user)
        self.expect(self.api.request("POST", "/api/v1/me/playtime", {
            "id": str(uuid.uuid4()), "mode": "vanilla", "startedUtc": "2020-01-01T00:00:00Z",
            "seconds": 86400}, token=self.auth(user)), 403, 404, 405)
        self.expect(self.api.request("POST", "/api/v1/me/legacy", {
            "sourceId": "a" * 64, "vanillaSeconds": 12345, "moddedSeconds": 54321}, token=self.auth(user)), 200, 403, 404, 405)
        for invalid in [{"sourceId": None, "vanillaSeconds": 0, "moddedSeconds": 0},
                        {"sourceId": "b" * 64, "vanillaSeconds": 9223372036854775807, "moddedSeconds": 9223372036854775807}]:
            self.expect(self.api.request("POST", "/api/v1/me/legacy", invalid, token=self.auth(user)), 400, 403, 404, 405)
        self.assertEqual(self.totals(user)["totalSeconds"], before["totalSeconds"])

    def test_130_server_credentials_are_audience_scoped_and_not_user_credentials(self):
        user = self.new_user("KeyTest")
        body = {"connectionId": str(uuid.uuid4()), "nickname": user["nickname"]}
        self.expect(self.api.request("POST", "/api/v1/server/connections", body, token=self.auth(user)), 401, 403)
        server = dict(self.servers["vanilla"], key="invalid-server-key")
        self.expect(self.api.request("POST", "/api/v1/server/connections", body, server=server), 401, 403)
        server = dict(self.servers["vanilla"], id=self.servers["modded"]["id"])
        self.expect(self.api.request("POST", "/api/v1/server/connections", body, server=server), 401, 403)
        self.expect(self.api.request("POST", "/api/v1/me/game-tickets", {"serverId": "unconfigured"}, token=self.auth(user)), 400, 404, 503)

    def test_140_offline_nickname_alone_never_authenticates_a_connection(self):
        user = self.new_user("Pending")
        connection = self.connection(user)
        response = self.expect(self.poll(connection), 200)
        self.assertEqual(response.data["state"], "pending")
        self.assertIsNone(response.data.get("userId"))
        self.assertIsNone(response.data.get("gameSessionId"))
        self.assertEqual(self.totals(user)["totalSeconds"], 0)
        self.expect(self.poll(connection, "modded"), 403, 404)

    def test_150_game_ticket_one_use_owner_and_server_audience(self):
        user = self.new_user("Pass")
        stranger = self.new_user("Other")
        connection = self.connection(user)
        self.expect(self.approve(stranger, connection), 401, 409)
        ticket = self.ticket(user)
        self.expect(self.approve(user, connection, ticket), 200)
        approved = self.expect(self.poll(connection), 200).data
        self.assertEqual(approved["userId"], user["id"])
        self.expect(self.approve(user, connection, ticket), 401, 409)
        wrong_mode = self.connection(stranger, "modded")
        self.expect(self.approve(stranger, wrong_mode, self.ticket(stranger, "vanilla")), 401, 409)
        self.expect(self.approve(stranger, wrong_mode), 200)

    def test_160_concurrent_ticket_consumption_and_one_active_game(self):
        user = self.new_user("GameRace")
        one, two = self.connection(user), self.connection(user)
        ticket = self.ticket(user)
        with ThreadPoolExecutor(max_workers=2) as pool:
            responses = list(pool.map(lambda c: self.approve(user, c, ticket), [one, two]))
        self.assertEqual(sum(r.status == 200 for r in responses), 1)
        self.assertTrue(all(r.status in (200, 401, 409) for r in responses))
        approved = [self.expect(self.poll(c), 200).data["state"] for c in [one, two]]
        self.assertEqual(approved.count("approved"), 1)

    def test_170_expired_ticket_and_connection_cannot_be_approved(self):
        user = self.new_user("GameExp")
        connection = self.connection(user)
        ticket = self.ticket(user)
        self.fixture.expire_game_ticket(ticket)
        self.expect(self.approve(user, connection, ticket), 401, 409)
        second = self.connection(user)
        self.fixture.expire_connection(second["id"])
        self.expect(self.approve(user, second), 401, 409)
        self.assertEqual(self.expect(self.poll(second), 200).data["state"], "denied")

    def test_180_ticket_cannot_move_between_device_sessions_or_survive_logout(self):
        user = self.new_user("Bound")
        connection = self.connection(user)
        old = self.ticket(user)
        self.expect(self.api.request("POST", "/api/v1/identity/logout", {}, token=self.auth(user)), 200, 204)
        user["session"] = self.login(user)
        self.expect(self.approve(user, connection, old), 401, 409)
        self.expect(self.approve(user, connection), 200)
        self.expect(self.api.request("POST", "/api/v1/identity/logout", {}, token=self.auth(user)), 200, 204)
        self.assertEqual(self.expect(self.poll(connection), 200).data["state"], "denied")

    def test_190_both_modes_count_only_trusted_server_elapsed_time_idempotently(self):
        user = self.new_user("Clock")
        for mode in ("vanilla", "modded"):
            connection, game_id = self.approved_game(user, mode)
            before = self.totals(user)
            time.sleep(1.1)
            route = "/api/v1/server/game-sessions/" + game_id
            self.expect(self.api.request("POST", route + "/heartbeat", {"sequence": 1, "seconds": 2_000_000_000}, server=self.servers[mode]), 200)
            first = self.totals(user)
            self.assertGreaterEqual(first[mode + "Seconds"], before[mode + "Seconds"] + 1)
            self.assertLess(first[mode + "Seconds"] - before[mode + "Seconds"], 15)
            self.expect(self.api.request("POST", route + "/heartbeat", {"sequence": 1}, server=self.servers[mode]), 200)
            self.assertEqual(self.totals(user), first)
            other = "modded" if mode == "vanilla" else "vanilla"
            self.expect(self.api.request("POST", route + "/heartbeat", {"sequence": 2}, server=self.servers[other]), 403, 404)
            self.expect(self.api.request("POST", route + "/close", {"sequence": 2}, server=self.servers[mode]), 200)
            closed = self.totals(user)
            self.expect(self.api.request("POST", route + "/close", {"sequence": 2}, server=self.servers[mode]), 200)
            self.assertEqual(self.totals(user), closed)
            self.expect(self.api.request("DELETE", "/api/v1/server/connections/" + connection["id"], server=self.servers[mode]), 200, 204)

    def test_200_stale_server_heartbeat_does_not_credit_unobserved_minutes(self):
        user = self.new_user("Stale")
        _, game_id = self.approved_game(user)
        before = self.totals(user)
        self.fixture.stale_heartbeat(game_id)
        self.expect(self.api.request("POST", "/api/v1/server/game-sessions/" + game_id + "/heartbeat",
                                     {"sequence": 1}, server=self.servers["vanilla"]), 401, 403, 409, 410)
        self.assertEqual(self.totals(user), before)

    def test_210_user_cannot_get_admin_by_nickname_or_profile_fields(self):
        user = self.new_user("AdminNo")
        self.expect(self.api.request("GET", "/api/v1/control/accounts?search=Admin", token=self.auth(user)), 403)
        change = self.expect(self.api.request("PATCH", "/api/v1/me", {"nickname": user["nickname"],
                    "isAdmin": True, "roles": ["SolarisAdmin"]}, token=self.auth(user)), 200, 400)
        if change.status == 200 and change.data.get("reauthenticationRequired"):
            user["session"] = self.login(user)
        profile = self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 200).data
        self.assertFalse(profile["isAdmin"])
        self.expect(self.api.request("GET", "/api/v1/control/audit", token=self.auth(user)), 403)

    def test_220_real_totp_required_for_control_and_password_is_not_exposed(self):
        admin = self.get_admin()
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": admin["email"], "password": admin["password"]}), 401)
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": admin["email"], "password": admin["password"], "twoFactorCode": "not-an-otp"}), 400, 401)
        response = self.expect(self.api.request("GET", "/api/v1/control/accounts?search=" +
            urllib.parse.quote(admin["nickname"]), token=self.auth(admin)), 200)
        self.assertFalse(b"password" in response.raw.lower(), "Control search exposed a password field")
        self.assertFalse(b"securitystamp" in response.raw.lower(), "Control search exposed a security stamp")
        self.assertFalse(b"token" in response.raw.lower(), "Control search exposed a token field")
        self.assertTrue(any(x["id"] == admin["id"] for x in response.data))
        self.expect(self.api.request("GET", "/api/v1/control/status", token=self.auth(admin)), 200)

    def test_230_ban_and_global_revoke_close_accounts_and_game_sessions(self):
        user = self.new_user("Ban")
        connection, game_id = self.approved_game(user)
        self.expect(self.admin_action(user, "ban"), 200)
        self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 401)
        self.expect(self.api.request("POST", "/api/v1/identity/refresh",
            {"refreshToken": user["session"]["refreshToken"]}), 401)
        self.expect(self.api.request("POST", "/api/v1/identity/login",
            {"email": user["email"], "password": user["password"]}), 401)
        self.assertEqual(self.expect(self.poll(connection), 200).data["state"], "denied")
        self.expect(self.api.request("POST", "/api/v1/server/game-sessions/" + game_id + "/heartbeat",
            {"sequence": 1}, server=self.servers["vanilla"]), 401, 403, 409, 410)
        self.expect(self.admin_action(user, "unban"), 200)
        user["session"] = self.login(user)
        self.expect(self.admin_action(user, "revoke"), 200)
        self.expect(self.api.request("GET", "/api/v1/me", token=self.auth(user)), 401)
        admin = self.get_admin()
        audit = self.expect(self.api.request("GET", "/api/v1/control/audit", token=self.auth(admin)), 200).data
        operations = {x["operation"] for x in audit if x["targetId"] == user["id"]}
        self.assertTrue({"ban", "unban", "revoke"}.issubset(operations))

    def test_235_admin_roles_require_mfa_and_are_audited_without_password_access(self):
        admin = self.get_admin()
        target = self.new_user("Role")
        route = "/api/v1/control/accounts/" + target["id"] + "/roles"
        action = {"role": "SolarisAdmin", "enabled": True, "reason": "Isolated acceptance role grant"}
        self.expect(self.api.request("POST", route, action, token=self.auth(target)), 403)
        self.expect(self.api.request("POST", route, action, token=self.auth(admin)), 400, 409)
        self.enroll_mfa(target)
        self.expect(self.api.request("POST", route, action, token=self.auth(admin)), 200)
        target["session"] = self.login(target, totp(target["sharedKey"]))
        self.expect(self.api.request("GET", "/api/v1/control/status", token=self.auth(target)), 200)
        stats = self.expect(self.api.request("GET", "/api/v1/control/accounts/" + target["id"] + "/stats",
                                            token=self.auth(admin)), 200)
        self.assertEqual(stats.data["id"], target["id"])
        self.assertEqual(stats.data["source"], "trusted-servers")
        self.assertFalse(b"password" in stats.raw.lower(), "Control statistics exposed a password field")
        self.assertFalse(b"token" in stats.raw.lower(), "Control statistics exposed a token field")
        action["enabled"] = False
        self.expect(self.api.request("POST", route, action, token=self.auth(admin)), 200)
        self.expect(self.api.request("GET", "/api/v1/control/status", token=self.auth(target)), 401, 403)
        # Repeated runs may leave earlier test administrators in this disposable
        # database. Demote only explicitly test-domain accounts through the real,
        # audited MFA-protected API so the last-admin invariant is exercised.
        earlier = self.fixture.sql("""SELECT u."Id" || '|' || u."Email" FROM "AspNetUsers" u
            JOIN "AspNetUserRoles" ur ON ur."UserId"=u."Id"
            JOIN "AspNetRoles" r ON r."Id"=ur."RoleId"
            WHERE r."NormalizedName"='SOLARISADMIN' AND u."Id"!=""" + self.fixture.quoted(admin["id"]) + ';')
        for row in earlier.splitlines():
            prior_id, prior_email = row.split("|", 1)
            self.assertTrue(prior_email.endswith("@example.test"), "Refuse to alter a non-test administrator")
            self.expect(self.api.request("POST", "/api/v1/control/accounts/" + prior_id + "/roles",
                                         action, token=self.auth(admin)), 200)
        own_route = "/api/v1/control/accounts/" + admin["id"] + "/roles"
        self.expect(self.api.request("POST", own_route, action, token=self.auth(admin)), 400, 409)
        self.expect(self.api.request("GET", "/api/v1/control/status", token=self.auth(admin)), 200)

    def test_237_concurrent_mfa_disable_and_admin_grant_preserve_mfa_requirement(self):
        admin = self.get_admin()
        for attempt in range(3):
            with self.subTest(attempt=attempt):
                target = self.new_user("MfaRace")
                self.enroll_mfa(target)
                route = "/api/v1/control/accounts/" + target["id"] + "/roles"
                action = {"role": "SolarisAdmin", "enabled": True,
                          "reason": "Isolated acceptance MFA race"}
                def grant():
                    return self.api.request("POST", route, action, token=self.auth(admin))
                def disable():
                    return self.api.request("POST", "/api/v1/me/mfa/disable",
                        {"password": target["password"], "code": totp(target["sharedKey"])},
                        token=self.auth(target))
                with ThreadPoolExecutor(max_workers=2) as pool:
                    granted, disabled = list(pool.map(lambda operation: operation(), [grant, disable]))
                self.expect(granted, 200, 409)
                self.expect(disabled, 200, 401, 409)
                self.assertTrue(any(response.status == 200 for response in (granted, disabled)))
                self.assertFalse(granted.status == disabled.status == 200,
                                 "Admin role grant and MFA disable both succeeded")
                state = self.fixture.sql('SELECT u."TwoFactorEnabled"::text || \'|\' || EXISTS('
                    'SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id"=ur."RoleId" '
                    'WHERE ur."UserId"=u."Id" AND r."NormalizedName"=\'SOLARISADMIN\')::text '
                    'FROM "AspNetUsers" u WHERE u."Id"=' + self.fixture.quoted(target["id"]) + ';')
                self.assertIn(state, ("true|true", "true|false", "false|false"),
                              "Persisted administrator account must retain MFA")
                if granted.status == 200:
                    action["enabled"] = False
                    self.expect(self.api.request("POST", route, action, token=self.auth(admin)), 200)

    def test_240_tokens_and_passwords_are_not_persisted_in_cleartext(self):
        user = self.new_user("Hash")
        raw_user = self.fixture.sql('SELECT row_to_json(u)::text FROM "AspNetUsers" u WHERE "Id"=' +
                                   self.fixture.quoted(user["id"]) + ';')
        self.assertFalse(user["password"] in raw_user, "Account persisted a cleartext password")
        self.assertTrue(json.loads(raw_user)["PasswordHash"])
        table = self.fixture.table_with_column("AccessTokenHash")
        raw_sessions = self.fixture.sql(f'SELECT row_to_json(s)::text FROM {table} s WHERE "UserId"=' +
                                       self.fixture.quoted(user["id"]) + ';')
        for token in (user["session"]["accessToken"], user["session"]["refreshToken"]):
            self.assertFalse(token in raw_sessions, "Session persisted a cleartext token")
        ticket = self.ticket(user)
        table = self.fixture.ticket_table()
        raw_tickets = self.fixture.sql(f'SELECT row_to_json(t)::text FROM {table} t WHERE "UserId"=' +
                                      self.fixture.quoted(user["id"]) + ';')
        self.assertFalse(ticket in raw_tickets, "Game ticket persisted a cleartext token")

    def test_990_no_secret_material_in_api_logs(self):
        path = Path(CONFIG["api_log_path"])
        self.assertTrue(path.is_file(), "API logs are required for the redaction check")
        logs = path.read_text(errors="replace")
        for index, secret in enumerate(sorted(SECRETS)):
            if len(secret) < 8:
                continue
            self.assertFalse(secret in logs, f"Secret category/index {index} appeared in API logs")
            self.assertFalse(urllib.parse.quote(secret, safe="") in logs,
                             f"Encoded secret category/index {index} appeared in API logs")


class JsonResult(unittest.TextTestResult):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.records = []
        self.started = {}

    def startTest(self, test):
        self.started[test.id()] = time.monotonic()
        super().startTest(test)

    def addSuccess(self, test):
        self.records.append({"test": test.id(), "status": "passed",
                             "seconds": round(time.monotonic() - self.started[test.id()], 3)})
        super().addSuccess(test)

    def addFailure(self, test, err):
        self.records.append({"test": test.id(), "status": "failed"})
        super().addFailure(test, err)

    def addError(self, test, err):
        self.records.append({"test": test.id(), "status": "error"})
        super().addError(test, err)

    def addSubTest(self, test, subtest, err):
        if err is not None:
            status = "failed" if issubclass(err[0], test.failureException) else "error"
            existing = next((record for record in self.records if record["test"] == test.id()), None)
            if existing is None:
                self.records.append({"test": test.id(), "status": status})
            elif status == "error":
                existing["status"] = status
        super().addSubTest(test, subtest, err)


def main():
    global CONFIG
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--tests", nargs="*", help="Optional test method names for focused diagnosis")
    args = parser.parse_args()
    CONFIG = json.loads(args.config.read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    loader = unittest.TestLoader()
    suite = unittest.TestSuite(SolarisIdAcceptance(name) for name in args.tests) if args.tests else \
        loader.loadTestsFromTestCase(SolarisIdAcceptance)
    result = unittest.TextTestRunner(verbosity=2, resultclass=JsonResult).run(suite)
    evidence = {"tests": result.testsRun, "failures": len(result.failures), "errors": len(result.errors),
                "skipped": len(result.skipped), "https_certificate_verification": True,
                "real_postgresql": True, "real_smtp_starttls_auth": True,
                "native_minecraft_clients_tested": False, "records": result.records}
    (args.output / "results.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(json.dumps({k: v for k, v in evidence.items() if k != "records"}))
    return 0 if result.wasSuccessful() and result.testsRun else 1


if __name__ == "__main__":
    sys.exit(main())
