# わざと固まる ffmpeg の代役

番犬（`Core\StallWatch.cs` と `RunFFmpegWithProgressAsync`）を**通しで**確かめるための
ffmpeg の代役。単体テストは `StallWatch` の数え方しか見ないので、
「実際に打ち切られてプロセスが片付くか」はこれで確かめる。

## 使い方

```powershell
dotnet publish Tests\StallStub -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o <どこか>\out
# できた stub.exe を ffmpeg.exe という名前で、確認用 exe の隣に置く
# （AppPaths.ResolveTool は exe の隣を先に見るので、それが使われる）
```

`STUB_MODE` で動きを選ぶ。

| 値 | 動き | 期待 |
|---|---|---|
| `stall`（既定） | 5 秒の位置まで進んでから、**同じ位置を出し続けて固まる** | 約 190 秒で打ち切られ、「動画の 00:00:05 の位置から」と出る |
| `advance` | 位置を進めて 10 秒で終わる | 打ち切られない |

**`stall` のほうは、打ち切られるまで 3 分待つ**（`FfmpegStallLimit`）。
だから `dotnet test` には入れていない。番犬を触ったときだけ手で流す。

2026-11-22 にこれで確かめた結果：動いている処理は 11 秒で完走（進捗 20 回）、
固まった処理は 190 秒で打ち切り、止まった位置を名指し、固まったプロセスは残らなかった。
