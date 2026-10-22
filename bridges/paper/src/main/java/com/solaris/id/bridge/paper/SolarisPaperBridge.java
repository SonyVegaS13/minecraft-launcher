package com.solaris.id.bridge.paper;

import com.solaris.id.bridge.BridgeCallbacks;
import com.solaris.id.bridge.ConnectionCoordinator;
import com.solaris.id.bridge.HttpBridgeApi;
import com.destroystokyo.paper.event.player.PlayerAdvancementCriterionGrantEvent;
import com.destroystokyo.paper.event.player.PlayerPickupExperienceEvent;
import io.papermc.paper.event.player.AsyncChatEvent;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.entity.Projectile;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.BlockBreakEvent;
import org.bukkit.event.block.BlockDamageEvent;
import org.bukkit.event.block.BlockPlaceEvent;
import org.bukkit.event.block.SignChangeEvent;
import org.bukkit.event.entity.*;
import org.bukkit.event.inventory.*;
import org.bukkit.event.player.*;
import org.bukkit.event.vehicle.*;
import org.bukkit.plugin.java.JavaPlugin;
import org.spigotmc.event.entity.EntityMountEvent;

import java.net.URI;
import java.time.Clock;
import java.time.Instant;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/** Server-only Paper 1.20.1 plugin. The client remains unmodified. */
public final class SolarisPaperBridge extends JavaPlugin implements Listener, BridgeCallbacks {
    private final ConcurrentHashMap<UUID, Quarantine> pending = new ConcurrentHashMap<>();
    private volatile ConnectionCoordinator coordinator;

    @Override public void onEnable() {
        getServer().getPluginManager().registerEvents(this, this);
        try {
            coordinator = new ConnectionCoordinator(new HttpBridgeApi(
                URI.create(requiredEnvironment("SOLARIS_ID_API_ORIGIN")),
                requiredEnvironment("SOLARIS_SERVER_ID"), requiredEnvironment("SOLARIS_SERVER_KEY")),
                this, Clock.systemUTC(), 256);
            getLogger().info("Solaris ID enabled; protected gameplay requires HTTPS approval.");
        } catch (RuntimeException invalidConfiguration) {
            // URI/header exceptions can contain input. Never log exception or config values.
            getLogger().severe("Solaris ID configuration unavailable; player access is denied.");
        }
        // Handles safe reloads without trusting players who were already online.
        for (Player player : getServer().getOnlinePlayers()) quarantine(player);
        getServer().getScheduler().runTaskTimer(this, () -> {
            for (Quarantine hold : pending.values()) {
                Player player = hold.player;
                if (!player.isOnline()) continue;
                player.setVelocity(new org.bukkit.util.Vector());
                player.setFallDistance(0);
                player.leaveVehicle();
                player.closeInventory();
                // Events prevent normal moves and teleports. A server/plugin direct
                // world transfer cannot leave a quarantined player in gameplay.
                if (player.getWorld() != hold.location.getWorld()) disconnect(player);
            }
        }, 1, 1);
    }

    private static String requiredEnvironment(String name) {
        String value = System.getenv(name);
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing Solaris configuration");
        return value;
    }

    /** Integration API: stable Solaris account ID, present only for this approved live connection. */
    public Optional<UUID> solarisId(Player player) {
        ConnectionCoordinator current = coordinator;
        return current == null || restricted(player) ? Optional.empty() : current.solarisId(player.getUniqueId());
    }

    private boolean restricted(Player player) {
        ConnectionCoordinator current = coordinator;
        return current == null || pending.containsKey(player.getUniqueId())
            || !current.isAuthenticated(player.getUniqueId());
    }
    private boolean restricted(Entity entity) {
        if (entity instanceof Player player) return restricted(player);
        return entity instanceof Projectile projectile && projectile.getShooter() instanceof Player player && restricted(player);
    }
    private void quarantine(Player player) {
        ConnectionCoordinator current = coordinator;
        pending.put(player.getUniqueId(), new Quarantine(player, player.getLocation().clone()));
        player.leaveVehicle();
        player.closeInventory();
        player.setVelocity(new org.bukkit.util.Vector());
        player.setFallDistance(0);
        if (current == null) { disconnect(player); return; }
        player.sendMessage("Подтвердите подключение в Solaris Launcher. Ожидаем код…");
        try { current.join(player.getUniqueId(), player.getName()); }
        catch (RuntimeException rejected) { disconnect(player); }
    }

