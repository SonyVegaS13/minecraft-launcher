package com.solaris.id.bridge.forge;

import com.mojang.logging.LogUtils;
import org.slf4j.Logger;
import com.solaris.id.bridge.BridgeCallbacks;
import com.solaris.id.bridge.ConnectionCoordinator;
import com.solaris.id.bridge.HttpBridgeApi;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.level.Level;
import net.minecraft.world.phys.Vec3;
import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.event.CommandEvent;
import net.minecraftforge.event.ServerChatEvent;
import net.minecraftforge.event.TickEvent;
import net.minecraftforge.event.entity.EntityJoinLevelEvent;
import net.minecraftforge.event.entity.EntityMountEvent;
import net.minecraftforge.event.entity.EntityTravelToDimensionEvent;
import net.minecraftforge.event.entity.ProjectileImpactEvent;
import net.minecraftforge.event.entity.item.ItemTossEvent;
import net.minecraftforge.event.entity.living.LivingAttackEvent;
import net.minecraftforge.event.entity.living.LivingDamageEvent;
import net.minecraftforge.event.entity.living.LivingDeathEvent;
import net.minecraftforge.event.entity.living.LivingHurtEvent;
import net.minecraftforge.event.entity.player.AttackEntityEvent;
import net.minecraftforge.event.entity.player.EntityItemPickupEvent;
import net.minecraftforge.event.entity.player.PlayerContainerEvent;
import net.minecraftforge.event.entity.player.PlayerEvent;
import net.minecraftforge.event.entity.player.PlayerInteractEvent;
import net.minecraftforge.event.entity.player.PlayerXpEvent;
import net.minecraftforge.event.level.BlockEvent;
import net.minecraftforge.event.server.ServerStartedEvent;
import net.minecraftforge.event.server.ServerStoppingEvent;
import net.minecraftforge.eventbus.api.EventPriority;
import net.minecraftforge.eventbus.api.SubscribeEvent;
import net.minecraftforge.fml.common.Mod;

import java.net.URI;
import java.time.Clock;
import java.time.Instant;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Server-only bridge. A transport/offline UUID is merely the lookup key for this
 * connection. Only the backend's approved immutable Solaris user ID is identity.
 * No player password, account token or game ticket enters Minecraft packets.
 */
@Mod(SolarisForgeBridge.MOD_ID)
public final class SolarisForgeBridge implements BridgeCallbacks {
    public static final String MOD_ID = "solaris_id_bridge";
    private static final Logger LOG = LogUtils.getLogger();
    private static volatile SolarisForgeBridge instance;
    private final ConcurrentHashMap<UUID, Quarantine> pending = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<UUID, ServerPlayer> activePlayers = new ConcurrentHashMap<>();
    private volatile MinecraftServer server;
    private volatile ConnectionCoordinator coordinator;

    public SolarisForgeBridge() {
        instance = this;
        MinecraftForge.EVENT_BUS.register(this);
    }

    /** Safe to call from a Netty packet handler: no world mutation or IO. */
    public static boolean isRestricted(ServerPlayer player) {
        SolarisForgeBridge bridge = instance;
        ConnectionCoordinator current = bridge == null ? null : bridge.coordinator;
        return player == null || bridge == null || current == null
            || bridge.activePlayers.get(player.getUUID()) != player
            || bridge.pending.containsKey(player.getUUID())
            || !current.isAuthenticated(player.getUUID());
    }

    /** Public ecosystem hook; never derive this value from nickname/offline UUID. */
    public static Optional<UUID> solarisId(ServerPlayer player) {
        SolarisForgeBridge bridge = instance;
        ConnectionCoordinator current = bridge == null ? null : bridge.coordinator;
        if (current == null || player == null || bridge.activePlayers.get(player.getUUID()) != player
                || bridge.pending.containsKey(player.getUUID())) return Optional.empty();
        return current.solarisId(player.getUUID());
    }

    @SubscribeEvent
    public void onServerStarted(ServerStartedEvent event) {
        server = event.getServer();
        try {
            String origin = requiredEnvironment("SOLARIS_ID_API_ORIGIN");
            String serverId = requiredEnvironment("SOLARIS_SERVER_ID");
            String key = requiredEnvironment("SOLARIS_SERVER_KEY");
            coordinator = new ConnectionCoordinator(
                new HttpBridgeApi(URI.create(origin), serverId, key), this, Clock.systemUTC(), 256);
            LOG.info("Solaris ID bridge enabled; protected gameplay requires HTTPS approval.");
        } catch (RuntimeException invalidConfiguration) {
            // Never print exception text: URI/header validation can contain input.
            coordinator = null;
            LOG.error("Solaris ID bridge is not configured; player access is denied.");
        }
    }

