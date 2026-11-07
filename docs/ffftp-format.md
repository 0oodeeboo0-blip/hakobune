# FFFTP の設定の形（読み込みのために調べたこと）

FFFTP 5.8（master c0347d1）のコードで確かめた。読み込みは `src/Hakobune.Core/Import/`（`FfftpSettings`・`FfftpHosts`・`FfftpPasswords`）。

## 置き場所

- レジストリ `HKEY_CURRENT_USER\Software\Sota\FFFTP`。文字の値は UTF-16、名前は ASCII。
  - `FFFTP` の直下: `Version`（設定の版。5.7 以降は 5700）、マスターパスワードを確かめる値（`CredentialSalt` と `CredentialCheck`）、`EncryptAll`。
  - `FFFTP\Options`: 全体の設定。接続先は `Host0`、`Host1`… のキーで、数は `SetNum`。`DefaultHost` は「共通の設定」。
  - FFFTP は「共通の設定」と同じ値を書かないので、接続先に値が無ければ `DefaultHost` を見て、それも無ければ既定の値にする。
- ffftp.ini（FFFTP の exe の隣。ポータブルで使っているとき）: 節の名前はキーの道筋（`[FFFTP\Options\Host0]`）。
  値は ASCII に直してある（0x20〜0x7E はそのまま、`\` は `\\`、ほかは `\XX`）。元のバイト列は UTF-8（`Version` が 1980 より前なら Shift_JIS）。
- 「設定をファイルに保存」: レジストリで使っていれば `reg.exe export` の .reg（UTF-16）、ini なら ini をそのまま写したもの。

## 接続先（HostN）

- `Set`: 0x8000 ならグループ（フォルダ）。下の 15 ビットは入れ子の深さ。一覧は上から順（深さ優先）に並ぶ。グループは `Set` と `HostName` だけ。
- `HostName`（名前）、`HostAdrs`（アドレス）、`Port`（既定 21）、`UserName`、`Password`、`Anonymous`、`LocalDir`、`RemoteDir`、`Pasv`（既定 1）。
- 暗号化は 3 つの許可: `NoEncryption`（暗号化なし）、`FTPES`（明示的）、`FTPIS`（暗黙的）。どれも既定は 1。FFFTP は暗黙的 → 明示的 → なしの順に試す。
  `SFTP` という値もあるが、FFFTP は SFTP をつないでいない。`Secu` は S/KEY（ワンタイムパスワード）で暗号化とは関係ない。
- `NameKanji`（ファイル名の文字コード）: -1 自動、0 Shift_JIS、1 JIS、2 EUC、3・4 Samba、5〜7 UTF-8。`ThreadCount`（同時接続数）。

## パスワード

- マスターパスワード（決めていなければ `DefaultPassword`）の UTF-8（128 バイトまで）から鍵を作る。
- 保存された文字の先頭で形が分かる: `0C` が AES（5.x はいつもこれで書く）、`0B`・`0A`・印なし（先頭が 0x40〜0x7F）は古い形。
- `0C`: 32 文字の 16 進が初期化ベクトル、`:` のあとが暗号文（16 進）。AES-256-CBC、詰め物なし。平文は NUL で終わる。
  鍵は SHA-1(鍵 + `` >g^r=@N7=//z<[`: ``) と SHA-1(鍵 + `VG77dO1#EyC]$|C@`) の 32 ビットの語（ビッグエンディアンで読む）を、
  リトルエンディアンで 8 語並べたもの。
- マスターパスワードを確かめる値 `CredentialCheck`: SHA-1(マスターパスワード + 0x00 + `CredentialSalt` のビッグエンディアン 4 バイト) の
  20 バイトを、4 ビットずつ「@」〜「O」の 40 文字にしたもの。
- 「設定をすべて暗号化して保存」（`EncryptAll`、キーが `EncryptedOptions` になる）は、値ごとに鍵で隠してあるので読まない。
