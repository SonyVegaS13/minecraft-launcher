package com.solaris.id.bridge;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import javax.net.ssl.SSLContext;

import java.io.ByteArrayOutputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Flow;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import java.util.function.Function;

/** Bounded, asynchronous Java 17 HTTPS client. TLS verification and redirects stay strict. */
public final class HttpBridgeApi implements BridgeApi {
    private static final int MAX_BODY = 16 * 1024;
    private final URI origin;
    private final String serverId;
    private final String key;
    private final HttpClient client;
    private final ExecutorService executor;
    private final Semaphore requests = new Semaphore(512);
    private volatile boolean closed;

    public HttpBridgeApi(URI origin, String serverId, String key) {
        this(origin, serverId, key, null);
    }

    // Test-only trust context; production always uses the JVM's verified trust store.
    HttpBridgeApi(URI origin, String serverId, String key, SSLContext trustContext) {
        this.origin = validateOrigin(origin);
        if (serverId == null || !serverId.matches("[a-z0-9][a-z0-9_-]{1,63}"))
            throw new IllegalArgumentException("Invalid Solaris server configuration");
        if (key == null || key.length() < 32 || key.length() > 256 || !key.matches("[\\x21-\\x7e]+"))
            throw new IllegalArgumentException("Invalid Solaris server configuration");
        this.serverId = serverId;
        this.key = key;
        executor = Executors.newFixedThreadPool(4, action -> {
            Thread thread = new Thread(action, "solaris-id-https");
            thread.setDaemon(true);
            return thread;
        });
        HttpClient.Builder builder = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5))
            .followRedirects(HttpClient.Redirect.NEVER).executor(executor);
        if (trustContext != null) builder.sslContext(trustContext);
        client = builder.build();
    }

    private static URI validateOrigin(URI origin) {
        if (origin == null || !"https".equalsIgnoreCase(origin.getScheme()) || origin.getHost() == null
                || origin.getRawUserInfo() != null || origin.getRawQuery() != null || origin.getRawFragment() != null
                || !(origin.getRawPath().isEmpty() || origin.getRawPath().equals("/")))
            throw new IllegalArgumentException("Solaris ID requires an HTTPS origin");
        return origin;
    }

    @Override public CompletableFuture<Challenge> createConnection(UUID connectionId, String nickname) {
        if (nickname == null || !nickname.matches("[A-Za-z0-9_]{3,16}"))
            return CompletableFuture.failedFuture(failure());
        JsonObject body = new JsonObject();
        body.addProperty("connectionId", connectionId.toString());
        body.addProperty("nickname", nickname);
        return mapCancelable(request("POST", "/connections", body), json -> {
            String code = string(json, "code");
            if (!code.matches("[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{10}")) throw failure();
            return new Challenge(code, Instant.parse(string(json, "expiresUtc")));
        });
    }

    @Override public CompletableFuture<Approval> poll(UUID connectionId) {
        return mapCancelable(request("GET", "/connections/" + connectionId, null), json -> {
            return switch (string(json, "state")) {
                case "pending" -> new Approval(State.PENDING, null, null, null);
                case "denied" -> new Approval(State.DENIED, null, null, null);
                case "approved" -> {
                    String nickname = string(json, "nickname");
                    if (!nickname.matches("[A-Za-z0-9_]{3,16}")) throw failure();
                    yield new Approval(State.APPROVED, uuid(json, "userId"), nickname, uuid(json, "gameSessionId"));
                }
                default -> throw failure();
            };
        });
    }

    @Override public CompletableFuture<Void> heartbeat(UUID gameSessionId, long sequence) {
        return event(gameSessionId, sequence, "heartbeat", "approved");
    }
    @Override public CompletableFuture<Void> closeSession(UUID gameSessionId, long sequence) {
        return event(gameSessionId, sequence, "close", "closed");
    }
    private CompletableFuture<Void> event(UUID gameSessionId, long sequence, String event, String expectedState) {
        if (sequence <= 0) return CompletableFuture.failedFuture(failure());
        JsonObject body = new JsonObject();
        body.addProperty("sequence", sequence);
        return mapCancelable(request("POST", "/game-sessions/" + gameSessionId + "/" + event, body), json -> {
            if (!expectedState.equals(string(json, "state"))) throw failure();
            return null;
        });
    }
    @Override public CompletableFuture<Void> cancelConnection(UUID connectionId) {
        return mapCancelable(request("DELETE", "/connections/" + connectionId, null), json -> {
            if (!"closed".equals(string(json, "state"))) throw failure();
            return null;
        });
    }

    private static <T, R> CompletableFuture<R> mapCancelable(CompletableFuture<T> source, Function<T, R> transform) {
        CompletableFuture<R> mapped = source.thenApply(transform);
        mapped.whenComplete((value, error) -> { if (mapped.isCancelled()) source.cancel(true); });
        return mapped;
    }

    private CompletableFuture<JsonObject> request(String method, String path, JsonObject body) {
        if (closed || !requests.tryAcquire()) return CompletableFuture.failedFuture(failure());
        try {
            HttpRequest.Builder builder = HttpRequest.newBuilder(origin.resolve("/api/v1/server" + path))
                .timeout(Duration.ofSeconds(5)).header("Accept", "application/json")
                .header("Authorization", "SolarisServer " + key).header("X-Solaris-Server", serverId);
            if (body != null) builder.header("Content-Type", "application/json");
            builder.method(method, body == null ? HttpRequest.BodyPublishers.noBody()
                : HttpRequest.BodyPublishers.ofString(body.toString(), StandardCharsets.UTF_8));
            CompletableFuture<HttpResponse<byte[]>> wire = client.sendAsync(builder.build(), info -> new LimitedBody());
            // Bound even a peer that sends headers and then stalls its response body.
            CompletableFuture<JsonObject> result = wire.copy().orTimeout(6, TimeUnit.SECONDS).thenApply(response -> {
                if (response.statusCode() != 200) throw failure();
                String type = response.headers().firstValue("Content-Type").orElse("");
                if (!type.split(";", 2)[0].trim().equalsIgnoreCase("application/json")) throw failure();
                try {
                    JsonElement parsed = JsonParser.parseString(new String(response.body(), StandardCharsets.UTF_8));
                    if (!parsed.isJsonObject()) throw failure();
                    return parsed.getAsJsonObject();
                } catch (RuntimeException invalid) { throw failure(); }
            });
            CompletableFuture<JsonObject> released = result.whenComplete((value, error) -> {
                requests.release();
                if (error != null || result.isCancelled()) wire.cancel(true);
            });
            released.whenComplete((value, error) -> { if (released.isCancelled()) result.cancel(true); });
            return released;
        } catch (RuntimeException rejected) {
            requests.release();
            return CompletableFuture.failedFuture(failure());
        }
    }
    private static String string(JsonObject object, String name) {
        JsonElement value = object.get(name);
        if (value == null || !value.isJsonPrimitive() || !value.getAsJsonPrimitive().isString()) throw failure();
        return value.getAsString();
    }
    private static UUID uuid(JsonObject object, String name) {
        String value = string(object, name);
        UUID id;
        try { id = UUID.fromString(value); } catch (RuntimeException invalid) { throw failure(); }
        if (!id.toString().equalsIgnoreCase(value) || id.equals(new UUID(0, 0))) throw failure();
        return id;
    }
    private static IllegalStateException failure() { return new IllegalStateException("Solaris ID request failed"); }
    @Override public void close() { closed = true; executor.shutdown(); }

    private static final class LimitedBody implements HttpResponse.BodySubscriber<byte[]> {
        private final CompletableFuture<byte[]> result = new CompletableFuture<>();
        private final ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        private Flow.Subscription subscription;
        @Override public CompletionStage<byte[]> getBody() { return result; }
        @Override public void onSubscribe(Flow.Subscription subscription) {
            this.subscription = subscription;
            subscription.request(1);
        }
        @Override public void onNext(List<ByteBuffer> buffers) {
            for (ByteBuffer buffer : buffers) {
                if (buffer.remaining() > MAX_BODY - bytes.size()) {
                    subscription.cancel(); result.completeExceptionally(failure()); return;
                }
                byte[] chunk = new byte[buffer.remaining()];
                buffer.get(chunk); bytes.writeBytes(chunk);
            }
            subscription.request(1);
        }
        @Override public void onError(Throwable error) { result.completeExceptionally(failure()); }
        @Override public void onComplete() { result.complete(bytes.toByteArray()); }
    }
}
