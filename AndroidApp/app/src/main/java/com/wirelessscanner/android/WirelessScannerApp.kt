package com.wirelessscanner.android

import android.app.Application

class WirelessScannerApp : Application() {
    /** One process-wide graph: state survives rotation and activity recreation (A-LRN-039/044); tests keep running. */
    val graph: AppGraph by lazy { AppGraph(this) }
}
