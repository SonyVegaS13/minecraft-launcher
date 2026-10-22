package com.solaris.id.bridge.forge.mixin;

import com.solaris.id.bridge.forge.SolarisForgeBridge;
import net.minecraft.server.PlayerAdvancements;
import net.minecraft.server.level.ServerPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Forge advancement notifications fire after the change and cannot cancel it. */
@Mixin(PlayerAdvancements.class)
public abstract class AdvancementGateMixin {
    @Shadow private ServerPlayer player;

    @Inject(method = "award", at = @At("HEAD"), cancellable = true, require = 1)
    private void solarisBlockUnauthenticatedAward(CallbackInfoReturnable<Boolean> callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.setReturnValue(false);
    }
    @Inject(method = "revoke", at = @At("HEAD"), cancellable = true, require = 1)
    private void solarisBlockUnauthenticatedRevoke(CallbackInfoReturnable<Boolean> callback) {
        if (SolarisForgeBridge.isRestricted(player)) callback.setReturnValue(false);
    }
}
