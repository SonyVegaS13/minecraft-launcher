#!/usr/bin/env python3
"""Loopback-only STARTTLS/AUTH mailbox for real Solaris ID integration tests.

Not a production SMTP service. Credentials and captured email links are read/written
only in an external private fixture directory, never printed or committed.
"""
from __future__ import annotations

import argparse
import hmac
import json
import os
from pathlib import Path
import signal
import ssl
import threading
import uuid

from aiosmtpd.controller import Controller
from aiosmtpd.smtp import AuthResult, LoginPassword, SMTP, syntax


class CompatibleSMTP(SMTP):
    @syntax("AUTH <mechanism>")
    async def smtp_AUTH(self, arg: str) -> None:
        # System.Net.Mail emits "AUTH login", whereas aiosmtpd 1.4.6 looks
        # mechanisms up with a case-sensitive dictionary containing "LOGIN".
        # Normalize that identifier alone: the actual LOGIN/PLAIN exchange,
        # STARTTLS requirement and credential verification stay in aiosmtpd.
        if arg:
            mechanism, separator, initial_response = arg.partition(" ")
            arg = mechanism.upper() + separator + initial_response
        await super().smtp_AUTH(arg)


class CompatibleController(Controller):
    def factory(self):
        return CompatibleSMTP(self.handler, **self.SMTP_kwargs)


class Mailbox:
    def __init__(self, directory: Path):
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)

    async def handle_DATA(self, server, session, envelope):
        destination = self.directory / (uuid.uuid4().hex + ".eml")
        with destination.open("xb") as stream:
            stream.write(envelope.original_content)
        os.chmod(destination, 0o600)
        return "250 Message accepted into isolated test mailbox"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    args = parser.parse_args()
    config = json.loads(args.config.read_text())
    host = config.get("smtp_host", "localhost")
    if host not in ("localhost", "127.0.0.1", "::1"):
        parser.error("The test mailbox must bind to loopback only")
    os.umask(0o077)
    certificate = config.get("tls_cert", config.get("ca_file"))
    key = config["tls_key"]
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(certificate, key)
    expected_user = config["smtp_user"].encode()
    expected_password = config["smtp_password"].encode()

    def authenticate(server, session, envelope, mechanism, auth_data):
        success = isinstance(auth_data, LoginPassword) and \
            hmac.compare_digest(auth_data.login, expected_user) and \
            hmac.compare_digest(auth_data.password, expected_password)
        return AuthResult(success=success, handled=False)

    controller = CompatibleController(
        Mailbox(Path(config["mailbox_dir"])), hostname=host,
        port=int(config.get("smtp_port", 55252)), tls_context=context,
        require_starttls=True, auth_required=True,
        auth_require_tls=True, authenticator=authenticate,
    )
    controller.start()
    stopped = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda signum, frame: stopped.set())
    print("Isolated STARTTLS/AUTH test mailbox ready", flush=True)
    try:
        stopped.wait()
    finally:
        controller.stop()


if __name__ == "__main__":
    main()
