# 外部ツールの手動セットアップ手順

通常は初回起動時にアプリが `%LOCALAPPDATA%\Mp4Merger\` へ自動ダウンロードします。
オフライン環境などで手動配置したい場合は、以下の 2 つのツールを実行ファイル（`Mp4Merger.exe`）と同じフォルダ、または `%LOCALAPPDATA%\Mp4Merger\` に置いてください。

## 1. FFmpeg / FFprobe (動画処理用)
必須です（`ffmpeg.exe` と `ffprobe.exe` の両方が必要）。

1. **ダウンロード**
   - サイト: [https://github.com/BtbN/FFmpeg-Builds/releases](https://github.com/BtbN/FFmpeg-Builds/releases)（または [https://www.gyan.dev/ffmpeg/builds/](https://www.gyan.dev/ffmpeg/builds/)）
   - Windows 64-bit 版の `.zip` をダウンロードします。
2. **配置**
   - zip を解凍し、`bin` フォルダの中にある `ffmpeg.exe` と `ffprobe.exe` を、`Mp4Merger.exe` と同じフォルダ（または `%LOCALAPPDATA%\Mp4Merger\`）にコピーします。

## 2. Real-ESRGAN (AIアップスケール用)
AIアップスケール機能を使う場合のみ必要です。

1. **ダウンロード**
   - サイト: [https://github.com/xinntao/Real-ESRGAN/releases](https://github.com/xinntao/Real-ESRGAN/releases)
   - `realesrgan-ncnn-vulkan-20220424-windows.zip` をダウンロードします。
2. **配置**
   - zip を解凍し、中にある以下のファイルすべてを `Mp4Merger.exe` と同じフォルダ（または `%LOCALAPPDATA%\Mp4Merger\`）にコピーします。
     - `realesrgan-ncnn-vulkan.exe`
     - `vcomp140.dll` 等の DLL ファイル
     - `models` フォルダ（これがないと動作しません）

## 最終的な配置確認
以下のようになっていれば準備完了です。

```text
%LOCALAPPDATA%\Mp4Merger\  (または Mp4Merger.exe と同じフォルダ)
  ├── ffmpeg.exe
  ├── ffprobe.exe
  ├── realesrgan-ncnn-vulkan.exe  (AI機能を使う場合)
  └── models/                     (AI機能を使う場合)
```
