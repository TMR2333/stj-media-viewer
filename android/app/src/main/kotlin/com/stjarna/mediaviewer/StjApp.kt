package com.stjarna.mediaviewer

import android.app.Application
import com.stjarna.mediaviewer.core.geo.OfflineGeocoder
import com.stjarna.mediaviewer.data.CatalogDb
import com.stjarna.mediaviewer.data.SettingsStore
import com.stjarna.mediaviewer.exchange.ExchangeIo
import com.stjarna.mediaviewer.scan.MediaScanner
import com.stjarna.mediaviewer.scan.MetadataReader
import com.stjarna.mediaviewer.scan.RootManager
import com.stjarna.mediaviewer.scan.ThumbnailLoader
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

class StjApp : Application() {
    lateinit var db: CatalogDb
        private set
    lateinit var settings: SettingsStore
        private set
    lateinit var thumbnails: ThumbnailLoader
        private set
    lateinit var metadata: MetadataReader
        private set
    lateinit var scanner: MediaScanner
        private set
    lateinit var exchange: ExchangeIo
        private set

    @Volatile
    var geocoder: OfflineGeocoder? = null
        private set

    override fun onCreate() {
        super.onCreate()
        instance = this
        db = CatalogDb(this)
        settings = SettingsStore(this)
        thumbnails = ThumbnailLoader(this)
        metadata = MetadataReader(this) { geocoder }
        scanner = MediaScanner(this, db, metadata)
        exchange = ExchangeIo(this, db, settings)

        CoroutineScope(Dispatchers.IO).launch {
            runCatching {
                assets.open("geo/cities.tsv.gz").use { OfflineGeocoder.load(it) }
            }.onSuccess { geocoder = it }
        }
        // 启动时顺手探测一次 root（不阻塞）
        if (settings.rootMode) {
            CoroutineScope(Dispatchers.IO).launch { RootManager.isAvailable(force = true) }
        }
    }

    companion object {
        lateinit var instance: StjApp
            private set
    }
}