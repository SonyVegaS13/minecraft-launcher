package com.solaris.id.bridge.forge.mixin;

import com.solaris.id.bridge.forge.SolarisForgeBridge;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.network.ServerGamePacketListenerImpl;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * Events alone do not cover inventory/creative-slot or arbitrary mod packets.
 * This required, version-pinned gate runs before any gameplay handler. It only
 * reads thread-safe authentication state; world mutations remain on server tick.
 * Keepalive, client settings, teleport acknowledgement and disconnect continue.
 * A missing injection is a startup error, never an authentication bypass.
 */
@Mixin(ServerGamePacketListenerImpl.class)
public abstract class ServerGameplayGateMixin {
    @Shadow public ServerPlayer player;

    @Inject(method = "handleContainerClose", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleContainerClose(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleAnimate", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleAnimate(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    // One injector per target: require=1 must hold for EVERY handler. A single
    // annotation containing many method names would only count total matches.
    @Inject(method = "handleMovePlayer", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleMovePlayer(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleMoveVehicle", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleMoveVehicle(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePlayerInput", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePlayerInput(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePlayerAction", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePlayerAction(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleUseItemOn", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleUseItemOn(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleUseItem", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleUseItem(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleInteract", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleInteract(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleContainerClick", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleContainerClick(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleContainerButtonClick", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleContainerButtonClick(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetCreativeModeSlot", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetCreativeModeSlot(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetCarriedItem", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetCarriedItem(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePlayerCommand", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePlayerCommand(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePlayerAbilities", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePlayerAbilities(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleEditBook", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleEditBook(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleRenameItem", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleRenameItem(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSignUpdate", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSignUpdate(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetCommandBlock", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetCommandBlock(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetCommandMinecart", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetCommandMinecart(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetJigsawBlock", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetJigsawBlock(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleJigsawGenerate", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleJigsawGenerate(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetBeaconPacket", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetBeaconPacket(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSetStructureBlock", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSetStructureBlock(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePickItem", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePickItem(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePlaceRecipe", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePlaceRecipe(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSelectTrade", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSelectTrade(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handlePaddleBoat", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handlePaddleBoat(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleTeleportToEntityPacket", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleTeleportToEntityPacket(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleClientCommand", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleClientCommand(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleCustomPayload", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleCustomPayload(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleCustomCommandSuggestions", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleCustomCommandSuggestions(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleBlockEntityTagQuery", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleBlockEntityTagQuery(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleEntityTagQuery", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleEntityTagQuery(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleRecipeBookChangeSettingsPacket", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleRecipeBookChangeSettingsPacket(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleRecipeBookSeenRecipePacket", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleRecipeBookSeenRecipePacket(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleSeenAdvancements", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleSeenAdvancements(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleChangeDifficulty", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleChangeDifficulty(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

    @Inject(method = "handleLockDifficulty", at = @At("HEAD"), cancellable = true, require = 1)
    private void solaris_handleLockDifficulty(CallbackInfo callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.cancel();
    }

}