    @EventHandler(priority = EventPriority.LOWEST)
    public void onJoin(PlayerJoinEvent event) { quarantine(event.getPlayer()); }
    @EventHandler(priority = EventPriority.MONITOR)
    public void onQuit(PlayerQuitEvent event) {
        Player player = event.getPlayer();
        Quarantine hold = pending.get(player.getUniqueId());
        // An old connection's quit must not remove a replacement connection.
        Player online = Bukkit.getPlayer(player.getUniqueId());
        if (online != null && online != player) return;
        if (hold != null && hold.player == player) pending.remove(player.getUniqueId(), hold);
        ConnectionCoordinator current = coordinator;
        if (current != null) current.leave(player.getUniqueId());
    }
    @Override public void onDisable() {
        ConnectionCoordinator current = coordinator;
        coordinator = null;
        if (current != null) current.close();
        // Removing the listener must never release unauthenticated connections.
        for (Player player : getServer().getOnlinePlayers())
            player.kickPlayer("Solaris ID остановлен. Подключитесь после запуска проверки доступа.");
        pending.clear();
    }

    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onMove(PlayerMoveEvent event) {
        if (!restricted(event.getPlayer())) return;
        Quarantine hold = pending.get(event.getPlayer().getUniqueId());
        if (hold != null && hold.player == event.getPlayer()) event.setTo(hold.location.clone());
        else event.setCancelled(true);
    }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onTeleport(PlayerTeleportEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onCommand(PlayerCommandPreprocessEvent event) {
        if (!restricted(event.getPlayer())) return;
        event.setCancelled(true);
        if (event.getMessage().trim().equalsIgnoreCase("/quit")) event.getPlayer().kickPlayer("Отключено.");
    }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onChat(AsyncChatEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    @SuppressWarnings("deprecation")
    public void onLegacyChat(AsyncPlayerChatEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInteract(PlayerInteractEvent event) {
        if (!restricted(event.getPlayer())) return;
        event.setCancelled(true);
        event.setUseInteractedBlock(org.bukkit.event.Event.Result.DENY);
        event.setUseItemInHand(org.bukkit.event.Event.Result.DENY);
    }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInteractEntity(PlayerInteractEntityEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInteractAtEntity(PlayerInteractAtEntityEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBreak(BlockBreakEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBlockDamage(BlockDamageEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onPlace(BlockPlaceEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onSign(SignChangeEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInventoryClick(InventoryClickEvent event) { if (event.getWhoClicked() instanceof Player player && restricted(player)) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onCreative(InventoryCreativeEvent event) { if (event.getWhoClicked() instanceof Player player && restricted(player)) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInventoryDrag(InventoryDragEvent event) { if (event.getWhoClicked() instanceof Player player && restricted(player)) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onInventoryOpen(InventoryOpenEvent event) { if (event.getPlayer() instanceof Player player && restricted(player)) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDamage(EntityDamageEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onAttack(EntityDamageByEntityEvent event) { if (restricted(event.getEntity()) || restricted(event.getDamager())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onTarget(EntityTargetLivingEntityEvent event) { if (event.getTarget() != null && restricted(event.getTarget())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onProjectile(ProjectileLaunchEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onShoot(EntityShootBowEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onPickup(EntityPickupItemEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onAttemptPickup(PlayerAttemptPickupItemEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onArrowPickup(PlayerPickupArrowEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onExperiencePickup(PlayerPickupExperienceEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onDrop(PlayerDropItemEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onSwap(PlayerSwapHandItemsEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onHeld(PlayerItemHeldEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onConsume(PlayerItemConsumeEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBucketFill(PlayerBucketFillEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBucketEmpty(PlayerBucketEmptyEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onHarvest(PlayerHarvestBlockEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onLectern(PlayerTakeLecternBookEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onFish(PlayerFishEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onArmorStand(PlayerArmorStandManipulateEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBook(PlayerEditBookEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onShear(PlayerShearEntityEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onLeash(PlayerLeashEntityEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onUnleash(PlayerUnleashEntityEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onBed(PlayerBedEnterEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onVehicleEnter(VehicleEnterEvent event) { if (restricted(event.getEntered())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onMount(EntityMountEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onVehicleDamage(VehicleDamageEvent event) { if (event.getAttacker() != null && restricted(event.getAttacker())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onVehicleDestroy(VehicleDestroyEvent event) { if (event.getAttacker() != null && restricted(event.getAttacker())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onFlight(PlayerToggleFlightEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onSprint(PlayerToggleSprintEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onSneak(PlayerToggleSneakEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onGlide(EntityToggleGlideEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onGameMode(PlayerGameModeChangeEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onMend(PlayerItemMendEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onItemDamage(PlayerItemDamageEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onFood(FoodLevelChangeEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onRegainHealth(EntityRegainHealthEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onTrample(EntityInteractEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onChangeBlock(EntityChangeBlockEvent event) { if (restricted(event.getEntity())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onStatistic(PlayerStatisticIncrementEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onExperience(PlayerExpChangeEvent event) { if (restricted(event.getPlayer())) event.setAmount(0); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onAdvancement(PlayerAdvancementCriterionGrantEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }
    @EventHandler(priority = EventPriority.HIGHEST, ignoreCancelled = false)
    public void onRecipe(PlayerRecipeDiscoverEvent event) { if (restricted(event.getPlayer())) event.setCancelled(true); }

    @Override public void dispatch(Runnable action) {
        if (!isEnabled()) return;
        try { getServer().getScheduler().runTask(this, action); }
        catch (org.bukkit.plugin.IllegalPluginAccessException stopping) { /* Shutdown already denies access. */ }
    }
    @Override public void challenge(UUID playerId, String code, Instant expiresUtc) {
        Player player = pendingPlayer(playerId);
        if (player != null) player.sendMessage("SOLARIS ID: код " + code
            + ". Подтвердите его в Solaris Launcher. Не отправляйте пароль в Minecraft.");
    }
    @Override public void authenticated(UUID playerId, UUID solarisUserId, String nickname) {
        Player player = pendingPlayer(playerId);
        if (player == null) return;
        pending.remove(playerId);
        player.sendMessage("Solaris ID подтверждён. Добро пожаловать, " + nickname + ".");
    }
    @Override public void kick(UUID playerId, String safeReason) {
        Player player = Bukkit.getPlayer(playerId);
        if (player != null) disconnect(player);
    }
    private Player pendingPlayer(UUID playerId) {
        Quarantine hold = pending.get(playerId);
        return hold != null && hold.player == Bukkit.getPlayer(playerId) ? hold.player : null;
    }
    private void disconnect(Player player) {
        pending.remove(player.getUniqueId());
        ConnectionCoordinator current = coordinator;
        if (current != null) current.leave(player.getUniqueId());
        player.kickPlayer("Solaris ID: доступ не подтверждён или сессия отозвана. Повторите вход позднее.");
    }
    private record Quarantine(Player player, Location location) {}
}
