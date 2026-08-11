# App Volume Boost

特定のアプリだけを、他のアプリに影響させずに最大+20 dB増幅するWindowsアプリです。

## 動作方式

- Windowsのアクティブな音声セッションからアプリを一覧表示
- 選択したアプリだけをVoicemeeterの仮想入力へルーティング
- Voicemeeterの専用入力ストリップのゲインだけを変更（+20 dBまで）
- 停止時に元の出力先とストリップ設定を復元
- アプリ名ごとの設定を `%LOCALAPPDATA%\AppVolumeBoost\profiles.json` に保存

Windows 11 25H2 / build 26200で問題が出るプロセスループバックを使わず、Voicemeeter Remote APIを使います。

## 前提

Voicemeeter（Basic / Banana / Potato）がインストールされ、起動している必要があります。
Voicemeeterの `A1` に、実際に音を出したいスピーカーまたはヘッドホンを設定してください。
このPCでは既にVoicemeeter Bananaと仮想入出力が確認できています。

## 実行

```powershell
dotnet run --project .\AppVolumeBoost.csproj
```

配布用の単一ファイルは次で生成できます。

```powershell
dotnet publish .\AppVolumeBoost.csproj -c Release -r win-x64 --self-contained true
```

Windows 10 2004 (build 19041) 以降が必要です。音量を上げすぎると音割れするため、小さい値から調整してください。
