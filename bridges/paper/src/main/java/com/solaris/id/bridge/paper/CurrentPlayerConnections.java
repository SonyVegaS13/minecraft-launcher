package com.solaris.id.bridge.paper;

import java.util.Objects;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Supplier;

/** Exact live player handles, separate from reusable Minecraft transport UUIDs. */
final class CurrentPlayerConnections<T> {
    private final ConcurrentHashMap<UUID, T> players = new ConcurrentHashMap<>();

    synchronized void bind(UUID playerId, T player) {
        players.put(Objects.requireNonNull(playerId), Objects.requireNonNull(player));
    }

    boolean isCurrent(UUID playerId, T player) {
        return playerId != null && player != null && players.get(playerId) == player;
    }

    synchronized boolean remove(UUID playerId, T player) {
        if (!isCurrent(playerId, player)) return false;
        players.remove(playerId);
        return true;
    }

    <R> Optional<R> resolve(UUID playerId, T player, Supplier<Optional<R>> lookup) {
        if (!isCurrent(playerId, player)) return Optional.empty();
        // Never hold this registry's lock while calling the coordinator: its
        // callbacks already execute under the coordinator lock. Recheck instead.
        Optional<R> result = lookup.get();
        return isCurrent(playerId, player) ? result : Optional.empty();
    }

    synchronized void clear() {
        players.clear();
    }
}