    private static String requiredEnvironment(String name) {
        String value = System.getenv(name);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing bridge configuration");
        return value;
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onJoin(PlayerEvent.PlayerLoggedInEvent event) {
        if (!(event.getEntity() instanceof ServerPlayer player)) return;
        // Publish quarantine before the new exact-player binding. Otherwise a
        // Netty packet could briefly observe a predecessor's approved UUID.
        Quarantine hold = new Quarantine(player);
        Quarantine previous = pending.put(player.getUUID(), hold);
        activePlayers.put(player.getUUID(), player);
        if (previous != null) previous.restore();
        ConnectionCoordinator current = coordinator;
        if (current == null) {
            releaseAndDisconnect(player, "Solaris ID недоступен. Вход на сервер закрыт.");
            return;
        }
        player.stopRiding();
        player.stopUsingItem();
        player.closeContainer();
        player.setDeltaMovement(Vec3.ZERO);
        player.fallDistance = 0;
        player.displayClientMessage(Component.literal("Подтвердите подключение в Solaris Launcher. Ожидаем код…"), false);
        try {
            current.join(player.getUUID(), player.getGameProfile().getName());
        } catch (RuntimeException rejected) {
            releaseAndDisconnect(player, "Solaris ID недоступен. Повторите подключение позднее.");
        }
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onLeave(PlayerEvent.PlayerLoggedOutEvent event) {
        if (!(event.getEntity() instanceof ServerPlayer player)) return;
        if (!activePlayers.remove(player.getUUID(), player)) return;
        Quarantine hold = pending.get(player.getUUID());
        if (hold != null && hold.player == player && pending.remove(player.getUUID(), hold)) hold.restore();
        ConnectionCoordinator current = coordinator;
        if (current != null) current.leave(player.getUUID());
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onRespawn(PlayerEvent.Clone event) {
        if (!(event.getOriginal() instanceof ServerPlayer original)
                || !(event.getEntity() instanceof ServerPlayer replacement)) return;
        if (!activePlayers.replace(original.getUUID(), original, replacement)) return;
        // Normal authenticated respawns retain the same live transport connection.
        // A pending/dead connection never gains approval by replacing its entity.
        Quarantine hold = pending.get(original.getUUID());
        if (hold != null && hold.player == original)
            pending.replace(original.getUUID(), hold, new Quarantine(replacement));
    }

    @SubscribeEvent
    public void onServerStopping(ServerStoppingEvent event) {
        ConnectionCoordinator current = coordinator;
        coordinator = null;
        if (current != null) current.close();
        pending.values().forEach(Quarantine::restore);
        pending.clear();
        activePlayers.clear();
        server = null;
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onPlayerTick(TickEvent.PlayerTickEvent event) {
        if (event.phase != TickEvent.Phase.END || !(event.player instanceof ServerPlayer player)) return;
        if (!isRestricted(player)) return;
        Quarantine hold = pending.get(player.getUUID());
        if (hold == null || hold.player != player) return;
        player.stopRiding();
        player.stopUsingItem();
        player.setDeltaMovement(Vec3.ZERO);
        player.fallDistance = 0;
        if (player.containerMenu != player.inventoryMenu) player.closeContainer();
        if (!player.level().dimension().equals(hold.dimension)) {
            ServerLevel original = player.server.getLevel(hold.dimension);
            if (original == null) {
                releaseAndDisconnect(player, "Безопасная зона Solaris ID недоступна.");
                return;
            }
            player.teleportTo(original, hold.position.x, hold.position.y, hold.position.z, hold.yaw, hold.pitch);
        } else if (player.position().distanceToSqr(hold.position) > 0.0001) {
            player.connection.teleport(hold.position.x, hold.position.y, hold.position.z, hold.yaw, hold.pitch);
        }
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onCommand(CommandEvent event) {
        if (event.getParseResults().getContext().getSource().getEntity() instanceof ServerPlayer player
                && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onChat(ServerChatEvent event) {
        if (isRestricted(event.getPlayer())) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onInteraction(PlayerInteractEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player) && event.isCancelable())
            event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onAttack(AttackEntityEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onBlockBreak(BlockEvent.BreakEvent event) {
        if (event.getPlayer() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onBlockPlace(BlockEvent.EntityPlaceEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onPickup(EntityItemPickupEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onExperience(PlayerXpEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player) && event.isCancelable())
            event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onDrop(ItemTossEvent event) {
        if (event.getPlayer() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onContainerOpen(PlayerContainerEvent.Open event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player)) player.closeContainer();
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onMount(EntityMountEvent event) {
        if (event.isMounting() && event.getEntityMounting() instanceof ServerPlayer player && isRestricted(player))
            event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onDimensionTravel(EntityTravelToDimensionEvent event) {
        if (event.getEntity() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onProjectileSpawn(EntityJoinLevelEvent event) {
        if (!event.getLevel().isClientSide && event.getEntity() instanceof Projectile projectile
                && projectile.getOwner() instanceof ServerPlayer player && isRestricted(player)) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onProjectileImpact(ProjectileImpactEvent event) {
        if (event.getProjectile().getOwner() instanceof ServerPlayer player && isRestricted(player)) {
            event.setImpactResult(ProjectileImpactEvent.ImpactResult.STOP_AT_CURRENT_NO_DAMAGE);
            event.getProjectile().discard();
        }
    }

    private static boolean restrictedEntity(Entity entity) {
        return entity instanceof ServerPlayer player && isRestricted(player);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onLivingAttack(LivingAttackEvent event) {
        if (restrictedEntity(event.getEntity()) || restrictedEntity(event.getSource().getEntity())) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onLivingHurt(LivingHurtEvent event) {
        if (restrictedEntity(event.getEntity()) || restrictedEntity(event.getSource().getEntity())) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onLivingDamage(LivingDamageEvent event) {
        if (restrictedEntity(event.getEntity()) || restrictedEntity(event.getSource().getEntity())) event.setCanceled(true);
    }

    @SubscribeEvent(priority = EventPriority.HIGHEST)
    public void onLivingDeath(LivingDeathEvent event) {
        if (restrictedEntity(event.getEntity())) event.setCanceled(true);
    }

    @Override
    public void dispatch(Runnable action) {
        MinecraftServer current = server;
        if (current != null && !current.isStopped()) current.execute(action);
    }

    @Override
    public void challenge(UUID playerId, String code, Instant expiresUtc) {
        ServerPlayer player = currentPendingPlayer(playerId);
        if (player == null) return;
        // Code is only a locator. The actual one-use pass stays in launcher HTTPS.
        player.displayClientMessage(Component.literal("SOLARIS ID: код " + code
                + ". Подтвердите его в Solaris Launcher. Не отправляйте пароль в Minecraft."), false);
        player.displayClientMessage(Component.literal("SOLARIS ID / " + code), true);
    }

    @Override
    public void authenticated(UUID playerId, UUID solarisUserId, String nickname) {
        ServerPlayer player = currentPendingPlayer(playerId);
        if (player == null) return;
        Quarantine hold = pending.remove(playerId);
        if (hold != null) hold.restore();
        player.displayClientMessage(Component.literal("Solaris ID подтверждён. Добро пожаловать, " + nickname + "."), false);
    }

    @Override
    public void kick(UUID playerId, String safeReason) {
        MinecraftServer current = server;
        if (current == null) return;
        ServerPlayer player = current.getPlayerList().getPlayer(playerId);
        if (player != null) releaseAndDisconnect(player, "Solaris ID: доступ не подтверждён или сессия отозвана.");
    }

    private ServerPlayer currentPendingPlayer(UUID playerId) {
        MinecraftServer current = server;
        if (current == null) return null;
        Quarantine hold = pending.get(playerId);
        ServerPlayer online = current.getPlayerList().getPlayer(playerId);
        return hold != null && hold.player == online && activePlayers.get(playerId) == online ? online : null;
    }

    private void releaseAndDisconnect(ServerPlayer player, String message) {
        ConnectionCoordinator current = coordinator;
        if (activePlayers.remove(player.getUUID(), player) && current != null) current.leave(player.getUUID());
        Quarantine hold = pending.get(player.getUUID());
        if (hold != null && hold.player == player && pending.remove(player.getUUID(), hold)) hold.restore();
        player.connection.disconnect(Component.literal(message));
    }

    private static final class Quarantine {
        final ServerPlayer player;
        final ResourceKey<Level> dimension;
        final Vec3 position;
        final float yaw;
        final float pitch;

        Quarantine(ServerPlayer player) {
            this.player = player;
            dimension = player.level().dimension();
            position = player.position();
            yaw = player.getYRot();
            pitch = player.getXRot();
        }

        void restore() {
            // No game mode, inventory, no-gravity or invulnerability flag is
            // mutated. Autosave/crashes must not persist temporary privileges.
            player.setDeltaMovement(Vec3.ZERO);
        }
    }
}
