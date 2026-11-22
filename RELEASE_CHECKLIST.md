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
- [ ] リリース番号は `Mp4Merger.csproj` の `<Version>` だけを変える。
      画面表示はアセンブリから、`setup.iss` はビルドしたexeから読むので、他に書く場所はありません。

## zip を作る

```powershell
.uild_exe.bat      # 先に ReleaseBuild を作り直す
.\make_zip.ps1       # dist\Mp4Merger-<版>-win-x64.zip ができる
```

`make_zip.ps1` が守ってくれること。

- **exe ひとつだけ**を名指しで入れる（`ReleaseBuild` を丸ごと固めない）
- 入れなかったものを**名前を出して報告**する（`.pdb` や、うっかり置いた `ffmpeg.exe` など）
- できた zip の中身を**開いて確かめる**。ffmpeg・ffprobe・realesrgan・`.pdb`・動画が
  混ざっていたら中止する
- 版番号は **exe から読む**（`Mp4Merger.csproj` の `<Version>` が唯一の出どころ）
- **exe よりソースが新しければ中止**する（ビルドし忘れて古いものを配る事故を防ぐ）
- SHA-256 を出す（公開時に添えられる）

**起動の確認だけは自動にできない。**空のフォルダに exe だけを置いて開くこと。

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
