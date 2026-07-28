# Rokid Control for Windows

Rokid AI Glasses RV101の画面をWindowsへ表示し、Windowsのマウスとキーボードで操作するアプリです。

このリポジトリは、[Rokid Control for macOS](https://github.com/ksuzukigh/rokid-mac-control) 1.2.0と同等の利用者向け仕様をWindows 11へ移植するプロジェクトです。

## 現在の状態

開発中です。現時点では一般利用向けのリリースはありません。

- Windows版の正式仕様を作成済み
- ADB接続、Rokid判定、背景なし表示の起動処理を実装済み
- Windows専用のキーボード入力制御を実装済み
- Windows Graphics Captureによるカメラ・HUD取得とライブ合成を実装済み
- ライブ映像の自動再接続、選択リング、見やすさ設定の保存を実装済み
- Rokid実機でUSB・Wi-Fi接続、15fpsで5分間の2画面取得、入力隔離、終了処理を確認済み
- GitHub ReleasesのポータブルZIPへ信頼されたコード署名を行う方針

詳しくは[仕様書](docs/SPECIFICATION.md)と[技術試験計画](docs/TECHNICAL-TEST-PLAN.md)を参照してください。

本アプリは画面や操作内容をクラウドへ送信しません。詳細は[プライバシー方針](PRIVACY.md)を参照してください。

## 開発環境

- Windows 11 24H2以降
- .NET 10 SDK
- x64
- 公式scrcpy 4.1 Windows x64版

## ビルド

```powershell
.\tools\verify.ps1
```

OneDrive上の並列ビルドでファイル競合が起きる場合があるため、検証スクリプトは直列ビルドを使用します。

公式scrcpy 4.1 Windows x64版は次のコマンドでダウンロードし、公開SHA-256と照合して配置します。

```powershell
.\tools\prepare-vendor.ps1
```

自己完結型のWindows x64配布フォルダーは次のコマンドで作成します。

```powershell
.\tools\publish.ps1
```

出力先は`artifacts/publish/win-x64`です。

バージョン付きの配布ZIPとSHA-256チェックサムは次のコマンドで作成します。

```powershell
.\tools\package.ps1 -Version 0.1.0-alpha
```

出力先は`artifacts/package`です。一般公開版はコード署名サービスの承認後、署名済みファイルから同じ形式のZIPを作成します。

## セルフテスト

```powershell
dotnet run --project tests/RokidControl.SelfTest
```

## ライセンス

本アプリのソースコードはApache License 2.0です。配布物へ同梱するscrcpy、Android Platform Tools、FFmpeg、SDLなどのライセンスは、各公式配布物の表示をそのまま収録します。
