# Tanayomi Aivis音声ワーカー

Windows x64、CPU、日本語用。アプリが非表示で起動する独立プロセスです。AivisSpeech Engineの日本語合成とStyle-Bert-VITS2のONNX推論を使用します。合成音声は話者本人や作品の公式音声ではありません。

## コードと対応ソース

- [AivisSpeech Engine 1.2.0](https://github.com/Aivis-Project/AivisSpeech-Engine/tree/0a310883265c64f43365fde5593b1296b14ae99b)、Aivis ProjectおよびVOICEVOX ENGINEの貢献者。LGPL-3.0。使用したPythonソースは同梱の `engine/voicevox_engine/` にあります。
- [Style-Bert-VITS2 2.7.0フォーク](https://github.com/tsukumijima/Style-Bert-VITS2/tree/1588a46595141a04652f9aae486f8a625c04ce55)、litagin02、tsukumijimaおよび貢献者。AGPL-3.0-only。一部にLGPL-3.0。使用したソースは `python/Lib/site-packages/style_bert_vits2/` にあります。
- [e2k 0.6.2フォーク](https://github.com/tsukumijima/e2k/tree/9920754e15d8c96404bef0bd81d424352fb7a48b)、Patchethiumおよび貢献者。コードはUnlicense。C2Kの学習元はWiktionaryとJMdict/EDICTです。CC BY-SAを保持します。N-GramモデルはCMUdictに基づきます。 [Wiktionary](https://ja.wiktionary.org/) [JMdict/EDICT](https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project) [CMUdict](https://github.com/cmusphinx/cmudict)
- `wheels.json` がPython部品の版・取得元・SHA-256を固定しています。各wheelの原本通知は `python/Lib/site-packages` のパッケージデータまたは `.dist-info` に保持しています。原本wheelにない本文は `licenses/` に追加しています。
- [python-soxr 0.5.0.post1](https://github.com/dofuuz/python-soxr/tree/v0.5.0.post1) とlibsoxrはLGPL-2.1以降。対応ソースとビルド設定は `licenses/sources/soxr-0.5.0.post1.tar.gz` です。num2wordsの対応ソースも `licenses/sources/` にあります。バンドル中のPythonソースと拡張は同位置に互換部品を再配置できます。
- [ONNX Runtime v1.23.0が固定するEigen](https://github.com/microsoft/onnxruntime/blob/v1.23.0/cmake/deps.txt) は `licenses/sources/Eigen-source.zip` と `licenses/Eigen-MPL-2.0.txt` に同梱しています。
- Microsoft Visual C++ 14.44.35112はMicrosoft Visual Studio再配布条件で同梱します。CPython 3.11.9の埋め込み配布ライセンスは `python/LICENSE.txt` です。NumPyの同梱OpenBLASおよびランタイムの通知はNumPyの配布ライセンスに含まれます。

`licenses/sources/Tanayomi/native/` にパッケージ作成スクリプト、ワーカー、変更を適用するコードを同梱しています。変更はstdio通信、ローカルBERT、AppDataとHTTPログ設定の除去、起動時のHub依存除去、外部許諾を確認できない「何」補助モデルの除外です。上流アプリの追加ユーザー辞書は同梱せず、pyopenjtalk-plusのNAIST系システム辞書とSudachiDict Coreを使います。原版と一部の語句の読みが異なる場合があります。

無音トリミングでは、従来と同じ閾値判定で先頭と末尾の位置を求め、閾値を超えた全サンプルの位置配列を作らないようにしています。変更済みの合成ソースと固定上流ソースから変更を適用するスクリプトを同梱しています。

## BERTと音声モデル

- [京都大学言語メディア研究室の日本語DeBERTa](https://huggingface.co/ku-nlp/deberta-v2-large-japanese-char-wwm) を元に、[tsukumijimaがONNX FP16に変換した固定版](https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx/commit/d701ec67708287b20d2063270f6b535e6eed09ab) と同版トークナイザーを使用。CC BY-SA 4.0。TanayomiはこのBERTの重みを変更していません。
- [阿井田 茂](https://hub.aivis-project.com/aivm-models/47e53151-a378-46f3-abee-ce13aa07feb1) と [澤原 玄二郎](https://hub.aivis-project.com/aivm-models/2e1fdde8-d089-42d7-b64f-cf89952b1bdc) はモデル制作・CV 古山キリヲ。 [morioki](https://hub.aivis-project.com/aivm-models/baaae3c0-7b22-4605-8ba5-80c959b41a48) はモデル制作 yuki、音声提供 もりおき。ACML 1.0の全文は `licenses/ACML-1.0.md` にあります。なりすまし、話者を貶める用途、実在する人物等への攻撃・中傷等の禁止条件に従ってください。
- [結亜ナル](https://hub.aivis-project.com/aivm-models/b79c40e4-8a5e-4f20-802c-2c0eba2f0983) はゆうしゃアシスタント。CC0 1.0。原本の埋込全文は `licenses/voices/aivis-yuia-naru.txt` にあります。

4本のAIVMXは、話者アイコンをTanayomiのプレースホルダーに置き換え、スタイル画像と試聴素材を取り除いています。元のONNXグラフ、重み、ライセンス、スタイルベクトルはそのままです。原本と同梱派生物のSHA-256は `voices.json` にあります。

JVNV F1・F2・M1・M2を含む版では、litaginの固定JP-Extraモデル4本をTanayomiがONNX/AIVMXに変換しています。変換後もCC BY-SA 4.0。元コーパスの著者はDetai Xin、Junfeng Jiang、Shinnosuke Takamichi、Yuki Saito、Akiko Aizawa、Hiroshi Saruwatariです。原本12ファイルのSHA-256と変換後のSHA-256は `voices.json` にあります。変換スクリプトとロックは `licenses/sources/Tanayomi/native/aivis/` にあります。 [固定モデル原本](https://huggingface.co/litagin/style_bert_vits2_jvnv/tree/205830ca1d49e666ddfbf2a755f0108e9cade4dd) [JVNV原典](https://sites.google.com/site/shinnosuketakamichi/research-topics/jvnv_corpus) [ライセンス](https://creativecommons.org/licenses/by-sa/4.0/)
