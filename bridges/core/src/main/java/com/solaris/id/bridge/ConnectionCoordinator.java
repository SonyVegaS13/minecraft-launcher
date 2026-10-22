package com.solaris.id.bridge;

import java.time.Clock;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/**
 * One random backend connection per actual join. State is independent of transport
 * UUID identity, and late responses/callbacks cannot approve a replacement join.
 */
public final class ConnectionCoordinator implements AutoCloseable {
    private final BridgeApi api;
    private final BridgeCallbacks callbacks;
    private final Clock clock;
    private final int capacity;
    private final ScheduledExecutorService scheduler;
    private final Map<UUID, Connection> connections = new HashMap<>();
    private boolean closed;

    public ConnectionCoordinator(HttpBridgeApi api, BridgeCallbacks callbacks, Clock clock, int capacity) {
        this(api, callbacks, clock, capacity, Executors.newSingleThreadScheduledExecutor(action -> {
            Thread thread = new Thread(action, "solaris-id-lifecycle");
            thread.setDaemon(true);
            return thread;
        }));
        scheduler.scheduleAtFixedRate(this::tickSafely, 250, 250, TimeUnit.MILLISECONDS);
    }

    // A null scheduler permits deterministic, clock-driven tests without sleeping.
    ConnectionCoordinator(BridgeApi api, BridgeCallbacks callbacks, Clock clock, int capacity,
                          ScheduledExecutorService scheduler) {
        if (api == null || callbacks == null || clock == null || capacity < 1 || capacity > 512)
            throw new IllegalArgumentException("Invalid Solaris bridge configuration");
        this.api = api; this.callbacks = callbacks; this.clock = clock;
        this.capacity = capacity; this.scheduler = scheduler;
    }

    public void join(UUID playerId, String nickname) {
        if (playerId == null || nickname == null || !nickname.matches("[A-Za-z0-9_]{3,16}"))
            throw new IllegalArgumentException("Invalid connection");
        leave(playerId);
        Connection connection;
        synchronized (this) {
            if (closed || connections.size() >= capacity) throw new IllegalStateException("Solaris ID capacity unavailable");
            connection = new Connection(playerId, nickname, clock.instant());
            connections.put(playerId, connection);
            connection.inFlight = true;
        }
        request(connection, true, () -> api.createConnection(connection.connectionId, nickname), challenge -> {
            Instant now = clock.instant();
            if (challenge == null || challenge.code() == null || !challenge.code().matches("[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{10}")
                    || challenge.expiresUtc() == null || !challenge.expiresUtc().isAfter(now)
                    || challenge.expiresUtc().isAfter(now.plusSeconds(120))) throw new IllegalStateException();
            if (challenge.expiresUtc().isBefore(connection.expiresUtc)) connection.expiresUtc = challenge.expiresUtc();
            connection.nextPoll = now;
            dispatchCurrent(connection, () -> callbacks.challenge(playerId, challenge.code(), challenge.expiresUtc()));
        });
    }

    public synchronized boolean isAuthenticated(UUID playerId) {
        Connection connection = connections.get(playerId);
        return connection != null && connection.userId != null && !closed;
    }
    public synchronized Optional<UUID> solarisId(UUID playerId) {
        Connection connection = connections.get(playerId);
        return connection == null || closed ? Optional.empty() : Optional.ofNullable(connection.userId);
    }

    public void leave(UUID playerId) {
        Connection connection;
        synchronized (this) { connection = connections.remove(playerId); }
        if (connection != null) cleanup(connection);
    }

    private void tickSafely() {
        try { tick(); }
        catch (RuntimeException unexpected) {
            // Exceptions never terminate the periodic security check or reveal inputs.
            ArrayList<Connection> snapshot;
            synchronized (this) { snapshot = new ArrayList<>(connections.values()); }
            snapshot.forEach(this::deny);
        }
    }

    void tick() {
        ArrayList<Connection> snapshot;
        synchronized (this) {
            if (closed) return;
            snapshot = new ArrayList<>(connections.values());
        }
        for (Connection connection : snapshot) {
            int operation = 0;
            long sequence = 0;
            synchronized (this) {
                if (!current(connection)) continue;
                Instant now = clock.instant();
                if ((connection.userId == null && !now.isBefore(connection.expiresUtc))
                        || (connection.userId != null && !now.isBefore(connection.lastVerified.plusSeconds(30)))) {
                    operation = 3;
                } else if (!connection.inFlight) {
                    if (connection.userId != null && !now.isBefore(connection.nextHeartbeat)) {
                        operation = 2; sequence = ++connection.sequence;
                    } else if (!now.isBefore(connection.nextPoll)) operation = 1;
                    if (operation != 0) connection.inFlight = true;
                }
            }
            if (operation == 3) deny(connection);
            else if (operation == 2) {
                long eventSequence = sequence;
                request(connection, false, () -> api.heartbeat(connection.gameSessionId, eventSequence), ignored -> {
                    connection.lastVerified = clock.instant();
                    connection.nextHeartbeat = clock.instant().plusSeconds(15);
                });
            } else if (operation == 1) request(connection, false, () -> api.poll(connection.connectionId), approval -> {
                Instant now = clock.instant();
                if (approval == null || approval.state() == null || approval.state() == BridgeApi.State.DENIED)
                    throw new IllegalStateException();
                if (approval.state() == BridgeApi.State.PENDING) {
                    if (connection.userId != null) throw new IllegalStateException();
                    connection.nextPoll = now.plusSeconds(2);
                    return;
                }
                if (approval.userId() == null || approval.gameSessionId() == null || approval.nickname() == null
                        || !connection.nickname.equalsIgnoreCase(approval.nickname())) throw new IllegalStateException();
                if (connection.userId != null && (!connection.userId.equals(approval.userId())
                        || !connection.gameSessionId.equals(approval.gameSessionId()))) throw new IllegalStateException();
                boolean first = connection.userId == null;
                connection.userId = approval.userId();
                connection.gameSessionId = approval.gameSessionId();
                connection.lastVerified = now;
                connection.nextPoll = now.plusSeconds(5);
                if (first) {
                    connection.nextHeartbeat = now.plusSeconds(15);
                    dispatchCurrent(connection, () -> callbacks.authenticated(connection.playerId, approval.userId(), approval.nickname()));
                }
            });
        }
    }

