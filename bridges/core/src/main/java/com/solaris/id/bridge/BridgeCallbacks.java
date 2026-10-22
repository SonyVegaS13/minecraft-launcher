package com.solaris.id.bridge;

import java.time.Instant;
import java.util.UUID;

/** Adapters dispatch all callbacks to their server's main thread. */
public interface BridgeCallbacks {
    void dispatch(Runnable action);
    void challenge(UUID playerId, String code, Instant expiresUtc);
    void authenticated(UUID playerId, UUID solarisUserId, String nickname);
    void kick(UUID playerId, String safeReason);
}
