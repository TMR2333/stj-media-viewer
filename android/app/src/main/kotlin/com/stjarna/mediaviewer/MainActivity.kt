package com.stjarna.mediaviewer

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.documentfile.provider.DocumentFile
import com.stjarna.mediaviewer.ui.LibraryScreen
import com.stjarna.mediaviewer.ui.SettingsScreen
import com.stjarna.mediaviewer.ui.StjTheme
import com.stjarna.mediaviewer.vm.LibraryViewModel
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

class MainActivity : ComponentActivity() {
    private val vm: LibraryViewModel by viewModels()
    private val cloudVm: com.stjarna.mediaviewer.cloud.CloudAlbumViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            StjTheme(themeMode = vm.state.themeMode, dynamicColor = vm.state.dynamicColor) {
                Surface(Modifier.fillMaxSize()) {
                    AppRoot(vm, cloudVm)
                }
            }
        }
    }
}

@Composable
private fun AppRoot(vm: LibraryViewModel, cloudVm: com.stjarna.mediaviewer.cloud.CloudAlbumViewModel) {
    var showSettings by rememberSaveable { mutableStateOf(false) }
    var showCloud by rememberSaveable { mutableStateOf(false) }
    val app = LocalContext.current.applicationContext

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { }
    LaunchedEffect(Unit) {
        val permissions = if (Build.VERSION.SDK_INT >= 33) {
            arrayOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        } else {
            arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        permissionLauncher.launch(permissions)
    }

    val folderLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { uri ->
        if (uri != null) {
            runCatching {
                val name = DocumentFile.fromTreeUri(app, uri)?.name
                vm.addSafRoot(uri, name)
            }
        }
    }

    val exportLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/json")
    ) { uri ->
        if (uri != null) vm.exportExchange(uri)
    }

    val importLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri ->
        if (uri != null) vm.prepareImport(uri)
    }

    if (showCloud) {
        com.stjarna.mediaviewer.cloud.CloudAlbumScreen(
            vm = cloudVm,
            libraryVm = vm,
            onBack = { showCloud = false },
        )
    } else if (showSettings) {
        SettingsScreen(
            vm = vm,
            onBack = { showSettings = false },
            onPickLibraryFolder = { folderLauncher.launch(null) },
            onPickRootPath = { showSettings = true },
            onExportExchange = {
                val name = "STJ-设置与图片数据-" +
                    DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss").format(LocalDateTime.now()) + ".json"
                exportLauncher.launch(name)
            },
            onImportExchange = { importLauncher.launch(arrayOf("application/json", "text/plain", "*/*")) },
        )
    } else {
        LibraryScreen(vm = vm, onOpenSettings = { showSettings = true }, onOpenCloud = { showCloud = true })
    }
}