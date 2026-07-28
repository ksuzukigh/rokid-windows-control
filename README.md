# Rokid Control for Windows

Rokid AI Glasses RV101の画面をWindowsへ表示し、Windowsのマウスとキーボードで操作するアプリです。

このリポジトリは、[Rokid Control for macOS](https://github.com/ksuzukigh/rokid-mac-control) 1.2.0と同等の利用者向け仕様をWindows 11へ移植するプロジェクトです。

## 現在の状態

開発中です。現時点では一般利用向けのリリースはありません。

- Windows版の正式仕様を作成済み
- ADB接続、Rokid判定、背景なし表示の起動処理を実装済み
- Windows専用のキーボード入力制御を実装済み
- Windows Graphics Capture APIとHUD合成の単体技術試験に合格
- Rokid実機を使う結合試験とライブ映像の画面取得は未実施

詳しくは[仕様書](docs/SPECIFICATION.md)と[技術試験計画](docs/TECHNICAL-TEST-PLAN.md)を参照してください。

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

## セルフテスト

```powershell
dotnet run --project tests/RokidControl.SelfTest
```

## ライセンス

本アプリのソースコードはApache License 2.0です。配布物へ同梱するscrcpy、Android Platform Tools、FFmpeg、SDLなどのライセンスは、各公式配布物の表示をそのまま収録します。
