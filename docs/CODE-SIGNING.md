# コード署名と配布

## 目的

Rokid ControlをSmart App Controlが有効なWindows 11でも安全に実行できる形で配布する。開発や試験のためにSmart App Control、Microsoft Defender、実行ポリシーを無効化しない。

## 確認済みの事象

- この開発PCでは、署名されていない新しいローカルビルドのDLLがSmart App Controlにより拒否された。
- 同じソースはローカルとGitHub ActionsのReleaseビルドで警告0、エラー0となる。
- したがって、公開ビルドではアプリ本体だけでなく、同梱する実行ファイルとDLLの署名確認が必要である。

## 採用方針

### GitHub Releasesで直接配布する場合

Microsoft Artifact Signing（旧称Trusted Signing）を第一候補とする。

1. Artifact Signingのアカウント、本人確認、証明書プロファイルを用意する。
2. GitHub Actionsでは短期資格情報を使い、長期の秘密鍵ファイルをリポジトリへ保存しない。
3. 公開物に含まれる自作の`.exe`と`.dll`をRSA証明書で署名する。
4. SHA-256のタイムスタンプを付与する。
5. 署名後に`signtool verify /pa /all`で全対象を検証する。
6. 署名済み成果物から配布用ZIPまたはMSIXを作成し、SHA-256を公開する。

### Microsoft Storeで配布する場合

MSIXとして提出し、Storeによる署名を利用する。Store経由のMSIXは証明書の購入と管理が不要で、利用者にSmartScreenの警告を出さない点を優先する場合に適する。

### オープンソース向け代替

プロジェクトが条件を満たす場合はSignPath Foundationの無償署名も候補とする。申請と承認が必要なため、公開日程が決まる前に適格性を確認する。

## 採用しない方式

- 自己署名証明書を一般利用者へ配布する。
- 利用者へSmart App ControlやMicrosoft Defenderの無効化を依頼する。
- 秘密鍵や証明書パスワードをリポジトリ、ログ、配布ZIPへ含める。
- ECC証明書だけでSmart App Control対応とする。Smart App ControlはRSA署名を必要とする。
- タイムスタンプなしで公開物へ署名する。

## 開発中の扱い

- GitHub Actionsのクリーン環境でビルドとセルフテストを継続する。
- このPCで新しいビルドが拒否された場合は、Code Integrityログを確認し、保護設定は変更しない。
- 実機UI試験が必要になった時点で、Artifact Signingまたは明示的に管理された開発用署名を準備する。
- 開発用自己署名証明書をこのPCの信頼済みストアへ追加する場合は、影響を説明し、利用者の明示的な承認を得てから実施する。

## 参考資料

- [Code signing options for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- [Smart App Control overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview)
- [Sign your app for Smart App Control compliance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
- [Set up signing integrations to use Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations)
