package com.shilapi.xcertplay

import android.app.Application

/** Creates the process-wide SimHub link owner at process start, before any activity or receiver runs. */
class RigPlayApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        RigSessionCoordinator.init(this)
    }
}
