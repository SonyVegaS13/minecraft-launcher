package com.solaris.id.bridge;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManagerFactory;
import java.io.InputStream;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.security.cert.CertificateFactory;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.TimeUnit;

/** Integration coordinator supplies paths only; credentials/tickets are never CLI arguments. */
public final class RealApiProbe {
    public static void main(String[] args) {
        try {
            if (args.length != 2) throw new IllegalArgumentException();
            run(Path.of(args[0]), Path.of(args[1]));
            System.out.println("Solaris Java HTTPS protocol probe passed.");
        } catch (Exception failed) {
            // Never print exception payloads, authorization headers or private fixture configuration.
            System.err.println("Solaris Java HTTPS protocol probe failed.");
            System.exit(1);
        }
    }
    private static void run(Path configPath, Path directory) throws Exception {
        JsonObject configuration = JsonParser.parseString(Files.readString(configPath)).getAsJsonObject();
        JsonObject identity = JsonParser.parseString(Files.readString(directory.resolve("identity.json"))).getAsJsonObject();
        String nickname = identity.get("nickname").getAsString();
        UUID expectedUser = UUID.fromString(identity.get("userId").getAsString());
        JsonObject credential = configuration.getAsJsonArray("server_credentials").get(0).getAsJsonObject();
        String serverId = credential.get("id").getAsString();
        KeyStore trustStore = KeyStore.getInstance(KeyStore.getDefaultType());
        trustStore.load(null);
        try (InputStream stream = Files.newInputStream(Path.of(configuration.get("ca_file").getAsString()))) {
            trustStore.setCertificateEntry("integration-ca", CertificateFactory.getInstance("X.509").generateCertificate(stream));
        }
        TrustManagerFactory trust = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        trust.init(trustStore);
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(null, trust.getTrustManagers(), null);
        UUID connectionId = UUID.randomUUID();
        try (HttpBridgeApi api = new HttpBridgeApi(URI.create(configuration.get("base_url").getAsString()), serverId,
                credential.get("key").getAsString(), tls)) {
            try {
                BridgeApi.Challenge challenge = api.createConnection(connectionId, nickname).get(10, TimeUnit.SECONDS);
                JsonObject locator = new JsonObject();
                locator.addProperty("code", challenge.code());
                locator.addProperty("connectionId", connectionId.toString());
                locator.addProperty("serverId", serverId);
                Path temporary = directory.resolve("challenge.tmp");
                Files.writeString(temporary, locator.toString());
                Files.move(temporary, directory.resolve("challenge.json"));
                BridgeApi.Approval approval;
                Instant deadline = Instant.now().plusSeconds(80);
                do {
                    approval = api.poll(connectionId).get(10, TimeUnit.SECONDS);
                    if (approval.state() == BridgeApi.State.DENIED) throw new IllegalStateException();
                    if (approval.state() == BridgeApi.State.APPROVED) break;
                    Thread.sleep(250);
                } while (Instant.now().isBefore(deadline));
                if (approval.state() != BridgeApi.State.APPROVED || !expectedUser.equals(approval.userId())
                        || !nickname.equals(approval.nickname())) throw new IllegalStateException();
                api.heartbeat(approval.gameSessionId(), 1).get(10, TimeUnit.SECONDS);
                api.heartbeat(approval.gameSessionId(), 1).get(10, TimeUnit.SECONDS);
                api.closeSession(approval.gameSessionId(), 2).get(10, TimeUnit.SECONDS);
                api.closeSession(approval.gameSessionId(), 2).get(10, TimeUnit.SECONDS);
                api.cancelConnection(connectionId).get(10, TimeUnit.SECONDS);
                if (api.poll(connectionId).get(10, TimeUnit.SECONDS).state() != BridgeApi.State.DENIED)
                    throw new IllegalStateException();
            } finally {
                api.cancelConnection(connectionId).handle((result, error) -> null).get(10, TimeUnit.SECONDS);
            }
        }
    }
}
