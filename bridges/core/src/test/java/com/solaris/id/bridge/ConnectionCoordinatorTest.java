package com.solaris.id.bridge;

import org.junit.jupiter.api.Test;

import java.time.Clock;
import java.time.Instant;
import java.time.ZoneId;
import java.time.ZoneOffset;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

import static org.junit.jupiter.api.Assertions.*;

/** Deterministic lifecycle tests: no Minecraft API stubs, network, sleeps or wall clock. */
class ConnectionCoordinatorTest {
    private static final UUID PLAYER = UUID.fromString("20000000-0000-4000-8000-000000000001");
    private static final UUID USER = UUID.fromString("30000000-0000-4000-8000-000000000001");
    private static final UUID GAME = UUID.fromString("40000000-0000-4000-8000-000000000001");
    private static final String NICKNAME = "SolarisPlayer";

    @Test void joinCreatesRandomConnectionAndRemainsQuarantinedUntilBackendApproval() {
        Fixture f = new Fixture();
        f.coordinator.join(PLAYER, NICKNAME);
        assertEquals(1, f.api.creates.size());
        assertNotEquals(PLAYER, f.api.creates.get(0).connection());
        assertEquals(NICKNAME, f.api.creates.get(0).nickname());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        f.completeCreate(0);
        assertTrue(f.callbacks.challenges.isEmpty(), "Server state callbacks require adapter dispatch");
        f.callbacks.drain();
        assertEquals(List.of(new ChallengeNotice(PLAYER, "23456789AB", f.clock.instant().plusSeconds(90))), f.callbacks.challenges);
        assertTrue(f.api.heartbeats.isEmpty());
        f.coordinator.tick();
        assertEquals(f.api.creates.get(0).connection(), f.api.polls.get(0).connection());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
    }

