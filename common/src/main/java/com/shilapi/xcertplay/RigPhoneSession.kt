package com.shilapi.xcertplay

import android.content.Context
import android.content.Intent
import com.shilapi.xcertplay.network.CarHotspotStatus
import com.shilapi.xcertplay.orchestration.ManualHotspotValidation
import com.shilapi.xcertplay.orchestration.WirelessHotspotMode

/**
 * The real [RigSessionLifecycle.PhoneSession]: [CarPlayBackgroundSession] plus the same start path as
 * the "Connect phone" button (open [CarPlayHostActivity], which starts the controller once the
 * display size is known), honouring the chosen iPhone and the saved wired/wireless mode.
 */
internal class RigPhoneSession(private val context: Context) : RigSessionLifecycle.PhoneSession {
    override fun hasSession(): Boolean = CarPlayBackgroundSession.hasSession()
    override fun isConnected(): Boolean = CarPlayBackgroundSession.active

    override fun phoneName(): String? =
        RigPlayPreferences.phoneAddress(context)?.let { RigPlayPreferences.phoneName(context) }

    override fun autoStartBlocker(): String? = autoStartBlocker(context)

    override fun start() {
        context.startActivity(
            Intent(context, CarPlayHostActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT),
        )
    }

    override fun stop(completion: () -> Unit) = CarPlayBackgroundSession.stop(completion)

    companion object {
        /**
         * What would make "Connect phone" ask the user something; `null` when it can start silently.
         * Mirrors the checks in RigPlayActivity.connect, which shows the matching dialog instead.
         */
        fun autoStartBlocker(
            context: Context,
            authenticationReady: () -> Boolean = { runCatching { RigPlayBootstrap.ensure(context) }.isSuccess },
        ): String? {
            if (!RigPlayPreferences.autoConnect(context)) return "automatic connection is off"
            if (!authenticationReady()) return "CarPlay authentication unavailable"
            if (!AirPlayPersistence.loadWirelessEnabled(context)) return null
            if (RigPlayPreferences.phoneAddress(context) == null) return "no iPhone chosen"
            if (AirPlayPersistence.loadWirelessHotspotMode(context) == WirelessHotspotMode.MANUAL) {
                val ssid = AirPlayPersistence.loadManualHotspotSsid(context)
                val passphrase = AirPlayPersistence.loadManualHotspotPassphrase(context)
                if (ManualHotspotValidation.error(ssid, passphrase) != null) return "hotspot details missing"
                if (CarHotspotStatus.isEnabled(context) == false) return "hotspot is off"
            }
            return null
        }
    }
}
