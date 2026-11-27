# 現在の作業

更新日時：2026-11-27 10:15 +09:00。引き継ぎID：20261127-04。
依頼一覧・優先順はPROJECT_STATUS.md。Windows以外・公開配布は保留。R26は明示終了まで未完。

## 目的・完了条件

R31：旧開発PCのファイル／固定フォルダーID／Codex環境を必須にせず、取得・生成・検査からWindows x64フル機能版を構築できる手順を整備しGitHubへ反映する。対象は取得script、固定入力lock、native build/stage、full構成検査、回帰試験、README。音声の製品仕様・既存利用者data・試用版は変更しない。

## 到達点と検証

- 固定Python、MSVC/Ninja/CRT、音声入力、Aivis/JVNV許諾の取得を追加。portable MSVC3461ファイル検証、新Python/venv/pip検証成功。Windows SDKは公式固定installer取得後に利用者が導入しVerifySdkで確認する。OS全体を変更する自動導入は行わない。
- SkiaのGitiles archive再生成差は固定展開内容で照合。新Skiaの全compile、PE/ABI/link、画像codec・日本語描画とStageが成功。
- ORT公式取得停止には固定mirrorを追加。信頼済みsourceとの9431entry/内容SHA完全一致と新取得checkoutを検証。portable toolchainで専用ORTを新規生成、source/patch/PE/link/model/許諾をStageで照合。生成DLLは明示manifest SHAでのみ採用。
- WORLD/Open JTalkを新規生成して固定source/member/include/flagsのpolicyを通過。Aivis割当試験の不公平な比較と現行UTAUの古い期待値を修正。
- publish-full-windows.ps1により共通・WORLD・Open JTalk・OpenVoice native検査、全機能publish、完成exeのOpen JTalk、VOICEVOX/Supertonic/Aivis/JVNV、OpenVoice5件、UTAU波形方式の製品試験が終了コード0で完了。OpenVoiceは全件fallback 0。
- full構成不足8ケース／生成OpenVoice identity5ケース、Python準備12、音声取得24、runtime policy22、ORT source9、Skia取得Python8のfixture成功。一時fixtureは清掃。
- 完全に空の別Windowsマシンでの一括実行は未実施。今回のモデル変換済み入力と既存Windows SDKは固定検査済みのものを再使用し、全モデルの再変換成功を今回の結果としては主張しない。

## 成果物と再現

- 今回の開発フル機能exe：`.local/build/app/Tanayomi.Desktop.exe`。利用者用コピーとは別。既存最新試用版`.local/tryout/20261123-183929-66ba7f/Tanayomi.Desktop.exe`は変更なし。
- 新WORLD：`.local/build/native-llvm/audit-20261127-094637-c9a62f02`。新Open JTalk：`.local/build/open-jtalk/audit-20261127-094152-519b9c70`。
- 新OpenVoice Stage：`.local/build/openvoice-stage-portable-20261127`、manifest SHA `4E65C2BB2A23543C201894FAA80B2E1F3895DB16A218F9B2D62E0CB2B289D62B`。
- 新Skia Stage：`.local/build/native-skia-portable-20261127`、manifest SHA `90C59DA4F0FBFAE0CB8C0743D05A4D9CAA76F2F7281614A724C8EAA57DCA2617`。
- 新Aivis package：`.local/vendor/aivis/package-937084d1fdd9478fb5795ce08dee3f5f`。
- このPCの再実行入力は`.local/build/full-windows-inputs.json`。リポジトリ直下のPowerShell 7で`$full=Get-Content .local/build/full-windows-inputs.json -Raw|ConvertFrom-Json -AsHashtable; ./publish-full-windows.ps1 @full`。これは生成記録であり他PCへコピーする入力ではない。新規環境はREADMEの取得順に従う。

## 次操作

R31は完了。実装・README・試験をc818c01でGitHub mainへpushし、remote SHA一致とclean worktreeを確認した。完全に空の別PCでの一括再検証は未実施であり、次に再現報告があれば具体的な入力・失敗工程から対応する。R30は利用者評価待ち、R26は有効な追加候補未確定。継続のためだけの反復測定はしない。共有build/test/担当は終了し、バックグラウンド作業はない。

## 保持物・保留

- 新Stage・native source/build receipt/MAP・固定取得cacheは再現と採用来歴として保持。不要になった今回の旧OpenVoice検証コピー146620283bytesは使用参照確認後に削除済み。native中間物の追加清掃は現在の採用証跡を巻き込まず行う。
- R30試聴版・録画・比較PCMは利用者評価待ちで保護。長文OpenVoice品質基準`.local/previews/openvoice-long-phrase-20261123-090829-6467e7e7e7b5413e81436829b7422a5f/`、UTAU基準`.local/previews/utau-devoicing-20261026-174938-ba809dc08d4443b88b657180e082ab0e/`は基準更新まで保持。
- R26の採用済み最適化は維持。追加の具体的遅延根拠／候補は未確定。既採否の反復や根拠のないcache追加はしない。
- 本体ライセンス・実行物公開は保留。GitHubソースpushは承認済み。Qwen再開禁止。他OSの実装・監査・SDK・実機試験は明示保留。
- Twitchは利用者試験済み。YouTube実受信はURLなし、同話者CVVC素材は「なし」で保留。再照会しない。
## R30 WindowsのシンプルUTAU：実機録音を基準に音程の段差を減らす（2026-11-25、引き継ぎID 20261125-11）

棒読みちゃんの提供録画をローカルで計測し、付属CVの前版で母音の前半と後半が別の音程に分かれる現象を確認。発話ごとの共通音程を有声の母音頭から決め、有声区間の立ち上がりを原音の周期から共通曲線へ緩やかに接続する。旧音量補正・句の先読みは維持。句内音量を揃える案は、頭が目立つ音が増えたため不採用。

旧版設定は変更しない。試聴版と比較PCMはユーザーの判断まで保持。次に録音にあった後半の山を追う場合は、原文・音源・唄詠でのアクセント設定を揃えてから切り分ける。今回の波形差は全体の聴感一致を意味しない。検証条件と再生成先はRESEARCH。

GitHub mainにシンプルUTAUの音声改善と合成方式メニュー変更を反映済み（32adeda）。Windowsの対象検査108件合格。録画と試聴成果物はローカルのみ。