    @Test void pendingPollsNeverGenerateTrustedHeartbeats() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        f.api.polls.get(0).future().complete(pending());
        f.clock.advance(1);
        f.coordinator.tick();
        assertEquals(1, f.api.polls.size());
        f.clock.advance(1);
        f.coordinator.tick();
        assertEquals(2, f.api.polls.size());
        f.api.polls.get(1).future().complete(pending());
        assertTrue(f.api.heartbeats.isEmpty());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
    }

    @Test void anInflightRequestPreventsOverlappingPollsAndHeartbeats() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.clock.advance(5);
        f.coordinator.tick();
        assertEquals(2, f.api.polls.size());
        f.clock.advance(10);
        f.coordinator.tick();
        f.coordinator.tick();
        assertEquals(2, f.api.polls.size());
        assertTrue(f.api.heartbeats.isEmpty());
        f.api.polls.get(1).future().complete(approved());
        f.coordinator.tick();
        assertEquals(1, f.api.heartbeats.size());
        f.coordinator.tick();
        assertEquals(1, f.api.heartbeats.size());
    }

    @Test void approvalExposesOnlyBackendStableUserIdAndDispatchesAuthentication() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        assertTrue(f.coordinator.isAuthenticated(PLAYER));
        assertEquals(USER, f.coordinator.solarisId(PLAYER).orElseThrow());
        assertNotEquals(PLAYER, f.coordinator.solarisId(PLAYER).orElseThrow());
        assertTrue(f.callbacks.authenticated.isEmpty());
        f.callbacks.drain();
        assertEquals(List.of(new AuthenticationNotice(PLAYER, USER, NICKNAME)), f.callbacks.authenticated);
        assertTrue(f.api.heartbeats.isEmpty());
    }

    @Test void activeConnectionsArePolledAgainForRevocation() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.callbacks.drain();
        f.clock.advance(4);
        f.coordinator.tick();
        assertEquals(1, f.api.polls.size());
        f.clock.advance(1);
        f.coordinator.tick();
        assertEquals(2, f.api.polls.size());
        f.api.polls.get(1).future().complete(denied());
        assertFalse(f.coordinator.isAuthenticated(PLAYER), "Identity is withdrawn before the main-thread kick");
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        assertTrue(f.callbacks.kicked.isEmpty());
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
        assertEquals(1, f.api.closes.size());
    }

    @Test void backendChallengeExpiryDeniesAtItsExactDeadline() {
        Fixture f = new Fixture();
        f.coordinator.join(PLAYER, NICKNAME);
        f.api.creates.get(0).future().complete(new BridgeApi.Challenge("23456789AB", f.clock.instant().plusSeconds(10)));
        f.clock.advance(10);
        f.coordinator.tick();
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
        assertTrue(f.callbacks.challenges.isEmpty(), "Queued challenge must not outlive its connection");
        assertFalse(f.api.cancellations.isEmpty());
    }

    @Test void hangingCreateTimesOutWithoutAllowingLateChallengeToEscape() {
        Fixture f = new Fixture();
        f.coordinator.join(PLAYER, NICKNAME);
        CompletableFuture<BridgeApi.Challenge> creation = f.api.creates.get(0).future();
        f.clock.advance(90);
        f.coordinator.tick();
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertFalse(creation.isCancelled(), "Bounded create must drain so a late inserted backend record can be revoked");
        int cancellationsBeforeLateResponse = f.api.cancellations.size();
        creation.complete(new BridgeApi.Challenge("23456789AB", f.clock.instant().plusSeconds(90)));
        assertEquals(cancellationsBeforeLateResponse + 1, f.api.cancellations.size());
        f.callbacks.drain();
        assertTrue(f.callbacks.challenges.isEmpty());
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
    }

    @Test void createNetworkErrorFailsClosedWithoutLeakingExceptionPayload() {
        Fixture f = new Fixture();
        f.coordinator.join(PLAYER, NICKNAME);
        f.api.creates.get(0).future().completeExceptionally(new IllegalStateException("must-not-reach-player-key-or-token"));
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
        assertEquals(1, f.callbacks.kickReasons.size());
        assertFalse(f.callbacks.kickReasons.get(0).contains("must-not-reach-player"));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
    }

    @Test void pendingPollNetworkFailureFailsClosedImmediately() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        f.api.polls.get(0).future().completeExceptionally(new IllegalStateException("network unavailable"));
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
        assertTrue(f.api.heartbeats.isEmpty());
    }

    @Test void heartbeatFailureWithdrawsIdentityAndClosesBackendSession() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.clock.advance(15);
        f.coordinator.tick();
        assertEquals(1, f.api.heartbeats.size());
        f.api.heartbeats.get(0).future().completeExceptionally(new IllegalStateException("410 revoked"));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        assertEquals(List.of(new GameEvent(GAME, 2)), f.api.closes);
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
        assertTrue(f.callbacks.authenticated.isEmpty(), "Queued authentication must not run after revocation");
    }

    @Test void heartbeatAndCloseUseStrictlyIncreasingServerSequences() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.clock.advance(15);
        f.coordinator.tick();
        assertEquals(GAME, f.api.heartbeats.get(0).session());
        assertEquals(1, f.api.heartbeats.get(0).sequence());
        f.api.heartbeats.get(0).future().complete(null);
        f.clock.advance(15);
        f.coordinator.tick();
        assertEquals(2, f.api.heartbeats.get(1).sequence());
        f.api.heartbeats.get(1).future().complete(null);
        f.coordinator.leave(PLAYER);
        f.coordinator.leave(PLAYER);
        assertEquals(List.of(new GameEvent(GAME, 3)), f.api.closes);
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
    }

    @Test void unexpectedUserOrGameSessionChangeFailsClosed() {
        for (boolean changeUser : List.of(true, false)) {
            Fixture f = new Fixture();
            f.start();
            f.approve();
            f.clock.advance(5);
            f.coordinator.tick();
            f.api.polls.get(1).future().complete(new BridgeApi.Approval(BridgeApi.State.APPROVED,
                changeUser ? UUID.randomUUID() : USER, NICKNAME, changeUser ? GAME : UUID.randomUUID()));
            assertFalse(f.coordinator.isAuthenticated(PLAYER));
            f.callbacks.drain();
            assertEquals(List.of(PLAYER), f.callbacks.kicked);
        }
    }

    @Test void approvalForAnotherNicknameIsRejected() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        f.api.polls.get(0).future().complete(new BridgeApi.Approval(BridgeApi.State.APPROVED, USER, "OtherPlayer", GAME));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
    }

    @Test void caseOnlyBackendNicknameDifferenceIsAccepted() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        f.api.polls.get(0).future().complete(new BridgeApi.Approval(BridgeApi.State.APPROVED, USER, "solarisplayer", GAME));
        assertEquals(USER, f.coordinator.solarisId(PLAYER).orElseThrow());
    }

    @Test void reconnectCreatesNewBackendConnectionAndDropsQueuedOldChallenge() {
        Fixture f = new Fixture();
        f.start();
        UUID first = f.api.creates.get(0).connection();
        f.coordinator.join(PLAYER, NICKNAME);
        UUID replacement = f.api.creates.get(1).connection();
        assertNotEquals(first, replacement);
        f.completeCreate(1);
        f.callbacks.drain();
        assertEquals(1, f.callbacks.challenges.size());
        assertTrue(f.api.cancellations.contains(first));
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
    }

    @Test void queuedOldAuthenticationCannotReleaseReplacementConnection() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        assertTrue(f.coordinator.isAuthenticated(PLAYER));
        f.coordinator.join(PLAYER, NICKNAME);
        f.completeCreate(1);
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        assertEquals(List.of(new GameEvent(GAME, 1)), f.api.closes);
    }

    @Test void lateOldApprovalCannotAuthorizeReplacementEvenWhenCancellationLosesTheRace() {
        Fixture f = new Fixture();
        f.api.pollsCancellable = false;
        f.start();
        f.coordinator.tick();
        CompletableFuture<BridgeApi.Approval> oldPoll = f.api.polls.get(0).future();
        UUID oldConnection = f.api.creates.get(0).connection();
        f.coordinator.join(PLAYER, NICKNAME);
        f.completeCreate(1);
        int cancellationsBeforeOldResponse = f.api.cancellations.size();
        assertTrue(oldPoll.complete(approved()));
        assertEquals(cancellationsBeforeOldResponse + 1, f.api.cancellations.size());
        assertEquals(oldConnection, f.api.cancellations.get(f.api.cancellations.size() - 1));
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertEquals(1, f.callbacks.challenges.size());
    }

    @Test void oldQueuedKickDoesNotKickReplacementConnection() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        f.api.polls.get(0).future().complete(denied());
        f.coordinator.join(PLAYER, NICKNAME);
        f.completeCreate(1);
        f.callbacks.drain();
        assertTrue(f.callbacks.kicked.isEmpty());
        assertEquals(1, f.callbacks.challenges.size());
    }

    @Test void disconnectCancelsPendingPollAndRevokesBackendConnection() {
        Fixture f = new Fixture();
        f.start();
        f.coordinator.tick();
        CompletableFuture<BridgeApi.Approval> poll = f.api.polls.get(0).future();
        f.coordinator.leave(PLAYER);
        assertTrue(poll.isCancelled());
        assertTrue(f.api.cancellations.contains(f.api.creates.get(0).connection()));
        assertFalse(poll.complete(approved()));
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertTrue(f.callbacks.challenges.isEmpty());
        assertTrue(f.callbacks.kicked.isEmpty());
    }

    @Test void disconnectDuringCreateRevokesAgainAfterLateBackendInsertion() {
        Fixture f = new Fixture();
        f.coordinator.join(PLAYER, NICKNAME);
        CompletableFuture<BridgeApi.Challenge> create = f.api.creates.get(0).future();
        f.coordinator.leave(PLAYER);
        assertFalse(create.isCancelled());
        UUID connection = f.api.creates.get(0).connection();
        assertEquals(List.of(connection), f.api.cancellations);
        f.completeCreate(0);
        assertEquals(List.of(connection, connection), f.api.cancellations);
        f.callbacks.drain();
        assertTrue(f.callbacks.challenges.isEmpty());
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertTrue(f.api.heartbeats.isEmpty());
    }

    @Test void disconnectWhileObtainingPollFutureStillCancelsThatFuture() {
        Fixture f = new Fixture();
        f.start();
        f.api.beforePollReturn = () -> f.coordinator.leave(PLAYER);
        f.coordinator.tick();
        assertTrue(f.api.polls.get(0).future().isCancelled());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertTrue(f.api.cancellations.contains(f.api.creates.get(0).connection()));
    }

    @Test void disconnectWhileObtainingHeartbeatFutureStillCancelsThatFuture() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.api.beforeHeartbeatReturn = () -> f.coordinator.leave(PLAYER);
        f.clock.advance(15);
        f.coordinator.tick();
        assertTrue(f.api.heartbeats.get(0).future().isCancelled());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertEquals(List.of(new GameEvent(GAME, 2)), f.api.closes);
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
    }

    @Test void capacityIsBoundedAndDisconnectReleasesSlot() {
        Fixture f = new Fixture(1);
        f.coordinator.join(PLAYER, NICKNAME);
        UUID second = UUID.randomUUID();
        assertThrows(IllegalStateException.class, () -> f.coordinator.join(second, "OtherPlayer"));
        assertEquals(1, f.api.creates.size());
        f.coordinator.leave(PLAYER);
        assertDoesNotThrow(() -> f.coordinator.join(second, "OtherPlayer"));
        assertEquals(2, f.api.creates.size());
    }

    @Test void shutdownIsIdempotentAndCannotRetainOrIssueIdentity() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.coordinator.close();
        f.coordinator.close();
        assertEquals(1, f.api.closeCalls);
        assertEquals(List.of(new GameEvent(GAME, 1)), f.api.closes);
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        assertTrue(f.coordinator.solarisId(PLAYER).isEmpty());
        int polls = f.api.polls.size();
        f.clock.advance(100);
        f.coordinator.tick();
        assertEquals(polls, f.api.polls.size());
        assertThrows(IllegalStateException.class, () -> f.coordinator.join(PLAYER, NICKNAME));
        f.callbacks.drain();
        assertTrue(f.callbacks.authenticated.isEmpty());
        assertTrue(f.callbacks.challenges.isEmpty());
    }

    @Test void malformedExpiredOrUnboundedChallengeFailsClosed() {
        for (BridgeApi.Challenge challenge : List.of(
            new BridgeApi.Challenge("password-is-not-a-code", Instant.parse("2026-10-22T00:01:00Z")),
            new BridgeApi.Challenge("23456789AB", Instant.parse("2026-10-22T00:00:00Z")),
            new BridgeApi.Challenge("23456789AB", Instant.parse("2026-10-22T00:03:00Z")))) {
            Fixture f = new Fixture();
            f.coordinator.join(PLAYER, NICKNAME);
            f.api.creates.get(0).future().complete(challenge);
            f.callbacks.drain();
            assertEquals(List.of(PLAYER), f.callbacks.kicked);
            assertTrue(f.callbacks.challenges.isEmpty());
        }
    }

    @Test void pendingStateAfterApprovalIsDenied() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.clock.advance(5);
        f.coordinator.tick();
        f.api.polls.get(1).future().complete(pending());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
    }

    @Test void staleVerifiedConnectionIsDeniedEvenIfPollHangs() {
        Fixture f = new Fixture();
        f.start();
        f.approve();
        f.clock.advance(5);
        f.coordinator.tick();
        CompletableFuture<BridgeApi.Approval> activePoll = f.api.polls.get(1).future();
        f.clock.advance(25);
        f.coordinator.tick();
        assertTrue(activePoll.isCancelled());
        assertFalse(f.coordinator.isAuthenticated(PLAYER));
        f.callbacks.drain();
        assertEquals(List.of(PLAYER), f.callbacks.kicked);
    }

    private static BridgeApi.Approval approved() {
        return new BridgeApi.Approval(BridgeApi.State.APPROVED, USER, NICKNAME, GAME);
    }
    private static BridgeApi.Approval pending() {
        return new BridgeApi.Approval(BridgeApi.State.PENDING, null, null, null);
    }
    private static BridgeApi.Approval denied() {
        return new BridgeApi.Approval(BridgeApi.State.DENIED, null, null, null);
    }
    private static final class Fixture {
        final MutableClock clock = new MutableClock();
        final FakeApi api = new FakeApi();
        final FakeCallbacks callbacks = new FakeCallbacks();
        final ConnectionCoordinator coordinator;
        Fixture() { this(4); }
        Fixture(int capacity) { coordinator = new ConnectionCoordinator(api, callbacks, clock, capacity, null); }
        void start() { coordinator.join(PLAYER, NICKNAME); completeCreate(0); }
        void completeCreate(int index) {
            api.creates.get(index).future().complete(new BridgeApi.Challenge("23456789AB", clock.instant().plusSeconds(90)));
        }
        void approve() {
            coordinator.tick();
            api.polls.get(api.polls.size() - 1).future().complete(approved());
        }
    }
    private static final class MutableClock extends Clock {
        private Instant current = Instant.parse("2026-10-22T00:00:00Z");
        void advance(long seconds) { current = current.plusSeconds(seconds); }
        @Override public ZoneId getZone() { return ZoneOffset.UTC; }
        @Override public Clock withZone(ZoneId zone) {
            if (!zone.equals(ZoneOffset.UTC)) throw new UnsupportedOperationException("UTC fixture");
            return this;
        }
        @Override public Instant instant() { return current; }
    }
    private record Creation(UUID connection, String nickname, CompletableFuture<BridgeApi.Challenge> future) {}
    private record Poll(UUID connection, CompletableFuture<BridgeApi.Approval> future) {}
    private record Heartbeat(UUID session, long sequence, CompletableFuture<Void> future) {}
    private record GameEvent(UUID session, long sequence) {}
    private static final class FakeApi implements BridgeApi {
        final List<Creation> creates = new ArrayList<>();
        final List<Poll> polls = new ArrayList<>();
        final List<Heartbeat> heartbeats = new ArrayList<>();
        final List<GameEvent> closes = new ArrayList<>();
        final List<UUID> cancellations = new ArrayList<>();
        Runnable beforePollReturn = () -> {};
        Runnable beforeHeartbeatReturn = () -> {};
        boolean pollsCancellable = true;
        int closeCalls;
        @Override public CompletableFuture<Challenge> createConnection(UUID connectionId, String nickname) {
            CompletableFuture<Challenge> future = new CompletableFuture<>();
            creates.add(new Creation(connectionId, nickname, future));
            return future;
        }
        @Override public CompletableFuture<Approval> poll(UUID connectionId) {
            CompletableFuture<Approval> future = pollsCancellable ? new CompletableFuture<>() : new CompletableFuture<>() {
                @Override public boolean cancel(boolean interrupt) { return false; }
            };
            polls.add(new Poll(connectionId, future));
            beforePollReturn.run();
            return future;
        }
        @Override public CompletableFuture<Void> heartbeat(UUID gameSessionId, long sequence) {
            CompletableFuture<Void> future = new CompletableFuture<>();
            heartbeats.add(new Heartbeat(gameSessionId, sequence, future));
            beforeHeartbeatReturn.run();
            return future;
        }
        @Override public CompletableFuture<Void> closeSession(UUID gameSessionId, long sequence) {
            closes.add(new GameEvent(gameSessionId, sequence));
            return CompletableFuture.completedFuture(null);
        }
        @Override public CompletableFuture<Void> cancelConnection(UUID connectionId) {
            cancellations.add(connectionId);
            return CompletableFuture.completedFuture(null);
        }
        @Override public void close() { closeCalls++; }
    }
    private record ChallengeNotice(UUID player, String code, Instant expires) {}
    private record AuthenticationNotice(UUID player, UUID user, String nickname) {}
    private static final class FakeCallbacks implements BridgeCallbacks {
        final ArrayDeque<Runnable> queue = new ArrayDeque<>();
        final List<ChallengeNotice> challenges = new ArrayList<>();
        final List<AuthenticationNotice> authenticated = new ArrayList<>();
        final List<UUID> kicked = new ArrayList<>();
        final List<String> kickReasons = new ArrayList<>();
        @Override public void dispatch(Runnable action) { queue.add(action); }
        void drain() { while (!queue.isEmpty()) queue.removeFirst().run(); }
        @Override public void challenge(UUID playerId, String code, Instant expiresUtc) {
            challenges.add(new ChallengeNotice(playerId, code, expiresUtc));
        }
        @Override public void authenticated(UUID playerId, UUID solarisUserId, String nickname) {
            authenticated.add(new AuthenticationNotice(playerId, solarisUserId, nickname));
        }
        @Override public void kick(UUID playerId, String safeReason) {
            kicked.add(playerId); kickReasons.add(safeReason);
        }
    }
}
