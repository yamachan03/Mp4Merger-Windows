# 公開・リリース時チェックリスト

このファイルは公開運用時の確認事項をまとめたものです。

## GitHubリポジトリ設定（初回のみ）

- [ ] Settings → Code security and analysis → **Private vulnerability reporting** を有効化
      （SECURITY.md に記載した非公開報告窓口が機能するようになります）
- [ ] 同ページで **Secret scanning**（および Push protection）を有効化

## GitHub Releases に成果物を上げるときの注意

- [ ] アップロードするのは **`setup.iss` で作成したインストーラー（Mp4Merger_Setup.exe）** または
      **`Mp4Merger.exe` 単体** のみにする
- [ ] **`ReleaseBuild` フォルダをそのままzipして配布しない**
  - `ffmpeg.exe` / `ffprobe.exe` を同梱して頒布すると **GPLのソース提供義務** が発生します
  - テスト用動画・画像（著作物）や `config.json`、`.pdb` などの開発ファイルが混入しています
- [ ] バージョン番号を `setup.iss`（AppVersion）と `Core/Localizer.cs`（Version表記）で一致させる

## 履歴squash後の後始末（該当する場合のみ）

- [ ] 旧履歴を指しているリモートタグを削除する: `git push origin --delete v0.9`
- [ ] ローカルの退避ブランチ `private-history-backup` は **絶対にpushしない**
      （個人パスを含む旧履歴が再公開されてしまいます）
