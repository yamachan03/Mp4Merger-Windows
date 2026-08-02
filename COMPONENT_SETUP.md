# 外部ツールのセットアップ手順

このアプリを実行するには、以下の2つのツールが必要です。
これらをダウンロードして、プロジェクトフォルダ（`Mp4Merger.csproj` がある場所）に「exeファイル」を置いてください。

## 1. FFmpeg (動画処理用)
必須です。

1. **ダウンロード**
   - サイト: [https://www.gyan.dev/ffmpeg/builds/](https://www.gyan.dev/ffmpeg/builds/)
   - "git-essentials" または "release-essentials" のリンクから `.zip` をダウンロードします。
2. **配置**
   - zipを解凍し、`bin` フォルダの中にある `ffmpeg.exe` を探します。
   - `ffmpeg.exe` をプロジェクトフォルダ（`Mp4Merger.csproj` がある場所）にコピーします。

## 2. Real-ESRGAN (AIアップスケール用)
AIアップスケール機能を使う場合のみ必要です。

1. **ダウンロード**
   - サイト: [https://github.com/xinntao/Real-ESRGAN/releases](https://github.com/xinntao/Real-ESRGAN/releases)
   - 最新の `realesrgan-ncnn-vulkan-xxxx-windows.zip` をダウンロードします。
2. **配置**
   - zipを解凍し、中にある以下のファイルすべてを `Mp4Merger` フォルダに解凍（またはコピー）します。
     - `realesrgan-ncnn-vulkan.exe`
     - `vcomp140.dll` 等のDLLファイル
     - `models` フォルダ（これがないと動作しません）

## 最終的な配置確認
以下のようになっていれば準備完了です。

```text
Mp4Merger/
  ├── Mp4Merger.csproj
  ├── MainWindow.xaml
  ├── ...
  ├── ffmpeg.exe                 <-- 追加
  ├── realesrgan-ncnn-vulkan.exe <-- 追加
  └── models/                    <-- 追加 (Real-ESRGAN用)
```
