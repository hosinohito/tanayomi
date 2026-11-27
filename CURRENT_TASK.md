# 現在の作業

更新日：2026-11-27（日本時間）。引き継ぎID：20261127-05。
依頼一覧はPROJECT_STATUS.md、取得・構築手順はREADME.md。

## 目的・範囲

R32：最新ビルドを保持し、一時テスト・旧生成物・用途不明物を復元用コピーなしで完全削除する。最新指示により、過去に保持していた旧試用実行物・比較PCM・native中間監査出力も削除対象とする。正式ソース・回帰試験・利用者data・Git管理・用途を確認した開発ツールは保持する。

## 到達点・次操作

R32完了。約35GiBを削除し、.localは約6.98GiB。最新app/data計8506ファイルのSHAと件数の一致を確認。旧実行物・比較音声・取得cache・native中間出力・一時helperを削除。残存build/bin・build/world・tryoutにはdata以外のファイルがない。正式source/test変更なし、cleanup helperも削除済み。今回の記録をGitHubへ反映する。再ビルドによる生成物の再作成は行わない。

## 最新ビルド

`C:/work/tanayomi/.local/build/app/Tanayomi.Desktop.exe`

2026-11-27のWindows x64フル機能版。新規生成OpenVoice／Skiaを含み、共通・native・完成exeの各合成試験成功。R31ソースはGitHub mainへ反映済み（c818c01、記録0ca85d9）。完全空の別PCで全工程を実行した結果ではない。

旧11/23試用版などの実行物は削除済み。各版のdataは元の場所を維持する。新アプリへ勝手に認証／設定を上書き移行しない。生成時のStage、full-windows-inputs.json、比較音声は削除済みであり、過去のパスを現存する入力として使わない。今後の構築はREADMEの固定入力取得から行う。

## 保持対象

- 最新app一式（音声モデル・依存DLL・許諾を含む）。各版のdata（設定・暗号化認証・取り込み音源）。
- 開発環境：`.local/dotnet/`、`.local/vendor/llvm-mingw/`、`.local/vendor/python/`、`.local/vendor/windows-toolchain-16.10.2/`。用途が確定したSDK／コンパイラー／開発Pythonであり、旧製品出力とは区別する。
- 正式source/tests/native/assets、正本文書、Git管理。非.localの未追跡・単発不要scriptは見つからなかった。

## 有効な保留

- R30シンプルUTAUの音程修正はソースへ反映・PCM検査済み。聴感評価未確定は残すが、過去の比較PCM保持は今回解除。計測結果と条件はRESEARCH、正式試験はtestsを参照。
- R26は明示終了まで未完。具体的な追加候補は未確定で、既採否の反復測定は行わない。
- 本体ライセンスと実行物公開、Windows以外の実装・監査・SDK・実機確認は保留。Qwen再開禁止。
- Twitchは利用者試験済み。YouTube実受信はURLなし、同話者CVVC素材は「なし」で保留。再照会しない。