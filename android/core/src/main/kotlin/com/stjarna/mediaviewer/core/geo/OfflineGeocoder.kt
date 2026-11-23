package com.stjarna.mediaviewer.core.geo

import java.io.BufferedReader
import java.io.InputStream
import java.io.InputStreamReader
import java.util.zip.GZIPInputStream
import kotlin.math.abs
import kotlin.math.asin
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * 离线城市反查（数据来自 GeoNames cities15000，CC BY 4.0；与 Windows 版同一份 31,701 城市数据）。
 * TSV 列：lat, lon, name, asciiname, country, admin1, population, cjkName, rank
 * 用 1° 网格分桶，GPS → 最近城市，够移动端"照片按地点看"用。
 */
class OfflineGeocoder private constructor(private val cities: List<City>, private val grid: Map<Long, List<Int>>) {

    data class City(
        val name: String,
        val cjkName: String?,
        val lat: Double,
        val lon: Double,
        val country: String,
        val admin1: String,
        val population: Long,
    ) {
        val display: String get() = cjkName?.takeIf { it.isNotBlank() } ?: name
    }

    /** 最近的已知城市；超过 [maxKm] 返回 null（避免把海上/荒野硬套城市名）。 */
    fun nearest(lat: Double, lon: Double, maxKm: Double = 80.0): City? {
        var best: City? = null
        var bestDistance = Double.MAX_VALUE
        val latCell = (lat + 90).toInt() / 1
        val lonCell = (lon + 180).toInt() / 1

        for (ring in 0..2) {
            for (dy in -ring..ring) for (dx in -ring..ring) {
                if (max(abs(dx), abs(dy)) != ring) continue
                val ids = grid[key(latCell + dy, lonCell + dx)] ?: continue
                for (idx in ids) {
                    val c = cities[idx]
                    val d = haversineKm(lat, lon, c.lat, c.lon)
                    if (d < bestDistance) {
                        bestDistance = d
                        best = c
                    }
                }
            }
            // 邻域里已经找到且足够近，就不用继续扩
            if (best != null && bestDistance <= ring * 111.0) break
        }
        return if (best != null && bestDistance <= maxKm) best else null
    }

    val size: Int get() = cities.size

    companion object {
        fun load(stream: InputStream): OfflineGeocoder {
            val cities = ArrayList<City>(34000)
            val grid = HashMap<Long, MutableList<Int>>(8192)
            GZIPInputStream(stream).use { gz ->
                BufferedReader(InputStreamReader(gz, Charsets.UTF_8), 1 shl 16).use { reader ->
                    var line = reader.readLine()
                    while (line != null) {
                        val cols = line.split('\t')
                        if (cols.size >= 7) {
                            val lat = cols[0].toDoubleOrNull()
                            val lon = cols[1].toDoubleOrNull()
                            if (lat != null && lon != null) {
                                val city = City(
                                    name = cols[2],
                                    cjkName = cols.getOrNull(7)?.takeIf { it.isNotBlank() },
                                    lat = lat,
                                    lon = lon,
                                    country = cols.getOrNull(4) ?: "",
                                    admin1 = cols.getOrNull(5) ?: "",
                                    population = cols.getOrNull(6)?.toLongOrNull() ?: 0,
                                )
                                val idx = cities.size
                                cities += city
                                val latCell = (lat + 90).toInt()
                                val lonCell = (lon + 180).toInt()
                                grid.getOrPut(key(latCell, lonCell)) { mutableListOf() }.add(idx)
                            }
                        }
                        line = reader.readLine()
                    }
                }
            }
            return OfflineGeocoder(cities, grid)
        }

        private fun key(latCell: Int, lonCell: Int): Long =
            (latCell.toLong() shl 32) or (lonCell.toLong() and 0xFFFFFFFFL)

        fun haversineKm(lat1: Double, lon1: Double, lat2: Double, lon2: Double): Double {
            val r = 6371.0
            val dLat = Math.toRadians(lat2 - lat1)
            val dLon = Math.toRadians(lon2 - lon1)
            val a = sin(dLat / 2) * sin(dLat / 2) +
                cos(Math.toRadians(lat1)) * cos(Math.toRadians(lat2)) * sin(dLon / 2) * sin(dLon / 2)
            return 2 * r * asin(min(1.0, sqrt(a)))
        }
    }
}