# NaninovelStartupTimeLogger

## これは何？

Naninovel プロジェクトの起動（Splash 前）→ initialize 相当の初回スクリプト終了・タイトル画面の表示までの経過時間を、途中の内訳（Naninovel の各サービスの初期化・UI の生成などの後処理タスク）とあわせて UnityEngine.Debug.Log（Info）で出力する外部パッケージです。
プロジェクト側の改変やカスタムコマンドの追加は不要です。

Naninovel製ゲームの起動時間を改善するのに、計測ツールとして使うことを想定しています。内訳が出るので、どこに時間がかかっているか（例: UI の一括生成）を切り分けられます。

想定：Naninovel 1.21（PlayedScript.ScriptName / PlayedScript.Script.Name を自動判別）

出力：CSV なし、Info ログのみ

動作条件：Editor または Development Build のみ

## 導入

UPMでGithubリポジトリを指定して追加します。

`git+ssh://git@github.com/tunacook/NaninovelStartupTimeLogger.git`

NaninovelStartupTimeLogger自身もNaninovelを含めているため、Naninovelのupmを使える状態にしてください。

https://naninovel.com/guide/getting-started#install-from-github


## ビルド条件

Editor：そのまま動作

実機/プレイヤー：Development Build を有効化

Info ログが抑止されていると出力されません

Player Settings →（Logging/Diagnostics 相当）で Info（LogType.Log） を許可してください

IL2CPP でも動作。[Preserve] を付与済みです（もしストリッピングで消える場合は Managed Stripping Level を下げるか、link.xml を用意してください）。

呼び出し側のプロジェクトで.asmdefを設定している場合は、StartupTimeLoggerを含めるようにしてください。

![](/Documentation~/asmdef.png)

## 使い方

追加・ビルドするだけで自動実行され、ログに時間が出ます。コードを呼ぶ必要はありません。

こんな感じのログが出ます（値は例。サービス名・行数はプロジェクトによって変わります）

```
[StartupTimeLogger] armed (BeforeSplashScreen)
[StartupTimeLogger] t=   310.2ms (+  310.2) engine_initialize_start
[StartupTimeLogger] t=   315.8ms (+    5.6) initialize_service ResourceProviderManager
[StartupTimeLogger] t=   318.1ms (+    2.3) initialize_service LocalizationManager
...
[StartupTimeLogger] t=   590.4ms (+   12.0) post_init_task (progress 0.750)
[StartupTimeLogger] t=  4120.9ms (+ 3530.5) post_init_task (progress 0.875)
[StartupTimeLogger] t=  4135.0ms (+   14.1) engine_initialize_finished
[StartupTimeLogger] t=  4136.2ms (+    1.2) engine_ready
[StartupTimeLogger] t=  5092.3ms (+  956.1) init_script_end
[StartupTimeLogger] t=  5110.7ms (+   18.4) title_ui_visible
```

- `t` は起点（BeforeSplashScreen）からの経過ミリ秒、`+` は前の行からの経過ミリ秒
- `+` の大きい行の直前の区間に時間がかかっています。上の例では `post_init_task`（UI の一括生成など）の 1 つに約 3.5 秒かかっています
- `initialize_service <名前>` は、その行から次の行までがそのサービスの初期化です

## 仕組み

- BeforeSplashScreen でストップウォッチ開始 → 監視オブジェクトを設置
- IScriptPlayer.PlayedScriptから最初に観測できたスクリプトIDをロック
- 以降、停止または別スクリプトに切り替わった瞬間を「初回スクリプトの終了」とみなして経過時間(ms)を出力
  - 秒変換しても良いけどミリ秒で充分だろう 
- 内訳は Naninovel の `Engine.OnInitializationStarted` / `OnInitializationProgress` / `OnInitializationFinished` で区切る
  - 進捗 0〜0.25 が前処理（`pre_init_task`）、0.25〜0.75 がサービスの `InitializeService`（`initialize_service`）、0.75〜1 が後処理タスク（`post_init_task`。UI の生成など）
- タイトル画面（`ITitleUI`）が表示された時点で `title_ui_visible` を出力。タイトルを経ずに始まるゲームでは、エンジン準備完了から 30 秒で `title_ui_not_shown_timeout` を出して見張りをやめる
- 取りこぼし低減のためUpdateとEndOfFrameの二重チェック

## トラブルシューティング

### ログが出ない（実機/プレイヤー）

- Development Build になっているか
- Player Settings の Info ログ許可がオンか
- 端末/OS の標準出力（Xcode, logcat, Player.log 等）を見ているか
