# Builds the zip that goes on GitHub Releases.
#
# The zip holds one file: Mp4Merger.exe. Everything else in ReleaseBuild is
# either forbidden or pointless to ship:
#
#   ffmpeg.exe / ffprobe.exe  GPL builds. Shipping them alongside this app
#                             would oblige us to offer their source. They are
#                             downloaded on first run instead, which is why
#                             this app's MIT licence covers only its own code.
#   *.pdb                     debug symbols, no use to anyone downloading
#   test footage              may not be ours to redistribute
#
# See RELEASE_CHECKLIST.md. Run .\build_exe.bat first.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$releaseBuild = Join-Path $root "ReleaseBuild"
$exe = Join-Path $releaseBuild "Mp4Merger.exe"
$distDir = Join-Path $root "dist"

function Fail($message) {
    Write-Host ""
    Write-Host "中止しました: $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $exe)) {
    Fail "ReleaseBuild\Mp4Merger.exe がありません。先に .\build_exe.bat を実行してください。"
}

# The version lives in Mp4Merger.csproj and nowhere else, so read it back off
# the binary rather than asking for it again here.
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    Fail "exe から版番号を読めませんでした。"
}
$version = ($version -split '\+')[0]
# FileVersion always carries four parts; the csproj says three, and that is what
# the release is called.
if ($version -match '^(\d+\.\d+\.\d+)\.0$') { $version = $Matches[1] }

# A stale exe is the easy mistake: edit, forget to build, ship yesterday's work.
$exeTime = (Get-Item $exe).LastWriteTime
$newestSource = Get-ChildItem -Path $root -Recurse -File -Include *.cs, *.xaml, *.csproj |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|ReleaseBuild|dist|Tests)\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestSource -and $newestSource.LastWriteTime -gt $exeTime) {
    Write-Host "exe より新しいソースがあります:" -ForegroundColor Yellow
    Write-Host "  $($newestSource.Name)  $($newestSource.LastWriteTime)" -ForegroundColor Yellow
    Write-Host "  exe                    $exeTime" -ForegroundColor Yellow
    Fail "ビルドし直してください（.\build_exe.bat）。"
}

# Say out loud what is being left behind, so nothing is dropped silently.
$others = Get-ChildItem $releaseBuild -File | Where-Object { $_.Name -ne "Mp4Merger.exe" }
if ($others) {
    Write-Host "zip に入れないもの:" -ForegroundColor DarkGray
    foreach ($f in $others) { Write-Host "  - $($f.Name)" -ForegroundColor DarkGray }
}

$zipName = "Mp4Merger-$version-win-x64.zip"
$zipPath = Join-Path $distDir $zipName
New-Item -ItemType Directory -Force $distDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Compress the one file by name, so nothing else can be swept in.
Compress-Archive -Path $exe -DestinationPath $zipPath -CompressionLevel Optimal

# Check what actually landed in there rather than trusting the call above.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    $forbidden = $entries | Where-Object { $_ -match 'ffmpeg|ffprobe|realesrgan|\.pdb$|\.mp4$|\.mov$|\.mpg$' }
    if ($forbidden) { Fail "zip に入ってはいけないものが入りました: $($forbidden -join ', ')" }
    if ($entries.Count -ne 1 -or $entries[0] -ne "Mp4Merger.exe") {
        Fail "zip の中身が Mp4Merger.exe ひとつではありません: $($entries -join ', ')"
    }
} finally {
    $archive.Dispose()
}

$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "できました" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  版        $version"
Write-Host "  大きさ    $mb MB"
Write-Host "  中身      Mp4Merger.exe ひとつだけ（確認ずみ）"
Write-Host "  SHA-256   $sha"
Write-Host ""
Write-Host "最後に手で確かめること（RELEASE_CHECKLIST.md）:" -ForegroundColor Yellow
Write-Host "  空のフォルダに exe だけを置いて、起動すること"
