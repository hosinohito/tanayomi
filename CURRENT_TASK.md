# 現在の作業

更新日：2026-11-28（日本時間）。引き継ぎID：20261128-24。
依頼一覧はPROJECT_STATUS.md、仕様はSPEC.md。

## 目的・範囲

R56：参照補完を撤去した最新のソースでWindows x64フル機能版をビルドし、製品試験後にソース・試験・文書をGitHub mainへ反映する。

## 到達点・次操作

Windows x64フル機能版は `.local/build/app/Tanayomi.Desktop.exe`。既存の監査済み入力から構成し、共通回帰2881件、ネイティブ試験、完成exeの合成試験に合格。参照補完は入っていない。再構築手順はREADME、当PCの入力指定はGit管理外の `.local/build/full-windows-inputs.json`。

## 有効な保留

- R30シンプルUTAUの音程修正はソースへ反映・PCM検査済み。聴感評価未確定は残すが、過去の比較PCM保持は今回解除。計測結果と条件はRESEARCH、正式試験はtestsを参照。
- R26は明示終了まで未完。具体的な追加候補は未確定で、既採否の反復測定は行わない。
- 本体ライセンスと実行物公開、Windows以外の実装・監査・SDK・実機確認は保留。Qwen再開禁止。
- Twitchは利用者試験済み。YouTube実受信はURLなし、同話者CVVC素材は「なし」で保留。再照会しない。