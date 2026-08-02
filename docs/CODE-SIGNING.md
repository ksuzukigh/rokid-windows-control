# Distribution policy（配布方針）

## 目的

Rokid ControlをGitHub Releasesから直接配布する。開発、試験、利用のために、Windowsの保護機能や実行ポリシーを無効にするよう利用者へ求めない。

## 確認済みの事象

- GitHub ActionsのReleaseビルドで、ビルド、セルフテスト、パッケージ作成を確認する。
- Windowsの設定によっては、ダウンロードしたアプリに確認画面が表示されたり、実行が許可されない場合がある。

## 採用方針

GitHub Releasesから自己完結型x64アプリを収録したZIPとSHA-256を直接配布する。Microsoft StoreとMSIXでの配布は対象外とする。

1. GitHub Actionsのクリーンなビルドで、ビルド、セルフテスト、パッケージ作成を実行する。
2. 配布用ZIPとSHA-256を同じGitHub Releaseへ掲載する。
3. READMEへ機能、入手方法、ライセンス、プライバシー方針を掲載する。
4. ダウンロード元とファイル名の確認を利用者へ案内する。

初回公開版にはコード署名を付けない。署名が必要になった場合は、配布方式と利用者への影響を確認したうえで別途導入する。

## 採用しない方式

- 利用者へSmart App ControlやMicrosoft Defenderの無効化を依頼する。
- 秘密鍵や証明書パスワードをリポジトリ、ログ、配布ZIPへ含める。
- Microsoft StoreまたはMSIXを初版の配布経路にする。

## 開発中の扱い

- GitHub Actionsのクリーン環境でビルドとセルフテストを継続する。
- 実行が許可されないWindows環境では、保護機能を無効にする手順を案内しない。
- 開発用自己署名証明書をこのPCの信頼済みストアへ追加する場合は、影響を説明し、利用者の明示的な承認を得てから実施する。

## 参考資料

- [Smart App Control overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview)
