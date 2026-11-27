# たなよみ

Twitch／YouTubeのチャットを日本語のOS標準音声、UTAU音源、同梱の日本語音声で読み上げるアプリ。Windows・macOS・Linux・Android・iOSを対象とする。

## Windows版の音声機能

| 方式 | 実装・構成の入口 |
| --- | --- |
| VOICEVOX（アプリ内蔵） | [音声一覧](src/Tanayomi.Desktop/VoicevoxCatalog.cs)、[合成ワーカー](src/Tanayomi.Desktop/VoicevoxPcmWorker.cs)、[資産準備](native/prepare-voicevox.ps1) |
| Supertonic 3 | [音声出力](src/Tanayomi.Desktop/SupertonicSpeech.cs)、[資産準備](native/prepare-supertonic.ps1) |
| Aivis系の同梱音声 | [音声出力](src/Tanayomi.Desktop/AivisSpeech.cs)、[資産準備](native/aivis/package.py) |
| NIT日本語・東北大f01・Mei・Takumi | [Open JTalkワーカー](src/Tanayomi.Desktop/OpenJtalkPcmWorker.cs) |
| UTAU・NIT日本語＋OpenVoice | [UTAU合成](src/Tanayomi/UtauSpeech.cs)、[OpenVoice変換](src/Tanayomi.Desktop/Neural/DesktopNeuralUtau.cs) |

音声を発言ごとに選ぶランダム設定、0〜400%の音量調整、OpenVoice専用の「間延びモード」に対応する。操作と保存仕様は[SPEC.md](SPEC.md)を参照する。

