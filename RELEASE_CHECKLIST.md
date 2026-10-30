# 公開・リリース時チェックリスト

このファイルは公開運用時の確認事項をまとめたものです。

## ビルドして配る前に

- [ ] `ReleaseBuild` を作り直す（フォルダを削除してから `build_exe.bat`）。
      作り直さずに中身を上書きすると、古いファイルやテスト素材が混ざったまま残ります。
- [ ] **空のフォルダに `Mp4Merger.exe` だけをコピーして、起動することを確認する。**
      インストーラーが配置するのは実質この1ファイルだけです。単一ファイル公開は
      WPFのネイティブDLLを実行ファイルの外に置くことがあり、その場合はウィンドウ生成時に
      `DllNotFoundException` で即落ちします（`Mp4Merger.csproj` の
      `IncludeNativeLibrariesForSelfExtract` がこれを防いでいます）。
- [ ] バージョン番号を `setup.iss`（AppVersion）と `Core/Localizer.cs`（Version表記）で一致させる

## GitHub Releases に成果物を上げるときの注意

- [ ] アップロードするのは **`setup.iss` で作成したインストーラー（Mp4Merger_Setup.exe）** または
      **`Mp4Merger.exe` 単体** のみにする
- [ ] **`ReleaseBuild` フォルダをそのままzipして配布しない**
  - `ffmpeg.exe` / `ffprobe.exe` を同梱して頒布すると **GPLのソース提供義務** が発生します
  - 動作確認に使った動画・画像（他人の著作物を含むことがあります）や `.pdb` が混入します
  - テスト素材はリポジトリの外（`../_testmedia/`）に置いてあります

## 済み（初回のみの作業）

- [x] Settings → Code security → **Private vulnerability reporting** を有効化
- [x] **Secret scanning** と Push protection を有効化
- [x] 旧履歴を指していたリモートタグ `v0.9` を削除
- [ ] ローカルの退避ブランチ `private-history-backup` は **絶対にpushしない**
      （個人パスを含む旧履歴が再公開されてしまいます）
