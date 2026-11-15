# 批量截图：三套皮肤 × 若干视图
param(
    [string[]]$Jobs = @(
        "fluent|library|12-library-fluent",
        "md3|library|13-library-md3",
        "glass|library|14-library-glass",
        "fluent|timeline|15-timeline",
        "fluent|duplicates|16-duplicates",
        "md3|settings|17-settings-md3",
        "glass|aiqueue|18-aiqueue-glass"
    )
)

$repo = (Get-Location).Path
foreach ($job in $Jobs) {
    $parts = $job.Split('|')
    $skin = $parts[0]; $view = $parts[1]; $name = $parts[2]
    node -e "const fs=require('fs');const p='C:/Users/Administrator/AppData/Local/StjMediaViewer/settings.json';const s=JSON.parse(fs.readFileSync(p,'utf8'));s.designLanguage='$skin';s.lastViewId='$view';fs.writeFileSync(p,JSON.stringify(s,null,2),'utf8');"
    & "$repo\tools\dev\shot.ps1" -Out "shots/$name.png" -Wait 14
    Write-Host "--- $name done"
}