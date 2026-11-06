# Hakobune 開発メモ（Claude 向け）

- 計画と決定事項は `docs/plan.md`。方針を変えたらそこも更新する。
- ユーザーとのやり取りは日本語。画面の文言も日本語。

## 構成

- `src/Hakobune.Core`: 通信・転送の仕組み（net10.0、画面に依存しない）
- `src/Hakobune.App`: WinUI 3 アプリ（Windows 専用。MSIX にせず、Windows App SDK を同梱）
- `tests/Hakobune.Core.Tests`: Core のテスト（xUnit v3。`dotnet run --project tests/Hakobune.Core.Tests` で実行できる）
- パッケージのバージョンは `Directory.Packages.props` で一元管理する。

## ビルドと確認

- Claude の作業環境からは nuget.org などに接続できず、手元でビルドできない。変更は push し、GitHub Actions（`.github/workflows/ci.yml`）の結果で確かめる。
  - `gh run list --limit 5`、`gh run view <id> --log-failed`
- アプリのビルド結果（zip）は Actions の成果物 `Hakobune-win-x64` からダウンロードできる。

## 守ること

- 画面用の接続と転送用の接続は必ず分ける。1 本の接続を同時に複数の場所から使わない。
- 一覧の取得に失敗しても、表示中の一覧は消さない。
- 一時的な失敗はその場でやり直す（`RetryPolicy`）。待ち行列の最後には回さない。直らない失敗は理由と一緒に「失敗」にする。
