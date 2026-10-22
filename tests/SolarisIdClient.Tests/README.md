# Solaris ID client transport unit tests

```sh
dotnet run --project tests/SolarisIdClient.Tests/SolarisIdClient.Tests.csproj -c Release
```

A dependency-free .NET8 console suite links the actual `SolarisIdTransport.cs` and `SolarisIdConfiguration.cs`. Its scripted `HttpMessageHandler` is used solely to force request concurrency and HTTP failure paths without a live service. These results **do not** validate account/password/session security on a real API, PostgreSQL, SMTP, Windows DPAPI, Paper, Forge or Minecraft clients. The independent integration suite covers the live API.

Checks include concurrent refresh success/refusal, proactive expiry, exactly one persistence callback, byte-identical request replay with preserved content headers, no repeated401 loops, preservation of403 role/MFA errors, strict HTTPS root origin and server-audience configuration, bounded configuration/payloads, and atomic rejection of malformed token responses. No real credentials are used or printed.
