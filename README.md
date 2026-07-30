# Rokid Control for Windows

Rokid AI Glasses RV101の画面をWindowsへ表示し、Windowsのマウスとキーボードで操作するアプリです。

[Rokid Control for macOS](https://github.com/ksuzukigh/rokid-mac-control)を元に、Windows 11向けとして実装しました。

## できること

- Rokidの画面をWindowsへ表示
- マウスとキーボードによるRokid操作
- カメラ映像へRokidの文字やアイコンを重ねる「ライブ映像」
- カメラを使わない「背景なし（省電力）」
- USBと暗号化Wi-Fiの両方で接続
- Wi-Fi切断、カメラ競合、画面受信停止からの自動復帰
- 他のWindowsアプリを操作している間はRokidへキーを送らない入力隔離

Rokidの画面とカメラ映像はPC内だけで処理し、クラウドへ送信しません。

## 現在の公開状態

Windows版の実装、実機確認、仕様の見直しは完了しています。ソースコードはApache License 2.0で公開しています。

一般利用者向けの配布ZIPには、Smart App Controlが有効なWindows 11でも信頼されるコード署名を付ける予定です。署名済み配布版の準備が完了するまでは、Windowsの保護機能を無効にして未署名版を実行するよう案内しません。

SignPath Foundationへの申請では、完成版と同じ配布形式を示すため、[GitHub Releases](https://github.com/ksuzukigh/rokid-windows-control/releases)に未署名プレリリースを掲載します。これは署名審査用であり、一般利用者向けの配布版ではありません。

## Code signing policy

一般公開版は、GitHub Actionsで`main`の公開ソースから生成した自作の実行ファイルとDLLだけを署名対象にします。署名要求はリリースごとに手動承認し、秘密鍵はリポジトリや開発PCへ保存しません。

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- Committers and reviewers: [ksuzukigh](https://github.com/ksuzukigh)
- Approvers: [ksuzukigh](https://github.com/ksuzukigh)
- Privacy policy: [PRIVACY.md](PRIVACY.md)
- Detailed policy: [Code signing and distribution](docs/CODE-SIGNING.md)

## 対応環境

- Rokid AI Glasses RV101
- Windows 11 24H2以降
- x64 PC
- Rokidの開発用5ピンケーブル（初回準備用）
- Windows PCとRokidを接続するWi-Fi
- スマートフォンのRokidアプリで開発者モード（ADB）を有効にしていること

RV101付属の3ピンケーブルは充電用です。Windowsとの初回接続には開発用5ピンケーブルが必要です。

## 初めて接続する

1. スマートフォンのRokidアプリで、Rokidの開発者モード（ADB）を有効にします。
2. Windows PCとRokidを同じWi-Fiへ接続します。
3. Rokidを開発用5ピンケーブルでWindows PCへつなぎます。
4. `Rokid Control.exe`を開きます。
5. RokidにUSB接続の確認が表示された場合は許可します。
6. 「ライブ映像」または「背景なし（省電力）」を選びます。
7. Rokidの画面がWindowsへ表示されたら、5ピンケーブルを外せます。

初回USB認証後、Rokid ControlはAndroid標準のTLSで暗号化された無線接続を準備します。接続先にはADBが通知する動的なポートを使用し、旧方式の固定5555番ポートは使用しません。

## 普段の使い方

1. Windows PCとRokidを同じWi-Fiへ接続します。
2. `Rokid Control.exe`を開きます。
3. 表示方法を選びます。
4. 表示されたRokid画面を一度クリックしてから操作します。

初回設定後は、普段の接続に5ピンケーブルは必要ありません。

Rokidの再起動や省電力動作でWi-Fiが切れた場合は、Rokidで[Wi-Fi ON](https://github.com/ksuzukigh/rokid-wifi-on)を開いてからRokid Controlを起動します。Wi-Fi ONを使用していない場合は、もう一度5ピンケーブルを接続すれば安全な無線接続を再準備できます。

## キーボード操作

| キー | 動作 |
| --- | --- |
| `M` | メモを開く |
| `H` | Homeを開く |
| `A` | アプリ一覧を開き、アプリ選択を開始 |
| `←` / `→` | `A`を押した後にアプリを選択 |
| `Enter` | 選択中のアプリを開く |
| `Esc` | 一つ前へ戻る |
| `Ctrl` + `Q` | Rokid Controlを終了 |
| `Alt` + `F4` | Rokid Controlを終了 |

通常時の矢印キーとEnterはRokidへ送りません。`A`でアプリ一覧を開いた後だけ使用します。

## 接続できないとき

1. Windows PCとRokidが同じWi-Fiにつながっているか確認します。
2. Rokidで「Wi-Fi ON」を開き、Wi-Fi接続が完了するまで待ちます。
3. Rokid Controlを終了して開き直します。
4. 改善しない場合は、開発用5ピンケーブルを接続します。
5. RokidにUSB接続の確認が表示された場合は許可します。

Rokid以外のAndroid端末がUSB接続されている場合は、誤操作を避けるため外してください。

## プライバシーと安全性

- 映像、キーボード入力、マウス操作をクラウドへ送信しません。
- アクセス解析、広告、クラウド同期、自動更新確認を含みません。
- 接続後にRokidの機種またはメーカーを確認し、Rokid以外の機器を拒否します。
- 無線ADBには、初回USB認証で登録されたPCだけが接続できるTLS方式を使用します。
- 公共Wi-Fiでの使用は推奨しません。自宅など信頼できるWi-Fiで使用してください。

保存情報と削除方法は[プライバシー方針](PRIVACY.md)を参照してください。

## 開発者向け

### 必要なもの

- .NET 10 SDK
- 公式scrcpy 4.1 Windows x64版

### 検証

```powershell
.\tools\verify.ps1
```

### 依存ファイルの準備

```powershell
.\tools\prepare-vendor.ps1
```

### 自己完結型Windows x64フォルダーの作成

```powershell
.\tools\publish.ps1 -Version 0.1.0-alpha.2
```

出力先は`artifacts/publish/win-x64`です。

### ZIPとSHA-256の作成

```powershell
.\tools\package.ps1 -Version 0.1.0-alpha.2
```

出力先は`artifacts/package`です。このコマンドはローカルの未署名候補を作成し、自動公開は行いません。

詳しい動作は[仕様書](docs/SPECIFICATION.md)、検証結果は[技術試験計画](docs/TECHNICAL-TEST-PLAN.md)、署名方針は[コード署名と配布](docs/CODE-SIGNING.md)を参照してください。

## ライセンス

Rokid Control for Windowsのソースコードは[Apache License 2.0](LICENSE)です。

配布物に含まれるscrcpy、Android Platform Tools、FFmpeg、SDLなどのライセンスと通知は、公式配布物の内容を保持して同梱します。詳細は[Third-party notices](THIRD-PARTY-NOTICES.md)を参照してください。
