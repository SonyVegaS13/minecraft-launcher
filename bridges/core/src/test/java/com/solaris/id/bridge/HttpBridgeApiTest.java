package com.solaris.id.bridge;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.sun.net.httpserver.HttpsConfigurator;
import com.sun.net.httpserver.HttpsServer;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManagerFactory;
import java.io.InputStream;
import java.net.InetSocketAddress;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.KeyStore;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.*;

/** Real Java HTTPS IO tests with an ephemeral explicitly trusted localhost certificate. */
class HttpBridgeApiTest {
    @TempDir static Path directory;
    private static HttpsServer server;
    private static SSLContext trustContext;
    private static URI origin;
    private static final AtomicReference<String> response = new AtomicReference<>();
    private static final AtomicInteger status = new AtomicInteger(200);
    private static final List<Captured> captured = new CopyOnWriteArrayList<>();
    private static final String KEY = "unit-test-only-server-key-1234567890";
    record Captured(String method, String path, String authorization, String audience, String body) {}

    @BeforeAll static void start() throws Exception {
        Path keyStorePath = directory.resolve("localhost.p12");
        Process generation = new ProcessBuilder(Path.of(System.getProperty("java.home"), "bin", "keytool").toString(),
            "-genkeypair", "-alias", "localhost", "-keyalg", "RSA", "-keysize", "2048", "-validity", "2",
            "-dname", "CN=localhost", "-ext", "SAN=dns:localhost", "-storetype", "PKCS12",
            "-keystore", keyStorePath.toString(), "-storepass", "test-password", "-keypass", "test-password", "-noprompt")
            .redirectErrorStream(true).redirectOutput(directory.resolve("certificate-generation.log").toFile()).start();
        assertTrue(generation.waitFor(30, TimeUnit.SECONDS));
        assertEquals(0, generation.exitValue());
        KeyStore store = KeyStore.getInstance("PKCS12");
        try (InputStream stream = Files.newInputStream(keyStorePath)) { store.load(stream, "test-password".toCharArray()); }
        KeyManagerFactory keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        keys.init(store, "test-password".toCharArray());
        TrustManagerFactory trust = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        trust.init(store);
        SSLContext serverContext = SSLContext.getInstance("TLS");
        serverContext.init(keys.getKeyManagers(), null, null);
        trustContext = SSLContext.getInstance("TLS");
        trustContext.init(null, trust.getTrustManagers(), null);
        server = HttpsServer.create(new InetSocketAddress("localhost", 0), 0);
        server.setHttpsConfigurator(new HttpsConfigurator(serverContext));
        server.createContext("/", exchange -> {
            captured.add(new Captured(exchange.getRequestMethod(), exchange.getRequestURI().toString(),
                exchange.getRequestHeaders().getFirst("Authorization"), exchange.getRequestHeaders().getFirst("X-Solaris-Server"),
                new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8)));
            byte[] bytes = response.get().getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().set("Content-Type", "application/json; charset=utf-8");
            exchange.getResponseHeaders().set("Location", origin + "/must-not-follow");
            exchange.sendResponseHeaders(status.get(), bytes.length);
            try (var stream = exchange.getResponseBody()) { stream.write(bytes); }
        });
        server.start();
        origin = URI.create("https://localhost:" + server.getAddress().getPort());
    }
    @AfterAll static void stop() { if (server != null) server.stop(0); }

    private HttpBridgeApi client() { captured.clear(); status.set(200); return new HttpBridgeApi(origin, "vanilla-test", KEY, trustContext); }

    @Test void actualHttpsHeadersRoutesAndOnlyServerSequence() throws Exception {
        try (HttpBridgeApi api = client()) {
            UUID connection = UUID.randomUUID();
            UUID user = UUID.randomUUID();
            UUID session = UUID.randomUUID();
            response.set("{\"code\":\"ABCD234567\",\"expiresUtc\":\"2030-01-01T00:00:00Z\"}");
            assertEquals("ABCD234567", api.createConnection(connection, "Player_1").get(10, TimeUnit.SECONDS).code());
            response.set("{\"state\":\"approved\",\"userId\":\"" + user + "\",\"nickname\":\"Player_1\",\"gameSessionId\":\"" + session + "\"}");
            assertEquals(user, api.poll(connection).get(10, TimeUnit.SECONDS).userId());
            response.set("{\"state\":\"approved\"}");
            api.heartbeat(session, 1).get(10, TimeUnit.SECONDS);
            response.set("{\"state\":\"closed\"}");
            api.closeSession(session, 2).get(10, TimeUnit.SECONDS);
            api.cancelConnection(connection).get(10, TimeUnit.SECONDS);
            List<Captured> requests = new ArrayList<>(captured);
            assertEquals(5, requests.size());
            for (Captured request : requests) {
                assertEquals("SolarisServer " + KEY, request.authorization());
                assertEquals("vanilla-test", request.audience());
            }
            assertEquals("/api/v1/server/connections", requests.get(0).path());
            JsonObject join = JsonParser.parseString(requests.get(0).body()).getAsJsonObject();
            assertEquals(connection.toString(), join.get("connectionId").getAsString());
            assertEquals(2, join.size());
            assertEquals("GET", requests.get(1).method());
            assertEquals("/api/v1/server/connections/" + connection, requests.get(1).path());
            assertEquals("/api/v1/server/game-sessions/" + session + "/heartbeat", requests.get(2).path());
            assertEquals("{\"sequence\":1}", requests.get(2).body());
            assertEquals("{\"sequence\":2}", requests.get(3).body());
            assertEquals("DELETE", requests.get(4).method());
        }
    }
    @Test void malformedApprovalAndUnauthorizedResponsesFailClosed() throws Exception {
        try (HttpBridgeApi api = client()) {
            response.set("{\"state\":\"approved\",\"userId\":\"not-a-uuid\"}");
            assertThrows(Exception.class, () -> api.poll(UUID.randomUUID()).get(10, TimeUnit.SECONDS));
            response.set("{\"state\":true}");
            assertThrows(Exception.class, () -> api.poll(UUID.randomUUID()).get(10, TimeUnit.SECONDS));
            response.set("{\"state\":\"approved\"}"); status.set(401);
            assertThrows(Exception.class, () -> api.heartbeat(UUID.randomUUID(), 1).get(10, TimeUnit.SECONDS));
        }
    }
    @Test void redirectsAndOversizedBodiesAreRejected() throws Exception {
        try (HttpBridgeApi api = client()) {
            response.set("{}"); status.set(302);
            assertThrows(Exception.class, () -> api.poll(UUID.randomUUID()).get(10, TimeUnit.SECONDS));
            assertEquals(1, captured.size());
            status.set(200); response.set("{\"padding\":\"" + "x".repeat(17000) + "\"}");
            assertThrows(Exception.class, () -> api.poll(UUID.randomUUID()).get(10, TimeUnit.SECONDS));
        }
    }
    @Test void productionTrustRejectsUnknownCertificateAndOriginRejectsHttp() {
        assertThrows(IllegalArgumentException.class, () -> new HttpBridgeApi(URI.create("http://localhost"), "vanilla-test", KEY));
        assertThrows(IllegalArgumentException.class, () -> new HttpBridgeApi(URI.create("https://user:password@localhost"), "vanilla-test", KEY));
        assertThrows(IllegalArgumentException.class, () -> new HttpBridgeApi(URI.create("https://localhost/api"), "vanilla-test", KEY));
        try (HttpBridgeApi api = new HttpBridgeApi(origin, "vanilla-test", KEY)) {
            assertThrows(Exception.class, () -> api.poll(UUID.randomUUID()).get(10, TimeUnit.SECONDS));
        }
    }
}
