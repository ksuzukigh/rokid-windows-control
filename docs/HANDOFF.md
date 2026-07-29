# Windows版 Rokid Control 引き継ぎ

更新日: 2026-07-29

## 目的

`ksuzukigh/rokid-mac-control`とできるだけ同じ仕様のWindows版を完成させ、
GitHub Releasesで配布する。

## リポジトリ

- GitHub: `https://github.com/ksuzukigh/rokid-windows-control`
- 作業ブランチ: `agent/initial-windows-foundation`
- Pull Request: #1
- Mac版参照ソース: `artifacts/reference/rokid-mac-control`
  - `artifacts`配下のためGit管理外

## 現在の実機・試験環境

- Rokid AI Glasses RV101
- Android 12、画面サイズ480×640
- Wi-Fi ADB: `192.168.11.46:5555`
- USBケーブルは外した状態でもWi-Fi ADBと画面表示を確認済み
- WindowsのSmart App Controlがオンのため、現在の未署名版はWindows Sandbox内で試験
- Sandbox起動: リポジトリ直下の`Start Rokid Control Test.cmd`
- Sandboxでは初回起動ごとにWindowsファイアウォールの「許可」を利用者が押す必要がある

## 直近で修正・確認した内容

### 選択リングの位置

- scrcpyとWPFで異なるDPIを使っていたため、リングがRokid画面外へ飛んでいた
- WPFオーバーレイ自身のDPIで座標変換するよう修正
- コミット: `84bcb8d fix navigation overlay DPI positioning`
- 実画面でHome、アプリ一覧、メモへの左右移動を確認

### 黒画面にリングだけが表示される問題

- H操作などの後、Rokidランチャーが下段アイコンを隠すことがある
- Windows版は最初の下キーをローカル処理だけに使っていたため、Rokid本体が画面を再表示しなかった
- 下段選択へ入る最初の下キーをRokid本体にも送り、アイコンを再表示してからリングを出すよう修正
- コミット: `90a47ad wake hidden launcher before lower navigation`
- Hで黒画面を再現した後、下キー1回でアイコンとHomeリングが同時に復帰することを実機確認
- 右キーでアプリ一覧へ移動し、Enterで決定できることも確認

### USBなしでのSandbox再起動

- ホスト側ですでに接続済みのWi-Fi ADBアドレスをSandbox起動スクリプトが再利用するよう修正
- USBを外したまま、Sandboxを再作成して接続できることを確認

## 次に行う作業

キーボード操作の体感速度を改善する。

現状はADB命令ごとに通信処理を開始するため遅い。ホストから実測した例:

- 最初の左右キー命令: 約1.6秒
- 以降の左右・下・Home命令: 約0.4～0.5秒/回
- HとEnterは複数のADB命令と350ミリ秒待機を含むため、1.5秒以上かかる場合がある
- 下段リング表示後の左右移動はローカル処理なので、本来はほぼ即時

改善候補:

1. scrcpyの既存制御接続を使ってキーやタップを送る
2. またはADBシェルを操作中は持続接続し、毎回の起動コストをなくす
3. H、Enter、上段矢印、下段進入を実機で計測し、体感待ち時間を減らす
4. Sandbox固有の入力遅延とアプリ固有の遅延を分けて確認する

利用者へは「Sandboxだから仕方ない」とせず、完成版として許容できる速度まで改善する方針を伝えている。

## ビルド・試験状態

- Releaseビルド: 成功、警告0
- セルフテスト: 11件成功
- ホスト上のWindows Graphics Capture probeは、再ビルドした未署名DLLをSmart App Controlが遮断
- 同じビルドをSandbox内で起動し、Rokidの画面・キー操作・リング表示を実機確認済み
- `tools/verify.ps1`の最後のCapture probe失敗を、アプリ機能失敗と誤認しないこと

## 配布方針

- Microsoft Storeでは配布しない
- Smart App Controlを無効化させる方式は採用しない
- GitHub Releases向け署名は、個人のオープンソース開発に使えるSignPath Foundationを予定
- 公開前に利用者とUIを調整し、実機で最終確認する

## 利用者との進め方

- 利用者は非技術者向けの分かりやすい説明を希望
- 遠回りや、見ていない状態での「直った」という報告を避ける
- UI問題は必ず実画面のスクリーンショットで確認する
- USB接続、ファイアウォール、セキュリティ画面など、利用者の操作が必要な時だけ明確に依頼する
- 公開前のUI調整を忘れない
