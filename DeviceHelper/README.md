# Home操作位置の読取

Mac版 `ksuzukigh/rokid-mac-control` のコミット `fc5830a` からJavaソースとビルド済みJARを移植し（Apache License 2.0）、Windows版のキーボード初回操作に必要な処理を追加した。`checksums.json` は現在のソース・JARのSHA-256。

実行するJARは `src/RokidControl.App/Resources/rokid_ui_reader.jar`。AndroidのUiAutomationを `FLAG_DONT_SUPPRESS_ACCESSIBILITY_SERVICES` で接続し、ユーザーの入力サービスを保持したまま純正ランチャーの一意なindicator領域だけを読む。

Windows版では `focus-apps` を追加した。純正ランチャーの現在表示されている一覧・中央のアプリ・表示名・一覧内の位置を一意に確認し、キーボードの入力先を合わせる。YodaOSが古い既定の項目へ入力先を戻す場合は、毎回現在の一覧を読み直し、左右のキー入力で元の位置へ戻す。位置・表示名・入力先が一致したことを2回確認してから完了し、確認できなければ失敗として返す。アプリは起動しない。

これにより、一覧をタップで開いた直後の最初の方向キーが古い入力先の復旧に使われる問題を防ぐ。`apps` は表示名・一覧内の位置・フォーカス状態だけを読み取る実機検証用モード。判定できない画面では操作しない。UiAutomationは両モードともユーザーのアクセシビリティサービスを抑制しない。

再生成はJDK 17とAndroid SDKのandroid.jar・d8.jarを指定して実行する。

```powershell
.\tools\build-device-helper.ps1 -AndroidJar <SDKのandroid.jar> -D8Jar <build-toolsのlib\d8.jar>
```

スクリプトはJARとチェックサムを更新する。現在のチェックサムはWindows版変更後のソース・JARを表す。Javaソースを変更した場合は、実機で最初の左右操作・表示中のアプリの保持・R08サービス保持を再検証する。
