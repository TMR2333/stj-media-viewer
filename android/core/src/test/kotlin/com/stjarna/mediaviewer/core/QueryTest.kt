package com.stjarna.mediaviewer.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class QueryTest {
    private fun asset(
        name: String,
        kind: MediaKind = MediaKind.Image,
        format: String = "JPG",
        raw: RawFormat = RawFormat.None,
        rating: Int = 0,
        city: String? = null,
        device: String? = null,
        themes: List<String> = emptyList(),
        isPick: Boolean? = null,
        postProcess: PostProcessState = PostProcessState.Unknown,
        capturedAt: Long? = null,
    ) = MediaAsset(
        id = name,
        fileName = name,
        kind = kind,
        formatCode = format,
        rawFormat = raw,
        rating = rating,
        city = city,
        deviceKey = device,
        themes = themes.toMutableList(),
        isPick = isPick,
        postProcess = postProcess,
        capturedAt = capturedAt,
    )

    @Test
    fun unsetSemantics() {
        val withCity = asset("a.jpg", city = "平遥")
        val without = asset("b.jpg")
        val filter = AssetFilter(cities = setOf(UNSET))
        assertTrue(filter.matches(without))
        assertFalse(filter.matches(withCity))
    }

    @Test
    fun multiThemeMatchesAny() {
        val a = asset("a.jpg", themes = listOf("风光", "夜景"))
        assertTrue(AssetFilter(themes = setOf("夜景")).matches(a))
        assertFalse(AssetFilter(themes = setOf("人像")).matches(a))
    }

    @Test
    fun unsetThemeMatchesEmpty() {
        val a = asset("a.jpg")
        assertTrue(AssetFilter(themes = setOf(UNSET)).matches(a))
        assertFalse(AssetFilter(themes = setOf(UNSET)).matches(asset("b.jpg", themes = listOf("风光"))))
    }

    @Test
    fun pickTriState() {
        val picked = asset("p.jpg", isPick = true)
        val rejected = asset("r.jpg", isPick = false)
        val unmarked = asset("u.jpg")
        assertTrue(AssetFilter(pickState = PickState.Picked).matches(picked))
        assertFalse(AssetFilter(pickState = PickState.Picked).matches(unmarked))
        assertTrue(AssetFilter(pickState = PickState.Rejected).matches(rejected))
        assertFalse(AssetFilter(pickState = PickState.Rejected).matches(unmarked))
        assertTrue(AssetFilter(pickState = PickState.Unmarked).matches(unmarked))
    }

    @Test
    fun crossGroupAnd() {
        val a = asset("a.jpg", kind = MediaKind.Video, format = "MP4", rating = 5)
        val filter = AssetFilter(kinds = setOf(MediaKind.Video), minRating = 4)
        assertTrue(filter.matches(a))
        assertFalse(AssetFilter(kinds = setOf(MediaKind.Image), minRating = 4).matches(a))
    }

    @Test
    fun sortByRating() {
        val list = listOf(asset("1.jpg", rating = 1), asset("5.jpg", rating = 5), asset("3.jpg", rating = 3))
        val sorted = AssetFilter(sort = SortKey.Rating, descending = true).sorted(list)
        assertEquals(listOf("5.jpg", "3.jpg", "1.jpg"), sorted.map { it.fileName })
    }

    @Test
    fun facetsCounts() {
        val list = listOf(
            asset("a.jpg", city = "平遥", rating = 5, themes = listOf("风光")),
            asset("b.jpg", themes = listOf("风光", "夜景")),
            asset("c.mp4", kind = MediaKind.Video, format = "MP4"),
        )
        val facets = computeFacets(list)
        assertEquals(3, facets.total)
        assertEquals(2, facets.kinds[MediaKind.Image])
        assertEquals(1, facets.kinds[MediaKind.Video])
        assertEquals(2, facets.themes["风光"])
        assertEquals(1, facets.themes[UNSET])
        assertEquals(2, facets.cities[UNSET])
    }
}