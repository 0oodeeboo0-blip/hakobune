# Hakobune 開発メモ（Claude 向け）

- 計画と決定事項は `docs/plan.md`。方針を変えたらそこも更新する。
- ユーザーとのやり取りは日本語。画面の文言も日本語。

## 構成

- `src/Hakobune.Core`: 通信・転送の仕組み（net10.0、画面に依存しない）。失敗の理由などの日本語の文言も `Text/DisplayText.cs` に置く。
- `src/Hakobune.Presentation`: 画面の動き（ViewModel。net10.0、WinUI に依存しないので Linux でテストできる）。見本のデータ（`Demo/`）もここ。
- `src/Hakobune.App`: WinUI 3 アプリ（Windows 専用。MSIX にせず、Windows App SDK を同梱）。画面（XAML）と Windows に固有の部分だけ。
- `tests/Hakobune.Core.Tests`、`tests/Hakobune.Presentation.Tests`: テスト（xUnit v3。`dotnet run --project <テストのプロジェクト>` で実行できる）
- パッケージのバージョンは `Directory.Packages.props` で一元管理する。
- ViewModel のプロパティは C# 14 の `field` と `SetProperty` で書く（CommunityToolkit.Mvvm のソース生成は使わない）。

## ビルドと確認

- Claude の作業環境からは nuget.org などに接続できず、手元でビルドできない。変更は push し、GitHub Actions（`.github/workflows/ci.yml`）の結果で確かめる。
  - `gh run list --limit 5`、`gh run view <id> --log-failed`、`gh run view <id> --log`
  - CI のジョブ: エンジンのテスト（Linux）、実サーバーでのテスト（Linux）、アプリのビルド（Windows。起動テストと画面写真つき）
- Actions の成果物（artifact）は作業環境からはダウンロードできない（保存先がブロックされる）。
  画面写真は CI が `screenshots` ブランチに置くので、git で取る:
  `git fetch --depth 1 origin +refs/heads/screenshots:refs/remotes/origin/screenshots && git show origin/screenshots:startup.png > /tmp/startup.png`
  - `startup.png`: ふつうに起動した画面（画面全体）。`main-light.png`、`main-dark-stale.png`、`main-light-stale.png`: 見本のデータの画面。
    `main-log-dark.png`: 見本のデータで、下の「ログ」を出した画面。
  - `startup-saved-settings.png`: 保存した設定（ダーク、コンパクト、接続先の一覧を閉じる、ウインドウの位置）で起動した画面（画面全体）。
    CI はそのあとふつうに閉じて、設定が書き戻されたかも確かめる。
  - `dialog-*.png`: 見本のダイアログ（新しい接続、編集、クイック接続、パスワード、パスフレーズ、サーバーの鍵、証明書、
    名前の変更、ファイルの削除、属性、同じ名前があるとき、転送中に閉じるとき、設定）。
  - `connected.png`: Windows の上の FTP サーバー（pyftpdlib）に `--connect` で実際に接続し、`--upload` でフォルダを送り終えた画面。
  - アプリが画面写真を撮れなかったときは、同じ名前の `.error.txt` もブランチに置かれる。
- 見本の画面は `Hakobune.exe --demo [--scenario stale] [--theme light|dark] [--log] [--screenshot ファイル名] [--size 1280x800]` で出せる。
  `--log` で、下の一覧の「ログ」を出しておく。
  `--screenshot` を付けると、アプリ自身が画面写真（RenderTargetBitmap）を保存して終わる。
  `--dialog new|edit|quick|password|passphrase|hostkey|hostkey-changed|certificate|delete|rename|delete-files|permissions|conflict|stop|settings`
  で見本のダイアログを出す（写真は画面から撮る）。
  `--connect URL` で起動してすぐクイック接続する（`--screenshot` と一緒なら、接続できなかったとき `.error.txt` を書く）。
  `--upload パス` を足すと、接続したあとそのファイルかフォルダを送り、終わるまで待つ（送れなかったら `.error.txt`）。
- アプリのビルド結果（zip）は、利用者が Actions の成果物 `Hakobune-win-x64` からダウンロードする。
- ライブラリの API は、公開リポジトリを `git clone --depth 1` して読む（GitHub には git で接続できる）。例: FluentFTP、SSH.NET。

## 実サーバーでのテスト

- `tests/servers/` に、vsftpd・Pure-FTPd・ProFTPD・OpenSSH の Docker 定義がある。`start.sh` が全部をホストのネットワークで起動する。
  - コンテナは `--init` で動かす（vsftpd は PID 1 だと落ちる）。Pure-FTPd は `--privileged` が要る。
- テストは環境変数 `HAKOBUNE_TEST_<名前>`（例: `ftp://hakobune:hakobune-pass@127.0.0.1:2121`）があるときだけ動き、無ければ飛ばす。
- `FlakyTcpProxy` で接続をわざと切り、転送が最後まで届くことを確かめている。

## 注意

- csproj などの XML のコメントに「--」を書かない（読み込みエラーになる）。XAML のコメントも同じ。
- XAML のパスのデータ（`Path.Data`）は、絶対座標の M・L・C・A・Z だけで書く（SVG の省略した書き方は読めないことがある）。

## 守ること

- 画面用の接続と転送用の接続は必ず分ける。1 本の接続を同時に複数の場所から使わない。
- 一覧の取得に失敗しても、表示中の一覧は消さない。
- 一時的な失敗はその場でやり直す（`RetryPolicy`）。待ち行列の最後には回さない。直らない失敗は理由と一緒に「失敗」にする。
