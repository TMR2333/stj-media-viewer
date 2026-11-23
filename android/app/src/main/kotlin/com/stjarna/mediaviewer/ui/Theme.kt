package com.stjarna.mediaviewer.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

/** STJ 的品牌色（没有动态取色时用）：暖白/炭黑 + 琥珀强调色。 */
private val Amber = Color(0xFFE9A23B)
private val AmberDark = Color(0xFFFFB95C)
private val Ink = Color(0xFF14141A)
private val InkDeep = Color(0xFF0C0C11)
private val Paper = Color(0xFFF6F2E9)

private val LightScheme = lightColorScheme(
    primary = Color(0xFF8C5A12),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFFFDDB1),
    onPrimaryContainer = Color(0xFF2C1700),
    secondary = Color(0xFF6E5B41),
    secondaryContainer = Color(0xFFF7E0C0),
    tertiary = Color(0xFF4F6350),
    background = Paper,
    onBackground = Color(0xFF1D1B16),
    surface = Paper,
    surfaceVariant = Color(0xFFEDE4D5),
    outline = Color(0xFF857463),
)

private val DarkScheme = darkColorScheme(
    primary = AmberDark,
    onPrimary = Color(0xFF492900),
    primaryContainer = Color(0xFF684000),
    onPrimaryContainer = Color(0xFFFFDDB1),
    secondary = Color(0xFFD8C3A4),
    secondaryContainer = Color(0xFF52432F),
    tertiary = Color(0xFFB6CCB7),
    background = InkDeep,
    onBackground = Color(0xFFEDE4D9),
    surface = Ink,
    surfaceVariant = Color(0xFF2A2A31),
    outline = Color(0xFF9A8C7B),
)

@Composable
fun StjTheme(
    themeMode: String,
    dynamicColor: Boolean,
    content: @Composable () -> Unit,
) {
    val dark = when (themeMode) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }
    val context = LocalContext.current
    val scheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= 31 ->
            if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        dark -> DarkScheme
        else -> LightScheme
    }
    MaterialTheme(
        colorScheme = scheme,
    ) {
        // Android 列表的拉伸回弹在快速滑动时观感很差；统一关掉 overscroll 效果。
        androidx.compose.runtime.CompositionLocalProvider(
            androidx.compose.foundation.LocalOverscrollFactory provides null,
        ) {
            content()
        }
    }
}

/** 强调色常量（用于自绘的小元素，如评分星、色标点）。 */
object StjColors {
    val amber = Amber
    val red = Color(0xFFE4572E)
    val yellow = Color(0xFFE9C46A)
    val green = Color(0xFF5A9E6F)
    val blue = Color(0xFF4A7BB7)
    val purple = Color(0xFF9B6BA8)

    fun colorLabel(value: String?): Color = when (value) {
        "Red" -> red
        "Yellow" -> yellow
        "Orange" -> Color(0xFFEE8A3C)
        "Green" -> green
        "Blue" -> blue
        "Purple" -> purple
        else -> Color.Transparent
    }
}