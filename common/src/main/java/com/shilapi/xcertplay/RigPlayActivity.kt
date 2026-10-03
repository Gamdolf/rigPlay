// SPDX-License-Identifier: AGPL-3.0-only
// UI copy and visual language adapted from DiAuto. See docs/THIRD_PARTY_NOTICES.md.
package com.shilapi.xcertplay

import android.Manifest
import android.app.AlertDialog
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.AudioFormat
import android.media.AudioTrack
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.*
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import com.shilapi.xcertplay.host.R
import com.shilapi.xcertplay.orchestration.WirelessHotspotMode
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** DiAuto's visual language, with a connection flow for an independent CarPlay receiver. */
class RigPlayActivity : ComponentActivity() {
    private val handler = Handler(Looper.getMainLooper())
    private var page = "home"
    private var pendingCarHotspotSetup = false
    private var setupError: String? = null
    private var status: TextView? = null
    private var connectButton: Button? = null
    private var disconnectButton: Button? = null
    private var lastRunning: Boolean? = null
    private var pendingWireless = false
    private var initialLaunch = true
    private var notificationTransport = true
    private var exportInProgress = false
    private var navigationStreamType = 14
    private var testToneTrack: AudioTrack? = null
    private var toneStop: Runnable? = null
    private var exportButton: Button? = null
    private var simhubContainer: LinearLayout? = null
    private var simhubRendered: Any? = null
    private var simhubStatusView: TextView? = null
    private var homeSimHubStatus: TextView? = null
    private val simhubObserver: () -> Unit = { onSimHubChanged() }
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        connect(notificationTransport)
    }
    private val tick = object : Runnable {
        override fun run() { refreshStatus(); handler.postDelayed(this, 1000) }
    }
    private val bluetoothPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) choosePhone() else permissionHelp(getString(R.string.nearby_devices), getString(R.string.allow_nearby_devices_so_rigplay_can_connect_to_your_paired))
    }
    private val locationPermission = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (hasPreciseLocation()) return@registerForActivityResult reconnectForLocation()
        AirPlayPersistence.saveLocationReportingEnabled(this, false)
        render()
        permissionHelp(getString(R.string.location), getString(R.string.allow_precise_location_for_rigplay_in_the_head_unit_s_app_p))
    }
    private val export = registerForActivityResult(ActivityResultContracts.CreateDocument("text/plain")) { uri ->
        if (uri != null) exportDiagnostics(uri)
    }

    private var languagePreferenceAtCreate = AppLocale.SYSTEM

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(AppLocale.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        languagePreferenceAtCreate = AppLocale.preference(this)
        window.addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.statusBarColor = BG; window.navigationBarColor = BG
        RigTabletWindow.immersive(window)
        setupError = runCatching { RigPlayBootstrap.ensure(this) }.exceptionOrNull()?.let {
            android.util.Log.e("RigPlaySetup", "CarPlay authentication could not be loaded", it)
            getString(R.string.setup_error_auth)
        }
        pendingCarHotspotSetup = savedInstanceState?.getBoolean("pending_car_hotspot") ?: false
        RigSessionCoordinator.init(this)
        page = routedPage(savedInstanceState?.getString("page") ?: intent.getStringExtra("page"))
        render()
        handleWirelessRecovery()
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (page != "home") showPage("home")
                else { isEnabled = false; onBackPressedDispatcher.onBackPressed(); isEnabled = true }
            }
        })
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent); setIntent(intent)
        page = routedPage(intent.getStringExtra("page")); render()
        handleWirelessRecovery()
    }
    override fun onSaveInstanceState(outState: Bundle) { outState.putString("page", page); outState.putBoolean("pending_car_hotspot", pendingCarHotspotSetup); super.onSaveInstanceState(outState) }
    override fun onConfigurationChanged(newConfig: Configuration) { super.onConfigurationChanged(newConfig); render() }
    override fun onResume() {
        super.onResume()
        if (Build.VERSION.SDK_INT < 33 && AppLocale.preference(this) != languagePreferenceAtCreate) {
            recreate()
            return
        }
        handler.removeCallbacks(tick); handler.post(tick)
        RigSessionCoordinator.addObserver(simhubObserver)
        RigSessionCoordinator.setOnboardingVisible(page == "simhub")
        // Back from the car settings: refresh the car hotspot reminder on the home page.
        if (!initialLaunch && (page == "home" || page == "settings" || page == "connection")) render()
        if (initialLaunch) {
            initialLaunch = false
            if (setupError == null && !CarPlayBackgroundSession.hasSession() &&
                RigPlayPreferences.autoConnect(this) && intent.getStringExtra("page") == null && page == "home" &&
                // With a paired PC the coordinator connects the phone when SimHub comes up (#29).
                !RigSessionCoordinator.isPaired) {
                handler.post { connect(AirPlayPersistence.loadWirelessEnabled(this)) }
            }
        }
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) RigTabletWindow.immersive(window)
    }

    override fun onPause() {
        handler.removeCallbacks(tick)
        RigSessionCoordinator.removeObserver(simhubObserver)
        RigSessionCoordinator.setOnboardingVisible(false)
        super.onPause()
    }

    /** Launch routing (#27): unpaired → SimHub onboarding, paired → the requested page or home. */
    private fun routedPage(requested: String?): String {
        val paired = RigSessionCoordinator.isPaired
        return when {
            requested == "simhub" && paired -> "home"
            requested != null -> requested
            paired || RigSessionCoordinator.onboardingSkipped -> "home"
            else -> "simhub"
        }
    }

    private fun showPage(next: String) {
        page = next
        RigSessionCoordinator.setOnboardingVisible(next == "simhub")
        render()
    }

    private fun onSimHubChanged() {
        if (isFinishing || isDestroyed) return
        when {
            page == "simhub" && RigSessionCoordinator.pairingStep is SimHubPairingFlow.Step.Paired &&
                RigSessionCoordinator.isPaired -> {
                toast(getString(R.string.rig_simhub_paired_toast, RigSessionCoordinator.pairing?.name ?: ""))
                showPage("home")
            }
            page == "simhub" -> refreshSimHubPage()
            // Token revoked or forgotten on the PC (§8): back to onboarding.
            !RigSessionCoordinator.isPaired && !RigSessionCoordinator.onboardingSkipped && page == "home" -> {
                toast(getString(R.string.rig_simhub_unpaired_toast))
                showPage("simhub")
            }
            else -> refreshStatus()
        }
    }

    private fun render() {
        status = null; connectButton = null; disconnectButton = null; lastRunning = null
        simhubContainer = null; simhubRendered = null; simhubStatusView = null; homeSimHubStatus = null
        val scroll = ScrollView(this).apply { setBackgroundColor(BG); isFillViewport = true; clipToPadding = false }
        val content = column().apply { setPadding(dp(32), dp(24), dp(32), dp(32)) }
        scroll.addView(content)
        val header = row().apply { gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply { setImageResource(R.drawable.ic_rigplay); contentDescription = getString(R.string.carplay) }, LinearLayout.LayoutParams(dp(36), dp(36)))
        header.addView(label(getString(R.string.rigplay), 26, TEXT, true).apply { setPadding(dp(12), 0, 0, 0) }, LinearLayout.LayoutParams(0, dp(56), 1f))
        if (page != "home") {
            header.addView(button(getString(R.string.back), false) { showPage("home") }, LinearLayout.LayoutParams(dp(130), dp(56)))
        }
        content.addView(header)
        content.addView(space(24))
        when (page) {
            "connection" -> connectionSetup(content)
            "simhub" -> simHubOnboarding(content)
            "settings" -> settings(content)
            "about" -> about(content)
            else -> home(content)
        }
        setContentView(scroll)
        refreshStatus()
    }

    /** The rig control panel (#28): SimHub and iPhone status on the left, actions on the right. */
    private fun home(content: LinearLayout) {
        val wide = resources.configuration.screenWidthDp >= 850
        val statusColumn = column()

        val simhubCard = card()
        simhubCard.addView(label(getString(R.string.rig_home_simhub_label), 12, ACCENT, true).apply { letterSpacing = .12f })
        homeSimHubStatus = label(simHubStatusText(), 24, TEXT, true).apply {
            setPadding(0, dp(10), 0, 0); accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        simhubCard.addView(homeSimHubStatus)
        if (!RigSessionCoordinator.isPaired) {
            simhubCard.addView(button(getString(R.string.rig_settings_simhub_pair), false) {
                RigSessionCoordinator.onboardingSkipped = false
                showPage("simhub")
            }, matchButton(16, 56))
        }
        statusColumn.addView(simhubCard)
        statusColumn.addView(space(18))

        val phoneCard = card()
        phoneCard.addView(label(getString(R.string.rig_home_phone_label), 12, ACCENT, true).apply { letterSpacing = .12f })
        status = label(getString(R.string.ready_when_you_are), 24, TEXT, true).apply {
            setPadding(0, dp(10), 0, 0); accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        phoneCard.addView(status)
        val connectionHint = when (AirPlayPersistence.loadWirelessHotspotMode(this)) {
            WirelessHotspotMode.MANUAL -> getString(R.string.hotspot_hint_manual)
            WirelessHotspotMode.LOCAL_ONLY_HOTSPOT -> getString(R.string.hotspot_hint_local)
            else -> getString(R.string.hotspot_hint_p2p)
        }
        phoneCard.addView(label(connectionHint, 15, MUTED).apply { setPadding(0, dp(12), 0, 0) })
        if (carHotspotOff()) {
            phoneCard.addView(label(getString(R.string.msg_car_hotspot_off, AirPlayPersistence.loadManualHotspotSsid(this)), 15, WARNING).apply { setPadding(0, dp(14), 0, 0) })
            phoneCard.addView(button(getString(R.string.open_car_hotspot_settings), false) { openCarWifiSettings() }, matchButton(10, 56))
        }
        disconnectButton = button(getString(R.string.disconnect), false) {
            disconnectButton?.isEnabled = false
            CarPlayBackgroundSession.stop { runOnUiThread { refreshStatus() } }
        }.apply { visibility = View.GONE }
        phoneCard.addView(disconnectButton, matchButton(16, 56))
        statusColumn.addView(phoneCard)
        setupError?.let { statusColumn.addView(label(it, 16, WARNING).apply { setPadding(0, dp(16), 0, 0) }) }

        val actions = column()
        connectButton = button(getString(R.string.connect_phone), true) {
            if (CarPlayBackgroundSession.hasSession()) openProjection()
            else connect(true)
        }
        actions.addView(connectButton, matchButton())
        actions.addView(button(getString(R.string.connect_with_usb), false) { connect(false) }, matchButton(12, 60))
        actions.addView(button(getString(R.string.choose_iphone), false) { choosePhone() }, matchButton(12, 60))
        homeExtraActions(actions)
        actions.addView(button(getString(R.string.settings), false) { showPage("settings") }, matchButton(12, 60))
        actions.addView(button(getString(R.string.about), false) { showPage("about") }, matchButton(12, 60))
        actions.addView(label("${getString(R.string.home_public_preview)}${version()}", 12, MUTED).apply {
            letterSpacing = .08f; gravity = Gravity.CENTER; setPadding(0, dp(16), 0, 0)
        })

        if (wide) {
            content.addView(row().apply {
                gravity = Gravity.TOP
                addView(statusColumn, LinearLayout.LayoutParams(0, -2, 1.6f))
                addView(space(40), LinearLayout.LayoutParams(dp(40), 1))
                addView(actions, LinearLayout.LayoutParams(0, -2, 1f))
            })
        } else {
            content.addView(statusColumn)
            content.addView(space(24))
            content.addView(actions)
        }
    }

    /** The SimHub button (#30): the dashboard chosen in SimHub. */
    private fun homeExtraActions(actions: LinearLayout) {
        actions.addView(button(getString(R.string.rig_dashboard_simhub), false) {
            RigSessionCoordinator.showDashboard(this)
        }, matchButton(12, 60))
    }

    private fun phoneStatusText(): String {
        val running = CarPlayBackgroundSession.hasSession()
        val chosen = RigPlayPreferences.phoneAddress(this) != null
        val name = RigPlayPreferences.phoneName(this)
        return when {
            setupError != null -> getString(R.string.setup_needs_attention)
            CarPlayBackgroundSession.active -> if (chosen) getString(R.string.rig_home_phone_connected, name) else getString(R.string.carplay_connected)
            running -> if (chosen) getString(R.string.rig_home_phone_connecting, name) else getString(R.string.connecting_to_your_iphone)
            chosen -> getString(R.string.rig_home_phone_waiting, name)
            else -> getString(R.string.rig_home_phone_none)
        }
    }

    private fun settings(content: LinearLayout) {
        content.addView(label(getString(R.string.your_drive_your_way), 34, TEXT, true))
        content.addView(label(getString(R.string.apply_reconnects_carplay_for_size_resolution_music_buffer), 17, MUTED).apply { setPadding(0, dp(8), 0, dp(24)) })
        simHubSettings(content)
        section(content, getString(R.string.connection_setup), R.drawable.ic_dp_connection) { card ->
            card.addView(label(getString(R.string.choose_how_to_connect_follow_the_setup_steps_and_save_your), 16, MUTED))
            card.addView(button(getString(R.string.open_connection_setup), false) { page = "connection"; render() }, matchButton(12, 60))
        }
        section(content, getString(R.string.diagnostics), R.drawable.ic_dp_diagnostics) { card ->
            exportButton = button(if (exportInProgress) getString(R.string.saving_report) else getString(R.string.save_diagnostic_report), false) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) exportDiagnostics()
                else chooseReportDestination()
            }.apply { isEnabled = !exportInProgress }
            card.addView(exportButton, matchButton(10, 60))
            card.addView(button(getString(R.string.choose_save_location), false) { chooseReportDestination() }, matchButton(10, 60))
            val destination = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) getString(R.string.reports_save_to_downloads_rigplay) else getString(R.string.choose_where_to_save_your_report)
            card.addView(label(destination + getString(R.string.nothing_is_sent_automatically_protocol_payloads_and_creden), 14, MUTED).apply { setPadding(0, dp(12), 0, 0) })
        }
        section(content, getString(R.string.automatic_connection), R.drawable.ic_dp_automation) { card ->
            toggle(card, getString(R.string.connect_when_rigplay_opens), getString(R.string.rig_connect_automatically_desc), RigPlayPreferences.autoConnect(this)) { RigPlayPreferences.saveAutoConnect(this, it) }
            toggle(card, getString(R.string.rig_open_after_boot), getString(R.string.rig_open_after_boot_desc), AirPlayPersistence.loadAutoStartOnBoot(this)) { AirPlayPersistence.saveAutoStartOnBoot(this, it) }
            card.addView(button("${getString(R.string.choose_iphone_prefix)}${RigPlayPreferences.phoneName(this)}", false) { choosePhone() }, matchButton(12, 60))
        }
        section(content, getString(R.string.display_and_performance), R.drawable.ic_dp_display) { card ->
            carPlaySizeControl(card)
            choice(card, getString(R.string.resolution), listOf(getString(R.string.resolution_native), getString(R.string.s_80_lighter_load), getString(R.string.s_60_lightest_load)), listOf(10, 8, 6).indexOf(AirPlayPersistence.loadDisplayScaleTenths(this)).coerceAtLeast(0)) { AirPlayPersistence.saveDisplayScaleTenths(this, listOf(10, 8, 6)[it]) }
            val bufferPresets = com.shilapi.xcertplay.media.MediaAudioBuffer.presets
            choice(card, getString(R.string.music_buffer), listOf(getString(R.string.s_300_ms_default), getString(R.string.s_500_ms), getString(R.string.s_1000_ms_most_stable)),
                bufferPresets.indexOf(AirPlayPersistence.loadMediaBufferMillis(this)).coerceAtLeast(0)) {
                AirPlayPersistence.saveMediaBufferMillis(this, bufferPresets[it])
            }
            choice(card, getString(R.string.frame_rate), listOf(getString(R.string.s_30_fps_lighter_load), getString(R.string.s_60_fps_smoother_motion)), if (AirPlayPersistence.loadFps(this) == 60) 1 else 0) { AirPlayPersistence.saveFps(this, if (it == 1) 60 else 30) }
            toggle(card, getString(R.string.efficient_video), getString(R.string.use_hevc_leave_off_for_the_widest_head_unit_compatibility), AirPlayPersistence.loadHevcEnabled(this)) { AirPlayPersistence.saveHevcEnabled(this, it) }
            toggle(card, getString(R.string.right_hand_drive), getString(R.string.place_carplay_s_controls_closer_to_the_driver), AirPlayPersistence.loadRightHandDrive(this)) { AirPlayPersistence.saveRightHandDrive(this, it) }
            toggle(card, getString(R.string.full_screen), getString(R.string.hide_the_car_s_system_bars_while_carplay_is_open), AirPlayPersistence.loadHideTopBar(this) && AirPlayPersistence.loadHideBottomBar(this)) {
                AirPlayPersistence.saveHideTopBar(this, it); AirPlayPersistence.saveHideBottomBar(this, it)
            }
        }
        section(content, getString(R.string.audio_routing)) { card ->
            toggle(card, getString(R.string.contrib_audio_home_toggle_audio_focus), getString(R.string.contrib_audio_home_toggle_audio_focus_desc), AirPlayPersistence.loadAudioFocusEnabled(this)) { AirPlayPersistence.saveAudioFocusEnabled(this, it) }
            if (resources.getBoolean(R.bool.config_advanced_audio_channel_mapping)) {
                toggle(card, getString(R.string.advanced_audio_channel_mapping),
                    getString(R.string.use_usage_content_type_routing_instead_of_stream_type),
                    AirPlayPersistence.loadAdvancedAudioChannelMapping(this)) {
                    AirPlayPersistence.saveAdvancedAudioChannelMapping(this, it)
                }
            }
            mediaChannelControl(card)
            navigationChannelControl(card)
        }
        section(content, getString(R.string.location), R.drawable.ic_dp_navigation) { card ->
            toggle(card, getString(R.string.report_location_to_iphone),
                getString(R.string.sends_precise_android_location_as_carplay_gps_data_when_th),
                AirPlayPersistence.loadLocationReportingEnabled(this)) {
                AirPlayPersistence.saveLocationReportingEnabled(this, it)
                if (it && !hasPreciseLocation()) {
                    locationPermission.launch(arrayOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION))
                } else {
                    reconnectForLocation()
                }
            }
        }
        section(content, getString(R.string.permissions_and_connection_help), R.drawable.ic_dp_permissions) { card ->
            card.addView(label(getString(R.string.nearby_devices_connects_your_iphone_microphone_enables_sir), 16, MUTED))
            card.addView(button(getString(R.string.app_permissions), false) { openSystem(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) }, matchButton(16, 60))
            card.addView(button(getString(R.string.bluetooth_settings), false) { openSystem(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }, matchButton(10, 60))
            card.addView(button(getString(R.string.wireless_connection_help), false) { wirelessHelp() }, matchButton(10, 60))
        }
        section(content, getString(R.string.about), R.drawable.ic_dp_about) { card ->
            card.addView(button(getString(R.string.about_rigplay), false) { page = "about"; render() }, matchButton(0, 60))
        }
        languageSettings(content)
    }

    // --- SimHub onboarding (#27) ---------------------------------------------------------------

    private fun simHubOnboarding(content: LinearLayout) {
        content.addView(label(getString(R.string.rig_simhub_title), 34, TEXT, true))
        content.addView(label(getString(R.string.rig_simhub_intro), 17, MUTED).apply { setPadding(0, dp(8), 0, dp(24)) })
        simhubContainer = column().also { content.addView(it) }
        refreshSimHubPage()
    }

    /** Rebuilds the step only when it changed, so typing a PIN survives host-list updates. */
    private fun refreshSimHubPage() {
        val container = simhubContainer ?: return
        val step = RigSessionCoordinator.pairingStep
        val key: Any = if (step is SimHubPairingFlow.Step.ChooseHost || step is SimHubPairingFlow.Step.Paired) {
            listOf(step, RigSessionCoordinator.hosts.map { it.copy(lastSeen = 0) }, RigSessionCoordinator.discoveryError)
        } else step
        if (key == simhubRendered) return
        simhubRendered = key
        container.removeAllViews()
        when (step) {
            is SimHubPairingFlow.Step.ChooseHost, is SimHubPairingFlow.Step.Paired -> simHubHostList(container)
            is SimHubPairingFlow.Step.Connecting ->
                simHubProgress(container, getString(R.string.rig_simhub_connecting, step.name ?: "${step.host}:${step.port}"))
            is SimHubPairingFlow.Step.WaitingForPin ->
                simHubProgress(container, getString(R.string.rig_simhub_waiting_pin, step.name))
            is SimHubPairingFlow.Step.EnterPin -> simHubPinEntry(container, step)
            is SimHubPairingFlow.Step.Failed -> simHubFailure(container, step)
        }
    }

    private fun simHubHostList(container: LinearLayout) {
        val card = card()
        val hosts = RigSessionCoordinator.hosts
        val discoveryError = RigSessionCoordinator.discoveryError
        when {
            discoveryError != null -> card.addView(label(getString(R.string.rig_simhub_discovery_unavailable, discoveryError), 16, WARNING))
            hosts.isEmpty() -> {
                val searching = row().apply { gravity = Gravity.CENTER_VERTICAL }
                searching.addView(ProgressBar(this).apply { isIndeterminate = true }, LinearLayout.LayoutParams(dp(32), dp(32)).apply { marginEnd = dp(16) })
                searching.addView(label(getString(R.string.rig_simhub_searching), 18, MUTED), LinearLayout.LayoutParams(0, -2, 1f))
                card.addView(searching)
            }
        }
        hosts.forEachIndexed { index, host ->
            val address = "${host.address.hostAddress}:${host.controlPort}"
            val details = host.simhubVersion?.let { getString(R.string.rig_simhub_host_details_simhub, address, it, host.version) }
                ?: getString(R.string.rig_simhub_host_details, address, host.version)
            val entry = column().apply {
                setPadding(dp(20), dp(16), dp(20), dp(16))
                background = android.graphics.drawable.RippleDrawable(ColorStateList.valueOf(0x336F9FD9), rounded(SURFACE, if (host.compatible) ACCENT else BORDER), null)
                isClickable = host.compatible; isFocusable = host.compatible
                contentDescription = "${host.name}, $details"
                if (host.compatible) setOnClickListener {
                    val ip = host.address.hostAddress ?: return@setOnClickListener
                    RigSessionCoordinator.connect(ip, host.controlPort, host.name)
                }
            }
            entry.addView(label(host.name, 20, if (host.compatible) TEXT else MUTED, true))
            entry.addView(label(details, 14, MUTED).apply { setPadding(0, dp(4), 0, 0) })
            if (!host.compatible) entry.addView(label(getString(R.string.rig_simhub_host_incompatible), 14, WARNING).apply { setPadding(0, dp(4), 0, 0) })
            card.addView(entry, LinearLayout.LayoutParams(-1, -2).apply { if (index > 0) topMargin = dp(12) })
        }
        card.addView(button(getString(R.string.rig_simhub_enter_manually), hosts.isEmpty()) { askSimHubAddress() }, matchButton(18, 60))
        card.addView(button(getString(R.string.rig_simhub_later), false) {
            RigSessionCoordinator.onboardingSkipped = true
            showPage("home")
        }, matchButton(10, 56))
        container.addView(card)
    }

    private fun simHubProgress(container: LinearLayout, message: String) {
        val card = card()
        val line = row().apply { gravity = Gravity.CENTER_VERTICAL }
        line.addView(ProgressBar(this).apply { isIndeterminate = true }, LinearLayout.LayoutParams(dp(40), dp(40)).apply { marginEnd = dp(16) })
        line.addView(label(message, 22, TEXT, true).apply { accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE }, LinearLayout.LayoutParams(0, -2, 1f))
        card.addView(line)
        card.addView(button(getString(R.string.cancel), false) { RigSessionCoordinator.cancelPairing() }, matchButton(20, 56))
        container.addView(card)
    }

    private fun simHubPinEntry(container: LinearLayout, step: SimHubPairingFlow.Step.EnterPin) {
        val card = card()
        card.addView(label(getString(R.string.rig_simhub_enter_pin_title), 24, TEXT, true))
        card.addView(label(getString(R.string.rig_simhub_enter_pin_body, step.name), 16, MUTED).apply { setPadding(0, dp(8), 0, 0) })
        step.expiresInSec?.let { card.addView(label(getString(R.string.rig_simhub_pin_expires, it), 14, MUTED).apply { setPadding(0, dp(4), 0, 0) }) }
        val error = when (step.error) {
            SimHubPairingFlow.PairingError.WRONG_PIN -> step.attemptsLeft?.let { getString(R.string.rig_simhub_error_wrong_pin, it) }
                ?: getString(R.string.rig_simhub_error_wrong_pin_plain)
            SimHubPairingFlow.PairingError.PIN_EXPIRED -> getString(R.string.rig_simhub_error_pin_expired)
            SimHubPairingFlow.PairingError.TOO_MANY_ATTEMPTS -> getString(R.string.rig_simhub_error_too_many)
            SimHubPairingFlow.PairingError.DENIED -> getString(R.string.rig_simhub_error_denied)
            else -> null
        }
        error?.let {
            card.addView(label(it, 16, WARNING).apply { setPadding(0, dp(12), 0, 0); accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE })
        }
        if (step.needsNewPin) {
            card.addView(button(getString(R.string.rig_simhub_new_pin), true) { RigSessionCoordinator.requestNewPin() }, matchButton(18, 60))
        } else {
            val pin = EditText(this).apply {
                hint = getString(R.string.rig_simhub_pin_hint); setSingleLine(); textSize = 34f; letterSpacing = .3f
                gravity = Gravity.CENTER; setTextColor(TEXT); setHintTextColor(MUTED)
                inputType = android.text.InputType.TYPE_CLASS_NUMBER
                filters = arrayOf(android.text.InputFilter.LengthFilter(6))
                imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_DONE or android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI
                isEnabled = !step.submitting
                contentDescription = getString(R.string.rig_simhub_pin_hint)
            }
            val pair = button(getString(R.string.rig_simhub_pair), true) { RigSessionCoordinator.submitPin(pin.text.toString()) }
            pair.isEnabled = false
            pin.addTextChangedListener(object : android.text.TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(text: android.text.Editable?) { pair.isEnabled = !step.submitting && text?.length == 6 }
            })
            pin.setOnEditorActionListener { _, action, _ ->
                if (action == android.view.inputmethod.EditorInfo.IME_ACTION_DONE && pin.text.length == 6) {
                    RigSessionCoordinator.submitPin(pin.text.toString()); true
                } else false
            }
            card.addView(pin, LinearLayout.LayoutParams(-1, dp(80)).apply { topMargin = dp(16) })
            if (step.submitting) {
                card.addView(label(getString(R.string.rig_simhub_checking_pin), 16, MUTED).apply { setPadding(0, dp(12), 0, 0) })
            } else {
                card.addView(pair, matchButton(12, 60))
                pin.requestFocus()
            }
        }
        card.addView(button(getString(R.string.cancel), false) { RigSessionCoordinator.cancelPairing() }, matchButton(10, 56))
        container.addView(card)
    }

    private fun simHubFailure(container: LinearLayout, step: SimHubPairingFlow.Step.Failed) {
        val card = card()
        val name = step.name ?: "${step.host}:${step.port}"
        val message = when (step.error) {
            SimHubPairingFlow.PairingError.INCOMPATIBLE -> getString(R.string.rig_simhub_error_incompatible, name)
            else -> getString(R.string.rig_simhub_error_unreachable, name)
        }
        card.addView(label(message, 18, WARNING).apply { accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE })
        card.addView(button(getString(R.string.rig_simhub_retry), true) { RigSessionCoordinator.retryPairing() }, matchButton(18, 60))
        card.addView(button(getString(R.string.rig_simhub_choose_other), false) { RigSessionCoordinator.cancelPairing() }, matchButton(10, 56))
        container.addView(card)
    }

    private fun askSimHubAddress() {
        val fields = column().apply { setPadding(dp(24), dp(12), dp(24), dp(12)) }
        val input = EditText(this).apply {
            hint = getString(R.string.rig_simhub_address_hint); setSingleLine()
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_URI
            imeOptions = android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI
        }
        val error = label("", 14, WARNING).apply { accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE }
        fields.addView(input); fields.addView(error)
        val dialog = AlertDialog.Builder(this).setTitle(getString(R.string.rig_simhub_address_title)).setView(fields)
            .setPositiveButton(getString(R.string.rig_simhub_connect), null)
            .setNegativeButton(getString(R.string.cancel), null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val parsed = SimHubAddress.parse(input.text.toString(), AirPlayPersistence.loadSimHubControlPort(this))
                if (parsed == null) error.text = getString(R.string.rig_simhub_address_invalid)
                else { dialog.dismiss(); RigSessionCoordinator.connect(parsed.host, parsed.port) }
            }
        }
        dialog.show()
    }

    private fun simHubStatusText(): String {
        val pairing = RigSessionCoordinator.pairing ?: return getString(R.string.rig_simhub_status_not_paired)
        val state = RigSessionCoordinator.state
        return when {
            state.paired -> getString(R.string.rig_simhub_status_connected, state.hostName ?: pairing.name)
            state.phase == com.shilapi.xcertplay.simhub.SimHubState.Phase.INCOMPATIBLE ->
                getString(R.string.rig_simhub_status_incompatible, pairing.name)
            else -> getString(R.string.rig_simhub_status_searching, pairing.name)
        }
    }

    private fun simHubSettings(content: LinearLayout) {
        section(content, getString(R.string.rig_settings_simhub), R.drawable.ic_dp_connection) { card ->
            val pairing = RigSessionCoordinator.pairing
            if (pairing != null) {
                card.addView(label(getString(R.string.rig_settings_simhub_paired, pairing.name, "${pairing.host}:${pairing.port}"), 18, TEXT, true))
                simhubStatusView = label(simHubStatusText(), 16, MUTED).apply { setPadding(0, dp(6), 0, 0) }
                card.addView(simhubStatusView)
                card.addView(button(getString(R.string.rig_settings_simhub_reconnect), false) { RigSessionCoordinator.reconnect() }, matchButton(16, 60))
                card.addView(button(getString(R.string.rig_settings_simhub_forget), false) {
                    AlertDialog.Builder(this).setTitle(getString(R.string.rig_settings_simhub_forget_title, pairing.name))
                        .setMessage(getString(R.string.rig_settings_simhub_forget_body))
                        .setPositiveButton(getString(R.string.rig_settings_simhub_forget)) { _, _ ->
                            RigSessionCoordinator.forget()
                            RigSessionCoordinator.onboardingSkipped = false
                            showPage("simhub")
                        }
                        .setNegativeButton(getString(R.string.cancel), null).show()
                }, matchButton(10, 60))
            } else {
                card.addView(label(getString(R.string.rig_simhub_status_not_paired), 16, MUTED))
                card.addView(button(getString(R.string.rig_settings_simhub_pair), true) { showPage("simhub") }, matchButton(12, 60))
            }
            card.addView(label(getString(R.string.rig_settings_simhub_advanced), 18, TEXT, true).apply { setPadding(0, dp(24), 0, 0) })
            card.addView(label(getString(R.string.rig_settings_simhub_ports_note), 14, MUTED).apply { setPadding(0, dp(6), 0, 0) })
            val controlPort = AirPlayPersistence.loadSimHubControlPort(this)
            card.addView(button(getString(R.string.rig_settings_simhub_control_port, controlPort), false) {
                askPort(getString(R.string.rig_settings_simhub_control_port, controlPort), controlPort) {
                    AirPlayPersistence.saveSimHubControlPort(this, it); render()
                }
            }, matchButton(12, 60))
            val discoveryPort = AirPlayPersistence.loadSimHubDiscoveryPort(this)
            card.addView(button(getString(R.string.rig_settings_simhub_discovery_port, discoveryPort), false) {
                askPort(getString(R.string.rig_settings_simhub_discovery_port, discoveryPort), discoveryPort) {
                    AirPlayPersistence.saveSimHubDiscoveryPort(this, it)
                    RigSessionCoordinator.restartDiscovery()
                    render()
                }
            }, matchButton(10, 60))
        }
    }

    private fun askPort(title: String, current: Int, save: (Int) -> Unit) {
        val input = EditText(this).apply {
            setText(current.toString()); setSingleLine()
            inputType = android.text.InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(android.text.InputFilter.LengthFilter(5))
        }
        AlertDialog.Builder(this).setTitle(title).setView(input)
            .setPositiveButton(getString(R.string.save)) { _, _ ->
                val port = input.text.toString().toIntOrNull()
                if (port == null || port !in 1..65535) toast(getString(R.string.rig_settings_simhub_port_invalid)) else save(port)
            }
            .setNegativeButton(getString(R.string.cancel), null).show()
    }

    private fun about(content: LinearLayout) {
        content.addView(label(getString(R.string.rigplay), 40, TEXT, true))
        content.addView(label(getString(R.string.carplay_at_home_in_your_car), 20, MUTED).apply { setPadding(0, dp(8), 0, dp(24)) })
        section(content, "${getString(R.string.about_public_preview_prefix)}${version()}") { card ->
            card.addView(label(getString(R.string.an_independent_carplay_receiver_for_android_head_units_wir), 17, TEXT))
        }
        section(content, getString(R.string.made_possible_by_open_source)) { card ->
            card.addView(label(getString(R.string.receiver_based_on_xcertplay_licensed_under_gpl_3_0_rigplay), 16, MUTED))
        }
    }

    // The car hotspot link needs the hotspot on; rigPlay only checks it (turning it on needs ADB-only permission).
    private fun carHotspotOff(): Boolean =
        AirPlayPersistence.loadWirelessHotspotMode(this) == WirelessHotspotMode.MANUAL &&
            com.shilapi.xcertplay.network.CarHotspotStatus.isEnabled(this) == false

    private fun carHotspotOffDialog() {
        AlertDialog.Builder(this).setTitle(getString(R.string.car_hotspot_is_off))
            .setMessage(getString(R.string.msg_car_hotspot_connect, AirPlayPersistence.loadManualHotspotSsid(this)))
            .setPositiveButton(getString(R.string.open_car_settings)) { _, _ -> openCarWifiSettings() }
            .setNeutralButton(getString(R.string.connect)) { _, _ -> connect(true) }
            .setNegativeButton(getString(R.string.cancel), null).show()
    }

    // Firmware without the AOSP tether screen falls back to Wi-Fi settings.
    private fun openCarWifiSettings() {
        val hotspot = Intent("com.android.settings.WIFI_TETHER_SETTINGS")
        val target = packageManager.resolveActivity(hotspot, 0)?.activityInfo?.packageName
        if (target == null) {
            openSystem(Intent(Settings.ACTION_WIRELESS_SETTINGS))
            return
        }
        if (runCatching { startActivity(hotspot) }.isSuccess) return
        openSystem(Intent(Settings.ACTION_WIRELESS_SETTINGS))
    }

    private fun openCarClientWifiSettings() {
        openSystem(Intent(Settings.ACTION_WIFI_SETTINGS))
    }

    private fun connectionSetup(content: LinearLayout) {
        content.addView(label(getString(R.string.connection_setup), 34, TEXT, true))
        content.addView(label(getString(R.string.set_up_once_your_details_stay_saved_for_the_next_drive_cha), 17, MUTED).apply { setPadding(0, dp(8), 0, dp(24)) })
        section(content, getString(R.string.s_1_choose_your_connection)) { card -> wirelessLinkControls(card) }
        section(content, getString(R.string.s_2_pair_your_iphone)) { card ->
            card.addView(label(getString(R.string.keep_bluetooth_and_wi_fi_on_your_iphone_pair_with_the_car), 16, MUTED))
            card.addView(button("${getString(R.string.choose_iphone_prefix)}${RigPlayPreferences.phoneName(this)}", false) { choosePhone() }, matchButton(12, 60))
            card.addView(button(getString(R.string.review_app_permissions), false) {
                openSystem(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
            }, matchButton(12, 60))
        }
        section(content, getString(R.string.s_3_connect)) { card ->
            card.addView(label(getString(R.string.return_from_car_settings_to_rigplay_then_connect_accept_the), 16, MUTED))
            card.addView(button(getString(R.string.connect_phone), true) { connect(true) }, matchButton(12, 60))
        }
        section(content, getString(R.string.prefer_a_cable)) { card ->
            card.addView(label(getString(R.string.use_a_usb_data_cable_and_the_car_s_usb_data_port_unlock_yo), 16, MUTED))
            card.addView(button(getString(R.string.connect_with_usb), false) { connect(false) }, matchButton(12, 60))
        }
    }

    private fun wirelessLinkControls(parent: LinearLayout) {
        val mode = if (pendingCarHotspotSetup) WirelessHotspotMode.MANUAL else AirPlayPersistence.loadWirelessHotspotMode(this)
        val modes = listOf(WirelessHotspotMode.MANUAL, WirelessHotspotMode.WIFI_P2P)
        val titles = listOf(getString(R.string.built_in_car_hotspot), getString(R.string.wifi_direct))
        val descriptions = listOf(
            getString(R.string.hotspot_mode_manual_desc),
            getString(R.string.hotspot_mode_p2p_desc)
        )
        val wide = resources.configuration.screenWidthDp >= 850
        val choices = if (wide) row().apply { gravity = Gravity.TOP } else column()
        parent.addView(choices)
        modes.forEachIndexed { index, candidate ->
            val option = column()
            choices.addView(option, if (wide) LinearLayout.LayoutParams(0, -2, 1f).apply {
                if (index > 0) marginStart = dp(16)
            } else LinearLayout.LayoutParams(-1, -2))
            option.addView(button("${if (mode == candidate) "✓  " else ""}${titles[index]}", mode == candidate) {
                if (candidate == WirelessHotspotMode.MANUAL) {
                    pendingCarHotspotSetup = true
                    render()
                } else {
                    pendingCarHotspotSetup = false
                    applyWirelessLink(candidate)
                }
            }, matchButton(12, 60))
            option.addView(label(descriptions[index], 15, MUTED).apply { setPadding(0, dp(6), 0, dp(12)) })
        }
        if (mode == WirelessHotspotMode.MANUAL) {
            parent.addView(label(getString(R.string.hotspot_setup), 22, TEXT, true))
            parent.addView(label(getString(R.string.s_1_open_car_hotspot_settings_turn_the_hotspot_on_and_sele), 16, MUTED).apply { setPadding(0, dp(8), 0, dp(12)) })
            parent.addView(button(getString(R.string.open_car_hotspot_settings), false) { openCarWifiSettings() }, matchButton(0, 60))
            parent.addView(button(if (pendingCarHotspotSetup) getString(R.string.save_hotspot_details_and_use_this_mode) else "${getString(R.string.edit_saved_hotspot_prefix)}${storedSsid()}", false) {
                askHotspotCredentials { ssid, password ->
                    saveHotspotCredentials(ssid, password)
                    pendingCarHotspotSetup = false
                    applyWirelessLink(WirelessHotspotMode.MANUAL)
                }
            }, matchButton(12, 60))
            parent.addView(label(if (pendingCarHotspotSetup) getString(R.string.finish_setup_save_your_hotspot_details_to_use_this_mode) else if (carHotspotOff()) getString(R.string.hotspot_details_off) else getString(R.string.hotspot_details_saved), 15, if (carHotspotOff()) WARNING else MUTED).apply { setPadding(0, dp(12), 0, 0) })
        } else {
            parent.addView(label(getString(R.string.turn_the_car_s_wi_fi_switch_on_allow_location_nearby_devic), 16, MUTED))
            parent.addView(button(getString(R.string.open_car_wi_fi_settings), false) { openCarClientWifiSettings() }, matchButton(12, 60))
        }
    }

    private fun mediaChannelControl(parent: LinearLayout) {
        val summary: (Int) -> String = {
            getString(R.string.contrib_audio_home_choice_summary, getString(R.string.contrib_audio_home_media_channel_label), channelLabel(it))
        }
        val control = button(summary(AirPlayPersistence.loadMediaAudioChannel(this)), false) {}
        control.setOnClickListener {
            val current = AirPlayPersistence.loadMediaAudioChannel(this)
            showChannelDialog(
                title = getString(R.string.contrib_audio_home_media_channel_label),
                current = current,
                navigation = false,
                onApply = { value -> applyMediaChannel(value, current, control, summary) },
            )
        }
        parent.addView(control, matchButton(0, 60))
    }

    private fun navigationChannelControl(parent: LinearLayout) {
        val summary: (Int) -> String = {
            getString(R.string.contrib_audio_home_choice_summary, getString(R.string.contrib_audio_home_nav_channel_label), channelLabel(it))
        }
        val control = button(summary(AirPlayPersistence.loadNavigationAudioChannel(this)), false) {}
        control.setOnClickListener {
            val current = AirPlayPersistence.loadNavigationAudioChannel(this)
            showChannelDialog(
                title = getString(R.string.contrib_audio_home_nav_channel_label),
                current = current,
                navigation = true,
                onApply = { value -> applyNavigationChannel(value, current, control, summary) },
            )
        }
        parent.addView(control, matchButton(0, 60))
        parent.addView(label(getString(R.string.contrib_audio_home_nav_channel_note), 14, MUTED).apply {
            setPadding(0, dp(8), 0, dp(18))
        })
    }

    private fun showChannelDialog(title: String, current: Int, navigation: Boolean, onApply: (Int) -> Unit) {
        val preview = AudioChannelPreview { channel ->
            toast(getString(R.string.contrib_audio_home_channel_preview_unavailable, channel))
        }
        val channels = AirPlayPersistence.AUDIO_CHANNELS
        val labels = channels.map(Int::toString).toTypedArray()
        var selection = current.coerceIn(channels.first, channels.last)
        AlertDialog.Builder(this).setTitle(title)
            .setSingleChoiceItems(labels, selection) { _, which ->
                selection = which
                preview.play(which, navigation)
            }
            .setPositiveButton(if (CarPlayBackgroundSession.hasSession()) getString(R.string.apply_and_reconnect) else getString(R.string.save)) { _, _ ->
                onApply(selection)
            }
            .setNegativeButton(getString(R.string.cancel), null)
            .setOnDismissListener { preview.close() }
            .show()
    }

    private fun applyMediaChannel(value: Int, previous: Int, control: Button, summary: (Int) -> String) {
        if (value == previous) return
        AirPlayPersistence.saveMediaAudioChannel(this, value)
        control.text = summary(value)
        if (CarPlayBackgroundSession.hasSession()) connect(AirPlayPersistence.loadWirelessEnabled(this))
    }

    private fun applyNavigationChannel(value: Int, previous: Int, control: Button, summary: (Int) -> String) {
        if (value == previous) return
        AirPlayPersistence.saveNavigationAudioChannel(this, value)
        control.text = summary(value)
        if (CarPlayBackgroundSession.hasSession()) connect(AirPlayPersistence.loadWirelessEnabled(this))
    }

    private fun channelLabel(value: Int): String = value.toString()

    private fun storedSsid() = AirPlayPersistence.loadManualHotspotSsid(this)
    private fun storedPassword() = AirPlayPersistence.loadManualHotspotPassphrase(this)
    private fun hotspotError(ssid: String, password: String) =
        com.shilapi.xcertplay.orchestration.ManualHotspotValidation.error(ssid, password)?.let { getString(it.messageResource()) }

    private fun saveHotspotCredentials(ssid: String, password: String) {
        AirPlayPersistence.saveManualHotspotSsid(this, ssid)
        AirPlayPersistence.saveManualHotspotPassphrase(this, password)
        AirPlayPersistence.saveManualHotspotSecurity(this,
            com.shilapi.xcertplay.orchestration.ManualHotspotValidation.securityFor(password))
        AirPlayPersistence.saveManualHotspotBand(this, com.shilapi.xcertplay.orchestration.ManualHotspotBand.AUTO)
        AirPlayPersistence.saveManualHotspotChannel(this, 0)
    }

    private fun askHotspotCredentials(done: (String, String) -> Unit) {
        val fields = column().apply { setPadding(dp(24), dp(12), dp(24), dp(12)) }
        fields.addView(label(getString(R.string.copy_these_from_the_car_s_hotspot_settings_use_5_ghz_if_av), 16, MUTED))
        val ssid = EditText(this).apply { hint = getString(R.string.hotspot_name); setText(storedSsid()); setSingleLine() }
        val password = EditText(this).apply {
            hint = getString(R.string.hotspot_password); setText(storedPassword()); setSingleLine()
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
        }
        ssid.imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_NEXT or android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI
        password.imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_DONE or android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI
        fun hideKeyboard() {
            val token = password.windowToken ?: ssid.windowToken
            (this.getSystemService(android.content.Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager)
                .hideSoftInputFromWindow(token, 0)
            ssid.clearFocus(); password.clearFocus()
        }
        ssid.setOnEditorActionListener { _, action, _ ->
            if (action == android.view.inputmethod.EditorInfo.IME_ACTION_NEXT) { password.requestFocus(); true } else false
        }
        password.setOnEditorActionListener { _, action, _ ->
            if (action == android.view.inputmethod.EditorInfo.IME_ACTION_DONE) { hideKeyboard(); true } else false
        }
        fields.addView(ssid); fields.addView(password)
        fields.addView(CheckBox(this).apply {
            text = getString(R.string.show_password)
            setOnCheckedChangeListener { _, checked ->
                password.transformationMethod = if (checked) null else android.text.method.PasswordTransformationMethod.getInstance()
                password.setSelection(password.text.length)
            }
        })
        val error = label("", 14, WARNING)
        error.accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        fields.addView(error)
        val dialog = AlertDialog.Builder(this).setTitle(getString(R.string.car_hotspot_details))
            .setView(ScrollView(this).apply { addView(fields) })
            .setPositiveButton(getString(R.string.save_details), null).setNegativeButton(getString(R.string.cancel)) { _, _ -> hideKeyboard() }
            .setNeutralButton(getString(R.string.hide_keyboard), null).create()
        dialog.setOnShowListener {
            dialog.window?.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
            dialog.getButton(android.app.AlertDialog.BUTTON_NEUTRAL).setOnClickListener { hideKeyboard() }
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val name = ssid.text.toString().trim()
                val secret = password.text.toString()
                val problem = hotspotError(name, secret)
                if (problem != null) error.text = problem
                else { hideKeyboard(); dialog.dismiss(); done(name, secret) }
            }
        }
        dialog.show()
    }

    private fun hasPreciseLocation() =
        checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED

    // The location component is part of the iAP2 identification, so a running session reconnects.
    private fun reconnectForLocation() {
        if (CarPlayBackgroundSession.hasSession()) connect(AirPlayPersistence.loadWirelessEnabled(this))
    }

    private fun applyWirelessLink(mode: WirelessHotspotMode) {
        AirPlayPersistence.saveWirelessHotspotMode(this, mode)
        render()
        toast(getString(R.string.saved_for_your_next_connection))
    }

    private fun textInput(title: String, current: String, secret: Boolean, save: (String) -> Unit) {
        val input = EditText(this).apply {
            setText(current)
            setSingleLine()
            inputType = if (secret) {
                android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_PASSWORD
            } else {
                android.text.InputType.TYPE_CLASS_TEXT
            }
        }
        AlertDialog.Builder(this).setTitle(title).setView(input)
            .setPositiveButton(getString(R.string.save)) { _, _ -> save(input.text.toString().let { if (secret) it else it.trim() }) }
            .setNegativeButton(getString(R.string.cancel), null).show()
    }

    private fun carPlaySizeControl(parent: LinearLayout) {
        val sizes = com.shilapi.xcertplay.airplay.CarPlaySize.entries
        val current = com.shilapi.xcertplay.airplay.CarPlaySize.fromWidthMillimeters(AirPlayPersistence.loadWidthPhysicalMm(this))
        choice(parent, getString(R.string.carplay_size), sizes.map { it.localizedLabel(this) }, sizes.indexOf(current)) {
            AirPlayPersistence.saveWidthPhysicalMm(this, sizes[it].widthMillimeters)
        }
        parent.addView(label(getString(R.string.changes_the_size_of_carplay_icons_and_text_applying_a_size), 14, MUTED).apply {
            setPadding(0, 0, 0, dp(18))
        })
    }

    private fun connect(wireless: Boolean) {
        if (wireless && pendingCarHotspotSetup) { toast(getString(R.string.save_your_hotspot_details_in_connection_setup_first)); page = "connection"; render(); return }
        if (setupError != null) { toast(setupError!!); return }
        if (wireless && AirPlayPersistence.loadWirelessHotspotMode(this) == WirelessHotspotMode.MANUAL &&
            hotspotError(storedSsid(), storedPassword()) != null) {
            pendingCarHotspotSetup = true
            page = "connection"
            render()
            toast(getString(R.string.save_the_name_and_password_from_the_car_s_hotspot_settings))
            return
        }
        if (wireless && carHotspotOff()) { carHotspotOffDialog(); return }
        if (wireless && RigPlayPreferences.phoneAddress(this) == null) {
            pendingWireless = true; choosePhone(); return
        }
        val preferences = getSharedPreferences("rigplay", MODE_PRIVATE)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED && !preferences.getBoolean("notification_asked", false)) {
            preferences.edit().putBoolean("notification_asked", true).apply()
            notificationTransport = wireless
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            return
        }
        // #29: allowed while SimHub is down, but the user should know why audio stays on the tablet.
        if (RigSessionCoordinator.onManualConnect()) toast(getString(R.string.rig_manual_connect_simhub_down))
        val open = {
            AirPlayPersistence.saveWirelessEnabled(this, wireless)
            openProjection()
        }
        if (CarPlayBackgroundSession.hasSession()) CarPlayBackgroundSession.stop { runOnUiThread { open() } }
        else open()
    }
    private fun openProjection() {
        startActivity(Intent(this, CarPlayHostActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT))
    }
    private fun choosePhone() {
        if (Build.VERSION.SDK_INT >= 31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            bluetoothPermission.launch(Manifest.permission.BLUETOOTH_CONNECT); return
        }
        val adapter = getSystemService(BluetoothManager::class.java)?.adapter
        if (adapter == null || !adapter.isEnabled) {
            AlertDialog.Builder(this).setTitle(getString(R.string.turn_on_bluetooth))
                .setMessage(getString(R.string.enable_the_car_s_bluetooth_and_pair_your_iphone_first))
                .setPositiveButton(getString(R.string.open_bluetooth)) { _, _ -> openSystem(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }
                .setNegativeButton(getString(R.string.later), null).show(); return
        }
        val devices = runCatching { adapter.bondedDevices.sortedBy { it.name ?: "" } }.getOrDefault(emptyList())
        if (devices.isEmpty()) {
            AlertDialog.Builder(this).setTitle(getString(R.string.pair_your_iphone))
                .setMessage(getString(R.string.on_your_iphone_open_settings_bluetooth_and_pair_with_the_c))
                .setPositiveButton(getString(R.string.open_bluetooth)) { _, _ -> openSystem(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }
                .setNegativeButton(getString(R.string.got_it), null).show(); return
        }
        AlertDialog.Builder(this).setTitle(getString(R.string.choose_your_iphone))
            .setItems(devices.map { device ->
                val name = device.name ?: getString(R.string.paired_device)
                if (devices.count { it.name == device.name } > 1) "$name · ${device.address.takeLast(5)}" else name
            }.toTypedArray()) { _, index ->
                val device = devices[index]
                RigPlayPreferences.savePhone(this, device.address, device.name ?: "iPhone")
                val start = pendingWireless; pendingWireless = false
                render()
                if (start) connect(true)
            }.setNeutralButton(getString(R.string.pair_another)) { _, _ -> openSystem(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }
            .setNegativeButton(getString(R.string.cancel)) { _, _ -> pendingWireless = false }.show()
    }

    private fun wirelessHelp() {
        AlertDialog.Builder(this).setTitle(getString(R.string.wireless_connection_help))
            .setMessage(getString(R.string.pair_your_iphone_with_the_car_s_bluetooth_keep_wi_fi_on_an))
            .setPositiveButton(getString(R.string.got_it), null)
            .setNeutralButton(getString(R.string.reset_carplay_wi_fi)) { _, _ ->
                confirmWirelessReset()
            }.show()
    }

    private fun handleWirelessRecovery() {
        if (page != "wireless-recovery") return
        page = "home"; render()
        confirmWirelessReset()
    }

    private fun confirmWirelessReset() {
        AlertDialog.Builder(this).setTitle(getString(R.string.reset_carplay_wi_fi_2))
            .setMessage(getString(R.string.this_ends_the_existing_wi_fi_direct_connection_including_o))
            .setPositiveButton(getString(R.string.reset_and_connect)) { _, _ ->
                CarPlayBackgroundSession.stop { runOnUiThread { resetWirelessGroup() } }
            }.setNegativeButton(getString(R.string.cancel), null).show()
    }

    private fun resetWirelessGroup() {
        val manager = getSystemService(android.net.wifi.p2p.WifiP2pManager::class.java)
        if (manager == null) { toast(getString(R.string.this_head_unit_does_not_support_wi_fi_direct)); return }
        val channel = manager.initialize(this, mainLooper, null)
        try {
            manager.requestGroupInfo(channel) { group ->
                if (group == null) { channel.close(); connect(true); return@requestGroupInfo }
                manager.removeGroup(channel, object : android.net.wifi.p2p.WifiP2pManager.ActionListener {
                    override fun onSuccess() {
                        val deadline = android.os.SystemClock.elapsedRealtime() + 4000
                        fun waitUntilRemoved() {
                            manager.requestGroupInfo(channel) { remaining ->
                                when {
                                    remaining == null -> { channel.close(); if (!isFinishing && !isDestroyed) connect(true) }
                                    android.os.SystemClock.elapsedRealtime() >= deadline -> {
                                        channel.close(); toast(getString(R.string.wi_fi_direct_is_still_busy_close_the_other_projection_app))
                                    }
                                    else -> handler.postDelayed({ waitUntilRemoved() }, 200)
                                }
                            }
                        }
                        waitUntilRemoved()
                    }
                    override fun onFailure(reason: Int) { channel.close(); toast(getString(R.string.could_not_reset_wi_fi_direct_close_the_other_projection_ap)) }
                })
            }
        } catch (_: SecurityException) {
            channel.close(); permissionHelp(getString(R.string.wireless_permissions), getString(R.string.allow_nearby_devices_and_on_older_android_versions_locatio))
        }
    }

    private fun refreshStatus() {
        simhubStatusView?.text = simHubStatusText()
        homeSimHubStatus?.text = simHubStatusText()
        val running = CarPlayBackgroundSession.hasSession()
        status?.text = phoneStatusText()
        if (lastRunning != running) {
            connectButton?.text = if (running) getString(R.string.open_carplay) else getString(R.string.connect_phone)
            disconnectButton?.visibility = if (running) View.VISIBLE else View.GONE
            disconnectButton?.isEnabled = true
            lastRunning = running
        }
        connectButton?.isEnabled = setupError == null
    }
    private fun reportFileName() = "rigPlay-${SimpleDateFormat("yyyyMMdd-HHmmss-SSS", Locale.US).format(Date())}.txt"

    private fun chooseReportDestination() {
        // Some head units omit or disable DocumentsUI. Launch itself can throw, before
        // the result callback and the background writer's exception handler ever run.
        runCatching { export.launch(reportFileName()) }.onFailure {
            toast(if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                getString(R.string.this_head_unit_could_not_open_a_save_location_please_try_s)
                else getString(R.string.this_head_unit_has_no_available_file_picker_to_save_the_re))
        }
    }

    private fun exportDiagnostics(uri: Uri? = null) {
        if (exportInProgress) return
        exportInProgress = true
        exportButton?.apply { isEnabled = false; text = getString(R.string.saving_report) }
        val appContext = applicationContext
        val fileName = reportFileName()
        Thread({
            val result = runCatching {
                val report = buildString {
                    appendLine("rigPlay ${version()} · private beta diagnostic report")
                    appendLine("Android ${Build.VERSION.RELEASE} / API ${Build.VERSION.SDK_INT}")
                    appendLine("Head unit: ${Build.MANUFACTURER} ${Build.MODEL}")
                    appendLine("Connection: ${if (AirPlayPersistence.loadWirelessEnabled(appContext)) "wireless" else "USB"}")
                    appendLine("Authentication: local experimental beta identity; no remote fallback")
                    appendLine("CarPlay setup: ${if (setupError == null) "ready" else "authentication unavailable"}")
                    appendLine("Saved video preference (may differ from active session): ${if (AirPlayPersistence.loadHevcEnabled(appContext)) "HEVC" else "H.264"}; ${AirPlayPersistence.loadFps(appContext)} fps")
                    appendLine("CarPlay size: ${com.shilapi.xcertplay.airplay.CarPlaySize.fromWidthMillimeters(AirPlayPersistence.loadWidthPhysicalMm(appContext)).label}")
                    appendLine("Saved resolution preference (may differ from active session): ${AirPlayPersistence.loadDisplayScaleTenths(appContext) * 10}%")
                    appendLine("Session: ${if (CarPlayBackgroundSession.active) "active" else if (CarPlayBackgroundSession.hasSession()) "connecting" else "stopped"}")
                    appendLine("Head-unit board: ${Build.BOARD}; hardware: ${Build.HARDWARE}; build: ${Build.DISPLAY}")
                    appendLine()
                    appendLine("--- Last display negotiation (timestamps distinguish it from current settings) ---")
                    appendLine(DisplayDiagnosticSnapshot.report(appContext))
                    appendLine()
                    appendLine("--- Last received boot and app-launch result ---")
                    appendLine(StartupDiagnosticSnapshot.report(appContext))
                    appendLine("Startup settings: openAfterBoot=${AirPlayPersistence.loadAutoStartOnBoot(appContext)} " +
                        "connectWhenOpened=${RigPlayPreferences.autoConnect(appContext)}")
                    appendLine()
                    for (name in SessionLogFile.REPORT_NAMES) {
                        val file = File(appContext.filesDir, "logs/$name")
                        if (file.isFile) {
                            appendLine("--- $name ---")
                            file.useLines { lines -> lines.forEach { line -> DiagnosticRedactor.redact(line)?.let { appendLine(it) } } }
                        }
                    }
                }
                if (uri != null) { DiagnosticExportStore.write(appContext.contentResolver, uri, report); uri }
                else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    DiagnosticExportStore.saveToDownloads(appContext.contentResolver, fileName, report)
                } else error("A save location is required")
            }
            runOnUiThread {
                exportInProgress = false
                if (isFinishing || isDestroyed) return@runOnUiThread
                exportButton?.apply { isEnabled = true; text = getString(R.string.save_diagnostic_report) }
                if (result.isSuccess) {
                    val savedUri = result.getOrThrow()
                    AlertDialog.Builder(this).setTitle(getString(R.string.diagnostic_report_saved))
                        .setMessage(if (uri == null) "Downloads/rigPlay/$fileName" else getString(R.string.your_report_was_saved_to_the_selected_location))
                        .setPositiveButton(getString(R.string.done), null)
                        .setNeutralButton(getString(R.string.share)) { _, _ ->
                            runCatching {
                                startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                                    type = "text/plain"; putExtra(Intent.EXTRA_STREAM, savedUri)
                                    clipData = android.content.ClipData.newRawUri(getString(R.string.report_clip_label), savedUri)
                                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                }, getString(R.string.share_diagnostic_report)))
                            }.onFailure { toast(getString(R.string.report_saved_open_it_from_your_file_manager_to_share_it)) }
                        }.show()
                } else {
                    AlertDialog.Builder(this).setTitle(getString(R.string.could_not_save_the_report))
                        .setMessage(getString(R.string.check_that_storage_is_available_or_choose_another_save_loc))
                        .setPositiveButton(getString(R.string.choose_location)) { _, _ -> chooseReportDestination() }
                        .setNegativeButton(getString(R.string.close), null).show()
                }
            }
        }, "rigplay-export").start()
    }
    private fun permissionHelp(title: String, body: String) {
        AlertDialog.Builder(this).setTitle(title).setMessage(body).setPositiveButton(getString(R.string.app_settings)) { _, _ ->
            openSystem(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        }.setNegativeButton(getString(R.string.later), null).show()
    }
    private fun openSystem(intent: Intent) { runCatching { startActivity(intent) }.onFailure { toast(getString(R.string.open_this_setting_from_your_car_s_settings_app)) } }
    private fun toast(message: String) { Toast.makeText(this, message, Toast.LENGTH_LONG).show() }

    private fun playTestTone(streamType: Int) {
        toneStop?.let { handler.removeCallbacks(it) }
        toneStop = null
        testToneTrack?.let { runCatching { it.stop(); it.release() } }
        testToneTrack = null
        var candidate: AudioTrack? = null
        val track = try {
            val pcm = assets.open("navigation_test.pcm").use { it.readBytes() }
            AudioTrack(streamType, 44100, AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT, pcm.size, AudioTrack.MODE_STREAM).also {
                candidate = it
                check(it.state == AudioTrack.STATE_INITIALIZED)
                check(it.write(pcm, 0, pcm.size) == pcm.size)
                it.play()
            }
        } catch (error: Exception) {
            val state = candidate?.state ?: AudioTrack.STATE_UNINITIALIZED
            candidate?.let { runCatching { it.release() } }
            Log.w("rigPlay", "playTestTone streamType=$streamType unavailable", error)
            toast(getString(R.string.audio_stream_unavailable, streamType, state))
            return
        }
        Log.i("rigPlay", "playTestTone streamType=$streamType state=${track.state} playState=${track.playState}")
        testToneTrack = track
        val stop = Runnable {
            track.stop()
            track.release()
            if (testToneTrack === track) testToneTrack = null
            toneStop = null
        }
        toneStop = stop
        handler.postDelayed(stop, 4500)
    }

    private val channelButtons = mutableListOf<Button>()

    private fun paintChannel(index: Int, selected: Boolean) {
        val target = channelButtons.getOrNull(index) ?: return
        target.isSelected = selected
        target.setTextColor(if (selected) BG else TEXT)
        target.background = android.graphics.drawable.RippleDrawable(
            ColorStateList.valueOf(0x336F9FD9),
            rounded(if (selected) ACCENT else SURFACE, if (selected) ACCENT else BORDER),
            null
        )
    }

    private fun channelSelector(): ViewGroup {
        channelButtons.clear()
        val grid = GridLayout(this).apply {
            columnCount = 7
            rowCount = 3
            setPadding(0, dp(8), 0, dp(8))
        }
        for (i in 0..20) {
            val btn = Button(this).apply {
                text = i.toString()
                isAllCaps = false
                textSize = 16f
                minHeight = dp(48)
                stateListAnimator = null
                setOnClickListener {
                    val previous = navigationStreamType
                    navigationStreamType = i
                    if (previous != i) {
                        paintChannel(previous, false)
                        paintChannel(i, true)
                    }
                    playTestTone(i)
                }
            }
            val params = GridLayout.LayoutParams().apply {
                width = 0
                height = dp(48)
                columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f)
                setMargins(dp(4), dp(4), dp(4), dp(4))
            }
            grid.addView(btn, params)
            channelButtons.add(btn)
            paintChannel(i, i == navigationStreamType)
        }
        return grid
    }
    private fun version() = packageManager.getPackageInfo(packageName, 0).versionName ?: "0.1.0-beta.1"
    private fun languageSettings(content: LinearLayout) {
        section(content, getString(R.string.language_section_title)) { card ->
            card.addView(label(getString(R.string.language_hint), 14, MUTED))
            val current = AppLocale.preference(this)
            val languageButton = button("${getString(R.string.language_app_language)} · ${AppLocale.displayName(this, current)}", false) { }
            languageButton.setOnClickListener { AppLocale.showPicker(this) }
            card.addView(languageButton, matchButton(12, 60))
        }
    }

    private fun section(parent: LinearLayout, title: String, icon: Int? = null, build: (LinearLayout) -> Unit) {
        val card = card()
        val heading = row().apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, 0, 0, dp(16)) }
        if (icon != null) heading.addView(ImageView(this).apply {
            setImageResource(icon); imageTintList = ColorStateList.valueOf(ACCENT)
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }, LinearLayout.LayoutParams(dp(28), dp(28)).apply { marginEnd = dp(12) })
        heading.addView(label(title, 22, TEXT, true), LinearLayout.LayoutParams(0, -2, 1f))
        card.addView(heading)
        build(card)
        parent.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(18) })
    }
    private fun toggle(parent: LinearLayout, title: String, description: String, value: Boolean, save: (Boolean) -> Unit) {
        val line = row().apply { gravity = Gravity.CENTER_VERTICAL; setPadding(0, dp(12), 0, dp(12)) }
        val text = column(); text.addView(label(title, 18, TEXT, true)); text.addView(label(description, 14, MUTED).apply { setPadding(0, dp(6), dp(16), 0) })
        line.addView(text, LinearLayout.LayoutParams(0, -2, 1f))
        line.addView(Switch(this).apply { contentDescription = title; isChecked = value; minHeight = dp(56); buttonTintList = ColorStateList.valueOf(ACCENT); setOnCheckedChangeListener { _, checked -> save(checked) } })
        parent.addView(line)
    }
    private fun choice(parent: LinearLayout, title: String, options: List<String>, current: Int, reconnects: Boolean = true, save: (Int) -> Unit) {
        var selection = current
        val button = button("$title · ${options[selection]}", false) {}
        button.setOnClickListener {
            var pendingSelection = selection
            AlertDialog.Builder(this).setTitle(title)
                .setSingleChoiceItems(options.toTypedArray(), selection) { _, index -> pendingSelection = index }
                .setPositiveButton(getString(if (reconnects && CarPlayBackgroundSession.hasSession()) R.string.apply_and_reconnect else R.string.save)) { _, _ ->
                    if (pendingSelection != selection) {
                        selection = pendingSelection
                        save(selection)
                        button.text = "$title · ${options[selection]}"
                        if (reconnects && CarPlayBackgroundSession.hasSession()) {
                            connect(AirPlayPersistence.loadWirelessEnabled(this))
                        }
                    }
                }.setNegativeButton(getString(R.string.cancel), null).show()
        }
        parent.addView(button, matchButton(0, 60)); parent.addView(space(12))
    }
    private fun card() = column().apply { background = rounded(SURFACE, BORDER); setPadding(dp(24), dp(24), dp(24), dp(24)) }
    private fun column() = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; layoutParams = LinearLayout.LayoutParams(-1, -2) }
    private fun row() = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; layoutParams = LinearLayout.LayoutParams(-1, -2) }
    private fun label(value: String, size: Int, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = value; textSize = size.toFloat(); setTextColor(color); gravity = Gravity.CENTER_VERTICAL
        typeface = if (bold) Typeface.create("sans-serif-medium", Typeface.NORMAL) else Typeface.create("sans-serif", Typeface.NORMAL)
        setLineSpacing(dp(3).toFloat(), 1f)
    }
    private fun button(title: String, primary: Boolean, click: () -> Unit) = Button(this).apply {
        text = title; isAllCaps = false; textSize = 18f; setTextColor(if (primary) BG else TEXT)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        background = android.graphics.drawable.RippleDrawable(ColorStateList.valueOf(0x336F9FD9), rounded(if (primary) ACCENT else SURFACE, if (primary) ACCENT else BORDER), null)
        setPadding(dp(16), 0, dp(16), 0); minHeight = dp(56); stateListAnimator = null
        setOnClickListener { click() }
    }
    private fun rounded(color: Int, stroke: Int) = GradientDrawable().apply { setColor(color); cornerRadius = dp(20).toFloat(); setStroke(dp(1), stroke) }
    private fun matchButton(top: Int = 0, height: Int = 68) = LinearLayout.LayoutParams(-1, dp(height)).apply { topMargin = dp(top) }
    private fun space(height: Int) = View(this).apply { layoutParams = LinearLayout.LayoutParams(1, dp(height)) }
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    companion object {
        private val BG = Color.rgb(12, 17, 27)
        private val SURFACE = Color.rgb(21, 30, 44)
        private val BORDER = Color.rgb(42, 56, 75)
        private val ACCENT = Color.rgb(166, 200, 255)
        private val TEXT = Color.rgb(241, 245, 252)
        private val MUTED = Color.rgb(168, 182, 202)
        private val WARNING = Color.rgb(255, 196, 128)
    }
}
