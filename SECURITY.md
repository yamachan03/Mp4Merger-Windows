# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in MP4 Merger, please report it privately via
**GitHub's "Report a vulnerability" feature** (Security tab → Advisories) on this repository.
Please do not open a public issue for security problems.

You can expect an initial response within a reasonable time frame. Once the issue is
confirmed and fixed, the fix will be released and the report credited (if desired).

## Scope Notes

- MP4 Merger downloads FFmpeg and Real-ESRGAN binaries from their official GitHub
  releases over HTTPS at first launch. Downloaded archives are verified against the
  SHA-256 digest published by the GitHub Releases API when available.
- The app runs these tools as separate local processes. It does not transmit your
  video files or any personal data to external servers.

---

# セキュリティポリシー（日本語）

## 脆弱性の報告

本アプリの脆弱性を発見した場合は、公開Issueではなく、このリポジトリの
**GitHubの脆弱性報告機能**（Securityタブ → Advisories →「Report a vulnerability」）から
非公開で報告してください。

## 補足

- 本アプリは初回起動時に、FFmpegおよびReal-ESRGANの公式GitHubリリースから
  HTTPS経由でバイナリをダウンロードします。取得可能な場合、GitHub Releases APIが
  公開するSHA-256ダイジェストと照合して完全性を検証します。
- これらのツールはローカルの別プロセスとして実行されます。動画ファイルや個人情報が
  外部サーバーへ送信されることはありません。
