# 技術試験計画

## 状態の意味

- PASS: 合格
- FAIL: 不合格
- READY: 試験を実行できる状態
- BLOCKED: 実機または外部条件待ち
- PLANNED: 実装前

## 試験一覧

| ID | 試験 | 状態 | 実機 | 合格条件 |
| --- | --- | --- | --- | --- |
| T01 | .NET 10 WPFビルド | PASS | 不要 | Releaseビルドが警告なしで成功 |
| T02 | キーボード下段ナビゲーション | PASS | 不要 | 境界、移動、解除、座標がMac版と一致 |
| T03 | ADB devices解析 | PASS | 不要 | USB、Wi-Fi、unauthorizedを正しく分類 |
| T04 | ADB mDNS解析 | PASS | 不要 | `_adb-tls-connect._tcp`だけを抽出し重複排除 |
| T05 | Rokid機種判定 | PASS | 不要 | Rokid識別子だけを許可 |
| T06 | 画面サイズとIPv4解析 | PASS | 不要 | 標準出力の表記揺れを処理 |
| T07 | HUD合成 | PASS | 不要 | 出力サイズ、黒透過、緑HUDを検証 |
| T08 | Windows Graphics Capture API利用可否 | PASS | 不要 | 現在のPCでAPIが利用可能 |
| T09 | scrcpy HWND取得 | PASS | Rokid必要 | HUDとカメラのHWNDをPIDから取得 |
| T10 | 画面外scrcpyの連続取得 | PASS | Rokid必要 | 2入力を15fpsで5分以上取得 |
| T11 | RV101 USB ADB認識 | PASS | Rokid必要 | `adb devices`が`device`として表示 |
| T12 | USBからWi-Fi移行 | PASS | Rokid必要 | TCP 5555で再接続しRokid判定成功 |
| T13 | 背景なし操作 | READY | Rokid必要 | マウスと全キー操作が動作 |
| T14 | 入力の隔離 | PASS | Rokid必要 | 他アプリの入力をRokidへ送らない |
| T15 | カメラ競合からの復帰 | READY | Rokid必要 | カメラ解放後30秒以内に復帰 |
| T16 | Wi-Fi瞬断からの復帰 | BLOCKED | Rokid必要 | 自動再接続して表示を再開 |
| T17 | 終了時クリーンアップ | PASS | Rokid必要 | PCとRokidに動作中プロセスを残さない |

## 実機試験を依頼するタイミング

T01からT08までがPASSになり、ADBとscrcpyを含む開発用ビルドが用意できた時点で、利用者へ次を依頼する。

1. RokidのADB開発者モードを有効にする。
2. Rokidを起動する。
3. 5ピン開発用ケーブルでWindows PCへ接続する。
4. Rokid側にUSBデバッグの確認が出た場合は許可する。
5. Windowsのデバイスマネージャーに警告が出た場合は画面を共有する。

ドライバは機器IDと署名元を確認するまでインストールしない。

## 2026-07-28 実行結果

- Release直列ビルド: 成功、警告0、エラー0
- セルフテスト: 8件成功
- Windows Graphics Capture: `GraphicsCaptureSession.IsSupported() = True`
- scrcpy: 公式v4.1 Windows x64版、公開SHA-256との一致を確認
- ADB: v37.0.0を起動し、デバイス一覧取得後にサーバーを終了
- WPF起動スモークテスト: 起動、終了ログ、終了コード0を確認
- 実機未接続のため、T09以降は未実施

## ライブ映像の判定

- 解像度: Rokidの`wm size`と一致
- フレームレート: 目標15fps
- 継続時間: 最低5分
- カーソル: 映像へ含めない
- 受信用ウインドウ: デスクトップとAlt+Tabへ表示しない
- 合成: 黒HUDはカメラへ影響せず、緑と白のHUDが明るく加算される
- スライダー: 0から1の変更が即時反映される

## 2026-07-29 実機試験結果

- USB ADB: `device`として認識し、Rokid製RG-glassesであることを確認
- OSと画面: Android 12、480×640
- USBからWi-Fi ADB: TCP 5555への移行と機種再確認に成功
- 画面受信: scrcpyで10秒間の非表示録画に成功、終了コード0
- HUDとカメラの同時受信: 15fpsで5分間継続し、両方とも正常終了
- Wi-Fiのみの継続確認: USBを外した後もADB接続を維持
- カメラ再取得: 直前の受信終了直後は競合する場合があり、約5秒の待機後に再取得できることを確認
- scrcpy HWND取得: 起動直後のPIDから枠なしウインドウを検出
- Windows Graphics Capture: 枠なしscrcpyから480×640の初回フレームを86msで取得
- 背景なしモード: Windowsアプリから実画面を表示
- キー入力: `H`と左右キーがRokidへ届くことを画面とログで確認
- Wi-Fiのみの再起動: USBを外した状態でアプリから画面表示に成功
- マウス入力: scrcpy画面のクリックがRokidへ届くことを確認
- 入力隔離: 電卓を前面にして押した`H`がRokidへ送信されないことを確認
- 互換終了: `Ctrl+Q`で終了し、クリーンアップに成功
- 終了処理: Windowsプロセス、Rokid側PID、生存信号の残存なし
- 実機固有のシリアル番号、IPアドレス、SSIDは記録しない
