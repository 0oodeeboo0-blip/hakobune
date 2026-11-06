# Hakobune

FTP / FTPS / SFTP に対応した Windows 用のファイル転送ソフトです（開発中）。

FFFTP の使い慣れた左右 2 画面はそのままに、次の 3 つを目指しています。

- **確実に届く**: 一覧が消えない、黙って後回しにしない、止まっても続きから再開できる
- **今どきの見た目**: Windows 11 のデザイン、ダークモード、高解像度の画面でもくっきり
- **乗り換えやすい**: FFFTP の接続先の設定を読み込める

## 状況

基本の画面ができたところです（試用版。まだ署名・インストーラーはありません）。計画は [docs/plan.md](docs/plan.md) にあります。

- 左右 2 画面（パソコン・サーバー）、接続先の管理、クイック接続（Ctrl+Q）
- アップロード・ダウンロード（ボタン、ドラッグ＆ドロップ、エクスプローラーから）、フォルダごと（空のフォルダも）
- 送信待ちの一覧（一時停止・取り消し・送り直し。閉じても次に続きから送れる）
- 新しいフォルダ・名前の変更・削除・属性の変更、ログ（通信の詳細つき）、ライト・ダーク

![見本の画面](https://raw.githubusercontent.com/0oodeeboo0-blip/hakobune/screenshots/main-light.png)

### 試し方

1. [Actions](https://github.com/0oodeeboo0-blip/hakobune/actions?query=branch%3Amain) で、いちばん上の緑のチェックが付いた実行を開く
2. 下の「Artifacts」にある **Hakobune-win-x64** をダウンロードする（GitHub へのログインが要ります）
3. zip を展開して `Hakobune.exe` を開く（インストールは要りません。Windows 10 1809 以降の x64）
4. 「Windows によって PC が保護されました」と出たら、「詳細情報」→「実行」（まだ署名していないため）

設定と接続先は `%APPDATA%\Hakobune` に、パスワードは Windows の資格情報マネージャーに保存します。

## ビルド

ビルドは GitHub Actions で自動的に行います。手元でビルドする場合は、Windows に .NET 10 SDK を入れてから次を実行します。

```
dotnet publish src/Hakobune.App/Hakobune.App.csproj -c Release -r win-x64 -p:Platform=x64
```

テストは次のとおりです（Windows 以外でも実行できます）。

```
dotnet run --project tests/Hakobune.Core.Tests
```

## ライセンス

[MIT License](LICENSE)