    private <T> void request(Connection connection, boolean creation,
                             java.util.function.Supplier<CompletableFuture<T>> operation, Consumer<T> success) {
        synchronized (this) {
            if (!current(connection)) return;
        }
        CompletableFuture<T> future;
        try { future = operation.get(); }
        catch (RuntimeException rejected) { deny(connection); return; }
        boolean cancelAfterRegistration;
        synchronized (this) {
            boolean active = current(connection);
            cancelAfterRegistration = !active && !creation;
            if (active) {
                connection.future = future;
                connection.creating = creation;
            }
        }
        future.whenComplete((value, error) -> {
            boolean stale;
            boolean invalid = error != null;
            synchronized (this) {
                stale = !current(connection);
                if (!stale) {
                    connection.inFlight = false;
                    connection.future = null;
                    connection.creating = false;
                    if (!invalid) {
                        try { success.accept(value); }
                        catch (RuntimeException invalidResponse) { invalid = true; }
                    }
                }
            }
            // A bounded create is drained on disconnect so its resulting record is
            // revoked after insertion, even if the player left before the response.
            if (stale) revoke(connection);
            else if (invalid) deny(connection);
        });
        // Disconnect may have happened while the nonblocking API call obtained
        // its future, before cleanup could see it. Register revocation first.
        if (cancelAfterRegistration) future.cancel(true);
    }

    private synchronized boolean current(Connection connection) {
        return !closed && connections.get(connection.playerId) == connection;
    }
    private void dispatchCurrent(Connection connection, Runnable action) {
        callbacks.dispatch(() -> {
            synchronized (ConnectionCoordinator.this) {
                if (current(connection)) action.run();
            }
        });
    }
    private void deny(Connection connection) {
        synchronized (this) {
            if (!current(connection)) return;
            // Immediately remove verified identity; adapter gameplay gates fail closed
            // while its queued main-thread disconnect is waiting.
            connections.remove(connection.playerId);
        }
        cleanup(connection);
        callbacks.dispatch(() -> {
            synchronized (ConnectionCoordinator.this) {
                if (!closed && !connections.containsKey(connection.playerId))
                    callbacks.kick(connection.playerId, "Solaris ID approval expired, revoked or unavailable");
            }
        });
    }
    private void cleanup(Connection connection) {
        CompletableFuture<?> future;
        boolean creating;
        UUID session;
        long sequence;
        synchronized (this) {
            future = connection.future; creating = connection.creating;
            session = connection.gameSessionId; sequence = ++connection.sequence;
        }
        if (future != null && !creating) future.cancel(true);
        if (session != null) {
            try { api.closeSession(session, sequence).exceptionally(error -> null); }
            catch (RuntimeException ignored) { /* DELETE also closes the session. */ }
        }
        revoke(connection);
    }
    private void revoke(Connection connection) {
        try { api.cancelConnection(connection.connectionId).exceptionally(error -> null); }
        catch (RuntimeException ignored) { /* Backend expiry is the final bound during an outage. */ }
    }

    @Override public void close() {
        ArrayList<Connection> snapshot;
        synchronized (this) {
            if (closed) return;
            closed = true;
            snapshot = new ArrayList<>(connections.values());
            connections.clear();
        }
        snapshot.forEach(this::cleanup);
        if (scheduler != null) {
            // Do not block the Minecraft main thread; allow bounded requests and
            // late-create revocation to drain before retiring daemon IO workers.
            // A create can use six seconds, then its DELETE another six seconds.
            scheduler.schedule(api::close, 13, TimeUnit.SECONDS);
            scheduler.shutdown();
        } else api.close();
    }

    private static final class Connection {
        final UUID playerId;
        final UUID connectionId = UUID.randomUUID();
        final String nickname;
        Instant expiresUtc;
        Instant nextPoll = Instant.MAX;
        Instant nextHeartbeat = Instant.MAX;
        Instant lastVerified;
        UUID userId;
        UUID gameSessionId;
        long sequence;
        boolean inFlight;
        boolean creating;
        CompletableFuture<?> future;
        Connection(UUID playerId, String nickname, Instant now) {
            this.playerId = playerId; this.nickname = nickname;
            expiresUtc = now.plusSeconds(90); lastVerified = now;
        }
    }
}
