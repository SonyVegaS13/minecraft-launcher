package com.solaris.id.bridge.paper;

import org.junit.jupiter.api.Test;

import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.*;

final class CurrentPlayerConnectionsTest {
    private final UUID transportId = UUID.randomUUID();
    private final UUID solarisId = UUID.randomUUID();
    private final CurrentPlayerConnections<PlayerHandle> connections = new CurrentPlayerConnections<>();

    @Test void currentConnectionCanExposeApprovedIdentity() {
        PlayerHandle player = new PlayerHandle(transportId);
        connections.bind(transportId, player);
        assertEquals(Optional.of(solarisId), connections.resolve(transportId, player, () -> Optional.of(solarisId)));
    }

    @Test void equalButDistinctPreviousPlayerCannotReadReplacementApproval() {
        PlayerHandle previous = new PlayerHandle(transportId);
        PlayerHandle replacement = new PlayerHandle(transportId);
        assertEquals(previous, replacement, "Some server wrappers compare by UUID; equality is insufficient");
        connections.bind(transportId, previous);
        connections.bind(transportId, replacement);
        AtomicBoolean queried = new AtomicBoolean();
        assertTrue(connections.resolve(transportId, previous, () -> {
            queried.set(true);
            return Optional.of(solarisId);
        }).isEmpty());
        assertFalse(queried.get(), "A stale player must not query the replacement's approval at all");
        assertEquals(Optional.of(solarisId), connections.resolve(transportId, replacement, () -> Optional.of(solarisId)));
    }

    @Test void staleQuitCannotRemoveEqualReplacementPlayer() {
        PlayerHandle previous = new PlayerHandle(transportId);
        PlayerHandle replacement = new PlayerHandle(transportId);
        connections.bind(transportId, previous);
        connections.bind(transportId, replacement);
        assertFalse(connections.remove(transportId, previous));
        assertTrue(connections.isCurrent(transportId, replacement));
        assertTrue(connections.remove(transportId, replacement));
        assertTrue(connections.resolve(transportId, replacement, () -> Optional.of(solarisId)).isEmpty());
    }

    @Test void reconnectDuringLookupDoesNotReturnReplacementIdentity() {
        PlayerHandle previous = new PlayerHandle(transportId);
        PlayerHandle replacement = new PlayerHandle(transportId);
        connections.bind(transportId, previous);
        assertTrue(connections.resolve(transportId, previous, () -> {
            connections.bind(transportId, replacement);
            return Optional.of(solarisId);
        }).isEmpty(), "The post-lookup guard must reject an identity obtained after reconnect");
    }

    @Test void asynchronousReconnectCannotInheritIdentityOrDeadlockLookup() throws Exception {
        PlayerHandle previous = new PlayerHandle(transportId);
        PlayerHandle replacement = new PlayerHandle(transportId);
        connections.bind(transportId, previous);
        CountDownLatch enteredLookup = new CountDownLatch(1);
        CountDownLatch rebound = new CountDownLatch(1);
        AtomicReference<Optional<UUID>> result = new AtomicReference<>();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        Thread lookup = new Thread(() -> {
            try {
                result.set(connections.resolve(transportId, previous, () -> {
                    enteredLookup.countDown();
                    try {
                        if (!rebound.await(3, TimeUnit.SECONDS)) throw new AssertionError("Lookup held the binding lock");
                    } catch (InterruptedException interrupted) {
                        Thread.currentThread().interrupt();
                        throw new AssertionError(interrupted);
                    }
                    return Optional.of(solarisId);
                }));
            } catch (Throwable error) { failure.set(error); }
        });
        lookup.start();
        try {
            assertTrue(enteredLookup.await(3, TimeUnit.SECONDS));
            connections.bind(transportId, replacement);
        } finally { rebound.countDown(); }
        lookup.join(4000);
        assertFalse(lookup.isAlive(), "Registry lookup must not block a reconnect");
        assertNull(failure.get());
        assertEquals(Optional.empty(), result.get());
    }

    @Test void shutdownDuringLookupWithdrawsIdentity() {
        PlayerHandle player = new PlayerHandle(transportId);
        connections.bind(transportId, player);
        assertTrue(connections.resolve(transportId, player, () -> {
            connections.clear();
            return Optional.of(solarisId);
        }).isEmpty());
    }

    @Test void missingConnectionDoesNotInvokeApprovalLookup() {
        PlayerHandle player = new PlayerHandle(transportId);
        assertTrue(connections.resolve(transportId, player, () -> { throw new AssertionError("No live player"); }).isEmpty());
        assertTrue(connections.resolve(transportId, null, () -> { throw new AssertionError("Null player"); }).isEmpty());
        assertFalse(connections.remove(transportId, null));
    }

    private record PlayerHandle(UUID id) {}
}
