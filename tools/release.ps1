# STJ Media Viewer release helper (ASCII only: Windows PowerShell 5.1 reads system codepage).
#
# Usage:
#   $env:GITCODE_TOKEN = "<personal access token>"
#   powershell -ExecutionPolicy Bypass -File tools/release.ps1 -Version 1.0.2 -Notes "what changed"
#
# Steps: bump versions -> build packages -> commit -> tag -> push -> create GitCode release.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipPackage,
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "[1/6] bump version -> $Version"
    $appCsproj = Join-Path $root 'src\StjMediaViewer.App\StjMediaViewer.App.csproj'
    $csproj = Get-Content $appCsproj -Raw
    $csproj = [regex]::Replace($csproj, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
    $csproj = [regex]::Replace($csproj, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
    $csproj = [regex]::Replace($csproj, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>")
    $csproj = [regex]::Replace($csproj, '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>")
    Set-Content -Path $appCsproj -Value $csproj -Encoding UTF8

    $instProj = Join-Path $root 'tools\Installer\Installer.csproj'
    (Get-Content $instProj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $instProj -Encoding UTF8

    $instProgram = Join-Path $root 'tools\Installer\Program.cs'
    (Get-Content $instProgram -Raw) -replace 'private const string Version = "[^"]+";', "private const string Version = `"$Version`";" | Set-Content $instProgram -Encoding UTF8

    $buildScript = Join-Path $root 'tools\Installer\build-installer.ps1'
    (Get-Content $buildScript -Raw) -replace '\[string\]\$Version = "[^"]+"', "[string]`$Version = `"$Version`"" | Set-Content $buildScript -Encoding UTF8

    $plist = Join-Path $root 'macos\Resources\Info.plist'
    if (Test-Path $plist) {
        (Get-Content $plist -Raw) -replace '(<key>CFBundleShortVersionString</key>\s*<string>)[^<]+', "`${1}$Version" | Set-Content $plist -Encoding UTF8
    }

    if (-not $SkipPackage) {
        Write-Host "[2/6] build packages"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Installer\build-installer.ps1') -Version $Version
        if ($LASTEXITCODE -ne 0) { throw "build-installer failed: $LASTEXITCODE" }
    } else {
        Write-Host "[2/6] skip package"
    }

    Write-Host "[3/6] commit"
    git add -A
    git -c user.email=release@stj.local -c user.name=release commit -m "release: v$Version`n`n$Notes" | Out-Host

    Write-Host "[4/6] tag v$Version"
    git tag -f "v$Version" | Out-Host

    if (-not $SkipPush) {
        Write-Host "[5/6] push"
        git push origin master | Out-Host
        git push -f origin "refs/tags/v$Version" | Out-Host
    } else {
        Write-Host "[5/6] skip push"
    }

    if ($env:GITCODE_TOKEN) {
        Write-Host "[6/7] create GitCode release"
        $repo = 'TMR2333/stj-media-viewer'
        $api = "https://api.gitcode.com/api/v5/repos/$repo"
        $tag = "v$Version"
        $payload = @{
            tag_name = $tag
            name     = "STJ Media Viewer v$Version"
            body     = if ($Notes) { $Notes } else { "STJ Media Viewer v$Version" }
            target_commitish = 'master'
            prerelease = $false
        } | ConvertTo-Json -Depth 4

        # GitCode 的 JSON 接口会把裸 UTF-8 当本地代码页解码 -> 中文变乱码。
        # 统一转义成 \uXXXX（纯 ASCII 请求体）后中文显示正常。
        $escaped = [regex]::Replace($payload, '[^\x00-\x7F]', {
            param($m) '\u{0:x4}' -f ([int][char]$m.Value)
        })
        try {
            $resp = Invoke-RestMethod -Method Post `
                -Uri "$api/releases?access_token=$($env:GITCODE_TOKEN)" `
                -Body ([System.Text.Encoding]::ASCII.GetBytes($escaped)) `
                -ContentType 'application/json'
            Write-Host ("release: " + $resp.tag_name)
        } catch {
            Write-Warning "release create failed: $($_.Exception.Message)"
        }

        Write-Host "[7/7] upload release assets"
        foreach ($file in Get-ChildItem (Join-Path $root 'dist') -Filter '*.exe' -ErrorAction SilentlyContinue) {
            try {
                $encodedName = [uri]::EscapeDataString($file.Name)
                $info = Invoke-RestMethod -Method Get `
                    -Uri "$api/releases/$tag/upload_url?access_token=$($env:GITCODE_TOKEN)&file_name=$encodedName"
                $headers = @{}
                $info.headers.PSObject.Properties | ForEach-Object { $headers[$_.Name] = [string]$_.Value }
                $null = Invoke-WebRequest -Method Put -Uri $info.url -InFile $file.FullName -Headers $headers -TimeoutSec 3600
                Write-Host ("  uploaded: " + $file.Name)
            } catch {
                Write-Warning ("  upload failed: " + $file.Name + " -> " + $_.Exception.Message)
            }
        }
    } else {
        Write-Host "[6/7] GITCODE_TOKEN not set -> release skipped"
    }

    Write-Host "done: v$Version"
}
finally {
    Pop-Location
}