package com.shilapi.xcertplay.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.Looper
import com.shilapi.xcertplay.transport.Iap2WirelessSecurity
import java.io.Closeable
import java.io.IOException
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.util.concurrent.TimeUnit

/**
 * Experimental (#33): serves wireless CarPlay on the Wi-Fi network the tablet is already a client of
 * (the home LAN that also reaches the PC), instead of creating a network for the iPhone.
 *
 * It reads the station interface from [ConnectivityManager], its address, and the channel from
 * [WifiInfo.getFrequency]. The network name comes from [WifiInfo] when Android exposes it (Android 10+
 * hides it without location permission) and otherwise from Settings; the password always comes from
 * Settings. It never creates, changes or tears down a network: [close] only releases its callback.
 *
 * The router's BSSID is read for diagnostics only. [WirelessHotspotInfo.bssid] doubles as the AirPlay
 * device identifier, which must stay rigPlay's own and stable across roaming, so it is left null here
 * and the controller uses the saved identifier. The iAP2 0x5703 message omits the BSSID anyway.
 */
class ExistingNetworkHotspotManager(
    context: Context,
    ssid: String,
    passphrase: String,
    private val onDiagnostic: (String) -> Unit = {},
    private val reader: StationWifiReader = AndroidStationWifiReader(context),
) : WirelessHotspotManager {
    private val configuredSsid = ssid.trim()
    private val passphrase = passphrase

    @Volatile private var closed = false
    @Volatile private var stationLost = false
    @Volatile private var watch: Closeable? = null

    init {
        require('\u0000' !in configuredSsid && '\u0000' !in passphrase) {
            "network name and password must not contain U+0000"
        }
        require(configuredSsid.encodeToByteArray().size <= 32) { "network name must be at most 32 bytes" }
        require(passphrase.isEmpty() || passphrase.length in 8..63) {
            "password must be empty or between 8 and 63 characters"
        }
    }

    override fun start(timeoutMillis: Long): WirelessHotspotInfo {
        check(Looper.myLooper() != Looper.getMainLooper()) {
            "ExistingNetworkHotspotManager.start must not run on the main thread"
        }
        require(timeoutMillis > 0) { "timeoutMillis must be positive" }
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
        while (true) {
            check(!closed) { "ExistingNetworkHotspotManager is closed" }
            val station = readStation()
            if (station?.hostAddress != null) {
                val info = existingNetworkHotspotInfo(station, configuredSsid, passphrase)
                if (watch == null) watch = reader.watch { stationLost = true }
                onDiagnostic(
                    "Existing network iface=${station.interfaceName} " +
                        "family=${if (info.hostAddress is Inet6Address) "IPv6" else "IPv4"} " +
                        "channel=${info.channel} channelKnown=${info.channel > 0} " +
                        "frequency=${info.frequencyMHz?.toString() ?: "unknown"}MHz " +
                        "networkNameReadable=${info.ssidReadable} routerAddressReadable=${station.bssid != null} " +
                        "security=${info.security}",
                )
                return info
            }
            val remaining = deadline - System.nanoTime()
            if (remaining <= 0) {
                throw IOException(
                    if (station == null) {
                        "The tablet is not connected to a Wi-Fi network. Join the home Wi-Fi and connect again."
                    } else {
                        "The tablet's Wi-Fi connection has no usable address yet"
                    },
                )
            }
            try {
                TimeUnit.NANOSECONDS.sleep(minOf(remaining, POLL_NANOS))
            } catch (interrupted: InterruptedException) {
                Thread.currentThread().interrupt()
                throw IOException("Interrupted while waiting for the Wi-Fi connection", interrupted)
            }
        }
    }

    private fun readStation(): StationWifiSnapshot? =
        stationWifiSnapshot(reader.wifiLinkProperties(), reader.wifiInfo(), reader::interfaceIndex)

    override fun connectionDiagnosticSnapshot(): String =
        "association=not_exposed station=${if (stationLost) "lost" else "connected"}"

    /** Releases the network callback only; the home Wi-Fi connection is left exactly as it was. */
    override fun close() {
        closed = true
        watch?.let { runCatching { it.close() } }
        watch = null
    }

    override fun toString(): String =
        "ExistingNetworkHotspotManager(configuredSsid=${configuredSsid.isNotEmpty()}, passphrase=<redacted>)"

    private companion object {
        val POLL_NANOS: Long = TimeUnit.MILLISECONDS.toNanos(250)
    }
}

/** Read-only view of the tablet's Wi-Fi client (station) connection; injectable for tests. */
interface StationWifiReader {
    /** Link properties of the connected Wi-Fi network, or null when the tablet is not on Wi-Fi. */
    fun wifiLinkProperties(): LinkProperties?

    /** Current connection info. Android redacts SSID and BSSID without location permission. */
    fun wifiInfo(): WifiInfo?

    /** Kernel index used to scope an IPv6 link-local address; 0 when unknown. */
    fun interfaceIndex(name: String): Int = 0

    /** Reports loss of the Wi-Fi network; closing the handle unregisters the callback. */
    fun watch(onLost: () -> Unit): Closeable = Closeable {}
}

/** [StationWifiReader] over [ConnectivityManager] and [WifiManager]. Never changes Wi-Fi state. */
class AndroidStationWifiReader(context: Context) : StationWifiReader {
    private val appContext = context.applicationContext ?: context
    private val connectivity = appContext.getSystemService(ConnectivityManager::class.java)
    private val wifi = appContext.getSystemService(WifiManager::class.java)

