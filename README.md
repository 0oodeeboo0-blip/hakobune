# Hakobune

FTP / FTPS / SFTP に対応した Windows 用のファイル転送ソフトです（開発中）。

FFFTP の使い慣れた左右 2 画面はそのままに、次の 3 つを目指しています。

- **確実に届く**: 一覧が消えない、黙って後回しにしない、止まっても続きから再開できる
- **今どきの見た目**: Windows 11 のデザイン、ダークモード、高解像度の画面でもくっきり
- **乗り換えやすい**: FFFTP の接続先の設定を読み込める

## 状況

開発を始めたところで、まだ使える版はありません。計画は [docs/plan.md](docs/plan.md) にあります。

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
