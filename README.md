# NaninovelStartupTimeLogger

## これは何？

Naninovel プロジェクトの起動（Splash 前）→ initialize 相当の初回スクリプト終了までの経過時間を UnityEngine.Debug.Log（Info）で出力する外部パッケージです。
プロジェクト側の改変やカスタムコマンドの追加は不要です。

想定：Naninovel 1.20（PlayedScript.ScriptName / PlayedScript.Script.Name を自動判別）

出力：CSV なし、Info ログのみ

動作条件：Editor または Development Build のみ

## 導入

UPMでGithubリポジトリを指定して追加します。

`git+ssh://git@github.com/tunacook/NaninovelStartupTimeLogger.git`


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

こんな感じのログが出ます
```
[StartupTimeLogger] ARMED src=BeforeSplashScreen
[StartupTimeLogger] ENGINE_READY
[StartupTimeLogger] init_script_end (t=5092.3 ms)
```

## トラブルシューティング

### ログが出ない（実機/プレイヤー）

- Development Build になっているか
- Player Settings の Info ログ許可がオンか
- 端末/OS の標準出力（Xcode, logcat, Player.log 等）を見ているか
