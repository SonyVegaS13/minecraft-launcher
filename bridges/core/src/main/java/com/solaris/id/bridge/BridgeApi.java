package com.solaris.id.bridge;

import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/** Server-only HTTPS protocol; no launcher tickets or account credentials enter this API. */
public interface BridgeApi extends AutoCloseable {
    record Challenge(String code, Instant expiresUtc) {}
    enum State { PENDING, APPROVED, DENIED }
    record Approval(State state, UUID userId, String nickname, UUID gameSessionId) {}
    CompletableFuture<Challenge> createConnection(UUID connectionId, String nickname);
    CompletableFuture<Approval> poll(UUID connectionId);
    CompletableFuture<Void> heartbeat(UUID gameSessionId, long sequence);
    CompletableFuture<Void> closeSession(UUID gameSessionId, long sequence);
    CompletableFuture<Void> cancelConnection(UUID connectionId);
    @Override void close();
}
