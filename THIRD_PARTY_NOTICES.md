# 依存部品と配布条件

本体の公開ライセンスは未決定。以下の復元済みパッケージは開発・検証用で、配布監査完了を意味しない。許諾の広い部品を優先し、ネイティブ部品・辞書・音源を別々に確認する。

## 同梱音源

- 著作者：hosinohito。取得元：[hosinohitoVoiceDataforUTAU](https://github.com/hosinohito/hosinohitoVoiceDataforUTAU)、コミット`783d2c473525da4e6999aee5a245dbdb429e96bc`。
- 2026-10-24、著作者であるユーザーが同梱と利用条件の選定を明示的に許諾。本プロジェクトの同梱コピーに[CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/)を採用した。上流リポジトリの内容は変更していない。
- assets/hosinohito.zipには原本のWAV149個とoto.ini、CC0の公式全文、出所と許諾経緯を含める。不要な解析キャッシュ・バックアップは含めない。SHA-256：`cf1f2fd5e014ece8a7fde04610b9e2da0a97824202431e5f5977be9d791f168b`。
- この条件は同梱音源に適用する。アプリ本体・第三者が追加した音源のライセンスとは独立する。

## 復元済みの固定版

詳細な推移依存とハッシュは各packages.lock.jsonを正本とする。NuGetのnuspecだけでなく、実パッケージ内のLICENSE／THIRD-PARTY-NOTICESとビルド元を確認した。

| 部品 | 固定版・確認結果 |
| --- | --- |
| .NET SDK / Windows x64 runtime | SDK 10.0.402は公式SHA-512照合済み。通常の自己完結型ビルドはMicrosoft.NETCore.App.Runtime.win-x64 10.0.13を同梱。対象パッケージのLICENSE.TXTはMIT。詳細は下記 |
| Avalonia、Desktop、Fluent、Skia、HarfBuzz、Native、Win32、X11、FreeDesktop、AtSpi、Remote.Protocol | 12.1.4、[MIT](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.1.4/licence.md)。商用付加製品は追加していない |
| Avalonia.Headless / BuildServices | 12.1.4 / 11.3.2、MIT。試験／ビルド用途 |
| MicroCom.Runtime / Tmds.DBus.Protocol | 0.11.6 / 0.94.1、nuspecはMIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419、同梱LICENSEはBSD形式。収録部品一式の監査は継続 |
| SkiaSharpと各NativeAssets | 3.119.4、ラッパーはMIT。ネイティブ内部は下記の未解決条件あり |
| HarfBuzzSharpと各NativeAssets | 8.3.1.3、ラッパーはMIT。共通NOTICEにはSkia由来の部品も列挙されており、実リンク対象との対応確認が必要 |

UI依存の全文表示は`assets/ui-third-party-NOTICES.txt`を正本とし、共通プロジェクトから`licenses/ui-third-party-NOTICES.txt`へbuild/publishでコピーする。Avalonia 12.1.4（2d79ba55fae1b1e71c69f93a668080c9029e6a31）、MicroCom 0.11.6（76785efcafd91b5902fd19dd11145f6dd655b7b4）、Tmds.DBus 0.94.1（b4a7fed0b878f74cb54f7cca84d2889af4e596ba）のMIT全文はnuspec指定コミットの公式ソースから取得。ANGLE・SkiaSharpラッパー・HarfBuzzSharpとそのnative共通NOTICEは固定NuGet内の全文を保持する。共通NOTICEの掲載物すべてが実リンク済みという意味ではなく、ANGLE/HarfBuzzの実収録部品監査や他OSの監査完了を表さない。DNG除外済みSkiaの専用表示は別途維持する。
### Windows同梱.NETランタイム

- 復元した`Microsoft.NETCore.App.Runtime.win-x64/10.0.13`のnuspec・LICENSE.TXT・THIRD-PARTY-NOTICES.TXTを確認。nuspecの公式ソースは[dotnet/dotnet](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699)、コミット`4099f83977f4d29604a9aeb971fb0a7bc1d5f699`。主要コードはMIT、収録第三者表示にはBSD・Apache-2.0・Unicode・zlib等が含まれる。NOTICE全体を保持し、各OSの実収録物の監査とは区別する。
- 配布物には同梱したランタイムと同じ版のLICENSE.TXT・THIRD-PARTY-NOTICES.TXTを`licenses/dotnet/`へ保持し、本書も含める。SDK自体は配布物へ含めない。生成手順は[README.md](README.md#開発手順)を参照する。
- ランタイム文書の同梱は、次項のUIネイティブ部品を含む配布監査の完了を意味しない。

### 配布前に解決する条件

- SkiaSharp 3.119.4の実パッケージnuspecが示すソースは`f568ac94dd768ef9a2f593537cfde2dd0d348ef5`。[Windowsビルド](https://github.com/mono/SkiaSharp/blob/f568ac94dd768ef9a2f593537cfde2dd0d348ef5/native/windows/build.cake)は`skia_use_dng_sdk=true`かつ`skia_use_piex=true`。固定Skiaは`7dbfc07dd33181f84e0958afb7ee805c6c769f0b`、[DEPS](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/DEPS)のDNG SDKは`c8d0c9b1d16bfda56f15165d39e0ffa360a11123`。
- 2026-10-24、復元済みwin-x64の`libSkiaSharp.dll`、`.local/build/app/`、初回確認の`.local/tryout/20261024-071304-de31a4/`および今回確認の`.local/tryout/20261024-092456-a49732/`内の同名DLLが同一SHA-256 `7dec3ba900ab353491e6446f0083739924c6f8dd668832e2f09d38ebffdbbe1c`であることを確認。DLLには`dng_priority_manager::fMutex`・`dng_read_tiles_task`等、対応PDBには`SkRawCodec.cpp`・`dng_abort_sniffer.cpp`を確認した。ビルド指定と併せ、当時のWindows x64ネイティブにはDNG SDK由来コードが含まれると判断する。単に共通NOTICEに名前が残っているだけとは扱わない。他RIDの実バイナリ検査は未実施。
- その[DNG SDKライセンス](https://android.googlesource.com/platform/external/dng_sdk/+/c8d0c9b1d16bfda56f15165d39e0ffa360a11123/LICENSE.source_code)§5は、商用製品へ配布する場合にAdobeの防御・補償・免責保持を求める。権利表示保持、商標による無断推奨禁止等もあり、ソース公開・再リンク・非商用限定の義務とは区別する。[固定版PATENTS](https://android.googlesource.com/platform/external/dng_sdk/+/c8d0c9b1d16bfda56f15165d39e0ffa360a11123/PATENTS)はDNG準拠実装のソースと文書に指定帰属文を目立つ形で載せる条件を持つ。無償・OSSなら商用条件が必ず免除されるとは判断しない。既存の許諾／PATENTS写しは固定一次本文と末尾空行以外の一致を再確認した。**この条件が未解決のため、この旧ネイティブバイナリの配布採用は保留。**
- Windows x64で採用した最小構成は、Avalonia 12.1.4／マネージドSkiaSharp 3.119.4を維持し、上記と同じ固定ソースで`skia_use_dng_sdk=false`、`skia_use_piex=false`としてWindows x64のネイティブだけ隔離再構成する方法。[固定SkiaのRAWターゲット](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/BUILD.gn)はDNG／JPEG／Piexの全条件で有効になるため、この構成は`SkRawCodec.cpp`・RAWデコードとDNG／Piex依存を除く案になる。全面的なrenderer切替は含めない。C ABI・通常の画像と日本語文字描画・実ロード先の検証が必要。`NoDependencies`というパッケージ名だけではDNG除外の根拠にならない。[Linux標準ビルド](https://github.com/mono/SkiaSharp/blob/f568ac94dd768ef9a2f593537cfde2dd0d348ef5/native/linux/build.cake)もPiexを有効にし、[DNGの既定値](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/gn/skia.gni)は通常のデスクトップ構成で有効になる。
- **Windows x64再構成候補の実物確認（2026-10-26）**：固定ソースをDNG/Piex無効・`/MD`で構築したDLLは8,368,128 bytes、SHA256 `828EF8C611AD23EC33CA1A6581A8CFE8F0CBB45925C0AE87CFC074BAED8C2458`。旧バイナリの924 named exportsと一致し、実MAPの1,171 library/objectにDNG/Piex/SkRawCodecおよび静的CRT対象objectがないことを確認。動的MSVCP140/VCRUNTIME140/_1とOS UCRTを利用する。Microsoft動的CRT条件は別節の承認・表示・引渡し手順を適用する。C ABIロード/2×2描画、managedでの日本語・PNG/JPEG/WebP/GIF読込とPNG/JPEG/WebP書出し、Avalonia headless共通試験を確認済み。Windows x64の通常build/publishへ固定ハッシュ検査付きで統合し、必要15本文・IJG帰属・WebP PATENTS・Chromium SIMD BSDをlicenses/skia-native-NOTICES.txtへ配置する。2026-10-26の最新完成コピーでもDLLハッシュと許諾文配置を照合した。GPU実機と他RID、および公開配布全体の監査は未完。
- **DNG除外後にも残るCRT条件**：固定WindowsビルドはMSVC toolset 14.2・Spectreライブラリーと`/MT`を指定する。[固定GN Windows toolchain](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/gn/toolchain/BUILD.gn)も、clang-clを選んだ場合を含めMSVCの環境・headers／librariesを使う。DNG除外やLLVMコンパイラーへの変更だけでMicrosoft静的CRTの再配布条件まで解消するとは扱わない。その条件の受入判断、またはMicrosoft CRTを再配布しない別構成の追加調査が必要。WORLDで監査済みのllvm-mingw構成を、そのままSkiaへ適用できることは未確認。
- **Microsoft CRTを再配布しない移植候補（未検証）**：固定[BUILDCONFIG](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/gn/BUILDCONFIG.gn)はWindowsをMSVC系へ固定し、既存`gcc_like`は`.so`／ELF向けsoname指定を使う。[Windows用config](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/gn/skia/BUILD.gn)もMSVC flags・SDK headers／librariesへ分岐するため、llvm-mingw／OS既存msvcrt.dllへの変更は引数の単純切替ではできない。GNU／PE用toolchain、Windows flags・import library・DLL exportの対応を追加する移植案となる。[Cヘッダー](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/include/c/sk_types.h)と[固定managed binding](https://github.com/mono/SkiaSharp/blob/f568ac94dd768ef9a2f593537cfde2dd0d348ef5/binding/SkiaSharp/SkiaApi.generated.cs)のextern C／Cdecl・opaque handleを維持できれば、managed／rendererを維持する余地はあるが、全exports・struct layout・callback・DLL内の確保／解放の一致は未検証。
- 機能範囲の候補はDNG／Piexを除き、OpenGLとソフトウェア描画、日本語を含むDirectWriteフォント処理を維持する構成。[Avalonia 12.1.4のWindows既定値](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Windows/Avalonia.Win32/Win32PlatformOptions.cs)はAngleEgl→Softwareであり、Skia自身のDirect3Dを無効化して移植範囲を減らす余地はあるが、既定経路・任意Vulkan・全native APIの互換性は未確認。MinGWのDirectX関連headersにはLGPL表記のもの（固定ツールチェーンの`d3dcompiler.h`等）があり、実際のinclude／inlineと[固定MinGW条件](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/COPYING.MinGW-w64/COPYING.MinGW-w64.txt)を別途照合する必要がある。WORLDの実リンク監査結果をこの移植へ広げず、許諾受入・移植開始・成功・配布採用のいずれも未確定とする。
- DEPSのDNG取得指定はGNフラグと独立している。新規再構成でSDK自体も取得しない場合、隔離したソースの当該DEPS指定を除いてから依存同期する必要がある。生成GN引数・依存グラフ・コンパイル対象・リンク記録からDNGと`SkRawCodec.cpp`の除外を確認し、文字列不在だけで非同梱と結論しない。Windows x64の隔離再構成は実施済み。製品出力への差替えは未実施で、候補の生成・監査成功だけで配布全体の完了とはしない。
- 共通NOTICEにはGIFのMPL/GPL/LGPL記載がある。一方、上記固定SkiaではGIF処理に[Wuffs](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/third_party/wuffs/BUILD.gn)を使用する。Wuffs固定版のLICENSEはApache-2.0、Skia側の接続部はBSD。古い共通表示と実バイナリの収録物を対応付けてから結論を出す。
- FreeTypeはFTLを選択できる部品。配布時は選択した条件・著作権・必要な帰属を保持する。OSのライブラリを利用する場合と、そのバイナリを同梱する場合を区別する。
- Android／iOSパッケージはプロジェクト宣言のみで未復元。推移依存・ネイティブ・SDK・ストア配布条件を未確認。全OSの配布監査が完了した扱いにしない。

### ANGLE/HarfBuzzの静的CRT構成の残条件

2026-10-26、HarfBuzzSharp 8.3.1.3の固定[Release x64設定](https://github.com/mono/SkiaSharp/blob/2888c737ad016d584c74525e2d35db5097ea8576/native/windows/libHarfBuzzSharp/libHarfBuzzSharp.vcxproj)はMultiThreaded（/MT）、v142を指定。ANGLE 2.1.27548.20260419の固定[workflow](https://github.com/AvaloniaUI/angle/blob/1c89805903c1482166356d3b950d474973180e61/.github/workflows/build.yml)とDEPSのbuild commit 71caaf82b3b605378fb82bf7380164c8104304adでは、非debug・非componentのWindows既定がstatic_crt（/MT）を選ぶ。手元DLLのimportsに動的CRTがないことと整合するが、製造時MAP/全object一致とANGLE全ターゲットのoverrideは未確認。動的VC142だけの承認をこの静的構成へ拡大しない。公開配布再開時に対象条件の確認、または固定ソースの/MD再構成と互換性・実リンク監査が必要。後者は未実装・未検証であり、既存3 DLLの流用も確定していない。
### MSVC動的CRTを使う場合の残条件

2026-10-26、導入済みBuildTools 2019（16.10.31410.357）のpackageが参照する[BuildTools EULA](https://visualstudio.microsoft.com/license-terms/mlt031519/)を確認。§1・§2.bはVisual Studio Community／Professional／Enterpriseとともに使う開発・ビルド条件で、BuildToolsの存在だけからCRT再配布権を確定しない。

[Community 2019 EULA](https://visualstudio.microsoft.com/license-terms/mlt031819/) §1.aは個人による販売を含むアプリ開発を許し、§3はREDIST対象object codeのアプリ内配布を認める一方、主要機能の追加と、配布者・利用者にMicrosoftコードを同等以上に保護する条項への同意を求める。Microsoftの推奨を装う商標使用と、MicrosoftコードをExcluded Licenseに従属させる改変・配布を禁じる。本体ソースの公開や非商用限定の義務ではない。利用者の契約資格は端末ファイルから確認できず、単なるNOTICE同梱だけで同意条件を満たしたとはしない。

[REDIST 2019](https://learn.microsoft.com/en-us/visualstudio/releases/2019/redistribution)は有効なVisual Studio許諾とその条件を前提に対象ファイルの未改変配布を認め、debug_nonredistを除外する。exeと同じフォルダーへのapp-local配置は[公式手順](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170)にある。[VC Runtime利用者EULA](https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/)のinstall/use許可だけで開発者の再配布権を得たとは扱わない。この条件はDNG/Piexを外すSkia再構成と、自前ORT CPU版の両方に関係する。2026-10-26、利用者は条件の採用を承認し、個人としてVisual Studio Communityを利用すると申告した。Eigenの承認とは別の承認として記録する。正規REDISTの必要ファイル・hash・利用者向け許諾文を確認して同梱し、公開配布では受領者の同意を得る手順が必要。条件採用の承認だけで全配布物の監査完了とはしない。

## ONNXのmanaged API

Microsoft.ML.OnnxRuntime.Managed 1.23.2（commit `a83fc4d58cb48eb68890dd689f94f28288cf2278`、MIT）と、net10.0で解決したSystem.Numerics.Tensors 9.0.0（MIT）を固定参照する。各NuGet実物のLICENSEとNOTICEは`assets/onnx-managed-*`／`assets/numerics-tensors-*`へ保持し、Desktop出力の`licenses/`へ配置する。nupkg SHA256は順に`6E29D09318B9EC1A3207FD97FE0DFEBFCCCEC15B936C85BDFF2F6C8080E91836`／`B750243C36002A62B28B1AC5D3FBC284AD340BA1494CC36ACA110611A0B1F959`。System.Memoryはこのtargetではフレームワークで満たされ、別パッケージの解決・同梱はない。

managed APIの採用とnative runtime／モデルの採用は別。共通NOTICEの全項目がmanaged DLLに入るとは扱わず、nativeの条件は下記を参照する。2026-10-26、利用者はEigen MPL-2.0の対象ソースと許諾文を同梱する条件で、Intel等の追加条件を含まない構成を確認できた場合の採用を承認した。この承認を未監査の別部品へ拡張しない。MSVC再配布条件は上節の別承認として扱う。

## 未導入の音声候補

miniaudio、worldlineは未採用。Mei/Takumiは後段の固定モデル構成で採用。Open JTalk／HTS EngineとNIT音声は、後述の固定監査構成をWindows自己完結版へ組み込んでいる。公開配布全体の監査完了とは区別する。UTAUの独自経路と利用者導入エンジンの外部呼出しも区別する。

### 複数の合成経路の調査候補（2026-10-26、未採用）

以下は調査時の候補と依存上の注意。OpenVoiceの採用範囲は後段の固定CPU構成に限定し、上流の標準依存一式を採用しない。使用するcommit／重みrevision／hashと実際の推移依存・native・辞書・必要表示を固定する。

| 部品・候補 | 確認した条件と根拠 | 採用前に残る条件 |
| --- | --- | --- |
| REAPER（周期点解析） | [公式LICENSE](https://raw.githubusercontent.com/google/REAPER/master/LICENSE)はApache-2.0。[公式README](https://raw.githubusercontent.com/google/REAPER/master/README.md)はGCI・有声／無声・F0を推定するC++実装を説明 | 固定版全ソース・追加著作権・ビルドruntime・対象OSの構成は未監査。Apache表示だけで新しいnativeバイナリ全体を確認済みにしない |
| OpenVoice V2 | [コードLICENSE](https://github.com/myshell-ai/OpenVoice/blob/main/LICENSE)はMyShell.ai 2024のMIT全文。[公式weight README](https://huggingface.co/myshell-ai/OpenVoiceV2/raw/main/README.md)にもV2のMIT宣言あり。確認したweight treeに独立LICENSEファイルは見当たらず、宣言とコード許諾全文の確認を区別 | [setup.py](https://github.com/myshell-ai/OpenVoice/blob/main/setup.py)に`unidecode==1.3.7`が含まれ、[対象版PyPI](https://pypi.org/project/Unidecode/1.3.7/)はGPLv2+。現状の依存一式を自動採用しない。NIT→converterに限定した実import／実行経路から制限部品を除けるか確認し、VAD・Whisper・wavmark・codec・推論runtime・weightの表示も監査。制限が残れば判断待ち |

対象話者の参照音源は既存hosinohitoの許諾を使えるが、別音源の参照・学習・同梱条件へ拡張しない。学術論文の公開は実装コード・重み・データの自由な再配布を意味しない。外部UTAUのresampler／wavtoolは次節の既存導入版の外部利用に限定する。

OpenVoiceの開発検証ではMelo／VAD／ASR／Unidecode等を除いたconverterのみを使用した。PyTorch 2.9.1 CPUはIntel oneMKLを含むため製品へ持ち込まない。製品のWindows x64構成はOpenVoiceコード`74a1d147b17a8c3092dd5430504bd83ef6c7eb23`、V2重み`f36e7edfe1684461a8343844af60babc2efbb727`から書き出した参照encoder／converterだけを採用し、MIT全文と変更・出典表示を保持する。

ONNX Runtime 1.23.2の固定source `a83fc4d58cb48eb68890dd689f94f28288cf2278`をCPU MLAS／Eigen・`EIGEN_MPL2_ONLY`・`/MD`でビルドした。native NuGetのDLLは同梱しない。自前DLLは10,176,000 bytes、SHA256 `1054ABA195985786A160E1F0220527B34734FACD5CA32D8DBEF094A53A07846C`。CPU限定patchに、固定MITソースからfloat CPU FusedConvのschema/kernel/activation/transformerだけを追加した。完全差分SHA256は`E8D0642CAE49EF71C28A9475266AC26B2140B3C0C10B21FB45CD4F820800DC5E`。既存のrank1 Col2im散布順を保持する限定経路を追加した。この変更でlink member579件とimport/exportの集合は不変、使用header1645件（同一絶対path9件・コピー1636件）にも差はない。contrib全体は無効のままで、追加compile sourceは3件、追加link memberは融合とselector処理の6件。使用headerの追加はMITのfused_activation.hだけで、コピー先1636件のhashは基準と一致し、import symbol／外部DLL／exportに差はない。実compile設定・link MAP・importsを照合し、独立したIntel MKL／DNNL／OpenMPやGPUランタイムを使わない構成を確認した。MSVCRT.lib中のstartup／数学supportは承認済みMicrosoftランタイム経路として扱い、`/MT`版の包括採用へ拡張しない。実リンク部品の表示は固定版[ThirdPartyNotices](https://raw.githubusercontent.com/microsoft/onnxruntime/v1.23.2/ThirdPartyNotices.txt)を省略せず保持する。

EigenはMPL-2.0の対象ソースと表示を同梱する承認条件に従い、固定`1d8b82b0740839c0de7f1242a3585e3390ff5f33`の全ソースZIP（SHA256 `6A60D76351F97132669DAEEB721D6BF14B008101883AD2D687A3201C5C461EB0`）とMPL全文を`licenses/openvoice/`へ配置する。Eigen自体の改変はない。本体全体のMPL化を意味しない（[公式FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/)）。正規VC142 REDIST 14.29.30038.0の`msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`は未改変・署名確認済み実物をORTと同じフォルダーへ置き、上節のMicrosoft許諾文と受領者同意手順を維持する。モデル2件・native・CRTの固定size/hashは`resources/openvoice/openvoice-build.json`に含め、実行時も検査する。Skia等の別部品の未解決条件や本体ライセンスの決定を、この採用で解決済みとしない。

## 読み上げソフトの参考調査（非採用）

- 2026-10-25の比較では、利用者環境に既存のUTAU付属resampler Ver14.02rを外部プロセスとして呼び出した。同梱原音の一時コピーのみを渡し、エンジンのコード・実行物はコピー／配布しない。導入済みUTAUのreadmeは配布物の全部・一部の再配布に作者許可が必要と明記しているため、同梱候補へ自動昇格しない。互換callerの公開引数仕様を参照したが、その実装コードやライブラリーは採用していない。唄詠本体・プラグインの採用許諾とは別に扱う。
  - 利用者評価後、Windows製品にも同じインストール済み実行物を使う任意の接続を追加した。外部実行のみで静的／動的リンクや再配布は行わない。検証対象exeのSHA-256は比較記録に保持し、エンジン交換時に任意の他エンジンの許諾まで承認されたとは扱わない。

- [初代唄詠7.62](https://shinta.coresv.com/old-logs/utayomi_jpn/)には本体ソースの公開リンクがあるが、本体の公開条件はCC BY-NC-ND 2.1 JP。[唄詠2 2.81](https://shinta.coresv.com/software/utayomi2-ja-jp/)も同条件。[公式ヘルプ](https://shinta.coresv.com/help/Utayomi2_ja-jp.html)のSAPI入力・調声プラグイン・resampler／wavtoolの役割分担と、単独音／連続音／CVVC（単独音扱い）の設定仕様を参考にする。公開ソースがあることを、本プロジェクトへコードを取り込む許諾とは扱わない。
- [唄詠調声プラグインSDK 2.00](https://shinta.coresv.com/old-logs/utayomi_pluginsdk_jpn/)のCC BY 2.1 JPと、[Lib UTAU 2.16](https://shinta.coresv.com/software-dev/libutau_jpn/)のCC BY-NC-SA 2.1 JPは本体と別の条件で、いずれも未採用。
- やまなみ・ダブルフラットは公式ヘルプと作者の説明から動作を調査した。各プラグイン固有の再利用条件は未確認で、SDKの条件を適用できるとは判断しない。コード・実行物は取り込まず、公開仕様を独自実装の検討材料にする。
- [棒読みちゃんの公式説明](https://chi.usamimi.info/Program/Application/BouyomiChan/)にあるIMEでの読み変換とAquesTalk／SAPI等への合成委譲を参考にする。本体ソースを本プロジェクトへ利用する許諾は確認できていない。今回の調査で、これらのコード・バイナリ・SDK・音源の採用や転載は行っていない。

## OS側のサービス・コマンド

Windows SAPI／DPAPI、macOS say／AudioQueue／Keychain、Android／iOSの音声APIはOS側の機能を呼び出す。OSコンポーネントやAppleの実装コードを本体へ取り込まない。

Linux音声は[SSIPの公式プロトコル仕様](https://github.com/brailcom/speechd/blob/0.12.1/doc/ssip.texi)から独自実装したC#クライアントで、OS側のSpeech Dispatcherへ接続する。Speech Dispatcher／libspeechd／spd-sayのコードやライブラリーを本体へ取り込まず、リンク・同梱もしない。PCM再生はOS側のaplayを別プロセスで利用する。

Linux認証はOS側の[libsecretのsecret-tool](https://github.com/GNOME/libsecret/blob/main/tool/secret-tool.c)を別プロセスで利用する。上流の条件はLGPL-2.1-or-laterだが、本アプリはそのコード・ライブラリーを取り込まず、リンク・同梱もしない。追加NuGet依存はない。将来これらの実行ファイルやライブラリーを同梱する場合は、対象版と配布形態の条件を改めて確認する。

## 日本語の読み辞書（採用）

- 取得元：[JPreprocess Team / naist-jdic](https://github.com/jpreprocess/naist-jdic/tree/394af50de2da4a9c73b1bd04fab43e85bb6f03ff)、固定コミット`394af50de2da4a9c73b1bd04fab43e85bb6f03ff`。2026-10-24に対象コミットの`naist-jdic.csv`・`matrix.def`・`COPYING`の実物を確認した。
- 条件：[対象版COPYING](https://github.com/jpreprocess/naist-jdic/blob/394af50de2da4a9c73b1bd04fab43e85bb6f03ff/COPYING)にあるBSD-3-Clause形式の4表示（NAIST 2009、UniDic Consortium 2011–2017、名古屋工業大学 2008–2016、JPreprocess Team 2023）。著作権・条件・免責の保持と、名称を無断で推奨表示に使用しない条件。ソース公開・再リンク義務、非商用・用途制限、ICOT条件は対象COPYINGには含まれない。
- アプリに埋め込む`assets/naist-jdic.bin.gz`は表記・左右文脈ID・単語コスト・発音と接続コスト表のみを保持した派生データ（486,688項目、1,377×1,377接続）。変換処理は`assets/build-reading-dictionary.py`。元の品詞・アクセント列および`unidic-csj.csv`等は収録しない。
- `assets/naist-jdic.COPYING`に上記COPYING全文を保持し、アセンブリへ埋め込み、実行・配布先の`naist-jdic.COPYING`にもコピーする。バイナリ配布時はこのファイルと本書を保持する。
- 元CSVのSHA-256：`140e0b8189732fa1e65d22079f399b895cb5d7e6ba8b1b1afed656d3fb10b84e`。接続表のSHA-256：`b8ff0c65c1024680ebf81b820d789435c64b039d077666e67b34093f041eb36a`。派生データのSHA-256：`c465adf0a064d678d35ac4a45871214ec00107d3a12c537d227749738099fcda`。
- この導入は辞書データのみ。独自のC#最短経路読み変換・UTAU単独音接合を使い、MeCab／NMeCab／Open JTalk／HTS Engineのコードやネイティブライブラリー、推移パッケージ、学習済み音声モデルは追加していない。音声の条件は上記「同梱音源」と利用者が選んだ音源のそれぞれに従う。
- NMeCab系候補とDotNetG2P 1.10.0は未採用。後者のApache-2.0表示だけで上流由来コード・辞書の条件を確定できないため、今回の辞書データ導入とは区別する。

## WORLD（UTAUの音程処理）

- 固定版：[mmorise/World commit d625e7608ca23a870018f01e7c562ac683d9847f](https://github.com/mmorise/World/tree/d625e7608ca23a870018f01e7c562ac683d9847f)。取得ZIP SHA-256 `a62635555243dfe4fb5a859a882fd92713137ec29dbc7a86f0a01f78362f2e60`。2026-10-24に実ソース・許諾文を確認。
- WORLDはBSD-3-Clause。`src/world/macrodefinitions.h`の追加MIT表示も含めて`assets/world-LICENSE.txt`へ全文保持。Ooura FFTの著作権・商用を含む任意目的の利用／複製／変更／配布許諾と原典は`assets/ooura-fft-LICENSE.txt`に保持する。[Ooura原典](https://www.kurims.kyoto-u.ac.jp/~ooura/fft.html)の参照依頼を、ソース公開や再リンクの義務と解釈しない。
- FFTW・学習済みモデルは採用しない。初期比較のWindows x64 C ABIブリッジはMSVC静的ランタイム版で、PE importsはKERNEL32.dllのみだった。通常構成には下記の監査済みLLVM版を使い、MSVC比較DLLは混入させない。WORLDを含む成果物にはWORLD・Ooura・使用runtimeの許諾文を保持する。
- **MSVC版は通常製品への採用保留**：実物はVS2019 Build Tools 16.10.2／MSVC v142（tools 14.29.30037、cl 19.29.30038.1）とWindows SDK 10.0.19041.0、`/MT` Release。CRT全体をMIT扱いしない。SDK実物EULA §2.aと[公式REDISTのStatic LIB files](https://learn.microsoft.com/en-us/legal/windows-sdk/redist)は、LIBをリンク済み結果として配布し、主機能追加、利用者／再配布者による同等保護条件への同意、アプリ自身の著作権表示、権利表示保持、Microsoft OS限定、補償等を求める。ソース公開・再リンク用obj提供・非商用限定の義務は確認されないが、追加条件の採用は未承認。[Build Tools 2019 EULA](https://visualstudio.microsoft.com/license-terms/mlt031519/)に合う開発環境の利用許諾確認も残る。STLヘッダーの[Apache-2.0 WITH LLVM-exception](https://github.com/microsoft/STL/blob/vs-2019-16.10/LICENSE.txt)をCRT全体へ広げない。
- **採用DLLのツールチェーン**：[llvm-mingw 20261020](https://github.com/mstorsjo/llvm-mingw/releases/tag/20261020)の`llvm-mingw-20261020-msvcrt-x86_64.zip`（165MB、公式asset SHA-256 `52a3e3f584a74c3cde8ceb0c6960549683178e03b36818cb02b7ed109a68ff67`）。LLVM 23.1.4／mingw-w64 commit `0d6dfbc348dfad714761c0a7151615767f96bcb5`を使い、OS既存のmsvcrt.dllへ動的参照し、Microsoft CRTを再配布しない案。LLVMランタイムはApache-2.0 WITH LLVM-exception、MinGW互換処理はZPL／BSD等の表示を保持する。GCC系ランタイムは候補のリンク対象から外す。[固定版runtime notices](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt)にはCephes数学処理の許諾未確定記載とWine由来LGPLヘッダーの記載が残るため、関連object／includeの不使用または個別条件の確認が必要。調査用に172,961,237 bytesの固定ZIPを取得し、公式SHA-256と展開済みcompiler／header／libraryの一致を確認した。ツールチェーン自体を製品へ同梱しない。
- **固定DLLの実リンク監査**：`native/build-world-llvm.ps1`で独立生成した`.local/build/native-llvm/audit-20261027-080217-8175a433/tanayomi-world.dll`（SHA-256 `6d07ebc9b9854065a3f5428e71ff05212221449f2bd640541519d82757ac3fff`）のPE importsは`KERNEL32.dll`／`msvcrt.dll`だけ。ABI-v1の従来exportにHarvest/DIOのF0専用2件と任意の並行解析2件を加えた計9件。LLVMのlibc++（libc++abiを含む）／libunwind／compiler-rt chkstkと、MinGW起動・互換処理の実リンクをMAP／verbose traceで確認した。libgcc・libstdc++・winpthreadsライブラリーやMicrosoft製CRTのバイナリーは同梱しない。下記の互換ヘッダー由来の表示とは区別する。
- 並行解析版は、同じ固定WORLD・LLVM・MinGW・コンパイル/リンク条件のF0専用基準版（audit-20261027-034455-432894cb、member73/include496）へ38 member行（static source37＋import group1）と71 headerを追加。固定一次sourceのLLVM10件はApache-2.0 WITH LLVM-exception、MinGW27件はPD／Lucent-Gayの許諾／個別表示のないwrapperは固定root COPYINGのZPL-2.1。追加headerは固定ZIPと全byte一致し、Apache＋LLVM exception63、PD6、MIT/BSD2を確認。`time.h`経由の[pthread_compat.h](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/mingw-w64-libraries/winpthreads/include/pthread_compat.h)（mingw-w64 MIT＋Lockless BSD-3-Clause）と[pthread_time.h](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/mingw-w64-libraries/winpthreads/include/pthread_time.h)（MIT）の全文表示をruntime noticeへ追加した。`libc++`はWin32 backendで、新たなwinpthreads library/object/importはない。固定sourceは未改変。根拠はaudit-20261027-074410-aa27613bの`parallel-runtime-source-review.json`（SHA-256 `3105780f46a44aa9fba28193932e9c17cd373e6d45bcd645310f236a4bb63b72`）と`licenses/`。本体のソース提供義務や用途制限はこの差分には確認されない。Windows x64の通常同梱入力へ採用し、公開配布全体の保留は維持する。
- DIO F0専用追加版は、上記並行解析版とmember／include／PE importsの差分なし、許諾文は完全一致。固定DIO／StoneMaskの同じ設定で不要なSP/AP解析を省き、追加の依存や条件はない。
- DIO／StoneMask追加版は同じ固定WORLDソースのBSD-3-Clause部分を追加コンパイル。元監査からのmember差分はlibc++.aのstdexcept.cpp.obj／stdlib_stdexcept.cpp.objのみで、既存のApache-2.0 WITH LLVM-exception表示の範囲。MinGW member・PE importsに追加なし。include差分は固定WORLDのdio.h／stonemask.hのみ。追加モデル・音源・別ライブラリーはない。
- 基準版のMinGW実リンク49 objectと内部source／headerを固定一次資料へ対応付け、今回の追加27件は上記差分監査で補った。数学処理18 objectは、[ZPLのpowと内部定義](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/mingw-w64-crt/math/x86/pow.def.h)、sin／cos／exp／log／sqrt／powi、またはPDのx87／丸め処理で、Cephes実装objectはこのDLLへリンクされていない。`math.h`のCephes記載は未使用cbrt系の宣言であり、実装の同梱とは区別する。ソース側の数値表示処理は[Lucentのgdtoa許諾](https://github.com/mingw-w64/mingw-w64/blob/0d6dfbc348dfad714761c0a7151615767f96bcb5/mingw-w64-crt/gdtoa/gdtoa.c)。基準版のinclude496件と今回の追加71件は固定WORLDとツールチェーン内に限定され、Wine由来LGPL実装の取り込みは確認されなかった。ツールチェーン全体や将来の別リンク構成まで解消したとは扱わない。
- この限定構成で確認した必要表示はWORLD／Ooura、[LLVM各runtimeのApache-2.0 WITH LLVM-exception](https://github.com/llvm/llvm-project/blob/llvmorg-23.1.4/libcxx/LICENSE.TXT)、MinGWのZPL／PDとLucentの著作権・条件・免責。名称による無断推奨をせず、改変したMinGWファイルを配る場合は変更と日付の表示を要する。本体のソース公開・再リンク用object提供・非商用限定の義務は対象部分には確認されない。実リンク対象の許諾・著作者表示の全文を`assets/world-runtime-LICENSE.txt`へ集約した。LLVM各runtimeの原文・現存するCREDITS、MinGWのCOPYING／PD免責、Lucentのgdtoa、Keith Marshallのstdio原文表示を保持し、未使用Cephes／Wine／getopt等の個別条件は実装採用として混入させない。元資料は独立監査出力の`licenses/`、根拠は`world.map`・`linked-members.txt`・`pe.stdout.txt`・`mingw-source-review.json`・`includes.txt`に保持する。`native/build-world-llvm.ps1`の再生成物は、member／include／importsと固定ソースの差分を確認し、追加調査の対象へ応じて再監査する。この固定LLVM版は実音源・実workerの回帰確認後、Windows x64の通常構成に採用する。DLLと3件の許諾文を同梱し、利用者の音源の条件は別に維持する。この部品の監査をアプリ全体の配布監査完了に代えない。

## 追加音源の条件調査（詳細）

2026-11-11確認。外部エンジンを利用者が別途用意する方式は撤回。以下の組込み候補は評価段階。東北大学の固定4モデルとCOPYRIGHTを取得し、無改変でWindows構成へ同梱する。全4モデルを製品workerで準備・同文連続PCM一致・モデル間差・有限出力として確認済み。アプリ全体の公開配布は引き続き保留。

- **HTS voice tohoku-f01**。東北大学 Intelligent Communication Network (Ito-Nose) Laboratory、Copyright 2015。固定rev `8e3306021db135c265f5eda5f062dc489707ddf8` のCOPYRIGHTは、neutralを含む4モデルをCC BY 4.0と明記する。商用利用、複製・再配布、改変を許諾する。配布時は著作権・作者名・許諾リンク・原典を表示し、改変した場合はその旨を明示する。権利者の推奨を示唆せず、音源に追加の法的・技術的制限を課さない。NC・ND・SA、用途の追加制限、本体のソース公開・再リンク要求は確認されない。生成音声については上流の別条件を確認できておらず、モデル自体の許諾と区別する。採用範囲は改変しないneutral・angry・happy・sadの4件。固定版のneutral 2,154,716 bytes／SHA-256 DED6ACB4243B93CC50896199D67B00A7C487662BFCDCD3F9F30A4FB0444B0B46、COPYRIGHT 1,765 bytes／SHA-256 F7F7A1A696F062704825FCC495D330BEEEAC8C0ED38D8F5BDD8E90BDC3471C95。追加原本はangry 1,830,649 bytes／SHA-256 C246AFAC0D41ADA4010DB80CA673B6B51CB8E16E9F3A47487D0DF3D2CA5B7EC8、happy 1,636,831 bytes／SHA-256 6BF9559A0757C5250165DE40DDA034A7E95504F3A3CE4ABCE58532CEE1040CC0、sad 2,005,039 bytes／SHA-256 1C76D4D5646C46E13410651C4232B95AF8B878E67AFD370A605B5DE792BA17EE。追加3件は公式固定Gitツリーのblob SHA-1・長さにも照合した。SHA-256は公式固定URLから取得した実体の計算値で、独立した上流公表SHA-256との照合ではない。CC公式の全文と、上流COPYRIGHT全文を保持した帰属文を用意した。 [同梱許諾全文](assets/tohoku-f01-LICENSE.txt) [同梱帰属文](assets/tohoku-f01-NOTICE.txt) [固定版COPYRIGHT](https://raw.githubusercontent.com/icn-lab/htsvoice-tohoku-f01/8e3306021db135c265f5eda5f062dc489707ddf8/COPYRIGHT.txt) [CC BY 4.0原文](https://creativecommons.org/licenses/by/4.0/legalcode)
- **MMDAgent Example 1.8内のMei 1.5／Takumi 1.0**。2026-11-11に公式ZIP SHA-256 `F702F2109A07DCA103C7B9A5123A25C6DDA038F0D7FCC899FF0281D07E873A63` と個別COPYRIGHTを実査。Mei全5・Takumi全4のHTSモデルを無改変で使用する。CC BY 3.0、名古屋工業大学（Mei 2009–2018／Takumi 2017–2018）とMMDAgent Project Teamへの帰属、許諾リンクを保持。公式補足は生成波形にも適用され、加工/改変時は変更を明示し同じ条件で配布する。利用者の収益化公開時の表示を選択欄へ案内し、許諾原文と補足は `assets/mmdagent-*-COPYRIGHT.txt` と `assets/mmdagent-voices-NOTICE.txt` を同梱する。3D素材や画像は取り込まない。 [公式ZIP](https://sourceforge.net/projects/mmdagent/files/MMDAgent_Example/MMDAgent_Example-1.8/) [公式補足](https://mmdagent.wordpress.com/2013/12/13/license-terms-of-for-the-all-files-other-than-the-3-d-model-mei/) [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/legalcode)

| HTSモデル（原本） | SHA-256 |
| --- | --- |
| mei_angry.htsvoice | 56A0759A4723CEC340E3BBA26199B64B9DF786F36C2851F7A15FB3D6D32B9471 |
| mei_bashful.htsvoice | 20F7ECD45E3DE5D9C43F28E4D9175C8DFEFB7A002CF4F03208D1431443626FBB |
| mei_happy.htsvoice | 93EC37412999548074249841C6339880E5ED34E946EEE1B4DFC19460307F8B2D |
| mei_normal.htsvoice | B19E8E5DDEF4D9D9559D654F395818E2BB3C0BBDBBCA533F96086DE21AC5DB2B |
| mei_sad.htsvoice | C06F110F7832C6E970B8E1D0870FF4F68A4F4D06CEABF8B7190A3C3892CB65F2 |
| takumi_angry.htsvoice | 773FD59058B976D2B5E8B1091B30ABE80BB38C521C99BCE2BD806E9E2E6EA173 |
| takumi_happy.htsvoice | D96F34309F3EA83322C0CFF53AA43362E42680DB350F966CE0A660A3A1292794 |
| takumi_normal.htsvoice | 9130CC911C881CC2C4C442A67374D8601B436F9DD415B555FE3C2D2C94444D42 |
| takumi_sad.htsvoice | F10A82F6B2A416FEB3332635C3F416698A14707E3337114A5ED81273C39E010D |

- **VOICEVOX CORE 0.17.1／VVM 0.16.4（Windows x64 CPU候補）**。2026-11-11に固定版の公式アーカイブ、許諾全文とWindows x64実DLLを照合。CPU専用の隔離ワーカーへ組み込み、同梱済みOpen JTalk辞書で合成を確認した。CORE C APIの候補は `voicevox_core-windows-x64-0.17.1.zip`（上流掲載SHA-256 `7e6a4f6d39c0a083e694a9191289df3b09725abfa676027228a72c01a2ae565b`）。COREはMITで著作権表示・許諾文を保持する。C APIはRuntime・辞書・モデルを別途必要とする。 [COREのライセンス](https://github.com/VOICEVOX/voicevox_core/blob/0.17.1/LICENSE) [CORE 0.17.1配布資産](https://github.com/VOICEVOX/voicevox_core/releases/expanded_assets/0.17.1) [固定版ダウンローダーガイド](https://github.com/VOICEVOX/voicevox_core/blob/0.17.1/docs/guide/user/downloader.md)
  - VOICEVOX ONNX RuntimeのCPU候補は `voicevox_onnxruntime-win-x64-1.23.2.tgz`（上流掲載SHA-256 `1bf9635d1c0ffd4e7345e0f628cecbabde65782c73c5051d52a52e7edf7ce11c`）。上流は製品版VVMを読むためにVOICEVOX ONNX Runtimeが必要と明記している。独自規約は商用・非商用の利用と組込み再配布を許諾し、生成音声に各音声ライブラリ規約を適用する。第三者に生成音声の利用を許諾するときは所定条件の遵守を義務付ける。逆コンパイル、リバースエンジニアリング、その方法の公開等を禁じる。最新の条件内で試用対象とするが、当該制限を取り除いて再配布することはできない。ビルド手順は `voicevox_onnxruntime.dll` と `TERMS.txt`、`third-party-notices.html` をパッケージ化する。CPU構成の追加GPUライブラリーは対象外。 [製品版VVMのRuntime要件](https://github.com/VOICEVOX/voicevox_core/blob/main/CHANGELOG.md) [Runtime 1.23.2の利用規約](https://github.com/VOICEVOX/onnxruntime-builder/releases/tag/voicevox_onnxruntime-1.23.2) [Runtime配布資産](https://github.com/VOICEVOX/onnxruntime-builder/releases/expanded_assets/voicevox_onnxruntime-1.23.2) [固定版のパッケージ化手順](https://github.com/VOICEVOX/onnxruntime-builder/blob/voicevox_onnxruntime-1.23.2/.github/workflows/build.yml#L603-L630)
  - VVM規約は商用・非商用の利用と組込み再配布を認め、VOICEVOXのクレジットを要求する。生成音声には各音声ライブラリ規約が適用され、第三者にその利用を許諾する場合は所定条件の遵守を義務付ける。配信・収益化・クレジット・用途条件を確認した話者とスタイルに対象を固定する。0.16.4は通常合成の候補。CORE 0.17.1のストリーミングトークにはVVM形式2が必要で、VVM側のストリーミング対応は0.17.0リリースに記載されている。 [VVM 0.16.4の規約](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md) [CORE 0.17.1リリース](https://github.com/VOICEVOX/voicevox_core/releases/tag/0.17.1) [VVM 0.17.0リリース](https://github.com/VOICEVOX/voicevox_vvm/releases/tag/0.17.0)
  - 固定版ダウンローダーの辞書は `r9y9/open_jtalk` のリリース `v1.11.1` の `open_jtalk_dic_utf_8-1.11.tar.gz`。既存製品の辞書は下記監査済みのOpen JTalk公式配布1.11。既存の監査済み辞書の読み込み・合成を確認して共用する。別の配布元から取った同名ファイルの同一性は仮定しない。 [固定版ダウンローダーソース](https://github.com/VOICEVOX/voicevox_core/blob/0.17.1/crates/downloader/src/main.rs#L75-L80) [辞書の配布資産](https://github.com/r9y9/open_jtalk/releases/expanded_assets/v1.11.1)
  - **固定実物と配布条件**：CORE DLL SHA-256 `EC9E730AE9E1AE897AECF78BF380B9D9C34120F8B4BE2D3844F52C9EFDE5BC91`、専用Runtime DLL `53BC7F528EC804FE4E80A57ED03BCECB580B1ED6F8EAE4B08613487E9135453E`。Microsoft公式VC143 14.44.35211の4DLLをアプリ内へ配置し、PE依存と必要export解決を照合した。固定入力の取得とSHAは `native/prepare-voicevox.ps1`。利用者に事前インストールを要求しない。Microsoftの利用者/再配布者への条件は既承認の全文をlicensesへ同梱する。CORE MIT、Runtime TERMS/third-party notices、VVM README/TERMSを保持し、話者別の表示・禁止用途は画面にも表示する。EigenのMPL-2.0全文と公開ORT基準版に対応するソースZIPも添付。ただし専用Runtimeのビルド元は非公開commit `6a4f734cbeb206ac09a2fd7fcda1bd45b689fe3c` で、公開ORTとの差分や静的同梱部品全体の独立照合は未完。試用で実DLLを確認したことと公開配布監査の完了を区別する。 [Microsoft再配布条件](https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files)
- **AivisSpeech Engine 1.2.0／Style-Bert-VITS2 2.7.0**。固定版AivisSpeech EngineはLGPL-3.0。mainの個別許諾案内を固定版への許可と扱わない。AIVMXは形式名で、モデル条件は個別に確認する。Style-Bert-VITS2のPythonパッケージはAGPL-3.0、user_dictはLGPL-3.0。候補と前処理資産の条件は下表とACML詳細へ集約する。 [Aivis固定版LICENSE](https://github.com/Aivis-Project/AivisSpeech-Engine/blob/1.2.0/LICENSE) [Style-Bert-VITS2固定版](https://github.com/litagin02/Style-Bert-VITS2/blob/2.7.0/pyproject.toml)
- **sherpa-onnx v2.0.1／Piper v1.7.0**。エンジンとモデルの許諾を分け、下表の固定日本語候補で判断する。sherpa-onnxはApache-2.0、PiperはGPL-3.0-or-later。ソース提供義務は最新指示で受入済み。個別の用途制限とモデルの商用・再配布条件は別に判断する。 [sherpa-onnxのライセンス](https://github.com/k2-fsa/sherpa-onnx/blob/v2.0.1/LICENSE) [Piper固定版配布設定](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/setup.py)

### 他エンジン・モデルの一括判断条件（2026-11-11）

Windowsオフライン同梱の条件を下表に整理する。固定実物の監査は後続の各詳細節を参照。2026-11-11の最新指示でソース提供義務とクレジットを受入済み。収益化・再配布可能で個別承諾不要なら実装対象とし、特定用途の明示がないだけでは除外しない。配布物の依存・LICENSE突合と実動は別途監査する。技術比較と一次資料の入口はRESEARCHの「Windows同梱候補の一括確認」、実装状況はPROJECT_STATUS。

| 候補 | 本体・同梱物の義務 | 音声・利用者への条件と未確認 |
| --- | --- | --- |
| MMDAgent Example 1.8のMei 1.5／Takumi 1.0 | 原本COPYRIGHTでモデルCC BY 3.0を確認。公式告知は生成波形も対象、帰属と加工物の同一許諾の補足あり | 原本9モデルを無改変で同梱対象。公開時のクレジット/リンクと加工時の条件を画面・許諾文に表示。3D素材のNCとは別で、画像等は含めない |
| AivisSpeech 1.2.0＋結亜ナル0.2.0 | エンジンLGPL-3.0。独立exe同梱でも当該engineの対応ソース提供。リンク形態なら置換/再リンク条件。結亜ナルは作者表示CC0、必須日本語ONNX DeBERTaはCC BY-SA4で帰属/変更表示/改変モデルの継承 | CC0モデルだけで全体を無条件としない。BERT/辞書/tokenizer/推移依存を別監査。アプリ全体の一律公開義務と、対象部品の義務を区別 |
| 阿井田 茂 AIVMX 1.0.0（ACML 1.0） | 個人法人商用・収益化配信・モデル再配布可、許諾全文同伴、作者クレジット任意。ACMLから本体のソース公開義務は課されない | 正当性を問わない批判活動・政治宗教への賛否の呼びかけ等も制限対象。任意入力アプリ特例とエンジンの別条件は下記詳細。最新採用基準で実装対象。現物監査は下記追加監査を参照 |
| Style-Bert-VITS2 2.7.0＋JVNV | 推論AGPL-3.0、user_dict部分LGPL-3.0。対応ソース提供、一体化した本体への適用、変更版ネット提供条件を判断。JVNV4モデルはCC BY-SA4 | モデル帰属と改変時継承。生成音声の表示範囲は未確認。小春音アミ/あみたろの別条件はこの4モデルへ混同しない |
| sherpa-onnx2.0.1＋Supertonic3 | engine Apache2、model OpenRAIL-M。許諾全文/帰属/変更表示、用途制限を実効性のある下流利用条件へ組込み通知 | 商用の一律禁止なし。機械生成表示、無許可なりすまし/中傷/他者加害の虚偽情報等禁止。医療助言等もAttachment A制限あり。§7の違反時利用制限/更新権留保・最新版利用への合理的努力も対象。実アーカイブと固定上流全文を突合済み。下記追加監査を参照 |
| Piper1.7.0＋Hi-Fi-CAPTAIN | engine GPL-3.0-or-later、対応ソース等の提供。一体化時の本体への適用は構成依存 | 公式が非商用データを理由に商用向け一覧から除外。重み/出力の許諾をデータ表記から断定しない。現在の一般向け同梱は見送り。再検討に必要な権利者確認は下記 |

[OpenRAIL-M全文](https://huggingface.co/Supertone/supertonic-3/blob/main/LICENSE)／[Aivis固定版](https://github.com/Aivis-Project/AivisSpeech-Engine/tree/1.2.0)／[ACML](https://github.com/Aivis-Project/ACML/blob/master/ACML-1.0.md)／[SBV2固定規約](https://github.com/litagin02/Style-Bert-VITS2/blob/2.7.0/docs/TERMS_OF_USE.md)。他の各モデル・配布原典はRESEARCHの同名行に記載。

#### ACML具体候補と配布条件

2026-11-11確認。候補は **阿井田 茂 AIVMX 1.0.0**、日本語Style-Bert-VITS2 JP-Extra／ONNX、ノーマルStyle ID 0（全7スタイル）。作者・公開者・CVは古山キリヲと表示され、オリジナルキャラクターとして公開。モデルUUID `47e53151-a378-46f3-abee-ce13aa07feb1`、話者UUID `561e4e59-3bc9-4726-9028-44a3c12a6f1d`、公称251.14 MB、SHA-256 `6dabe29de5ec2c1715e12a430805e1bff6ec64a315cccec2d26fad029df83243`。取得原本のSHAと埋込ACML全文を照合済み。 [作者の配布ページ](https://hub.aivis-project.com/aivm-models/47e53151-a378-46f3-abee-ce13aa07feb1)

同じ最新基準で、既調査の **澤原 玄二郎1.0.0**（ACML1.0、モデル `2e1fdde8-d089-42d7-b64f-cf89952b1bdc`、SHA `a17a053af538d51ec6294473f2d30e85580a05154bee56e0d6788dcf118f8c1f`）と **morioki1.0.0**（ACML1.0、`baaae3c0-7b22-4605-8ba5-80c959b41a48`、SHA `352858a737b23fe0bd0b7bd73a4956be262aad9bb24a7bf0d1eae9a71a466469`）も対象。別条件の **結亜ナル0.2.0** はCC0、`b79c40e4-8a5e-4f20-802c-2c0eba2f0983`、SHA `7cb87a8915569f2e689106ddc4ab23f8174fae78bbbffc7733d4be2e27743e34`。全4原本を取得し、SHAと埋込の許諾（ACML3件、結亜ナルCC0）を照合済み。 [澤原作者ページ](https://hub.aivis-project.com/aivm-models/2e1fdde8-d089-42d7-b64f-cf89952b1bdc) [morioki作者ページ](https://hub.aivis-project.com/aivm-models/baaae3c0-7b22-4605-8ba5-80c959b41a48) [結亜ナル作者ページ](https://hub.aivis-project.com/aivm-models/b79c40e4-8a5e-4f20-802c-2c0eba2f0983)

- **許可と義務**：ACML 1.0は条件内で改変・モデル同梱再配布・個人法人の商用を許可。再配布時は全文を添付し、作者クレジットは任意。本体ソースの公開・同一ライセンス化を要求する条項はない。収益化動画・ライブ配信も公式FAQが明示する。 [固定ACML原文](https://github.com/Aivis-Project/ACML/blob/867c0a1198686fdbf6ae14d1b067df828dd721df/ACML-1.0.md) [公式FAQ](https://note.com/aivis_project/n/n0ef78e29ddd1)
- **用途制限**：本人・公式と誤認させる利用、話者や作品の評価・尊厳を害する利用、実在の人物・団体・商品への批判/攻撃活動、欺くための虚偽情報、虚偽/誇大広告や問題のある事業、政治・宗教等への賛否を呼びかける活動、犯罪・反社会的利用は禁止。批判や政治等の主張は正当性を問わず対象で、違法行為や誹謗中傷だけに限定されない。一方「なるべく守ってほしいこと」は任意であり必須条件に変えない。禁止事項の正本は上記全文。
- **チャットアプリへの適用判断**：不特定の任意入力を合成する公開アプリには、開発元自身の遵守と現実的な範囲での禁止用途防止努力を条件とする特例がある。完全な技術的防止は要求されない。Tanayomiにも適用できると条文から判断するが、開発元や配信者自身による禁止用途を許可するものではない。採用時は用途条件の案内、非公式の合成音声と分かる表示、停止・除外手段を用意する案。これは実装案であり、特定のフィルター方式を原文が必須にしたわけではない。
- **別の同梱条件**：AivisSpeech Engine 1.2.0はLGPL-3.0、実際の固定SBV2 fork `1588a46595141a04652f9aae486f8a625c04ce55` はAGPL-3.0で、使用するコードと変更を含む対応ソース等の提供が別途必要。独立プロセスへ分離した構成で本体との結合と配布範囲を確認する。日本語ONNX DeBERTaはCC BY-SA 4.0で帰属・許諾表示・変更表示、改変物の同条件配布が必要。これを本体全体の公開義務と混同しない。辞書・tokenizer・native・CRT等の実配布物監査は別途必要。ソース提供等は最新指示で承認済み。 [固定エンジンLICENSE](https://github.com/Aivis-Project/AivisSpeech-Engine/blob/1.2.0/LICENSE) [固定依存](https://github.com/Aivis-Project/AivisSpeech-Engine/blob/1.2.0/pyproject.toml) [日本語ONNX DeBERTa](https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx)
- **現物の扱い**：埋込の許諾と上記表示の一致、SHA、推論資産の監査結果は下記追加監査を参照。AIVMXは画像・試聴音声も格納可能なので、それらの別利用をモデル許諾だけで許可されたと扱わない。不要な付随素材は配布対象から外し、加工した場合は別のSHAを固定する。原本と加工物を別SHAで保持する。 [AIVMX仕様](https://github.com/Aivis-Project/aivmlib/blob/master/Readme.md#aivmx-file-format-specification) [Hub規約（サービスと個別モデルの条件を区別）](https://hub.aivis-project.com/terms-of-service/)

#### 同梱実物の追加監査（2026-11-11）

- Aivisの4原本は埋込許諾とSHAを照合。製品では話者アイコンを自作の代替画像へ変更し、スタイル画像・試聴素材を除外する。ONNXグラフ・重み・スタイルベクトルは変更しない。原本/派生物のSHA、版、作者、公開元は `native/aivis/voices.json` と生成packageの `voices.json` に固定する。許諾本文は `resources/aivis/licenses/voices/`、日本語BERTの固定版は `d701ec67708287b20d2063270f6b535e6eed09ab`（FP16 SHA `23f633ae7c5900ff82b35a428b67a54e7e7911d5d6a6dcfc77967be8f1c94dc6`）。元モデル・変換者のCC BY-SA4帰属と変更内容をNOTICEへ収録する。
- Aivis推論はCPython 3.11.9の埋込配布、日本語用に固定した60個のwheel/sdistと使用Pythonソースを同梱する。正確な版・SHA・取得元は `native/aivis/wheels.json`。元のHTTPサーバー・Sentry・Hubの自動取得・AppData設定処理は除去し、stdio通信とローカル資産へ変更。無音trimの全index配列を両端探索へ変更し、改変済みソース・固定原本へ適用する生成スクリプト・変更表示を同梱する。使用したLGPL/AGPLソースに加え、Tanayomiの変更・生成スクリプト、python-soxr 0.5.0.post1と内包libsoxrの対応ソース、num2wordsの対応ソース、ONNX Runtime 1.23.0が固定するEigen `1d8b82b0740839c0de7f1242a3585e3390ff5f33` とMPL全文を `resources/aivis/licenses/sources/` 等へ同梱。各wheelの許諾・NOTICEも保持する。追加ユーザー辞書21点と「何」補助モデルは別許諾を確定できず除外し、NAIST系システム辞書とSudachiDict Coreを使う。本体公開ライセンスと公開配布全体の判断は保留のまま。
- JVNVはlitaginの固定版 `205830ca1d49e666ddfbf2a755f0108e9cade4dd` のF1/F2/M1/M2（各safetensors/config/style_vectors）をSHA固定し、上流SBV2のONNX exporterで変換。任意のonnxsim最適化を省略し、ONNX checkerを通したグラフをAIVMXへ格納する。変換後もCC BY-SA4で、原モデルのlitagin、JVNVコーパスの6著者への帰属、変更内容、許諾全文、原本/変換後SHAを同梱する。変換スクリプト・環境lockも対応ソースへ収録。生成音声へSAが自動で適用されるとは断定せず、画面で出典と条件を案内する。元safetensorsや開発用PyTorchは製品に含めない。 [固定モデル](https://huggingface.co/litagin/style_bert_vits2_jvnv/tree/205830ca1d49e666ddfbf2a755f0108e9cade4dd) [コーパス原典](https://sites.google.com/site/shinnosuketakamichi/research-topics/jvnv_corpus)
- Supertonicはsherpa-onnx **v2.0.1 Windows x64 shared MD Release**（archive SHA `c3c78220db8bebf97d5f932496165bb3897b163410427d7c021013439c80f183`）、公式INT8変換モデル **2026-05-11**（SHA `82fa96f91c4ef8abaae3a14a3f4153facf88bed821d1f7331cec2700f432c427`）を採用。ONNX Runtime 1.28.2、VC143 14.44.35211、Eigen対応ソース2系統とMPL全文、Kaldi/OpenFst/UTF8処理等の全同梱通知を `resources/supertonic/licenses/` へ配置する。変換アーカイブのMIT通知は変換コードの条件で、重みのOpenRAIL-Mを置換しない。
- Supertonicの上流規約は固定commit `724fb5abbf5502583fb520898d45929e62f02c0b` の全文15,007bytes、SHA `0D944A9110FED9A9602D60E0423A272903E7BD21AB060490774EFC77C2275E9F`。アプリで本文を閲覧して明示同意し、規約版のSHAを保存する。機械生成表示とAttachment Aの禁止用途、§7を含む全文を下流へ通知・同意対象とし、不同意時は標準音声を利用できる。表示操作と復元はSPECを参照。 [固定上流規約](https://huggingface.co/Supertone/supertonic-3/blob/724fb5abbf5502583fb520898d45929e62f02c0b/LICENSE)
- 小春音アミ・あみたろは生成音声の商用条件とは別に、音声素材配布元の2026-10-23改定規約表がソフト同梱等を事前相談としている。本件の「個別承諾不要」対象から外す。研究・実験利用の相談不要を製品同梱へ拡大しない。 [素材元の利用規約](https://amitaro.net/voice/voice_rule/) [SBV2固定版モデル規約](https://raw.githubusercontent.com/litagin02/Style-Bert-VITS2/2.7.0/docs/TERMS_OF_USE.md)
- VOICEVOX固定VVMの共通規約はファイルのアプリ組込み再配布を認め、話者別条件は生成音声の利用へ適用する。同梱する21VVMのうち13/14/15/18/21/24には対象外7話者28スタイルも混載されるが、許可した95スタイル以外はアプリとワーカーで生成を拒否する。対象外専用の6/7/16/17/20は同梱しない。これは話者の生成音声を個別許諾なしで利用可能にするものではない。逆解析・独自のVVM分割は行わない。 [固定VVM共通規約](https://raw.githubusercontent.com/VOICEVOX/voicevox_vvm/0.16.4/README.md)

#### Piper＋Hi-Fi-CAPTAINの結論と再検討条件

2026-11-11確認。対象はPiper **v1.7.0** と `ja_JP-hi_fi_captain-medium`（22,050Hz、男女2話者、ONNX公称76,753,841 bytes）。固定ONNXの改名commit `9bfdf190bebcb712500c7076aee7ea9c0365aa78` では旧 `ja_JA` と重みは同一。checkpointカード `e358e1eb6a04a0fcc8a7c50d83f8bd157c397896` はHi-Fi-CAPTAINデータのCC BY-NC-SA 4.0とLibriTTS-R baseからの微調整を記載する。ONNXカード全文の取得は未成功で、改名差分とcheckpointカードで照合した。 [公式ONNX改名差分](https://huggingface.co/rhasspy/piper-voices/commit/9bfdf190bebcb712500c7076aee7ea9c0365aa78) [学習者のcheckpointカード](https://huggingface.co/datasets/rhasspy/piper-checkpoints/commit/e358e1eb6a04a0fcc8a7c50d83f8bd157c397896)

**現在の一般向け同梱は見送る。** Piper公式の許諾説明commit `31c3011` は、プロジェクトから重みへの独自許諾を与えず、MODEL_CARDのLicense欄は録音データを指すと説明。商用を認める学習データのみを使う一覧から `hi_fi_captain` を明示的に除外している。これは本件で商用同梱・収益配信を許可済みと扱えない根拠であり、モデルや出力へNC・SAが自動適用されるとの法的断定ではない。 [Piper公式のモデル許諾・商用条件](https://github.com/OHF-Voice/piper1-gpl/commit/31c3011)

- **学習元の未確定点**：同じ作者の専用 `_base_model` はLibriTTS-Rから新規学習したCC BY 4.0データ由来で、日本語カードの説明と整合する。一方、別経路 `libritts_r/medium` は旧Lessac系から後日再学習版へ変わった。日本語カードは固定base IDを示さないため、どちらかを推定採用せず、現行baseの許諾を過去の日本語重みへ遡及しない。 [専用baseの原典](https://huggingface.co/datasets/rhasspy/piper-checkpoints/commit/8e12c3ffc938570a3f31d730dfe4e7b9cd6d7a86) [別経路の置換](https://huggingface.co/datasets/rhasspy/piper-checkpoints/commit/a56f74664555184d36626f6bc9ceb295410466fd) [LibriTTS-Rの原典](https://www.openslr.org/141/)
- **エンジンの義務**：v1.7.0はGPL-3.0-or-later。独立CLIを通常の引数/テキスト/PCMで呼び出す集合物として扱えるかは結合度に依存し、別プロセスだけで本体への影響なしと保証しない。採用するならPiperおよび同梱GPL部品の著作権・許諾文、対応ソースと必要なビルド/導入スクリプトを提供する。ダウンロード配布でGPLv3 §6(d)を使うなら、ソースへ追加料金なく同等にアクセスできる配置が必要。これだけではモデルの許諾不足は解消しない。 [v1.7.0配布設定](https://github.com/OHF-Voice/piper1-gpl/blob/v1.7.0/setup.py) [GPLv3 §5・§6](https://www.gnu.org/licenses/gpl-3.0.html) [集合物の説明](https://www.gnu.org/licenses/gpl-faq.html#MereAggregation)
- **再検討に必要な確認を一括化**：学習者には固定ONNX/設定の再配布と個人法人の商用許諾、全baseの固定ID/SHAと条件、生成音声の収益化ライブ・アーカイブ・切り抜き利用と表示を確認する。NICT等のデータ権利者には、このモデルの商用組込みと生成音声利用に適用するデータ条件・話者許諾の範囲と、必要なら追加許諾を確認する。利用者の採用承認は第三者の許諾に代わらない。NICTのHi-Fi-CAPTAIN公式ページは確認時メンテナンス中で原規約を読めず、別モデルFIRNetの条件を本モデルへ転用しない。問い合わせ送信はしていない。 [NICT配布元](https://ast-astrec.nict.go.jp/en/release/hi-fi-captain/)

### VOICEVOX VVM 0.16.4の全話者条件（Windows組込み候補）

2026-11-11確認。固定版の対応表にある通常トーク43話者とNemoトーク9話者の計52話者を対象とする。ソングにもある話者は重複計上せず、ユーレイちゃんのツクモちゃんは同じ話者のスタイルとして記載する。里石ユカは「つぼみ」のみ収録。以下は生成した通常トーク音声の利用条件。最新基準で試用対象41話者95スタイル、対象外11話者、判断待ち0。個別の禁止用途や素材利用条件は残る。 [固定版の話者・スタイル・VVM対応表](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#音声モデルvvmファイルと声キャラクタースタイル名とスタイル-id-の対応表) [固定版配布資産](https://github.com/VOICEVOX/voicevox_vvm/releases/expanded_assets/0.16.4)

VVM共通規約が許諾する再配布はアプリ組込み。生成音声を第三者に利用許諾するときは話者規約と条件継承を適用する。共通規約はVOICEVOXの表示を求め、逆コンパイル・リバースエンジニアリングやその方法の公開、製作者・第三者に不利益をもたらす行為、公序良俗違反を禁じる。音声素材の販売、公式画像・ロゴ・立ち絵、キャラクターの利用には各権利者の条件が別に適用される。収益化配信の明示がない行では、一般の商用許諾に基づく判断を記載した。 [VVM固定版共通規約](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#voicevox-音声モデル-利用規約)

#### 通常トークを試用対象とする41話者

| 話者 | 個人・法人・収益化配信 | 帰属・主な制限・出典 |
| --- | --- | --- |
| 四国めたん、ずんだもん、九州そら、中国うさぎ、中部つるぎ、東北ずん子、東北きりたん、東北イタコ、あんこもん（9） | 商用可。音声の法人地域条件や個別承認条項は見当たらない | `VOICEVOX:話者名` を動画・概要欄・アプリ紹介等に表示。無表記商用は有償契約。政治・宗教活動、特定の個人・団体の非難・応援目的、情報商材、意図的な虚偽の拡散、指定業態・反社会的勢力等に制限。キャラクター素材は地域別法人条件等が別にある。 [音源規約](https://zunko.jp/con_ongen_kiyaku.html) [キャラクター規約](https://zunko.jp/guideline.html) |
| 玄野武宏、白上虎太郎、雀松朱司、麒ヶ島宗麟、黒沢冴白（5） | 商用可。通常の法人利用・収益化配信に事前承認条項は見当たらない | `VOICEVOX:話者名`。省略と他者の公衆送信用音声素材の販売は要許可。犯罪助長、権利侵害、政治団体・宗教の宣伝等、特定対象の毀損、反社会的勢力、学習用利用等に制限。 [VirVox規約](https://www.virvoxproject.com/voicevoxの利用規約) |
| Nemo 女声1、女声2、女声3、女声4、女声5、女声6、男声1、男声2、男声3（9） | 商用可。法人・収益化配信への追加承認条項は見当たらない | `VOICEVOX Nemo`。著しい信用・イメージ毀損、権利侵害、著作情報の偽装、犯罪助長、機械学習等は禁止。公式はキャラクターなしの音声として案内。 [Nemo音声規約](https://voicevox.hiroshiba.jp/nemo/term/) [Nemo公式案内](https://voicevox.hiroshiba.jp/nemo/) |
| 雨晴はう（1） | 個人・企業の商用可 | `VOICEVOX` は必須、話者名併記は推奨。R18音声利用を控える。VOICEVOXで直接生成していない音声や出力から作ったボイスチェンジャーの利用は禁止。固定版READMEに個別規約の項目がない。 [雨晴はう規約](https://amehau.com/rules/amehare-hau-rule) |
| 冥鳴ひまり（1） | 個人・法人、収益化とも申請・報告不要 | `VOICEVOX:冥鳴ひまり`。公序良俗、第三者への不利益・損害、特定思想・団体に関する用途等に制限。 [冥鳴ひまり規約](https://www.meimeihimari.com/terms-of-use) |
| ちび式じい（1） | 商用可。クレジット付き通常利用に法人の追加承認条項は見当たらない | `VOICEVOX:ちび式じい`。成人向け・キャラクター虐待、差別、犯罪、政治・宗教等の特定主張に制限。無表記商用等の有償条項あり。 [権利者規約](https://docs.google.com/presentation/d/1AcD8zXkfzKFf2ertHwWRwJuQXjNnijMxhz7AJzEkaI4/htmlpresent) |
| 小夜/SAYO（1） | 音声は商用可。通常の法人利用・収益化配信に追加承認条項は見当たらない | `VOICEVOX:小夜/SAYO` または `VOICEVOX:小夜`。誹謗中傷、権利侵害、著しいイメージ毀損、犯罪助長等は禁止。音声素材配布は帰属条件を継承。公式素材・法人二次創作に別条件。 [小夜/SAYO規約](https://316soramegu.wixsite.com/sayo-official/guideline) |
| ナースロボ＿タイプＴ（1） | 音声は個人・法人・団体の商用利用に連絡不要 | `VOICEVOX:ナースロボ＿タイプＴ`。学習、他者利用向け音声素材の無許可販売、AIによる声変換、AI生成画像・動画への声当て等は禁止。公式素材の企業商用は別条件。 [倉成町私立病院規約](https://www.krnr.top/rules) |
| 春歌ナナ（1） | 通常トークの商用・アプリ利用可。歌唱の商用は事前連絡 | `VOICEVOX:春歌ナナ`。公序良俗、誹謗中傷、権利侵害、特定思想・団体等に制限。公式画像・ロゴには別条件。 [春歌ナナ規約](https://nanahira.jp/haruka_nana/guideline.html) |
| あいえるたん（1） | 商用可。収益化配信、機器、ゲーム・アプリを明示 | `VOICEVOX:あいえるたん`。成人向け、特定思想・宗教・政治的発言、名誉毀損、権利侵害、公式誤認等は禁止。 [あいえるたん規約](https://infiniteloop.co.jp/special/aieru-tan/terms/) |
| 満別花丸（1） | 個人・法人の商用、収益化に連絡不要 | `VOICEVOX:満別花丸`。無表記は連絡。公序良俗、反社会的勢力、極端な政治的賛否、他者への損害、公式の詐称等は禁止。公式素材の商品化は要相談。 [満別花丸規約](https://100hanamaru.wixsite.com/manbetsu-hanamaru/rule) |

| 夜語トバリ、暁記ミタマ（2） | 生成音声はソフト側条件で商用可 | `VOICEVOX:夜語トバリ`／`VOICEVOX:暁記ミタマ`。公式本文がVOICEVOX使用範囲をソフト規約へ委ねる。キャラクターや公式素材の法人商用等の問い合わせ条件を音声のみへ拡大しない。 [権利者規約](https://yogataritobari.studio.site/#rules) |
| 春日部つむぎ（1） | 通常商用可、帰属必須 | `VOICEVOX:春日部つむぎ`。誹謗中傷、権利侵害、特定思想・団体に関する用途、生成音声素材の二次配布・自作発言等は禁止。モデルの組込み許諾は生成音声の再配布とは別。 [公式規約](https://tsumugi-official.studio.site/rule) |
| 波音リツ（1） | 権利者本文が商用可・使用ソフトの規約優先を明記 | `VOICEVOX:波音リツ`。 [権利者規約](https://www.canon-voice.com/terms/) |
| 剣崎雌雄（1） | 権利者の音声条項が商用可を明記 | `VOICEVOX:剣崎雌雄`。犯罪、詐欺・脅迫、特定思想・政治運動への勧誘、意図的な医療誤情報等は禁止。 [権利者投稿](https://frontier.creatia.cc/fanclubs/413/posts/4507) |
| WhiteCUL（1） | 固定VVM商用許諾と旧公式索引の全8条を照合、通常商用可 | `VOICEVOX:WhiteCUL`。省略、他者の公衆送信用素材販売は要許可。権利侵害・信用毀損・学習等は禁止。現行旧ドメインはパーキング転送のため画面では固定VVM規約へ案内する。 [固定規約](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#whitecul) |
| 櫻歌ミコ（1） | 個人法人の商用、生成音声を含む作品の公開・配布・販売を明示 | `VOICEVOX:櫻歌ミコ`。誹謗中傷、実在する特定政治・宗教の支持/批判、犯罪助長、情報商材等は禁止。 [公式規約](https://voicevox35miko.studio.site/rule) |
| †聖騎士 紅桜†（1） | 2026-10-25改定で個人法人商用・収益化は事前許可不要 | `VOICEVOX †聖騎士 紅桜†`。公式公開データの4収益区分も全て許可。 [規約](https://commons.nicovideo.jp/works/nc296132) [公式公開データ](https://public-api.commons.nicovideo.jp/v1/materials/296132) |
| 栗田まろん（1） | VOICEVOX音声の通常商用可 | `VOICEVOX:栗田まろん`。キャラクター利用と一部の法人スポンサー案件には個別相談条件があるため該当案件は対象外と画面へ明記。通常の収益化と分ける。 [エーアイ規約](https://aivoice.jp/character/maron/) |
| 離途（1） | 個人法人の生成音声波形の商用可 | `VOICEVOX 離途`。法人キャラクター利用は別許諾。政治・宗教目的、学習、過激な成人向け作品の一般公開等は禁止。サイト素材の再配布禁止と公式VVM組込み許諾を区別する。 [LitMus規約](https://litmus9.com/#/voicebank#rules) |

#### 個別承認・用途の限定により対象外の11話者

| 話者 | 個人商用・収益化 | 法人・帰属・主な制限・出典 |
| --- | --- | --- |
| 青山龍星（1） | 一般個人はクレジット付き商用可。個人事業主や企業スポンサー契約で該当する利用は収益の有無を問わず事前許可 | 法人はななはぴへ事前許可。`VOICEVOX:青山龍星` の省略不可。VirVoxの共通禁止事項を適用。 [VirVox規約](https://www.virvoxproject.com/voicevoxの利用規約) |
| 後鬼（1） | 個人の商用可 | 企業商用は事前確認。`VOICEVOX:後鬼`。別キャラクターへの声当ては原則禁止、商用は相談。クレジット付きナレーション可。 [後鬼規約](https://ついなちゃん.com/voice_terms/) |
| もち子さん（1） | 個人の無料公開動画・配信の収益化等は可。有料公開、用途外、音声作品、ゲーム等に制限 | 企業商用は事前に利用許可。`VOICEVOX もち子さん(cv明日葉よもぎ)`。無断の音声素材配布・販売、学習、ボイスチェンジャー等は禁止。 [もち子さん規約](https://vtubermochio.wixsite.com/mochizora/利用規約) |
| 猫使アル、猫使ビィ（2） | クレジット付き個人商用可 | 企業による公開商用利用は事前問い合わせ。社内等の非公開利用は例外。`VOICEVOX:猫使アル`／`VOICEVOX:猫使ビィ`。R18作品の音声のみ利用、学習等は禁止。公式画像の再配布は禁止。 [猫使公式規約](https://nekotukarb.wixsite.com/nekonohako/利用規約) |
| No.7（1） | 個人・同人の広告収益や同人活動に例外。通常商用は相談 | 企業アカウントの広告収益、テレビ・ラジオ等は相談。`VOICEVOX:No.7`。権利侵害、イメージ毀損、独占利用、反社会的勢力等は禁止。 [No.7公式規約](https://voiceseven.com/#j0200) |
| ユーレイちゃん（ツクモちゃんスタイルを含む）（1） | 個人・同人の広告収益や実費回収等に例外。一般商用・営利は要許可 | 法人は要許可。権利者表記例は `VOICEVOX:ユーレイちゃん（CV:神崎零）`／`VOICEVOXツクモちゃん（CV:神崎零）`、権利者ページではCV名任意。公式誤認、イメージ毀損、占有等は禁止。 [ユーステラ規約](https://u-stella.co.jp/voicevox-ure-chan-tucumo-terms-of-use/) |
| Voidoll（1） | 個人商用可と固定READMEに明記 | 法人は個別問い合わせ。`VOICEVOX:Voidoll(CV:丹下桜)`。権利者告知ページ本文を取得できず、細目・配信例外は未確認。 [固定版規約](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#voidoll) [権利者告知](https://blog.nicovideo.jp/niconews/224589.html) |
| ぞん子（1） | 商用は個別問い合わせ。収益化配信の扱いは一次本文未確認 | 法人商用も問い合わせ対象。`VOICEVOX:ぞん子`。権利者ページ本文を取得できず、禁止用途・例外は未確認。 [固定版規約](https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#ぞん子) [権利者ガイドライン](https://zonko.zone-energy.jp/guideline) |
| 琴詠ニア（1） | 個人の無料配信収益化のみ許可 | 個人その他/法人の収益化はCONTACT_REQUIRED、インターネット限定。 [規約](https://commons.nicovideo.jp/works/nc315435) [公式公開データ](https://public-api.commons.nicovideo.jp/v1/materials/315435) |
| 里石ユカ（つぼみ）（1） | 一部の動画収益化は報告不要 | 2026-11-05改定の音源・キャラクター規約で企業利用は事前相談、音声合成を含むAIによる二次創作は事前許可。 [権利者規約](https://satoishiyuka.wixsite.com/satoishi/kiyaku) |

2026-11-11の追加モデル対応native監査：モデル容量境界のみ2MiBから3MiBへ拡張したWindows DLLはSHA-256 `2653321ed26bd15c974ebd023cb28302e5ef9c38a40617a89e1e7588f68290d6`。固定toolchain archive・compile flags・PE import/export・linked membersは既採用監査と一致し、include集合も一時展開パスを正規化して一致した。新しいruntime部品は追加せず、既存の`open-jtalk-runtime-LICENSE.txt`を保持する。監査出力は`.local/build/open-jtalk/audit-20261111-100248-14e31a95/`、既存NITのnative32項目を確認した。公開配布全体の承認とは区別する。

### 2026-10-24の調査記録

確認日：2026-10-24。候補の条件確認と、製品の実リンク・同梱・配布監査は区別する。実パッケージを確認した範囲を明記し、以下だけで同梱完了とはしない。

- **VOICEVOX外部API**：[公式連携仕様](https://github.com/VOICEVOX/voicevox_engine/blob/master/docs/VOICEVOX音声合成エンジンとの連携.md)の公開JSON／WAVだけを独自クライアントで扱い、ENGINE／CORE／モデル／既存クライアントのコードを取り込まずリンク・同梱しない。この限定構成で本体のソース公開・再リンク義務を課す条項は確認できず、実装対象とする。[ENGINE LGPLv3](https://github.com/VOICEVOX/voicevox_engine/blob/master/LGPL_LICENSE)の独立ApplicationとCombined Workの区別に基づき、HTTPなら常に同条件を免れると一般化しない。
- **VOICEVOXの同梱候補との区別**：[ENGINE](https://github.com/VOICEVOX/voicevox_engine/blob/master/LICENSE)はLGPLv3または個別ライセンス、[CORE](https://github.com/VOICEVOX/voicevox_core#ライセンス)の0.16以降はMIT。[VVM規約](https://github.com/VOICEVOX/voicevox_vvm#voicevox-音声モデル-利用規約)は組込み再配布を明示許諾するが、各音声ライブラリ条件・必要クレジットは別。製品ソフト一式の無断再配布禁止とVVMの許諾を混同しない。いずれも今回の外部API対応では同梱しない。
- **Open JTalk／HTS**：以下の公式固定アーカイブの実物を2026-10-24に取得し、COPYING・モデルヘッダーを確認した。記載hashは公式配布から実取得したファイルのSHA-256で、独立した上流署名・公開SHA-256との照合済みとはしない。Windows x64の推論専用DLL・辞書・NIT音源を通常ビルドへ同梱する。実製品workerの連続合成と不正入力拒否を確認。5件の許諾文をlicensesへ保持し、他OSへの同梱やアプリ全体の公開配布監査完了とはしない。

  | 固定対象・公式配布 | 圧縮bytes | 実取得SHA-256 |
  | --- | ---: | --- |
  | [Open JTalk 1.11 source](https://downloads.sourceforge.net/open-jtalk/open_jtalk-1.11.tar.gz) | 19,464,975 | `20fdc6aeb6c757866034abc175820573db43e4284707c866fcd02c8ec18de71f` |
  | [UTF-8辞書 1.11 binary](https://downloads.sourceforge.net/open-jtalk/open_jtalk_dic_utf_8-1.11.tar.gz) | 23,643,819 | `33e9cd251bc41aa2bd7ca36f57abbf61eae3543ca25ca892ae345e394cb10549` |
  | [hts_engine API 1.10 source](https://downloads.sourceforge.net/hts-engine/hts_engine_API-1.10.tar.gz) | 169,852 | `e2132be5860d8fb4a460be766454cfd7c3e21cf67b509c48e1804feab14968f7` |
  | [NIT ATR503 M001 1.05 model](https://downloads.sourceforge.net/open-jtalk/hts_voice_nitech_jp_atr503_m001-1.05.tar.gz) | 818,909 | `2e555c88482267b2931c7dbc7ecc0e3df140d6f68fc913aa4822f336c9e0adfc` |

  - 本体`COPYING`、`mecab/COPYING`、`mecab-naist-jdic/COPYING`はBSD-3-Clause形式。本体の名古屋工業大学2008–2016（README等には2008–2018も存在）、MeCabのTaku Kudo2001–2008／NTT2004–2008、辞書のNAIST2009／UniDic Consortium2011–2017／名古屋工業大学2008–2016の著作権・条件・免責を保持する。辞書binaryのCOPYINGはsource内辞書COPYINGとbyte hash一致（SHA-256 `f4eca42ebd930e2c6e57fca58319d989bebcd1510cb7714b149c50f5425135ea`）。ソース公開・再リンク・非商用限定・用途制限は当該COPYINGにない。[公式本体説明](https://open-jtalk.sourceforge.net/readme_open_jtalk.php)の商用利用連絡・論文引用はお願いとして記載され、必須の別許諾としない。
  - HTS 1.10の実COPYINGは[公式説明](https://hts-engine.sourceforge.net/readme_hts_engine_API.php.html)と同じBSD形式。名古屋工業大学2001–2015／東京工業大学2001–2008、条件・免責・無断推奨禁止を保持する。学習に使うHTK／HTS toolkit／SPTKや英語Flite一式は、推論runtimeに必要な部品として採用しない。
  - モデルtarには`COPYING`・`README`・`INSTALL`と`nitech_jp_atr503_m001.htsvoice`（1,169,264bytes）だけがあり、COPYINGは訓練元NIT日本語DBとHTS Voice双方のCC BY 3.0を明記する。NAIST辞書の条件とは別。名古屋工業大学2003–2012／東京工業大学2003–2008の表示、名称「NIT ATR503 M001」、[原典](https://open-jtalk.sourceforge.net/readme_hts_voice_nitech_jp_atr503_m001.php)、[CC BY 3.0](https://creativecommons.org/licenses/by/3.0/legalcode)を保持し、モデル改変時は変更を明示する。NC・ND・SAや本体のソース公開・再リンク義務はない。モデルをアプリ全体のライセンスへ変更せず、モデルの再配布条件を利用者へ示す。生成音声の公開時の帰属扱いは、モデル自体の配布と区別して案内する。
  - 本体の設定にはGNU libiconvの自動検出やlibstdc++へのリンク経路があるため、そのままのビルドを採用しない。監査用DLLはUTF-8／PCM出力専用で、MeCabの辞書書込み関数・学習用L-BFGSを実リンク対象から除いている。HTSの致命的エラーとWindows UTF-8パス対応は自前bridgeと作業コピー上の限定変更で扱い、元アーカイブを変更しない。上流条件は[本体・MeCab](assets/open-jtalk-LICENSE.txt)・[HTS](assets/hts-engine-LICENSE.txt)・[辞書](assets/open-jtalk-dictionary-LICENSE.txt)・[NIT音声](assets/nit-atr503-LICENSE.txt)へ保持する。コンパイラーruntime・追加object・include・PE依存はこのDLL固有の監査対象で、WORLDの監査結果を自動適用しない。
  - Windows x64監査DLLのSHA-256は`13c88534b8d422d5d16c4c8bd5f377f9585f2b6ed7a796f77cdfbbc31b6670f8`。固定LLVM23.1.4／MinGW-w64 `0d6dfbc348dfad714761c0a7151615767f96bcb5`で、実リンク122 unique entry（MinGW実装84objectとimport member1件を含む）とincludeを独立照合した。PE依存はWindowsのKERNEL32.dll／msvcrt.dllのみで、GNU libiconv／libgcc／libstdc++／winpthreads libraryは未リンク。静的runtimeのLLVM例外付きApache-2.0、MinGWのPublic Domain／ZPL／Lucent／Keith等の条件は[専用runtime許諾文](assets/open-jtalk-runtime-LICENSE.txt)へ全文保持する（SHA-256 `6ae3ba39a687031b8cd88a05131b0818d6ee4496a4d14dbe395439ea9f6740ba`）。includeされた`pthread_compat.h`のMIT／Lockless BSD-3-Clauseと`pthread_time.h`のMIT表示は、winpthreads library未リンクでも保持する。この固定DLLに対する確認であり、再生成時は実リンクmember・include・importsの差分を再監査する。製品採用・公開配布の承認とはしない。
- **COEIROINK**：[2026-10-21更新規約](https://coeiroink.com/terms)はローカルAPI連携を認めるが、ソフト・公式モデルの再配布は禁止。声別条件・クレジット・用途条件は別。外部連携の候補に限定し、同梱はしない。[API入口](https://coeiroink.com/help/002)、[配布OS](https://coeiroink.com/download)。
- **A.I.VOICE**：[v1 API規約](https://aivoice.jp/manual/editor/api.html#termsandconditions)は正規製品が前提で、法人利用・個人の商用API利用は問い合わせ対象。[v2仕様](https://aivoice.jp/about/aivoice2/)はAPI連携なし。音声利用許諾とAPI条件を分け、判断待ち。
- **UTAU外部エンジン**：[UTAU公式](https://utau2008.xrea.jp/)と[OpenUtau対応説明](https://github.com/openutau/OpenUtau/wiki/Resamplers-and-Wavtools)だけから任意resampler／wavtoolの同梱を許諾済みとはできない。エンジン別に対象版・条件を確認する。
- **OS標準音声の利用範囲**：OS音声資産は同梱しない。生成音声の公開・商用利用が無条件とは限らず、たとえば[macOS Tahoe 26 SLA §2.F](https://www.apple.com/legal/sla/docs/macOSTahoe.pdf#page=3)には個人・非商用利用と公開等の制約がある。手元で聞く用途と配信へ音声を載せる用途を分け、他OSへこの条件を一般化しない。OS音声API呼び出しを実装することと、任意音声の公開許諾は別。






2026-11-23 NIT長文境界修正：Windows DLL SHA-256 b3c483117d983336fc152805a8732f8e5e7dfd6d00a1b84ec04a0315dc9c8966。監査 .local/build/open-jtalk/audit-20261123-180305-2d4109a3。固定toolchain、PE import/export、linked membersは既採用版と一致し、追加依存なし。NIT専用のNJD短休止挿入のみ変更。許諾文は従来通り保持。

2026-11-23 間延びモード追加：Windows Open JTalk DLL SHA-256 200c9249e57df9bef944e59b083054c667a96629aa8586300b04f025b3192fd7。audit-20261123-183016-4b48d01c。既存ABIを維持してtoj_synthesize_drawn_outを追加。toolchain/import/linked members従来一致、追加依存なし。