    @Suppress("DEPRECATION") // allNetworks: the active network may be a VPN or Ethernet.
    override fun wifiLinkProperties(): LinkProperties? {
        val cm = connectivity ?: return null
        return try {
            val candidates = listOfNotNull(cm.activeNetwork) + cm.allNetworks.toList()
            candidates.firstOrNull { network ->
                cm.getNetworkCapabilities(network)?.let {
                    it.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) &&
                        !it.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
                } == true
            }?.let(cm::getLinkProperties)
        } catch (_: SecurityException) {
            null
        }
    }

    @Suppress("DEPRECATION") // connectionInfo is the only synchronous source of SSID and frequency.
    override fun wifiInfo(): WifiInfo? = try {
        wifi?.connectionInfo
    } catch (_: SecurityException) {
        null
    }

    override fun interfaceIndex(name: String): Int = try {
        NetworkInterface.getByName(name)?.index ?: 0
    } catch (_: Exception) {
        0
    }

    override fun watch(onLost: () -> Unit): Closeable {
        val cm = connectivity ?: return Closeable {}
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onLost(network: Network) = onLost()
        }
        return try {
            cm.registerNetworkCallback(
                NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(),
                callback,
            )
            Closeable { runCatching { cm.unregisterNetworkCallback(callback) } }
        } catch (_: RuntimeException) {
            Closeable {}
        }
    }
}

/** What existing-network mode needs from the station connection, already normalised. */
data class StationWifiSnapshot(
    val interfaceName: String,
    val addresses: List<InetAddress>,
    val interfaceIndex: Int,
    /** Unquoted network name, or null when Android hides it. */
    val ssid: String?,
    /** Router BSSID, or null when Android redacts it. */
    val bssid: String?,
    val frequencyMHz: Int?,
) {
    val channel: Int get() = frequencyMHz?.let(::wifiFrequencyMhzToChannel) ?: 0
    val bandLabel: String? get() = frequencyMHz?.let(::wifiFrequencyBandLabel)
    /** The address the AirPlay listener binds and the iPhone is given. */
    val hostAddress: InetAddress? get() = stationHostAddress(addresses, interfaceIndex)
}

/** Combines [LinkProperties] and [WifiInfo] into a snapshot; null when the tablet is not on Wi-Fi. */
fun stationWifiSnapshot(
    link: LinkProperties?,
    info: WifiInfo?,
    interfaceIndex: (String) -> Int = { 0 },
): StationWifiSnapshot? {
    val name = link?.interfaceName?.takeIf { it.isNotBlank() } ?: return null
    return StationWifiSnapshot(
        interfaceName = name,
        addresses = link.linkAddresses.map { it.address },
        interfaceIndex = interfaceIndex(name),
        ssid = readableSsid(info?.ssid),
        bssid = info?.bssid?.takeUnless { it.isBlank() || it in REDACTED_BSSIDS }?.lowercase(),
        frequencyMHz = info?.frequency?.takeIf { it > 0 },
    )
}

/** Android returns UTF-8 names quoted and `<unknown ssid>` without location permission. */
internal fun readableSsid(raw: String?): String? {
    val value = raw?.trim() ?: return null
    if (value.length < 3 || value.first() != '"' || value.last() != '"') return null
    return value.substring(1, value.length - 1).takeIf { it.isNotEmpty() }
}

/** Prefers the LAN IPv4 address; a scoped IPv6 link-local address is the fallback. */
internal fun stationHostAddress(addresses: List<InetAddress>, interfaceIndex: Int): InetAddress? {
    addresses.firstOrNull {
        it is Inet4Address && !it.isLoopbackAddress && !it.isLinkLocalAddress &&
            !it.isAnyLocalAddress && !it.isMulticastAddress
    }?.let { return it }
    if (interfaceIndex <= 0) return null
    return addresses.filterIsInstance<Inet6Address>().firstOrNull { it.isLinkLocalAddress }
        ?.let { Inet6Address.getByAddress(null, it.address, interfaceIndex) }
}

/**
 * Builds the hotspot details handed to the iPhone. The live network name wins when readable; a
 * different saved name means the saved password is for another network, so it fails instead.
 */
internal fun existingNetworkHotspotInfo(
    station: StationWifiSnapshot,
    configuredSsid: String,
    passphrase: String,
): WirelessHotspotInfo {
    val hostAddress = stationHostAddress(station.addresses, station.interfaceIndex)
        ?: throw IOException("The tablet's Wi-Fi connection has no usable address")
    val configured = configuredSsid.trim()
    val ssid = when {
        station.ssid == null && configured.isEmpty() -> throw IOException(
            "Android hides the Wi-Fi network name. Enter it in Settings → Connection setup.",
        )
        station.ssid == null -> configured
        configured.isNotEmpty() && configured != station.ssid -> throw IOException(
            "The tablet is connected to a different Wi-Fi network than the one saved in Settings",
        )
        else -> station.ssid
    }
    return WirelessHotspotInfo(
        ssid = ssid,
        passphrase = passphrase,
        security = if (passphrase.isEmpty()) Iap2WirelessSecurity.NONE else Iap2WirelessSecurity.WPA_WPA2,
        channel = station.channel,
        frequencyMHz = station.frequencyMHz,
        bssid = null,
        interfaceName = station.interfaceName,
        hostAddress = hostAddress,
        bandLabel = station.frequencyMHz?.let(::wifiFrequencyBandLabel) ?: "Unknown band",
        backend = WirelessHotspotBackend.EXISTING_NETWORK,
        ssidReadable = station.ssid != null,
    )
}

internal fun wifiFrequencyBandLabel(frequencyMHz: Int): String = when (frequencyMHz) {
    in 2400..2500 -> "2.4 GHz"
    in 5150..5895 -> "5 GHz"
    in 5925..7125 -> "6 GHz"
    else -> "Unknown band"
}

private val REDACTED_BSSIDS = setOf("02:00:00:00:00:00", "00:00:00:00:00:00")
