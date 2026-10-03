package com.shilapi.xcertplay

import android.app.Activity
import android.app.Application
import android.os.Bundle
import com.shilapi.xcertplay.orchestration.CarPlayController

/**
 * Creates the process-wide SimHub link owner at process start, before any activity or receiver runs,
 * and tells it which rigPlay screen is in the foreground (`status.screen`, #29).
 */
class RigPlayApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        RigSessionCoordinator.init(this)
        registerActivityLifecycleCallbacks(ForegroundTracker())
        // CarPlay's OEM icon ("SimHub") opens the dashboard instead of the launcher (#30).
        CarPlayController.hostUiOpener = { context ->
            RigSessionCoordinator.showDashboard(context)
            true
        }
    }

    private class ForegroundTracker : ActivityLifecycleCallbacks {
        private var started = 0

        override fun onActivityStarted(activity: Activity) { started++ }

        override fun onActivityResumed(activity: Activity) {
            RigSessionCoordinator.onForegroundChanged(foregroundOf(activity))
        }

        override fun onActivityStopped(activity: Activity) {
            started = (started - 1).coerceAtLeast(0)
            if (started == 0) RigSessionCoordinator.onForegroundChanged(RigSessionLifecycle.Foreground.NONE)
        }

        override fun onActivityCreated(activity: Activity, savedInstanceState: Bundle?) = Unit
        override fun onActivityPaused(activity: Activity) = Unit
        override fun onActivitySaveInstanceState(activity: Activity, outState: Bundle) = Unit
        override fun onActivityDestroyed(activity: Activity) = Unit
    }

    companion object {
        fun foregroundOf(activity: Activity): RigSessionLifecycle.Foreground = when (activity) {
            is CarPlayHostActivity -> RigSessionLifecycle.Foreground.CARPLAY
            is DashboardActivity ->
                if (activity.idleMode) RigSessionLifecycle.Foreground.IDLE_DASHBOARD else RigSessionLifecycle.Foreground.DASHBOARD
            is OfflineIdleActivity -> RigSessionLifecycle.Foreground.OFFLINE_IDLE
            else -> RigSessionLifecycle.Foreground.HOME
        }
    }
}
