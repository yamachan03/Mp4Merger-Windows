# MP4 Merger (for Windows)

**"Combine multiple videos into one with consistent volume and resolution, powered by AI."**

MP4 Merger is a simple and intuitive native Windows application designed to merge video files seamlessly. Without complex editing software, you can align multiple clips into a single file with unified audio levels, your choice of output resolution, and even enhance them using state-of-the-art AI upscaling.

---

## 📥 Download

[**Download Mp4Merger-win-x64.zip**](https://github.com/yamachan03/Mp4Merger-Windows/releases/latest) — one self-contained exe, no .NET runtime to install.

The exe is not code-signed, so Windows SmartScreen warns on first launch; choose **More info → Run anyway** if you trust the source. FFmpeg and the AI upscaler are downloaded on first launch, not bundled.

---

## ✨ Key Features

* **Normalize Audio (Volume Balancing):** When merging multiple clips, it analyzes and adjusts varying audio levels to a consistent, comfortable volume throughout the entire output video.
* **Resolution Scaling & AI Upscaling:** Set your output size to **1080p FHD** or **4K UHD**. Additionally, you can utilize **Real-ESRGAN AI** to upscale your videos (including Anime-optimized models) for unprecedented clarity.
* **Fix Jitter:** Reduces playback stutter and stabilizes video jitter for a smoother viewing experience.
* **Native Windows Integration:**
    * **Hardware Acceleration Support:** Fully supports NVIDIA NVENC, Intel QSV, and AMD AMF encoders for lightning-fast exports.
    * **Zero Configuration:** Automatically downloads all required dependencies (FFmpeg & AI models) on first launch. No complicated paths or manual installations needed!
    * **Modern Dark Theme:** A sleek and simple UI built natively with WPF.

---

## 🚀 How to Use

1. **Add Videos:** Drag and drop your video files into the central area. Use **[Sort by Name]** to organize them or **[Clear All]** to reset the list.
2. **Configure Options:** In the **Options** panel (bottom-right):
   * Select your **Encoder** (Auto detects your GPU, or force CPU/NVIDIA/Intel/AMD).
   * Check **Normalize Audio** to balance sound, or **Fix Jitter** to stabilize frame sync.
   * Choose your desired **Resolution** (Standard or AI-Enhanced).
3. **Merge:** Enter a name in the **Output Filename** field and click the blue **[Merge Files]** button.

---

## 🛠️ Requirements

**None!** This app is fully self-contained. 
When you launch the app for the first time, it will automatically check for and download the required **FFmpeg** engine and **Real-ESRGAN** AI components. It also features an auto-update checker on startup to ensure your tools are always up to date.

*OS Requirement: Windows 10 or Windows 11 (64-bit)*

---

## 🌐 Network Access & Privacy

On startup, this app connects to `api.github.com` / `github.com` **only** to check for and download FFmpeg and Real-ESRGAN from their official GitHub releases. Downloaded archives are verified against the SHA-256 digest published by the GitHub Releases API when available. Your video files and personal data are never uploaded anywhere — all processing happens locally.

> **Note:** The executable is not code-signed, so Windows SmartScreen may show a warning on first launch. Click "More info" → "Run anyway" if you trust the source.

---

## 📄 License

This project (the MP4 Merger application source code) is licensed under the **MIT License**. See the [LICENSE](LICENSE) file.

### Third-Party Components
This application does **not** bundle FFmpeg or Real-ESRGAN. On first launch, it downloads them from their official GitHub releases and runs them as separate external processes:

* **[FFmpeg](https://ffmpeg.org)** — downloaded from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (a **GPL** build, which includes x264/x265). FFmpeg itself is distributed under the terms of the GPL; see the FFmpeg project for details. FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.
* **[Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)** (realesrgan-ncnn-vulkan and its models) — licensed under the **BSD 3-Clause License**.

The MIT license above applies only to this repository's own code, not to these downloaded components.

---
---

# MP4 Merger (for Windows) - 日本語版

**「複数の動画を、一定の音量と指定の解像度でひとつに結合。AI高画質化にも対応」**

MP4 Mergerは、Windowsユーザーのために新しく設計された直感的な動画結合ツールです。複雑な編集ソフトを使わずに、バラバラの動画ファイルを、音量バランスの整った高画質な一本の動画へとまとめあげます。

---

## 📥 ダウンロード

[**Mp4Merger-win-x64.zip をダウンロード**](https://github.com/yamachan03/Mp4Merger-Windows/releases/latest) — 自己完結の実行ファイル1つで、.NETランタイムのインストールは不要です。

コード署名をしていないため初回起動時にWindows SmartScreenの警告が出ます。配布元を信頼できる場合は「**詳細情報 → 実行**」を選んでください。FFmpegとAI超解像コンポーネントは同梱しておらず、初回起動時にダウンロードします。

---

## ✨ 主な機能

* **音量の自動均一化 (Normalize Audio):** 複数の動画を結合する際、ファイルごとにバラバラな音量を解析し、全体を一定の聞き取りやすいレベルに自動調整します。
* **解像度の指定＆AI超解像度 (Resolution & AI Upscale):** 出力サイズを **1080p FHD** や **4K UHD** に指定できるだけでなく、最新のAI技術（Real-ESRGAN）を用いて、実写やアニメを高画質のままアップスケールできます。
* **映像の安定化補正 (Fix Jitter):** 動画特有の微細なカクつき（音ズレやジッター）を抑え、スムーズな再生を実現します。
* **Windows専用設計:**
    * **ハードウェアエンコード対応:** NVIDIA NVENC, Intel QSV, AMD AMFなど、お使いのグラフィックボードを自動判別し、超高速で書き出しを行います。
    * **完全自動セットアップ:** 使うための面倒な初期設定は一切不要！初回起動時に自動で専用サーバーから「FFmpeg」や「AIモデル」をダウンロードして配置します。アップデートチェックも全自動です。
    * 最新のダークモードUI環境を採用しています。

---

## 🚀 使い方

1. **動画を読み込む:** 中央のエリアに動画をドラッグ＆ドロップします。 **[Sort by Name]** で名前順に整列させたり、**[Clear All]** でリストをリセットできます。
2. **オプションを選択:** 右下の **Options** パネルで設定を行います。
   * **Encoder:** 使うグラフィックボードを選択します（通常はAutoのままでOKです）。
   * 音量を揃えるなら **Normalize Audio**、動きのガタつきを抑えるなら **Fix Jitter** にチェックを入れます。
   * 希望の出力サイズやAI補正を **Resolution** から選びます。
3. **実行:** **Output Filename** にファイル名を入力し、青い **[Merge Files]** ボタンを押せば結合が始まります。

---

## 🛠️ 事前準備

**一切不要です！**
本アプリを利用するための面倒な事前準備（FFmpegのインストールや環境変数の設定等）は一切必要ありません。
アプリの初回起動時に、画面の指示に従って「はい」を押すだけで、必要な映像処理エンジンとAIコンポーネントが自動的にダウンロードされ、次回以降も自動で最新版を確認します。

*対応OS: Windows 10 または Windows 11 (64ビット)*

---

## 🌐 通信とプライバシー

本アプリは起動時に `api.github.com` / `github.com` へ接続しますが、これはFFmpegとReal-ESRGANの公式GitHubリリースの更新確認とダウンロードのため**だけ**です。取得可能な場合、ダウンロードしたファイルはGitHub Releases APIが公開するSHA-256ダイジェストと照合して検証されます。動画ファイルや個人情報が外部へ送信されることは一切なく、すべての処理はローカルで実行されます。

> **注意:** 実行ファイルはコード署名されていないため、初回起動時にWindows SmartScreenの警告が表示される場合があります。配布元を信頼できる場合は「詳細情報」→「実行」を選択してください。

---

## 📄 ライセンス

このプロジェクト（MP4 Mergerアプリ本体のソースコード）は **MITライセンス** のもとで公開されています。詳細は [LICENSE](LICENSE) ファイルを参照してください。

### サードパーティコンポーネント
本アプリはFFmpegおよびReal-ESRGANを**同梱していません**。初回起動時に各公式GitHubリリースからダウンロードし、外部プロセスとして実行します。

* **[FFmpeg](https://ffmpeg.org)** — [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) の **GPL版ビルド**（x264/x265を含む）を取得します。FFmpeg自体はGPLの条件で配布されています。FFmpegは、FFmpegプロジェクトの創設者であるFabrice Bellard氏の商標です。
* **[Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)**（realesrgan-ncnn-vulkanおよびモデル）— **BSD 3-Clauseライセンス** で配布されています。

上記MITライセンスは本リポジトリのコードにのみ適用され、これらのダウンロードされるコンポーネントには適用されません。
