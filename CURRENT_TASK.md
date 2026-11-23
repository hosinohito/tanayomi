# 現在の作業

更新日時：2026-11-23 18:42:57 +09:00。引き継ぎID：20261123-05。
依頼一覧・優先順はPROJECT_STATUS.md。R26はユーザーの明示終了まで未完。Windows以外と公開配布は保留。

## 現在の目的・到達点

最新依頼：NIT＋OpenVoiceの『間延びモード』を追加し、完了後 https://github.com/hosinohito/tanayomi.git にソース/文書をpush。UI/core/nativeの実装・共通2679/native40/製品79+plan6成功。既定OFF修正版、ON修正前の長さ、次発言snapshot・保存復元・切替cache破棄。他方式/標準NIT不変。追加native export toj_synthesize_drawn_out、採用audit183016-4b48d01c、DLL 200C9249E57DF9BEF944E59B083054C667A96629AA8586300B04F025B3192FD7。

次操作：機能実装/検証/新copyは完了。ソース/文書のGit初期commit/pushを実行する。remote既存refsなしを確認済み。Git初期化とlocal credential.helper manager/credentialStore wincredmanのみ設定し、global safe.directoryは変更しない。Gitは利用者所有のため非管理者の正式利用者権限を使う。tokenはregister-github-token.ps1の非表示stdin→Windows資格情報、秘密表示/ファイル保存なし。利用者は登録済みと回答。GitHub CLIは未導入で不使用。PowerShell7はC:/Users/hoshi/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exeを使用可。

容量：完了ORT col2im/fusedの再生成可能LIB/OBJ252個1,400,306,855bytesを検査後清掃。最初のpublish resources抑制は効かず容量不足、失敗したbin/resourcesのみ安全清掃してcsprojの明示metadataへ修正。再publish成功、通常buildコピー維持。旧141155版削除の個別承認ありだが、実行時に指定フォルダーが既に消えており削除せず。空き約28GBへ変化したことを観測。残る181159版/dataは保護して新copy作成中。
前提：通常モードの長文修正は利用者が『治った』と確認済み。今回ONはその修正前を意図して選ぶ機能。品質比較の詳細はRESEARCH、履歴はWORK_LOG。

## 最新成果物・確認範囲

- 最新exe：C:/work/tanayomi/.local/tryout/20261123-183929-66ba7f/Tanayomi.Desktop.exe。間延びモード追加版、非data8189全SHA一致を確認済み。既存版/dataは保護し、非管理者の正式な利用者権限で暗号化認証と設定を引継ぎ。
- publishと非data8189ファイル全SHA一致。core 2F6EF80F35AAC70E5FABED6FC713EF9C7C6AE2F2D842766FCCD7E8BD65A41CC4、Desktop 720A67118DA06194BC1C26E309FF39C4A0B36D8709B8EE6A169F13D7CF2F0CD6。
- 共通2654、Aivis製品11、OpenVoice製品5文/退避0/同文再利用成功。先行VOICEVOX変更は製品104（全95声・再選択PCM一致・取消後再起動）成功。Aivis新packageは8声16PCM/上限拒否/量子化・trim対照成功。
- 最新Aivis package：.local/vendor/aivis/package-4a8ce577e6f5417b9d9e90a508e0f8c2。JVNV再生成入力：.local/vendor/aivis/jvnv/converted-112f21b518a34650835c185115797484。原本/wheel/対応sourceを保持。
- 新testは一度ローカル変数名衝突とエラーメッセージ期待値で失敗。変数名を変更し、既存の説明付きエラー識別子を検査するよう試験だけを訂正して再通過。製品のエラー挙動は変更していない。

## 次操作と注意点

- R26全体は未完。今回の採用5件は完了し、未統合差分なし。Windows標準音声のSpVoice再利用も有限監査し、OS既定音声/出力先の途中変更反映を変えるため単純保持は見送った。現在追加の採用候補は未確定。VOICEVOX/Supertonicの追加ロード削減、OpenVoice一覧wrapper、Aivis末尾ガの再解析は有限監査後に見送り。既採否を反復せず、次は新しい具体的な重複処理または再現可能な遅延根拠が必要。根拠なしの新cache/依存/品質変更を追加しない。
- 再開時は本書と実ファイルを照合し、旧開発package66の清掃session58271は完了（3,243,383,079bytes）。共有build/test/agentは非稼働。旧e543も3,243,381,095bytes整理済み。どちらも再生成可能開発物のみで、試用版/data/原本は対象外。
- 入口はリポジトリ直下で ./dev.ps1 test -Configuration Release、./dev.ps1 publish。対象方式の製品確認はREADMEのコマンドを使う。新規copyは ./dev.ps1 tryout、通常sandboxの認証ACL拒否には正式な利用者権限を申請し、平文退避/ACL変更はしない。
## 保持物・有効な保留

- 長文修正後品質基準：.local/previews/openvoice-long-phrase-20261123-090829-6467e7e7e7b5413e81436829b7422a5f/。次の基準更新まで保持。修正前085320版は再生参照終了確認後の清掃対象。
- GitHubソースpushは今回明示承認。実行物の公開配布・本体ライセンス決定とは別。



- 公開配布/本体ライセンス選択は利用者判断待ち。R19のソース提供/クレジットは承認済みで、適格モデルを実装する。禁止条件に該当する候補・許諾未確定の資産を黙って製品へ追加しない。Qwen再開禁止。
- Twitchは利用者試験済み。投稿・再試験依頼なし。YouTubeはURLなしで実受信保留。外部VOICEVOX実ENGINE確認は方式撤回により対象外。
- 同話者の実CVVC素材は利用者回答「なし」。再照会不要、人工検証を実素材評価としない。
- Windows以外の環境対応タスク全体は最新指示で明示保留。他OS実機試験は行わない。Unix nativeの実build/ABI/配布監査、mobile SDK/workload/Xcode/署名は未整備。iOS無音時常時受信/Android Doze通信を保証しない。
- 採用native：OpenVoice 1054ABA195985786A160E1F0220527B34734FACD5CA32D8DBEF094A53A07846C、WORLD 6D07EBC9B9854065A3F5428E71FF05212221449F2BD640541519D82757AC3FFF。監査の入口はREADME/THIRD_PARTY_NOTICES。col2im/fusedの限定source・監査、WORLD audit-20261027-080217-8175a433とaudit-20261027-074410-aa27613bを保護。
- 品質基準：.local/previews/utau-devoicing-20261026-174938-ba809dc08d4443b88b657180e082ab0e/のA/B/conditions.json。B PCM7966B6CCF30AB43444453A8C8557B11EA233FA5AB803391D24F0E4027109E243を次の品質基準更新まで保持。同じ比較の再生成/質問なし。
- Open JTalk採用監査：.local/build/open-jtalk/audit-20261123-183016-4b48d01c。DLL SHA256 200C9249E57DF9BEF944E59B083054C667A96629AA8586300B04F025B3192FD7。旧監査とのtoolchain/flags/import/export/linked-member一致を確認。採用版の由来証跡として置換監査まで保持。入口README。
