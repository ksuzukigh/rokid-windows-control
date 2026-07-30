# Code signing policy（コード署名方針と配布）

## 目的

Rokid ControlをSmart App Controlが有効なWindows 11でも安全に実行できる形で配布する。開発や試験のためにSmart App Control、Microsoft Defender、実行ポリシーを無効化しない。

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## 担当者

- Committers and reviewers: [ksuzukigh](https://github.com/ksuzukigh)
- Approvers: [ksuzukigh](https://github.com/ksuzukigh)

署名要求はリリースごとにApproverが手動承認する。担当者が増えた場合は、役割と氏名をこの方針へ追記する。

## 確認済みの事象

- この開発PCでは、署名されていない新しいローカルビルドのDLLがSmart App Controlにより拒否された。
- 同じソースはローカルとGitHub ActionsのReleaseビルドで警告0、エラー0となる。
- したがって、公開ビルドではアプリ本体だけでなく、同梱する実行ファイルとDLLの署名確認が必要である。

## 採用方針

GitHub Releasesから直接配布し、一般公開版は信頼されたRSAコード署名を付ける。Microsoft StoreとMSIXでの配布は対象外とする。

日本在住の個人開発者はMicrosoft Artifact SigningのPublic Trust対象地域外であるため、初版ではオープンソースプロジェクト向けのSignPath Foundationへ申請する。採用可否はSignPath Foundationの審査による。

1. GitHub Actionsのクリーンなビルドだけを署名対象にする。
2. 長期の秘密鍵ファイルをリポジトリや開発PCへ保存しない。
3. 公開物に含まれる自作の`.exe`と`.dll`をRSA証明書で署名する。
4. SHA-256のタイムスタンプを付与する。
5. 署名後に`signtool verify /pa /all`で全対象を検証する。
6. 署名済み成果物から配布用ZIPを作成し、SHA-256を公開する。

### SignPath Foundation

本プロジェクトはApache License 2.0で公開し、配布物へ含めるscrcpy、ADB、FFmpeg、SDL、.NETもオープンソースまたはシステムライブラリで構成する。申請前に次を満たす。

- 署名前と同じ形式の公開プレリリースを1回作成する。
- READMEへ機能、入手方法、ライセンス、プライバシー方針を掲載する。
- GitHub Actionsから再現可能な配布ZIPを生成する。
- コミット権限者とリリース承認者を明示する。
- 審査承認後、SignPathの指定する署名ワークフローを追加する。

### Microsoft Artifact Signing

2026年7月30日時点で、Public Trustは日本の組織にも提供されているが、個人開発者は米国・カナダ在住者に限定される。日本在住の個人による初版では使用しない。個人向けの対象地域が拡大した場合は再検討する。Private Trustは一般利用者向けの信頼を提供しないため採用しない。

## 採用しない方式

- 自己署名証明書を一般利用者へ配布する。
- 利用者へSmart App ControlやMicrosoft Defenderの無効化を依頼する。
- 秘密鍵や証明書パスワードをリポジトリ、ログ、配布ZIPへ含める。
- ECC証明書だけでSmart App Control対応とする。Smart App ControlはRSA署名を必要とする。
- タイムスタンプなしで公開物へ署名する。
- Microsoft StoreまたはMSIXを初版の配布経路にする。

## 開発中の扱い

- GitHub Actionsのクリーン環境でビルドとセルフテストを継続する。
- SignPath Foundationへの申請用に限り、一般利用を推奨しないことを明記した未署名プレリリースをGitHub Actionsから作成する。
- このPCで新しいビルドが拒否された場合は、Code Integrityログを確認し、保護設定は変更しない。
- 実機UI試験が必要になった時点で、SignPath Foundationの承認済み署名または明示的に管理された開発用署名を準備する。
- 開発用自己署名証明書をこのPCの信頼済みストアへ追加する場合は、影響を説明し、利用者の明示的な承認を得てから実施する。

## 参考資料

- [Code signing options for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- [Smart App Control overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview)
- [Sign your app for Smart App Control compliance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
- [Set up signing integrations to use Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations)
- [Artifact Signing quickstart and availability](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)
- [SignPath Foundation](https://signpath.org/)
- [SignPath Foundation conditions](https://signpath.org/terms.html)