**GitHubのソースZIPと、音声資産を準備済みのWindowsアプリ一式は内容が異なる。** Git管理対象はソース・試験・文書・許諾付きの同梱原本で、追加取得するエンジンDLL・モデルや生成物を置く`.local/`は含めない。クローンしただけで`publish`しても、準備していない方式は含まれず、起動時の音声選択肢にも表示されない。各資産の準備手順は[Open JTalkの固定入力](#open-jtalkの固定入力)以降、許諾条件は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)を参照する。実行物の参照先は[CURRENT_TASK.md](CURRENT_TASK.md)に記載する。

## 起動と利用操作

Windows版はアプリ一式の`Tanayomi.Desktop.exe`を直接起動する。利用時に.NET SDKや起動用コマンドは不要。接続、音源選択、保存データの扱いは[SPEC.md](SPEC.md)を参照する。開発中の最新の起動パスは[CURRENT_TASK.md](CURRENT_TASK.md)に記載する。

## 設計と構成

C#／.NET 10とAvalonia 12系を使用する。チャット受信、上限付き待ち行列、読みへの変換、合成、再生、画面を分離し、発言・接続・音源ごとの失敗から復帰する。モバイルの制約でPC版の機能や障害隔離を制限しない。

UTAUは同梱のNAIST-JDIC派生辞書とC#の読み変換、日本語原音の切り出し・接合でPCMを生成する。原音直接方式は、VCVエイリアスとprefix.mapに基づく素材選択にも対応する（操作・制限はSPEC）。同梱hosinohitoと利用者が追加した音源は同じ取り込み・合成経路を使う。UTAUで扱えない発言はOS標準音声へ退避する。PCの標準音声とPCM再生は別プロセスへ隔離する。


| 場所 | 内容 |
| --- | --- |
| `src/Tanayomi/` | 共通画面、チャット、認証、待ち行列、読み変換、UTAU合成、音源取り込み |
| `src/Tanayomi.Desktop/` | PC起動部、音声ワーカー、OS別の認証保存 |
| `src/Tanayomi.Android/`・`src/Tanayomi.iOS/` | 各OSの起動部・バックグラウンド動作・標準音声・PCM出力 |
| `tests/Tanayomi.Tests/` | 正常系・異常系の試験と画面描画 |
| `assets/` | 同梱音源、派生辞書、変換スクリプト、許諾文 |
| `native/` | WORLD・Open JTalk・OpenVoiceのブリッジ／ビルドと、VOICEVOX・Supertonic・Aivis等の資産準備 |
| `.local/` | 開発環境、キャッシュ、一時生成物 |
| `.local/build/` | ビルド・中間・publish出力 |
| `.local/tryout/<日時>-<短ID>/` | 新規作成する各自己完結型コピーと、それぞれの`data/` |

標準音声はWindowsのSAPI、macOSの`say`、LinuxのSpeech Dispatcher（独自SSIPクライアント）、AndroidのTextToSpeech、iOSのAVSpeechSynthesizerを使う。PCM再生はWindowsのWinMM、macOSのAudioQueue、Linuxの`aplay`、AndroidのAudioTrack、iOSのAVAudioPlayerへ渡す。LinuxのSpeech DispatcherはOS側で起動しておく。認証保存はWindowsのDPAPI、macOSのKeychain、LinuxのSecret Serviceを使い、Linuxでは`/usr/bin/secret-tool`が必要。これらのOSサービス・コマンドは同梱しない。

## 開発手順

### 初回のWindows開発環境

以下は開発者向け。完成したアプリを使うだけなら不要。Windows x64の通常ユーザーで作業し、コマンドの作業場所はクローンしたリポジトリの直下とする。

1. Git for Windowsと、**x64のPowerShell 7.4以降**を用意する。Windows標準の`powershell.exe`（5.1）では開発スクリプトを実行できない。PowerShellの導入は[Microsoft公式手順](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)に従う。ZIP版なら任意の開発用ディレクトリーへ展開できる。`pwsh`が見つからない場合は、展開先の`pwsh.exe`を絶対パスで起動する。CodexやGitHub CLIは必要ない。
2. 空の作業先へソースを取得する。既存の作業フォルダーへ重ねてcloneしない。

   ```powershell
   git clone https://github.com/hosinohito/tanayomi.git C:/work/tanayomi
   Set-Location C:/work/tanayomi
   $PSVersionTable.PSVersion
   ```

3. [global.json](global.json)のSDK（現在.NET **10.0.402**、`latestPatch`）を用意する。Runtimeだけではビルドできない。[公式.NET 10ダウンロード](https://dotnet.microsoft.com/download/dotnet/10.0)の該当SDKのWindows x64バイナリーZIPを検証して、`dotnet.exe`が直下に来るよう`.local/dotnet/`へ展開する。既に互換SDKを導入済みならPATH上の`dotnet`も利用できるが、`dev.ps1`は`.local/dotnet/`を優先する。
4. 同じPowerShellで保存先を設定してSDKを確認する。これらは現在のシェルと子プロセスだけに適用される。通常の開発操作では`dev.ps1`が同じ設定を行う。

   ```powershell
   $env:TEMP = Join-Path $PWD '.local/tmp'
   $env:TMP = $env:TEMP
   $env:TMPDIR = $env:TEMP
   $env:DOTNET_CLI_HOME = Join-Path $PWD '.local/cli'
   $env:NUGET_PACKAGES = Join-Path $PWD '.local/nuget'
   $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
   New-Item -ItemType Directory -Force $env:TEMP | Out-Null
   & ./.local/dotnet/dotnet.exe --version
   ```

   システムSDKを使う場合だけ最後を`dotnet --version`へ替える。リポジトリ直下で実行し、SDK不一致を`global.json`の書換えで回避しない。
5. 下の`build`→`test`→`run`を実行する。初回はNuGetへの通信が必要。`dev.ps1`が`--locked-mode`で復元するため、個別の`dotnet restore`やパッケージ更新は不要。失敗した場合は終了コードと表示された不足条件を確認する。

この段階ではOS標準音声と同梱UTAU原本を使う基本構成になる。追加音声・監査済みネイティブ資産は次節で準備する。通常のC#編集だけならVisual Studio、C++コンパイラー、Pythonは不要。

### 全音声入り構成を準備する順序

追加資産はGit clone／NuGet復元で取得されない。以下の順に、必要な方式の固定入力を準備してから`publish`する。取得元・版・SHA・許諾は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)、詳細操作は本書の対応節を正本とする。ダウンロード・ビルドの中間物と完成コピーを複数保持するため、完成アプリの容量だけで空き容量を見積もらない。

| 順序・対象 | 準備入口と必要条件 | 通常ビルドが読む場所 |
| --- | --- | --- |
| 1. WORLD／LLVM | [WORLDビルド](#world比較用のネイティブビルド)の`build-world-llvm.ps1 -DownloadToolchain`で固定LLVMを取得。監査・試験後のDLLを配置 | `.local/build/native-world/` |
| 2. Open JTalk／NIT | [固定入力](#open-jtalkの固定入力)の公式4アーカイブを取得し、上記LLVMで`build-open-jtalk.ps1`を実行。監査出力のDLL・辞書・モデル・manifestを同節の手順で配置 | `.local/build/native-open-jtalk/` |
| 3. VOICEVOX | `./native/prepare-voicevox.ps1`（PowerShell 7.4以降）。固定モデル・CORE・Runtime・許諾を取得 | `.local/vendor/voicevox/package/` |
| 4. 追加HTS音声 | 固定入力節の東北大4モデルの配置と`./native/prepare-mmdagent-voices.ps1` | `.local/vendor/tohoku-f01/`、`.local/vendor/mmdagent/1.8/` |
| 5. 開発用Python | Python 3.12.14 x64と下記venv。OpenVoice変換は固定requirementsも導入 | `.local/neural-openvoice/venv/` |
| 6. Supertonic | `./native/prepare-supertonic.ps1 -Download`。上記Python・LLVMのreadobjと同じフォルダーのclang・VOICEVOXのCRTを使用 | `.local/vendor/supertonic/prepared-md-vc143/` |
| 7. Aivis／JVNV | 固定入力節の`prepare-aivis.ps1`→`prepare-jvnv.ps1`→`-JvnvDirectory`付き再package。各固定原本・wheelも事前に必要 | 新規`package-<ID>/`を`$env:AivisDirectory`に指定 |
| 8. NIT＋OpenVoice | 本書のOpenVoiceモデル変換・ORTビルド・資産配置手順。NIT、固定モデル、監査済みORT、CRT、許諾・対象ソースが必要 | `.local/build/native-openvoice/` |
| 9. 監査済みUI描画 | 本書の`build-skia.ps1`と`SkiaNative.targets`の手順。固定ソース／C++環境と4 DLLのハッシュを照合 | `.local/build/native-skia/` |

**VOICEVOX・Supertonic・Aivisも、現行のプロジェクト定義ではOpen JTalkの準備が前提。** packageだけを置いても、Open JTalkが無効な構成には同梱されない。判定・入力先は[Tanayomi.Desktop.csproj](src/Tanayomi.Desktop/Tanayomi.Desktop.csproj)を参照する。

Pythonは利用者側の必須環境ではない。上記TEMP設定をしたシェルで、用意したPython 3.12.14の実体から`& 'Python本体の絶対パス' -m venv .local/neural-openvoice/venv`を実行する。以後は`.local/neural-openvoice/venv/Scripts/python.exe`を使う。OpenVoice変換の固定依存導入コマンドは後述の`openvoice-export-requirements.txt`の説明を参照する。Aivisの製品用Python／wheelは別の固定入力であり、このvenvをそのまま製品へコピーしない。

Skia／OpenVoiceのC++再構築には、スクリプトが固定するMSVC 14.29.30037（compiler 19.29.30038.1）、Windows SDK 10.0.19041.0、Ninja、OpenVoiceではCMake 3.31.6が必要。Skiaスクリプトは既定でVS 2019 Build Toolsを参照し、別配置は引数で指定できる。Visual Studio最新版を入れただけで同じ成果物になるとは限らず、別ツールチェーンへの変更には再監査が必要。ビルドツールと再配布物の条件はTHIRD_PARTY_NOTICESを確認する。

これは全音声の自動bootstrapではない。Open JTalk原典、Aivis固定原本／wheel、ORTソース、監査済みDLLの採用配置などに手動工程が残る。Skiaのmanifestがない通常ビルドではNuGet由来のnativeを使用するため、監査済みアプリと同一の配布構成とは扱わない。DLL・manifestだけを別版へ差し替えたり、ハッシュ検査を無効にして合わせたりしない。

準備後は`./dev.ps1 test -Configuration Release`、`./dev.ps1 publish`、[合成方式の製品確認](#合成方式の製品確認)と固定入力節の対象製品試験を実施する。完成先は`.local/build/app/Tanayomi.Desktop.exe`。`resources/`の対象音声資産と画面の選択肢も確認する。検証済み出力の利用者用コピーは`./dev.ps1 tryout -UseExistingPublish`で新規作成できる。資産を変更した後に古いpublishを再利用しない。

### ビルド・実行・試験

#### 別マシンで資産の場所を指定する

`CURRENT_TASK.md`や過去ログの`audit-日時-ID`／`package-ID`は開発時の実在成果物を指す記録で、取得元ではない。別マシンでは上の手順で生成した成果物を使う。GitHubにこれらのローカルフォルダーは含まれない。

WORLD／Open JTalk／OpenVoiceの既定入力先は、それぞれリポジトリ内`.local/build/native-world`／`native-open-jtalk`／`native-openvoice`。別配置を使う場合は同じPowerShellで`$env:WorldNativeDirectory`／`$env:OpenJtalkNativeDirectory`／`$env:OpenVoiceNativeDirectory`へ実在する絶対パスを設定してから`dev.ps1`を実行する。音声原本と許諾・manifestを一式揃える。アプリの利用者データ`data/`をビルド入力に流用しない。

Aivisの既定入力先は`.local/vendor/aivis/package/`。準備スクリプトは安全のため毎回別の`package-<ID>/`を生成するので、出力された**自分のマシンの完成先**を以下へ指定する。以前の開発機固有のIDを指定する必要はない。

```powershell
# <ID>はprepare-aivis.ps1が実際に出力した名前に置き換える。
$env:AivisDirectory = (Resolve-Path '.local/vendor/aivis/package-<ID>').Path
if (-not (Test-Path (Join-Path $env:AivisDirectory 'voices.json'))) {
    throw '完成したAivis packageではありません。'
}
./dev.ps1 publish
```

環境変数は現在のシェル限り。次のシェルでも同じ完成先を指定する。未準備の音声が消える場合は、選択した資産の不足とOpen JTalkの有効条件を先に確認する。固定ハッシュの不一致はパス指定だけでは解決しないため、対象バージョンと原本を照合する。

SDK版は`global.json`、依存パッケージ版はプロジェクト定義と`packages.lock.json`で固定する。リポジトリ直下からPowerShell 7以降で[dev.ps1](dev.ps1)を使用する。

```powershell
./dev.ps1 build
./dev.ps1 run
./dev.ps1 test
./dev.ps1 preview
./dev.ps1 sample -SamplePath .local/previews/utau-review.wav
./dev.ps1 publish
./dev.ps1 tryout
```

`publish`はRelease・Windows x64・自己完結型を`.local/build/app/`へ出力し、`THIRD_PARTY_NOTICES.md`と`licenses/dotnet/`の.NET許諾文も含める。`tryout`はpublish後の一式を毎回新しい`.local/tryout/<yyyyMMdd-HHmmss>-<短ID>/`へ作成し、完成したexeのパスを表示する。通常の`build`・`test`・`publish`は利用者用コピーを変更しない。

既存の試用版・`data/`は変更・削除せず、旧版のアプリや音声ワーカーを終了しなくても新規版を作成できる。新規版の`data/`は最新の既存tryoutから、既存版がなければ`.local/build/app/data/`からコピーする。旧固定配置のtryoutも引き継ぎ元として認識する。`tmp/`と取り込み途中の`.import-*`はコピーせず、元データは保持する。引き継ぐのはコピー時点のデータで、作成後の旧版の変更は新規版へ同期しない。

一時stagingへアプリとデータを揃えてから固有フォルダーへ移す。作成失敗時はstagingを清掃し、既存版には手を加えない。

`build`・`run`・`test`・`preview`・`sample`は`-Configuration Release`も指定できる。開発時の通常ビルドは`.local/build/bin/`、中間生成物は`.local/build/obj/`へ集約し、`src/`・`tests/`へ生成しない。各実行ファイル横の`data/`は利用者データとして保持する。
容量を整理する場合、終了済みの開発・試験で使った `.local/build/bin/Tanayomi.Desktop/release/` と `.local/build/bin/Tanayomi.Tests/release/` の非`data`ファイルは再生成可能な出力。対象から起動中・参照中のプロセスがないこととリンクを確認したうえで整理し、`data/`は保持する。以後その開発exe/DLLを使う前に、リポジトリ直下で `./dev.ps1 build -Configuration Release` または `./dev.ps1 test -Configuration Release` を実行して再生成する。`build/app`・既存tryout・native監査資産とは別の出力である。

Windows x64 SDKでRIDを指定せず、Windows x64で起動する`Tanayomi.Tests`をビルドする場合、SkiaSharpとHarfBuzzSharpのネイティブ出力はWindows x64のDLLだけをコピーする。監査済みSkiaとVCランタイムは従来どおりハッシュを照合して配置する。他OSや別アーキテクチャーで試験するときは対象環境でビルドする。依存パッケージに含まれる監査用ファイルはNuGetキャッシュに保持される。

`dev.ps1`は`.local/dotnet/`のSDKがあれば使用し、開発用キャッシュとTEMPを`.local/`へ設定する。.NETとAvaloniaの任意のビルドテレメトリーは、その実行プロセス内で無効にする。Android／iOSのビルドには対応する.NET workloadとOS SDK、iOSにはMac・Xcode・署名環境が必要。

### PCの対象OSを指定する

`publish`と`tryout`は`-RuntimeIdentifier win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64`を受け付ける。省略時は従来の`win-x64`。例：`./dev.ps1 publish -RuntimeIdentifier linux-x64`。Windows以外の出力は`.local/build/publish/<RID>/`、起動ファイルは`Tanayomi.Desktop`。Windowsの既定出力は上記のまま。対応RIDの.NET自己完結runtimeと許諾を配置し、Windows専用のWORLD／Open JTalk DLLを他RIDへ混入させない。

`publish`は別OS向けにも出力できる入口だが、そのOSで動作確認したことを意味しない。macOSは未署名のフォルダー形式で、`.app`・署名・notarization・インストーラー作成は含まない。利用時は書き込める配置に置き、実行ファイル横`data/`を使う。Linuxでは上記の音声・認証コマンド等を別途用意する。

Unix向けWORLDの監査用入力は[native/build-world-unix.ps1](native/build-world-unix.ps1)で準備する。PowerShell 7から`./native/build-world-unix.ps1 -Rid linux-x64 -PrepareOnly`で、既存の固定WORLD ZIP・bridge・許諾文を検査できる。外部取得やcompiler実行は行わず、一時展開先`.local/tmp/world-unix-<ID>/`は終了時に清掃する。実ビルドは対象OS・CPUに一致するホストで`-PrepareOnly`を外し、`-CompilerExecutable`、`-NmExecutable`、`-DependencyInspectorExecutable`へ既存ツールの絶対パスを指定する。Linuxではコンパイルとリンクの両方で、ブリッジのスレッド処理に必要な`-pthread`を指定する。Linuxの依存検査には`readelf`互換、macOSには`otool`互換のツールを使う。RIDは`linux-x64`／`linux-arm64`／`osx-x64`／`osx-arm64`。新規`.local/build/native-world-unix/audit-<RID>-<日時>-<ID>/`へライブラリー、許諾文、compiler・symbol・依存ログ、未採用を示すmanifestを出力する。通常アプリへ自動コピーしない。完成した共有ライブラリーのELF/Mach-OヘッダーでRIDと種類を検査し、公開された9個のWORLD関数を照合する。ABI呼出し・リンク依存・再配布条件とUnix実ビルドは未確認で、Windowsでは準備処理・構文・ホスト不一致拒否のみ確認している。
他OS向け`tryout`は対象OSと実行ホストが一致する場合だけ使い、`.local/tryout/<RID>/<日時>-<短ID>/`へ新規作成する。`deps.json`のRIDが一致する既存版だけをデータ引き継ぎ元とし、異なるOSの認証や設定を混ぜない。UnixではローカルSDKの`dotnet`と`TMPDIR=.local/tmp`を使い、起動ファイルの実行権限を引き継ぐ。別OSでの実行確認状況はPROJECT_STATUSを参照する。

### モバイルの開発ビルド

入口は`./dev.ps1 build -ProjectTarget Android`／`./dev.ps1 build -ProjectTarget iOS`。iOSはMacが必要。スクリプトは対象workloadの登録と実SDK packを検査し、Androidでは既存SDKの`ANDROID_HOME`または`ANDROID_SDK_ROOT`とJDKの`JAVA_HOME`、iOSでは選択済みのフルXcodeを確認する。不足時はrestore前に理由を示して停止し、workload・SDK・署名情報を自動で取得・変更しない。

初回のモバイル依存ロックは、対応する環境を用意した後、リポジトリ内のCLI home／NuGet／TEMPを上記スクリプトと同じ場所へ設定し、対象プロジェクトに`dotnet restore src/Tanayomi.Android --use-lock-file`（iOSなら対象名を変更）を行い、解決した版と許諾を確認して保存する。通常buildはロックを必須にする。出力は他の開発ビルドと同じ`.local/build/`。AndroidのAPK作成・署名配布、iOSの実機／Simulator指定・署名配布は、この入口だけで確立済みとはしない。

### 検証と一時出力

`dev.ps1 test`は`tests/Tanayomi.Tests/`の試験を実行する。入力はリポジトリ内のソース・同梱音源・辞書。PowerShell 7以降と固定版.NET SDKを使用し、試験結果は文字出力で確認する。fixtureやモデル用音源は`.local/tmp/`の一意な試験領域へ置き、終了時に成功・失敗とも清掃する。通常の試験で合成サンプル音声を自動保存しない。

`dev.ps1 preview`はランナーの`--render`を使って画面を描画し、PC幅・390px幅の画像を`.local/previews/`へ保存する。Supertonicの同意画面も`supertonic-terms-closed/open/narrow.png`へ出力し、展開時は規約本文と同意ボタンが見える位置へスクロールする。準備済みの規約全文がある場合は固定SHAで照合して使用し、未準備なら試験用本文を使う。これは明示的な確認用出力としてレビュー後に削除する。

`dev.ps1 sample -SamplePath .local/previews/utau-review.wav`は同梱音源で固定の確認文を合成し、指定した新規WAVへ保存する。保存先は`.local/previews/`内に限定し、既存ファイルを上書きしない。次の確認では別名を指定する。実行時に確認文と保存先を表示し、自動再生は行わない。音声確認後に出力を削除する。通常のアプリ操作や既定試験では音声をファイル保存しない。

長音の粗さを切り分ける対照は、同じsample操作に`-WithoutPitch`を付けて別名で作成する。音源・文章・長さ・接合は同じまま音高補正だけを省く開発用対照で、アプリの既定動作や利用者設定は変えない。

実サービスの受信確認では指定チャンネルへ接続し、必要な試験文はユーザー本人が投稿する。受信・重複防止・音源切り替え後の読み上げを確認する。アプリから投稿する試験は行わない。

### 合成方式の製品確認

Windows版の内蔵UTAU「シンプル（波形接続）」は、句読点で区切れる長文を先読みし、句全体のピッチ曲線を原音の有声部分へ適用する。比較時は起動プロセスで `TANAYOMI_UTAU_PREFETCH_TRIAL=0`、`TANAYOMI_UTAU_CONTINUOUS_PITCH=0`、`TANAYOMI_UTAU_VOICED_JOIN=0` を個別に指定できる（旧位相接続の設定はピッチ曲線が無効のときに適用）。開発確認はテスト実行ファイルの `--utau-prefetch-trial-check`（PCM受渡し）と `--utau-voiced-join-check`（旧方式の継ぎ目）と `--utau-continuous-pitch-check`（最終波形の基本周波数、促音、先読み）と `--utau-overlap-envelope-check`（重なり、原音の音頭）を使う。音量の新旧比較では `TANAYOMI_UTAU_COMPLEMENTARY_ENVELOPE=0` と `TANAYOMI_UTAU_ATTACK_ENVELOPE=0` を使い、連続ピッチを維持したまま無効化できる。名詞のアクセントだけの比較は `TANAYOMI_UTAU_LEXICAL_ACCENT=0`（専用確認 `--utau-lexical-accent-check`）。弱い先行部分の接続だけを以前と比較するには `TANAYOMI_UTAU_LEAD_HANDOFF=0`（専用確認 `--utau-lead-handoff-check`。任意の第2引数は既存のWAV出力先）。

Windows標準音声の増幅経路は、リポジトリ直下で `./dev.ps1 windows-volume-test -Configuration Release` を実行する。実SAPIをメモリーへ合成し400%の波形を確認する。音声の再生・ファイル保存は行わない。Windowsと利用可能なSAPI音声が必要。


波形方式は、リポジトリ直下で `./dev.ps1 utau-context-product-test -WaveformOnly -OpenJtalkExecutable '対象exeのパス'` を実行する。Timing／Psola／PhaseVocoder／DirectPsola各5文、計20件を180秒以内で確認する。OpenVoiceだけの5文確認には同じコマンドの `-WaveformOnly` を `-NeuralOnly` に替える。全内蔵方式は `-UtauModes` で確認する。無再生・音声保存なしで、一時fixtureは終了時に清掃する。

NIT＋OpenVoiceの長文間延びを切り分ける明示診断は、開発版publish後にリポジトリ直下で `./dev.ps1 openvoice-long-phrase-diagnostic -Configuration Release` を実行する。報告された固定文の同一NIT音声を変換し、変換前後のWAV2本と音素時間のconditions.jsonを `.local/previews/` の新規領域へ保存する。音声は自動再生しない。子プロセスの一時物は終了時に清掃し、出力比較物は原因調査・利用者確認が済むまで保持する。

### 読み辞書を再生成する

[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md#日本語の読み辞書採用)に記載した固定版の`naist-jdic.csv`・`matrix.def`・`COPYING`を`.local/dictionary-source/`へ置き、リポジトリ直下から[変換スクリプト](assets/build-reading-dictionary.py)を実行する。Python標準ライブラリーだけを使用する。

```sh
python assets/build-reading-dictionary.py
```

`assets/naist-jdic.bin.gz`と`assets/naist-jdic.COPYING`を再生成する。続けて `python assets/build-reading-accents.py` で同じ固定版と読み辞書に対応するアクセント表を再生成する。派生辞書と許諾文を保持し、版や内容を変更する場合は第三者表示も更新する。

### WORLD比較用のネイティブビルド

旧MSVC構成の比較用DLLは、Windows x64とVisual Studio 2019 Build Tools C++環境で`./native/build-world.ps1`により生成する。固定コミット・ZIP SHA-256を確認し、`.local/vendor/world/`へソース、`.local/build/native/`へDLLとビルド情報を置く。中間ファイルは`.local/tmp/`で処理後に消去する。`WorldComparison=true`の現行比較はこの旧出力を使わず、`WorldNativeDirectory`（既定`.local/build/native-world`）の採用DLLと3件の許諾文をコピーする。MSVC静的ランタイムの条件は別途判断を要し、この出力を通常版のDLLへ流用しない。

[native/compare-world.ps1](native/compare-world.ps1)はPowerShell 7から既存の44.1kHz mono PCM16 WAV（100ms〜30秒）を比較する。例：`./native/compare-world.ps1 -InputWave .local/previews/source.wav -OutputPrefix .local/previews/world-source`。元F0と一定F0の同長WAV2件、計測JSONを接尾辞`-original.wav`／`-flat.wav`／`-result.json`で新規作成する。原本と既存出力は変更しない。`-FlatF0 300`のように40〜1200Hzを指定でき、省略時は有声F0中央値を使う。

比較のmanagedビルドは`.local/build/world/`へ分離し、既存exeのワーカーだけでDLLをロードする。SDK・コンパイラーの常駐再利用をこの操作だけ無効にし、子のTEMPは専用領域へ指定する。通常版・tryoutを変更しない。ワーカーは120秒で打ち切り、完成音声のコピー後にexe横の一意jobとビルド一時領域を清掃する。計測・聴取が終わった比較出力は削除する。

`-Utau -OutputPrefix .local/previews/world-direct`は同梱音源の原音から固定の比較文を直接分析・配置・合成する別の比較経路。InputWaveとは同時に指定しない。既定259.4256Hz、`-FlatF0`で変更できる。通常のUTAU合成や採用方式を切り替える操作ではない。

`-Prosody -OutputPrefix .local/previews/world-prosody`は同梱音源の既存B合成と実配置時刻を使い、一定音程・句成分のみ・手指定アクセントを加えた3条件を比較する。`InputWave`／`Utau`とは排他。既定259.4256Hzを有声時間平均に合わせ、同じ分析結果から各条件を1回ずつ合成する。出力は`-flat.wav`／`-phrase.wav`／`-accent.wav`／`-result.json`。計測JSONには配置時刻・音程範囲・音量調整を記録する。通常版の抑揚設定を変更する操作ではない。

`-Timing -OutputPrefix .local/previews/world-timing`は通常音の間隔180ms／140msだけを変える比較。長音の追加分160ms、子音の配置、促音・休止を据え置き、各入力を同じ一定音程で合成する。`InputWave`／`Utau`／`Prosody`とは排他。2本は長さが異なるため、それぞれ別に分析し、発話全体のRMSと共通headroomを合わせる。通常版の速度を変更する操作ではない。

`./dev.ps1 joins`は同じ固定公開文のB合成で、実際の有声判定・フェード長と、接合後／次音単体のRMSを標準出力へ表示する。値は発話全体の音量調整・PCM化・WORLDより前のもの。音声を再生・保存せず、比較用音源コピーは終了時に清掃する。実音響の音素境界を検出する処理ではない。

`-Join -OutputPrefix .local/previews/world-join`は、純母音への接合で前の音を残す長さだけを変える比較。他の比較モードとは排他。180ms・一定音程・同じ配置時刻と長さを保ち、各入力を別に分析して発話全体のRMSと共通headroomを合わせる。出力は`-baseline.wav`／`-vowelcarry.wav`／`-result.json`。通常版の接合を変更する操作ではない。

`-Context -OpenJtalkAudit .local/build/open-jtalk/<監査出力> -OutputPrefix .local/previews/world-context`は、固定公開文のHTS音素計画を同じUTAUの読みへ照合し、文脈時間・相対音程を使う比較。`toj_plan`を含む監査DLLが必要で、他の比較モードとは排他。読みの不一致や保持できない原音の時間は拒否する。出力は`-baseline.wav`／`-context.wav`／`-result.json`で、長さの違い・実配置・モデル時間・F0・音量を記録する。固定文の短い「て」1箇所だけ、原音SHA-256とOTOが一致する場合に測定した境界・使用長を適用し、使用範囲をJSONへ残す。一般の音源に自動適用する機能ではない。通常アプリへ音源や機能を追加する操作ではない。 `-ContextPitch`を追加すると、同じ文脈時間・原音配置の一定F0／文脈F0だけを比較する。長さ・SP/AP/VUVのhash・anchor一致を検査し、音量を揃える。 `-ContextOriginalTiming`は通常180msの配置を保った同長の抑揚比較で、原音境界の特例を使わない。`-ContextPitch`とは排他。

`-ContextDuration`を`-Context`へ加えると、固定文「てすとです」の現行配置と、モデル母音時間＋実際の次音先行長の配置を比較する。原音の時間制約を下回る場合だけ、rendererが示す下限へ引き上げる。語句別例外や原音境界の変更は行わない。`-ContextPitch`／`-ContextOriginalTiming`とは排他。`-ContextConsonants`を追加するとモデルの子音長を先行部へ割り当て、母音時間と分ける。0msの子音を予測した母音単独unitは原音の先行部を保持する。比較専用で通常版を変更しない。`./native/build-open-jtalk.ps1 -VoicedPlanComparison`は、HTSラベルの5音素文脈の無声化母音を有声へ置き換える別の計画実験DLLを新規監査先へ作る。既定の製品DLLへコピーしない。

[native/build-world-llvm.ps1](native/build-world-llvm.ps1)は別ツールチェーンによるWindows DLLのリンク・許諾監査用。初回は`./native/build-world-llvm.ps1 -DownloadToolchain`で固定版llvm-mingwを`.local/vendor/llvm-mingw/`へ取得・ハッシュ照合する。以後は引数なし（O2）で再構築できる。`-Optimization O3`は厳密な浮動小数点設定を維持した比較監査用で、通常版へ自動採用しない。指定水準はmanifestへ記録する。出力は毎回`.local/build/native-llvm/audit-日時-ID/`へ分離し、DLL・リンクMAP・使用ヘッダー・PE依存・ビルド情報を保持する。コンパイル成功だけで配布条件の確認済みとはせず、条件はTHIRD_PARTY_NOTICESを参照する。

実DLLの回帰確認は`./dev.ps1 native-test -WorldLibrary .local/build/native-llvm/<監査出力>/tanayomi-world.dll`で行う。ビルド記録のハッシュを照合し、通常テストとは別に、無再生の実音源・一定音程・基準の再利用・取消後の復帰・ワーカー終了を確認する。試験用に置いたDLLは終了時に削除する。隔離したF0 audit子（180秒上限）では、同梱基準文の原音解析cache有無を各1warmup/3組交互で比べ、生成PCM一致・解析回数・時間を表示する。続いてHarvest/DIOのfull解析/F0専用についてF0・時刻・中央値・応答を照合する。任意の並行解析exportがある場合はHarvest/DIOの直列/並列で全分析値と合成PCMの一致・不正入力の拒否も確認し、各1組の時間を表示する。新exportのない旧DLLも従来経路で確認できる。全体の発声開始時間や反復平均と混同しない。

同じコマンドに`-WorldParallelResources`を加えると、通常回帰の代わりに資源比較だけを行う。直列/並列はそれぞれ新規の非表示子（各180秒上限）で同梱基準文を1回解析し、単一の結果配列、同じ計時外GC条件で時間・private/working set・全分析値のdigestを比較する。DLLロードと基準文生成は計時外。peak working setは基準文生成も含むプロセス全期間の値であり、native単体の確保量ではない。音声は保存・再生せず、子終了後にfixtureと一時DLLを清掃する。他のactionとの併用は拒否する。

同じnative-testに`-WorldLifterAudit`を加えると、同梱基準文のHarvest/DIO F0専用解析から、固定CheapTrickのfloor適用後F0の連続一致数・既定F0のframe数・最長連続区間を表示する。`-WorldParallelResources`との併用は拒否する。係数再利用の対象割合を調べる診断で、速度計測や通常発言の代表性を保証するものではない。既存のDLL監査・180秒隔離・fixture清掃を共有し、音声を再生／保存しない。実装はtests/Tanayomi.Tests/WorldIntegrationTests.cs。

通常のWindows x64構成には、監査・回帰確認したLLVM版の`tanayomi-world.dll`と`world-build.json`を`.local/build/native-world/`へ配置する。別の配置はMSBuildの`WorldNativeDirectory`で指定できる。DLLが存在する構成では後段の一定音程処理を有効化し、DLLとWORLD・Ooura・runtimeの3件の許諾文をビルド／publishへコピーする。利用者はexeの直接起動だけで使える。DLL未配置のソースビルドや他OSは従来の合成方式を維持する。WORLDのRID・プロセスarchitecture・ライブラリー名は`WorldNativePlatform`で一元判定する。他OSの名前定義はあるが、監査済み資産がまだないため任意のローカルファイルやmanifestを置いても有効にならない。生成DLLをsrcへ置かず、再生成時のリンク差分を監査してから配布対象を更新する。

## Open JTalkの固定入力

再利用する公式入力は`.local/vendor/open-jtalk/`の`open_jtalk-1.11.tar.gz`、`open_jtalk_dic_utf_8-1.11.tar.gz`、`hts_engine_API-1.10.tar.gz`、`hts_voice_nitech_jp_atr503_m001-1.05.tar.gz`。取得元・固定サイズ・SHA-256・条件は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md#追加音源の条件調査詳細)から辿れる。作業場所はリポジトリ直下で、既存入力の照合は`Get-FileHash .local/vendor/open-jtalk/*.tar.gz -Algorithm SHA256`で行う。アーカイブ自体は開発用の取得入力として保持する。アプリ一式には、準備したDLL・辞書・モデルをresourcesへ別途配置する。再取得では対象の公式リンクを使い、既存ファイルを上書きせずhashを照合する。

辞書binaryは展開すると約107.3MB（`sys.dic`だけで約103.1MB）、モデル本体は約1.17MB。製品へ含める場合は辞書も配布サイズに加わり、モデルだけでは動かない。既存のUTAU用読み辞書は情報・形式が異なり、そのまま代用しない。

監査用のWindows x64 DLLは、PowerShell 7でリポジトリ直下から`./native/build-open-jtalk.ps1`を実行して生成する。既存の`.local/vendor/llvm-mingw/llvm-mingw-20261020-msvcrt-x86_64`と同名ZIPが必要で、追加SDKの取得やグローバル環境の変更は行わない。固定入力・展開toolchainをhash照合し、推論専用ソースをRelease相当（`-O2 -DNDEBUG`）でコンパイルする。出力は毎回新しい`.local/build/open-jtalk/audit-<日時>-<ID>/`で、DLL・辞書・モデル・許諾文・実リンクmember・PE依存・build JSONを含む。展開ソースと中間objectは`.local/tmp/`へ一時配置し終了時に清掃する。通常build・publish・試用コピーは更新しない。この操作はDLLの生成と静的監査までで、合成や音声再生は実行しない。

Linux x64／ARM64（glibc）の監査準備は、PowerShell 7でリポジトリ直下から`./native/build-open-jtalk.ps1 -Rid linux-x64 -PrepareOnly`を実行する。ARM64では`-Rid linux-arm64`を指定する。Windowsでも、`.local/vendor/open-jtalk/`の固定4アーカイブのhash照合、安全な展開、共通パッチの適用、推論用ソースの確認まで行える。Linux向けのコンパイル定義と必要なツールを標準出力に表示し、一時展開は終了時に清掃する。iconvを使わないMeCabの恒等変換は固定UTF-8辞書に限定する。

実ビルドはRIDとホスト・プロセスのCPUが一致するLinux環境のPowerShell 7で`-PrepareOnly`を外し、`-CompilerExecutable`、`-NmExecutable`、`-DependencyInspectorExecutable`に既存のClang++、nm、readelf互換ツールの絶対パスを指定する。新しい`.local/build/open-jtalk-linux/audit-<日時>-<ID>/`に、未採用の共有ライブラリー、辞書、音声モデル、許諾文と監査ログを出力する。ELF形式・対象CPUと公開C ABIの5関数を照合し、依存を記録する。Linux上の実ビルドとロード・合成、および実行時ライセンスの監査は未確認。製品へは自動採用しない。

macOSの監査準備は`./native/build-open-jtalk.ps1 -Rid osx-arm64 -PrepareOnly`で行う。Intel Mac向けには`-Rid osx-x64`を指定する。実ビルドはRIDとホスト・プロセスのCPUが一致するmacOSで実行し、`-PrepareOnly`を外して既存のClang++、nm、otool互換ツールの絶対パスと、`-MacOsSdkPath`に既存macOS SDKの絶対パスを指定する。SDKの識別情報と必要なヘッダー・リンク用ファイルを確認し、Clang++に対象CPUとSDKを明示する。出力先は`.local/build/open-jtalk-osx/audit-<日時>-<ID>/`。Mach-O dylibの形式、CPU、公開C ABIの5関数とシステム依存を検査する。macOSでの実ビルド、ロード・合成、実行時ライセンスの監査は未確認で、製品へは自動採用しない。

Unixのライブラリー監査判定は、リポジトリ直下のPowerShell 7で`./tests/open-jtalk-audit.ps1`を実行して確認できる。模擬ヘッダー・シンボル・依存出力を使い、ネイティブツールは起動しない。

追加HTSモデルの固定入力は`.local/vendor/tohoku-f01/8e3306021db135c265f5eda5f062dc489707ddf8/`内の`tohoku-f01-neutral.htsvoice`、`tohoku-f01-angry.htsvoice`、`tohoku-f01-happy.htsvoice`、`tohoku-f01-sad.htsvoice`。原典とSHA-256は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md#追加音源の条件調査詳細)を参照する。固定版ファイルと`assets/tohoku-f01-LICENSE.txt`・`assets/tohoku-f01-NOTICE.txt`が揃うWindows Open JTalk構成で、モデルを`resources/open-jtalk/`、許諾を`licenses/`へコピーする。実行時もモデルの固定SHAを確認する。Open JTalk bridgeはモデル3MiBまでに対応する監査済み版が必要。利用者が外部エンジンを用意する手順はない。

Mei 1.5の5音声とTakumi 1.0の4音声は、リポジトリ直下で `./native/prepare-mmdagent-voices.ps1` によりMMDAgent Example 1.8公式ZIPから準備する。公式SHAと各モデルSHA・HTS形式を照合し、音声と原典README/COPYRIGHTだけを `.local/vendor/mmdagent/1.8/` へ置く。取得済みZIPは `-ArchivePath <リポジトリ内の絶対パス>` で指定できる。PowerShell 7/.NET標準機能を使い、新規一時領域は成功・失敗とも清掃する。通常ビルドは同ディレクトリーの9モデルと `assets/mmdagent-*-COPYRIGHT.txt`、`assets/mmdagent-voices-NOTICE.txt` を既存のresources/許諾へ配置する。実体と表示条件は[THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)を参照。MMDAgent本体・3D素材は配布しない。

VOICEVOXの内蔵Windows CPU構成は `./native/prepare-voicevox.ps1` で固定CORE・専用Runtime・対象VVM・VC Runtimeと許諾文を取得し、`.local/vendor/voicevox/package/` へ準備する。取得済み入力だけを使う確認は `-Offline`。PowerShell 7.4以降が必要で、公式資産のSHA・DLL依存を照合し、辞書は上記のOpen JTalk辞書を共用する。通常Windowsビルドはこのpackageを `resources/voicevox/` へ配置する。対象話者は `src/Tanayomi.Desktop/VoicevoxCatalog.cs` の固定一覧で、別エンジンの起動やインストールを要求しない。

Supertonic 3はリポジトリ直下で `./native/prepare-supertonic.ps1 -Download` により固定sherpa-onnx 2.0.1・INT8モデル・許諾・対応ソースを準備する。開発用Pythonは既定の `.local/neural-openvoice/venv/Scripts/python.exe`、PE検査は `.local/vendor/llvm-mingw/llvm-mingw-20261020-msvcrt-x86_64/bin/llvm-readobj.exe`、VC Runtimeは上記VOICEVOXの準備済み資産を使う。必要なら `-Python`・`-ReadObj`・`-CrtDirectory` でリポジトリ内の実体を指定する。取得済み入力で再検査する場合は `-Download` を外す。出力 `.local/vendor/supertonic/prepared-md-vc143/` のモデル・DLL・licensesだけを通常Windowsビルドで `resources/supertonic/` へ配置する。

AivisSpeechのWindows CPUパッケージは `./native/prepare-aivis.ps1 -BuildPython .local/neural-openvoice/venv/Scripts/python.exe` で準備する。固定原本とwheelを `.local/vendor/aivis/` に用意し、`native/aivis/wheels.json`・`native/aivis/voices.json`・スクリプト内のSHAと照合する。入力一覧・取得元は同スクリプトとlockを参照。完成先は毎回固有の `.local/vendor/aivis/package-<ID>/`。再生成した場合はそのシェルで `$env:AivisDirectory = (Resolve-Path '.local/vendor/aivis/package-<ID>').Path` を設定して `./dev.ps1 publish` または `tryout` を実行する。Python・前処理ソース・BERT・音声・許諾・対応ソースを `resources/aivis/` へ配置する。開発側のarchive-auditは配布対象外。使用する上流処理との差分は同梱NOTICEに記載する。利用者側のPython・エンジン導入は不要。

組立後のAivis検証は、リポジトリ直下でTEMP/TMPを`.local/tmp/`へ設定し、`native/process-error-mode.ps1`のエラーモードを継承した非表示プロセスで `.local/neural-openvoice/venv/Scripts/python.exe -B native/aivis/verify_worker.py <完成packageの絶対パス>` を実行する。同梱PythonによるPCM量子化の境界/最大長/不正値、過大要求の拒否、全8音声×2文を確認し、音声を再生・保存せず一時領域を清掃する。

JVNVのF1/F2/M1/M2を追加する場合は、上記Aivisパッケージを前処理環境として `./native/aivis/prepare-jvnv.ps1 -AivisPackage <packageのパス> -BuildPython .local/neural-openvoice/venv/Scripts/python.exe` を実行する。固定入力は `native/aivis/jvnv-models.json` と `native/aivis/jvnv-conversion-wheels.json`、出力は `.local/vendor/aivis/jvnv/converted-<ID>/`。元のモデル12点を用意してSHAを照合し、変換専用のPyTorchを一時領域へ展開して終了時に清掃する。変換出力を `./native/prepare-aivis.ps1 -BuildPython .local/neural-openvoice/venv/Scripts/python.exe -JvnvDirectory <convertedのパス>` へ渡すと8音声パッケージを作成できる。PyTorchや元のsafetensorsは製品へ含めない。

同梱エンジンの完成exe確認は `./dev.ps1 bundled-speech-product-test -Configuration Release -OpenJtalkExecutable C:/work/tanayomi/.local/build/app/Tanayomi.Desktop.exe`。既存試験ランナーから非表示の隔離ワーカーを起動し、同梱されたVOICEVOX・Supertonic・Aivisの全選択肢のPCM、準備・モデル切替・取消後の再起動・不正入力拒否・終了を確認する。一方式だけ変更した場合は `-BundledEngine aivis|voicevox|supertonic` で対象を絞れる。生成音声の再生と保存はしない。通常の `test` はモデル取得やnative起動なしでプロトコル・選択・復元・規約同意の保存を検証する。

実nativeの確認は`./dev.ps1 open-jtalk-test -OpenJtalkAudit .local/build/open-jtalk/<監査出力>`で行う。既存のテストランナーから非表示の子プロセスを起動し、日本語パスからの読み込み、PCM合成、入力拒否と障害後の扱いを確認する。辞書・モデルは所有する一時領域へコピーし、子の終了を確認してから清掃する。音声の再生・保存は行わず、通常の`test`ではnativeライブラリーを要求しない。

通常Windows x64ビルドで同梱する入力先は`.local/build/native-open-jtalk/`。監査とnative確認を通した出力から`tanayomi-open-jtalk.dll`、`nitech_jp_atr503_m001.htsvoice`、`dictionary/`の`char.bin`・`matrix.bin`・`sys.dic`・`unk.dic`をコピーし、SHA-256一致を確認する。`open-jtalk-build.json`には監査JSONの`dllSha256`と`abiVersion: 1`のみを保持し、開発機のパスやコンパイルログを製品へ入れない。既存入力を変更する場合は使用中の参照を確認してから更新する。

この入力があると`./dev.ps1 build`／`tryout`は`resources/open-jtalk/`へ必要ファイルと`licenses/`へ5件の許諾文を配置する。辞書は約107MB。入力がなければ選択機能を含めず、他OS向けビルドにもWindows DLLを混入させない。利用者側のSDK・外部エンジンの導入は不要。

同梱後の実ワーカーは`./dev.ps1 open-jtalk-product-test -OpenJtalkExecutable .local/build/bin/Tanayomi.Desktop/debug/Tanayomi.Desktop.exe`で確認する。先に対象構成をビルドし、固定公開文2件のPCMを冷起動と準備済みの別ワーカーで照合し、文脈計画の形式・再利用、不正フレームの拒否、子の終了も検証する。再生・音声ファイル保存は行わない。

固定文「てすとです。」の単音伸縮診断は`./dev.ps1 utau-pronunciation-comparison -Configuration Release`。事前に開発用`publish`を行う。`.local/build/app/Tanayomi.Desktop.exe`の現行Context処理から、最初の「す」の実使用原音区間とShapeMora直後（音程処理・接合・WORLD前）の単音を同一呼出で取得する。`.local/previews/utau-pronunciation-<日時>-<ID>/`へ現行発話・伸縮前単音・伸縮後単音の3WAVと条件JSONを新規保存。連続有声位置・時間予算等の条件が成立する場合だけ、その前後20msを名目等速で残す診断候補D-acoustic-transition-su.wavも追加する。不成立理由は条件JSONに記録し、代替波形を候補として保存しない。移行保護のみの全文E-adopted-context.wavに加え、単音内の時間配分規則を通したF-unit-rules-su.wavと全文G-unit-rules-context.wavを保存する。単音D/Fは同じ録音を使うが、Fは切り出し直前の摩擦候補の取り込みと必要な単音長の確保を行うため、使用範囲・総長が異なる場合がある。全文E/Gの長さも変わり得る。復元sample数・元と実際の長さ・内部母音位置・配置用leadを条件JSONで区別する。基準Aは改善処理を内部比較用に無効化して取得する。条件JSONにはOTO前後60ms内の最大21窓による連続有声位置の観測も含む（音素境界の確定ではない）。原音区間Bと旧伸縮Cは長さが異なる。各音声を同一係数でPCM変換し、音量正規化・自動再生はしない。原録音ファイル全体ではなく、読み込み時のDC除去等を経て実際に伸縮へ渡した区間の比較。全体3分上限、所有子の終了を確認して`.local/tmp/tests-*`のfixtureを清掃する。比較音声は聴感確認後に削除する。

固定文の文脈摩擦候補は`./dev.ps1 utau-devoicing-comparison -Configuration Release`。事前に開発用`publish`を行う。採用済み単音規則を共通基準に、既存モデルの無声化印があるs-U-t/kだけを対象にした実験を比較する。原録音の摩擦区間と後続閉鎖を確認できない場合は変更しない。後続子音の切り出し前に連続した弱い区間があれば最大60ms内で回収し、同じ閉鎖条件を満たす再描画と摩擦処理をペアで採用する。原音と絶対OTO位置を保ち、後続PCMが変わる場合は条件JSONへ記録する。「てすとです。」「すきです。」と有声・句末対照の4文を生成し、長さ・配置・退避・変更有無を照合する。`.local/previews/utau-devoicing-<日時>-<ID>/conditions.json`へ記録し、先頭文で候補が成立した場合だけA-accepted-context.wav／B-context-friction.wavを保存する。原音の無声母音調音を復元したものではない。Aは文脈接続を比較用に無効にした単音規則版、Bは通常のContext処理で生成する。このコマンド自体は設定を変更せず、音量正規化・再生もしない。全体3分上限、所有worker終了後にfixtureを清掃する。音声と条件は評価後に整理する。
OpenVoiceの参照集約比較は`./dev.ps1 openvoice-reference-comparison -Configuration Release`。事前に開発用`publish`で`.local/build/app/Tanayomi.Desktop.exe`と承認済みモデルを揃える。利用者の試用版は使わず、完成exeのNIT/声質変換workerから、3固定文の同一donorを原音単位A／約10秒群Bで変換する。`.local/previews/openvoice-reference-<日時>-<ID>/`へ6WAVと`conditions.json`を毎回新規作成し、自動再生しない。全体4分上限、音源fixtureは`.local/tmp/`、製品worker所有rootは開発exe横`data/tmp/nv-*`に限定し、終了確認後に清掃する。音声は聴感確認まで保持し、確認後に削除する。製品の既定参照処理は変更しない。
通常UTAUの文脈合成は、PowerShell 7で`./dev.ps1 utau-context-product-test -OpenJtalkExecutable .local/build/bin/Tanayomi.Desktop/debug/Tanayomi.Desktop.exe`により確認する。先にWORLDとOpen JTalkを含む対象構成をビルドしておく。この操作自体はビルド・復元・publishをせず、指定済みexeの非表示workerを60秒上限で実行する。「こんにちは。」「ありがとう。」「てすとです」「すきです」「みずです」「てすとですね」の文脈合成と、固定の長い確認文の通常速度への退避1回（文脈の抑揚は保持）を検査し、長さ・ピーク・退避回数・処理時間、および固定文の時間配分のJSON（最大16KiB）と成功結果を表示する。新しい対象exeは7件、旧版は4件の応答を識別する。音声は再生・保存しない。

検証用音源は指定exe横の`data/tmp/context-<一意ID>/`だけへ展開し、子の終了とパイプの閉鎖を確認後に成功・失敗とも清掃する。中止・期限超過時は今回起動した子ツリーだけを停止し、終了確認は3秒で打ち切る。終了を確認できない場合は一時領域を保持して場所を表示する。既存の試用版やその利用者データは変更しない。

全UTAU方式の固定文比較は同じコマンドへ `-UtauModes` を追加する。対象exeで利用可能な各方式×5件（4文と問題文の再実行）を非表示workerで測定し、上限180秒・応答16KiBを適用する。方式別の生成時間・音声長・ピーク・退避回数と、追加波形方式が時間予測のみのPCMから変更したサンプル数を表示し、音声は再生／保存しない。初回には準備時間が含まれ、後の方式は共通辞書と原音キャッシュが温まるため、準備後の同一文と初回を分けて読む。

OpenVoiceだけを検証する場合は同じコマンドへ`-NeuralOnly`を指定する。`-UtauModes`とは併用しない。対象exeに監査済みのOpenVoice資産とNIT日本語資産が必要で、未準備なら成功扱いにせず失敗する。最初に選択音源を事前準備し、その時間を`PreparationMilliseconds`へ分離する。固定5件・上限180秒、音声の再生／保存なしという条件で、子ワーカー経由の変換と同文の再利用を確認する。`NeuralStages`は資産検査／参照PCM／NIT／変換の待機時間の内訳で、通常チャットのファイル記録を追加しない。準備済みのNIT生成と参照検査を重ねる経路では、参照段が終わるまでの時間を`ReferencesMilliseconds`、その後に残ったNIT待機と入力検証を`DonorMilliseconds`へ記録し、重複時間を加算しない。後者をNIT生成自体の所要時間と解釈しない。同文・同じ準備済みワーカー・同参照で再合成を省いた場合はDonorMillisecondsが0になる。固定5件の最後の同文では直前のPCMとの完全一致も確認する。これは声の自然さの合格判定ではない。

Windows x64で同梱するOpenVoice資産の入力先は`.local/build/native-openvoice/`（MSBuildの`OpenVoiceNativeDirectory`で変更可能）。監査した`reference.onnx`、`converter.onnx`、`onnxruntime.dll`、VC142の3 DLL、portableな`openvoice-build.json`、許諾文・Eigen対象ソースZIPを揃える。具体的な固定版・hash・必要表示は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)を参照する。通常build／publishはモデルと実行物を`resources/openvoice/`、許諾・対象ソースを`licenses/openvoice/`へ明示コピーする。Windows以外の出力へこれらのDLLをコピーしない。

モデルの再変換は開発専用Python 3.12.14のvenvを`.local/neural-openvoice/venv/`へ用意し、シェルのTEMP/TMP/TMPDIRを存在する`.local/tmp/`の絶対パスへ設定して行う。固定依存は[native/openvoice-export-requirements.txt](native/openvoice-export-requirements.txt)。そのvenvのPythonで`-m pip install --no-deps --require-hashes --cache-dir .local/neural-openvoice/pip-cache -r native/openvoice-export-requirements.txt`を実行する。PyTorch等は変換専用で、製品へコピーしない。[export_openvoice.py](src/Tanayomi.Desktop/Neural/export_openvoice.py)へ`--root .local/neural-openvoice --prepare-inputs`を渡すと固定source・重みの不足分だけを取得・hash照合する。同じコマンドから`--prepare-inputs`を外すと新規`onnx/`へ2graphと許諾・manifestを出力する。既存出力・不一致入力を上書きしない。

### 取得に失敗した場合の固定入力

**Supertonic**：ONNX Runtimeの固定許諾2件のURLが404になるため、検証済み原本を[LICENSE](assets/supertonic-ort-LICENSE.txt)と[第三者表示](assets/supertonic-ort-NOTICES.txt)としてソースへ同梱する。`prepare-supertonic.ps1`は不足時に補完し、従来のSHAを検査する。既存の破損入力は上書きせず停止する。他の入力取得には従来どおり`-Download`を使う。

**Skia**：主ソースはSkiaSharp ZIPではなく[mono/skia固定commit](https://github.com/mono/skia/tree/7dbfc07dd33181f84e0958afb7ee805c6c769f0b)。`build-skia.ps1 -Action Acquire`が依存も取得する。主tar.gzのSHA256は`5265180696746618C344ACDA2916C9AD1022A0313289018197876CF2612743A9`。別形式のZIPや別版を改名して代用しない。

C++環境はMSVC `14.29.30037`（cl FileVersion `19.29.30038.1`）、SDK `10.0.19041.0`。[MicrosoftのVS2019履歴](https://learn.microsoft.com/en-us/visualstudio/releases/2019/history)の16.10.2固定Build Tools bootstrapperを参照し、個別コンポーネント`Microsoft.VisualStudio.Component.VC.14.29.16.10.x86.x64`、`Microsoft.VisualStudio.Component.Windows10SDK.19041`、`Microsoft.VisualStudio.Component.VC.CMake.Project`を確認する。最新版v142の16.11系は別版。旧installerの取得・導入は今回未検証なので、導入後の版を必ず確認する。Ninjaにも固定SHAの検査がある。

```powershell
$skiaTools = @{
    PythonExecutable = (Resolve-Path '.local/neural-openvoice/venv/Scripts/python.exe').Path
    VisualStudioDirectory = 'C:/Program Files (x86)/Microsoft Visual Studio/2019/BuildTools'
    WindowsSdkDirectory = 'C:/Program Files (x86)/Windows Kits/10'
}
./native/build-skia.ps1 -Action Plan @skiaTools
./native/build-skia.ps1 -Action Acquire @skiaTools
./native/build-skia.ps1 -Action Prepare @skiaTools
./native/build-skia.ps1 -Action Compile @skiaTools
./native/build-skia.ps1 -Action Audit @skiaTools
```

Community等の同一版を使用する場合は`VisualStudioDirectory`、別配置のNinjaには`NinjaExecutable`を指定する。版・SHA検査は維持する。既定出力は`.local/build/skia-rebuild/win-x64-md/`。既存試行の継続時だけ`-Resume`を使う。別マシンのリンク時刻・絶対パス等で完成DLLのSHAが異なる可能性があり、Audit成功だけで製品の固定SHAとの一致は保証されない。既存の描画・ABI・許諾確認後に採用し、検査を無効化して合わせない。

**OpenVoice**：公式repositoryへの接続が失敗する場合、固定tagを持つGit checkoutを別マシンから`.git`込みでコピーし、取得スクリプトに`-SourceRepository 'コピー先の絶対パス'`を指定できる。入力は変更せず、固定commitを新規checkoutする。通常ZIPはGit情報がないため使えない。部分cloneで不足objectがある場合は元の通信可能環境で取得を完了させる。CMake依存の取得は別途必要で、完全オフラインビルドを保証するものではない。失敗したWorkRootは検査用に残るので、内容と稼働参照を確認して整理し、再取得には新しいWorkRootを使う。

### OpenVoice専用ランタイムを初めて生成する

モデル変換とONNX Runtimeの生成は別工程。`pip install onnxruntime`のDLLは専用ランタイムの代用ではない。固定ソースは[Microsoft ONNX Runtime](https://github.com/microsoft/onnxruntime)、tag `v1.23.2`、commit `a83fc4d58cb48eb68890dd689f94f28288cf2278`。[取得スクリプト](native/acquire-openvoice-ort.ps1)は新しい作業先へ取得して、このcommitとの完全一致を検査する。

```powershell
$ortWork = Join-Path $PWD '.local/build/openvoice-first'
./native/acquire-openvoice-ort.ps1 -WorkRoot $ortWork

$ortTools = @{
    WorkRoot = $ortWork
    PythonExecutable = (Resolve-Path '.local/neural-openvoice/venv/Scripts/python.exe').Path
    CmakeExecutable = (Resolve-Path '.local/neural-openvoice/venv/Lib/site-packages/cmake/data/bin/cmake.exe').Path
    # 実際の固定版ツールの導入先に合わせる。Community等の同一版も指定可能。
    VisualStudioDirectory = 'C:/Program Files (x86)/Microsoft Visual Studio/2019/BuildTools'
    WindowsSdkDirectory = 'C:/Program Files (x86)/Windows Kits/10'
}
# CPU基準パッチを適用し、CMakeが固定依存ソースを準備。コンパイルはしない。
./native/build-openvoice-ort.ps1 @ortTools -ConfigureOnly
# 同じ作業先・依存を再利用して採用構成の差分を適用・検査。
./native/build-openvoice-ort.ps1 @ortTools -FusedConv -Col2im1D -PrepareOnly
./native/build-openvoice-ort.ps1 @ortTools -FusedConv -Col2im1D
```

各コマンドの失敗時は次へ進まず、表示された入力・版を確認する。`-PrepareOnly`は依存ダウンロードを行わないため、初回の`-ConfigureOnly`を省略しない。初回configureには上流依存の取得通信が必要。FusedConv／Col2imのconfigureは取得済み依存を使う。`-ConfigureOnly`と`-PrepareOnly`は併用不可。生成先は`$ortWork/ort-build/`で、最大2並列・1回600秒のコンパイル上限がある。時間切れ後は同じ採用構成で再開する。`-RowSplit`は採用構成ではないので付けない。

生成DLL・モデル・manifest・CRT・許諾と対象ソースを上記`native-openvoice`入力へ揃え、`neural-test`等の検証を通してから製品へ採用する。ビルド成功だけで現行の固定DLLハッシュと一致するとは限らない。異なるハッシュを検査回避のために書き換えない。

自前runtimeの入口は[native/build-openvoice-ort.ps1](native/build-openvoice-ort.ps1)。ORTの固定commit `a83fc4d58cb48eb68890dd689f94f28288cf2278`と、既存のVS2019/MSVC 19.29.30038.1・SDK 10.0.19041.0・Ninja 1.10.2・CMake 3.31.6を使う。全構成で`.local/build/`直下の独立した子フォルダーを`-WorkRoot`へ明示する。sourceはその`ort-source/`、生成物は`ort-build/`、子TEMPは`tmp/`。未指定・範囲外・リンク経由は拒否する。`-PrepareOnly`はsource/patch/tool検査のみ、外すとconfigureと最大600秒・2threadのcompileを行う。既定外の既存toolは`-GitExecutable`／`-CmakeExecutable`／`-NinjaExecutable`／`-VisualStudioDirectory`／`-WindowsSdkDirectory`／`-PythonExecutable`で指定する。依存は固定checkoutの`cmake/deps.txt`のURL/hashに従う。通常製品へ自動コピーせず、実link/依存/許諾と下記数値試験を確認してから同梱入力のhashを更新する。

FusedConvだけを追加した基準構成は、基準CPUパッチ適用済みの固定source（Git管理情報を含む）と準備済み`ort-build/deps/*-src` 13件を、独立した作業先へコピーして使う。旧作業先のCMakeCache・objをコピーしない。リポジトリ直下から`./native/build-openvoice-ort.ps1 -FusedConv -WorkRoot .local/build/openvoice-fused -PythonExecutable .local/neural-openvoice/venv/Scripts/python.exe -CmakeExecutable .local/neural-openvoice/venv/Lib/site-packages/cmake/data/bin/cmake.exe -PrepareOnly`で入力を検査し、`-PrepareOnly`を外すと同じ上限でビルドする。固定追加patch・完全差分・取得禁止を検査する。時間切れ後は所有子プロセスの終了を確認してから同じコマンドで増分再開できる。監査証跡は作業先の`ort-build/`内のMAP・native-pe-audit.txt・compiled-includes.txt・fused-native-audit.json・fused-header-audit.json、有限比較結果はfused-runtime-comparison.json・fused-lifetime-comparison.json。旧比較入力は`.local/build/openvoice-fused/baseline-runtime-audit/`へ保持し、下記`-NeuralPreviousRuntime`で再利用できる。

現在の製品と同じrank1 Col2im構成は、上記と同じ固定FusedConv source・13依存を独立した`.local/build/openvoice-col2im`へ準備し、`./native/build-openvoice-ort.ps1 -FusedConv -Col2im1D -WorkRoot .local/build/openvoice-col2im -PythonExecutable .local/neural-openvoice/venv/Scripts/python.exe -CmakeExecutable .local/neural-openvoice/venv/Lib/site-packages/cmake/data/bin/cmake.exe -PrepareOnly`で検査する。[限定patch](native/openvoice-col2im1d.patch)と完全差分の固定hashを検査し、通常構成は変更しない。`-PrepareOnly`を外すと同じ時間上限でbuildし、監査前の候補を製品入力へコピーしない。`requested-build.json`と`cpu-fused-col2im1d.patch`は独立作業先の`ort-build/`に保存する。同所の`col2im-config-audit.json`は基準とのコンパイル入力集合・固定構成の照合記録で、実linkや数値検証の代わりにはしない。同所のcol2im-native-audit.jsonは実link・header・import/export、col2im-runtime-comparison.jsonは固定入力の数値・速度・上限・取消の監査記録。

短文の行分割を評価する開発候補は、固定Col2im sourceと13依存のsourceだけを独立した .local/build/openvoice-row-split へ準備し、上記コマンドのWorkRootを同ディレクトリーへ変更して -RowSplit を追加する。-FusedConv -Col2im1D が必須。[限定patch](native/openvoice-row-split.patch)と完全差分hashを照合し、候補の ort-build/requested-build.json と cpu-fused-col2im1d-rowsplit.patch に条件を保存する。この候補は短文の比較で不採用とした。比較値とPCM照合結果は .local/build/openvoice-row-split/ort-build/rowsplit-comparison.json に保存してある。

参照PCMの所有権と割当量の確認は、Windowsのリポジトリ直下で`./dev.ps1 neural-reference-test -Configuration Release`を実行する。同梱149原音を一意fixtureへ取り込み、参照PCMを旧scalar丸めと全サンプル照合する。cacheをwarmup後、外部provider用cloneと内部所有配列の引継ぎを1組だけ比較する。初回/再利用時の原音読取/hashとsnapshotの時間を分離し、割当量・全PCM/順序一致・所有権を確認する。非同期読取の割当は隔離runner全体の差分、同期snapshotは実行threadの差分として測る。cacheなしの正確長読取とも全PCMを照合する。直列と最大2件の先読みを各1回warmup後に各1回比較し、全PCM/hash/順序を確認する。この比較では診断を無効にする。直列warm読取の任意集計OFF/ONも各1回とし、path/file・read・rawHash・cache・decode・bufferと未分類時間/件数を表示する。Readはasync継続再開も含むwall、集計は通常アプリで既定無効でパス/音声を記録しない。続いて検査済み参照hashの再利用と従来の全PCM再hashを1組だけ比べ、参照fingerprintの一致とsource/参照keyの安定性を確認する。続いて既定renderer専用cacheのcold/warmと一般cacheを照合し、同梱149音声の全PCM・順序・raw/PCMハッシュとwarm割当を1組確認する。一般APIの防御cloneは維持する。モデルやnative DLLの実行は不要。既存の非表示runner・180秒上限を使い、音声は保存/再生せずfixtureを清掃する。単発値を発声開始時間や一般的な速度保証と扱わない。

数値の一致確認はリポジトリ直下で`./dev.ps1 neural-test -NeuralAudit .local/build/native-openvoice -NeuralFixture tests/Fixtures/openvoice-oracle-v1.json`を実行する。独立したPyTorch計算による人工PCM・STFT・埋め込み・変換結果とC#／自前runtimeを照合する任意native試験で、既定`test`では実行しない。非表示プロセスを180秒で打ち切り、生成音声を再生・保存しない。fixtureの再生成は[tests/neural_openvoice_oracle.py](tests/neural_openvoice_oracle.py)を使用し、固定モデルを含む開発用Python環境が必要。neural-testは同じ人工PCMでCPU thread数1/2/3/4を各3回比較し、出力差と中央値を文字出力する。モデル読み込みと参照準備を除く計測であり、チャット発声開始時間ではない。検証済みモデルのprivate所有について、呼出元配列の変更後も従来入口とPCMが一致すること、種類違い・解放後の拒否を確認する。初回の読取/ハッシュと、従来入口の再ハッシュ/検証済み入口の同一性確認は別計時する。続いて同梱149原音を一意なfixtureへ取り込み、検証済み入口から新しいセッションを作って参照準備を1回計測する。ORT構築・参照fingerprint・特徴抽出・encoderを分離する。同梱149原音の直列/2件並列をfresh sessionで各1回warmup後に3組交互に比べ、参照準備のwall時間と変換PCM一致を確認する。特徴抽出時間は各組の待ち時間を含むwall時間で、CPU時間の総和ではない。これは既にruntimeをロードしたプロセスでの計測で、exeのcold起動時間とは区別する。原音・利用者dataを変更せずfixtureを清掃する。用意済み環境で`.local/neural-openvoice/venv/Scripts/python.exe tests/neural_openvoice_oracle.py --root .local/neural-openvoice --output .local/tmp/openvoice-oracle-new.json`を実行し、既存fixtureとの差を確認してから更新する。出力は新規ファイルだけを受け付け、比較後の一時JSONは削除する。本体へのPyTorch導入手順ではない。

演算別の観測は同じ`neural-test`へ`-NeuralProfile`を加える。固定人工PCMを32768samplesへ反復した1.486秒入力・4thread・現行モデルでconverterを2回動かし、最初のwarmupを除いたmodel_run内の演算を集計する。通常経路とのPCM一致を確認し、演算別上位10種・最長5node・長さ/hashと、モデル読込／セッション初期化のprofile時間を有限の文字出力にまとめる。計測自体の負荷を含むため通常の発声開始時間としない。生JSONは唯一の`.local/tmp/tests-<ID>/`へ出力し、集計・資源解放後に清掃する。音声保存・再生なし、通常workerの記録設定は変更しない。

`neural-test`は既定で製品と同じ固定runtimeを検査する。融合なしの旧runtimeとの比較は、旧DLLと同じ固定モデル・CRTを分離した入力フォルダーへ置き、`-NeuralAudit`へ指定して`-NeuralPreviousRuntime`を加える。旧版も試験コードに固定されたsize/hashだけを許可する。`-NeuralProfile`では既定のFusedConv実行件数が正、旧版は0であることも確認する。非profileの速度比較は代わりに`-NeuralRuntimeBenchmark`を加え、8192／32768／131072samplesを各1warmup・3回生成する。4thread・参照準備後・同一sessionでの時間とPCM hashを表示し、両版は別プロセスで実行する。conversionStagesは参照照合・元音声の特徴抽出・元encoder・noise/入力構築・converter・量子化の6段を分離する。診断はこの明示試験だけで収集し、通常workerでは時刻取得や記録を行わない。

`-NeuralRuntimeLifetime`は短文→上限45秒入力1回→短文で固定PCMと復帰を検査し、同じモデル/Sessionを保持した計時外GC後のprivate／working set／peak working setを表示する。`-NeuralRuntimeCancel`は入力特徴量を用意してからnative Run呼出中に100ms後の取消を発火し、終了例外と新しいRunOptionsによる同converterの復帰を検査する。協調取消を親の終了保証と同一視しない。profile・benchmark・lifetime・cancelは併用せず、いずれも親の180秒上限と通常の試験清掃を使う。

Windowsの開発用子プロセスは[native/process-error-mode.ps1](native/process-error-mode.ps1)で一時的なエラーモードを継承し、終了時に復元する。OSの恒久設定は変更しない。リポジトリ直下で`pwsh -NoProfile -File tests/process-error-mode.ps1`を実行すると、非表示の子への継承・入れ子の寿命・非ゼロ終了・元の設定への復元を検証する。意図的なネイティブ障害やレジストリー操作は行わず、一時出力は`.local/tmp/`から清掃する。

Skiaの固定再構成は[native/build-skia.ps1](native/build-skia.ps1)が入口。リポジトリ直下で`-Action Plan`は設定、`Prepare`は固定取得済みsourceのGit blob照合とGN生成、`Audit`は既存DLL/MAP/許諾の確認。`Acquire`だけが[native/acquire-skia.py](native/acquire-skia.py)で固定取得を行い、`Compile`だけが最大600秒/2threadのビルドを行う。前の試行がある場合は明示`-Resume`が必要。[toolchain差分](native/skia-direct-msvc.patch)をhashで検査し、環境初期化でreg/vcvarsallを起動しない。source/archive/auditは`.local/skia-rebuild/`、中間出力は`.local/build/skia-rebuild/`。この入口は製品・tryoutを直接置換しない。

Windows x64のDNG/PiexなしSkia入力は`.local/build/native-skia/`に置く。`skia-build.json`・固定`libSkiaSharp.dll`・承認済みVCランタイム3件がある場合、[native/SkiaNative.targets](native/SkiaNative.targets)が各DLLのSHA256を確認しbuild/publishへ配置する。許諾文は`licenses/`へ同梱する。他RIDのネイティブは変更しない。`./dev.ps1 skia-native-test -SkiaLibrary .local/build/native-skia/libSkiaSharp.dll -SkiaSourceRoot .local/skia-rebuild/source/skia`は固定素材・日本語描画の独立チェック、`-SkiaFullSuite`追加は同じ候補をロードした共通/Avalonia headless試験。非表示の既存runnerに180秒上限を設ける。GPU実機試験とは区別する。

## Microsoft動的ランタイムを含む配布

対象ファイルと再配布条件は[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md#msvc動的crtを使う場合の残条件)を参照する。採用承認だけで公開配布を開始しない。配布担当者は、監査したREDIST実物と[許諾文](assets/microsoft-vc-runtime-LICENSE.txt)を配布物へ含め、ダウンロード・引渡しの前に受領者へ適用条件を提示し、必要な同意を得る。再配布する人にも条件の維持を案内する。単なるNOTICE配置を同意の取得とみなさない。通常のソースビルド操作や試用コピー作成は公開配布・同意取得を代行しない。

UI依存の許諾全文は[assets/ui-third-party-NOTICES.txt](assets/ui-third-party-NOTICES.txt)に集約し、共通プロジェクトからbuild/publishのlicenses/ui-third-party-NOTICES.txtへ配置する。更新時は固定パッケージと公式ソースの対象版を照合し、詳細はTHIRD_PARTY_NOTICESを更新する。

## 文書案内

- [SPEC.md](SPEC.md)：利用操作・接続方式・保存場所・機能仕様の正本。
- [PROJECT_STATUS.md](PROJECT_STATUS.md)：OS別対応、認証保存、配布条件、実サービス・実機検証を含む依頼全体の状況。
- [CURRENT_TASK.md](CURRENT_TASK.md)：現在の作業と再開手順。
- [WORK_LOG.md](WORK_LOG.md)：変更・判断・検証の履歴。
- [AGENTS.md](AGENTS.md)：恒常的な開発規則。
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)：固定依存・辞書・音源のライセンスと配布条件。
- [RESEARCH.md](RESEARCH.md)：技術比較・機能調査・採用検討の根拠と未確認点。




検証済みの既存publishから再ビルドせず新規試用コピーを作る場合は、リポジトリ直下で ./dev.ps1 tryout -UseExistingPublish を使う。先に publish と対象製品試験を終えた出力だけを指定RIDで使用する。ソース変更を自動反映する操作ではない。通常tryoutと同じRID検査・data引継ぎ・一時出力・完成判定を通る。容量不足時はpublishとの照合と稼働参照確認後に再生成可能なbin/resourcesを清掃してから使える。既存試用版/dataは清掃対象にしない。

### GitHubへのソース登録

WindowsのGit for Windows／Git Credential Managerを使用する。PowerShell 7で `pwsh -NoProfile -File C:\work\tanayomi\register-github-token.ps1` を実行し、非表示の入力欄へトークンを入力する。既定ユーザー名はhosinohito、必要なら `-Username` を指定する。保存先はWindows資格情報マネージャーで、トークンを引数・プロジェクトファイルに書かない。GitHubのfine-grained tokenは対象リポジトリとContentsのRead and writeを選ぶ。登録は保存のみで、権限の確認は実際のGit操作で行う。

このリポジトリのremoteは https://github.com/hosinohito/tanayomi.git 。`.local/`、bin/obj、生成モデル、利用者data、秘密設定はコミットしない。ソースのGitHub pushと、実行物の公開配布・本体ライセンス決定は別の操作。

専用publishは大容量resourcesを元資産からpublish先へ直接コピーし、binへは複製しない。通常build/runのresourceコピーは維持する。publish済み製品の検証には `.local/build/app` を使う。
