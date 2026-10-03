# 開発者向け情報

[一般ユーザー向けの使い方はREADMEをご覧ください。](../README.md)

## ビルドと検証

Windows版は.NET 10 SDKで作成しています。リポジトリのルートで次のコマンドを実行します。

```powershell
.\tools\verify.ps1
.\tools\prepare-vendor.ps1
.\tools\package.ps1 -Version 0.2.0
```

## 技術資料

- [仕様書](SPECIFICATION.md)
- [技術試験計画](TECHNICAL-TEST-PLAN.md)
- [0.2.0の変更と実機確認](UPDATE-2026-10-03.md)
- [配布方針](CODE-SIGNING.md)
- [Android補助処理の実装と再生成](../DeviceHelper/README.md)

Mac版から取り入れた変更、Homeの操作位置の取得、R08との併用時の不具合対応などの実装詳細は、上記の技術資料で扱います。

## 文書の編集方針

READMEは一般ユーザー向けです。アプリでできること、導入方法、操作方法を、利用する人に分かる言葉で説明します。

既存の一般向けの説明は維持し、修正が必要な箇所だけを変更してください。開発経緯、他の版への追従、内部の仕組み、不具合の原因、ビルド・検証手順はREADMEへ追加せず、このページから参照する技術資料に記載します。
