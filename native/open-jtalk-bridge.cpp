// Tanayomi's bounded inference-only adapter. See the pinned upstream COPYING
// files for Open JTalk/MeCab, HTS and the separately licensed dictionary/voice.
// No playback, training, file output or native object pointer crosses the ABI.
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <string>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#endif
#include "open-jtalk-local.h"
#include "mecab.h"
#include "njd.h"
#include "jpcommon.h"
#include "HTS_engine.h"
#include "text2mecab.h"
#include "mecab2njd.h"
#include "njd_set_pronunciation.h"
#include "njd_set_digit.h"
#include "njd_set_accent_phrase.h"
#include "njd_set_accent_type.h"
#include "njd_set_unvoiced_vowel.h"
#include "njd_set_long_vowel.h"
#include "njd2jpcommon.h"

#ifdef _WIN32
#define TOJ_EXPORT extern "C" __declspec(dllexport)
#else
#define TOJ_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr int kRate = 48000;
constexpr int kPeriod = 240;
constexpr int kMaximumSamples = kRate * 45;
constexpr int kMaximumTextBytes = 4096;
constexpr int kMaximumCharacters = 1024;
constexpr int kMaximumPathBytes = 32768;
constexpr int kMaximumPhones = 8192;
constexpr int kPhoneSlotBytes = 32;
constexpr int kMaximumFrames = kMaximumSamples / kPeriod;
enum Status { Ok = 0, Invalid = 1, NotLoaded = 2, DataError = 3, Capacity = 4, Poisoned = 5, Busy = 6 };
std::atomic_flag busy = ATOMIC_FLAG_INIT;
bool poisoned = false;
struct Gate {
    bool acquired = !busy.test_and_set(std::memory_order_acquire);
    ~Gate() { if (acquired) busy.clear(std::memory_order_release); }
};
struct State {
    bool boundedNitechBreaths = false;
    Mecab mecab{};
    NJD njd{};
    JPCommon common{};
    HTS_Engine hts{};
    State() {
        Mecab_initialize(&mecab); NJD_initialize(&njd);
        JPCommon_initialize(&common); HTS_Engine_initialize(&hts);
    }
    void refresh() {
        HTS_Engine_refresh(&hts); JPCommon_refresh(&common);
        NJD_refresh(&njd); Mecab_refresh(&mecab);
    }
    void clear() {
        HTS_Engine_clear(&hts); JPCommon_clear(&common);
        NJD_clear(&njd); Mecab_clear(&mecab);
    }
};
// A fatal upstream exception poisons this process. Its partially initialized
// state is intentionally retained for OS reclamation, never cleared/reused.
// PC callers must terminate/reap that worker before starting another one.
State* state = nullptr;

bool utf8(const char* bytes, int length, int maximumBytes, int maximumCharacters) {
    if (!bytes || length < 1 || length > maximumBytes) return false;
    int characters = 0;
    for (int i = 0; i < length;) {
        const unsigned a = static_cast<unsigned char>(bytes[i++]);
        unsigned value; int count; unsigned minimum;
        if (a > 0 && a < 0x80) { value = a; count = 0; minimum = 0; }
        else if (a >= 0xc2 && a <= 0xdf) { value = a & 31; count = 1; minimum = 0x80; }
        else if (a >= 0xe0 && a <= 0xef) { value = a & 15; count = 2; minimum = 0x800; }
        else if (a >= 0xf0 && a <= 0xf4) { value = a & 7; count = 3; minimum = 0x10000; }
        else return false;
        if (i > length - count) return false;
        for (int j = 0; j < count; ++j) {
            const unsigned next = static_cast<unsigned char>(bytes[i++]);
            if ((next & 0xc0) != 0x80) return false;
            value = value * 64 + (next & 63);
        }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return false;
        if (++characters > maximumCharacters) return false;
    }
    return true;
}

bool file_size(const std::string& path, long maximum) {
    FILE* file = toj_fopen_utf8(path.c_str(), "rb");
    if (!file) return false;
    const bool seeked = std::fseek(file, 0, SEEK_END) == 0;
    const long length = seeked ? std::ftell(file) : -1;
    std::fclose(file);
    return length > 0 && length <= maximum;
}

bool bounded_files(const std::string& directory, const std::string& voice) {
    if (!file_size(voice, 3 * 1024 * 1024)) return false;
    for (const char* name : {"char.bin", "matrix.bin", "sys.dic", "unk.dic"}) {
        if (!file_size(directory + "/" + name, 128 * 1024 * 1024)) return false;
    }
    return true;
}

void normal_clear() {
    if (state) { state->clear(); delete state; state = nullptr; }
}

struct NjdNodeDeleter {
    void operator()(NJDNode* node) const noexcept {
        if (node) { NJDNode_clear(node); std::free(node); }
    }
};

bool bound_nitech_breaths(NJD* njd) {
    // The pinned 48-kHz nitech_jp_atr503_m001 duration tree first tests
    // C-Acc_Fw-Pos-in_Br_Acc<=7. In a single long breath, later phrases
    // can reach PDF196 (106 frames) irrespective of the current phone.
    // Insert only a short pause at an existing accent-phrase boundary;
    // retain every original pronunciation, accent and chain flag.
    constexpr const char* comma = "\xe3\x80\x81";
    constexpr const char* question = "\xef\xbc\x9f";
    constexpr const char* longVowel = "\xe3\x83\xbc";
    int accents = 0;
    for (auto* node = njd->head; node; node = node->next) {
        const char* pron = NJDNode_get_pron(node);
        if (std::strcmp(pron, comma) == 0 || std::strcmp(pron, question) == 0) {
            accents = 0;
            continue;
        }
        if (NJDNode_get_mora_size(node) <= 0 || std::strcmp(pron, "*") == 0) continue;
        if (accents != 0 && NJDNode_get_chain_flag(node) == 1) continue;
        if (accents == 7) {
            // Never split a chained phrase or attach a pause to an initial
            // long-vowel mark (JPCommon would discard that mark after a pause).
            if (!node->prev || std::strncmp(pron, longVowel, 3) == 0) return false;
            std::unique_ptr<NJDNode, NjdNodeDeleter> pause(
                static_cast<NJDNode*>(std::calloc(1, sizeof(NJDNode))));
            if (!pause) return false;
            NJDNode_initialize(pause.get());
            NJDNode_set_string(pause.get(), comma);
            NJDNode_set_orig(pause.get(), comma);
            NJDNode_set_read(pause.get(), comma);
            NJDNode_set_pron(pause.get(), comma);
            NJDNode_set_pos(pause.get(), "\xe8\xa8\x98\xe5\x8f\xb7"); // 記号
            NJDNode_set_pos_group1(pause.get(), "\xe8\xaa\xad\xe7\x82\xb9"); // 読点
            NJDNode_set_pos_group2(pause.get(), "*");
            NJDNode_set_pos_group3(pause.get(), "*");
            NJDNode_set_ctype(pause.get(), "*");
            NJDNode_set_cform(pause.get(), "*");
            NJDNode_set_chain_flag(pause.get(), 0);
            if (!pause->string || !pause->orig || !pause->read || !pause->pron || !pause->pos ||
                !pause->pos_group1 || !pause->pos_group2 || !pause->pos_group3 || !pause->ctype || !pause->cform)
                return false;
            NJDNode_insert(node->prev, node, pause.get());
            pause.release(); // NJD owns the linked node and clears it on refresh.
            accents = 0;
        }
        ++accents;
    }
    return true;
}

// Both exits use exactly the same frontend and state-duration generation.
int generate_state(const char* textUtf8, int textBytes, bool voicedPlan = false, bool drawnOutMode = false) {
    const std::string text(textUtf8, static_cast<size_t>(textBytes));
    // The pinned UTF-8 rules expand ASCII to at most three bytes per input byte.
    std::vector<char> normalized(static_cast<size_t>(textBytes) * 3 + 1, '\0');
    text2mecab(normalized.data(), text.c_str());
    if (!Mecab_analysis(&state->mecab, normalized.data())) return DataError;
    const int features = Mecab_get_size(&state->mecab);
    if (features < 1 || features > 4096) return DataError;
    mecab2njd(&state->njd, Mecab_get_feature(&state->mecab), features);
    njd_set_pronunciation(&state->njd); njd_set_digit(&state->njd);
    njd_set_accent_phrase(&state->njd); njd_set_accent_type(&state->njd);
    njd_set_unvoiced_vowel(&state->njd); njd_set_long_vowel(&state->njd);
    if (state->boundedNitechBreaths && !drawnOutMode && !bound_nitech_breaths(&state->njd)) return DataError;
    njd2jpcommon(&state->common, &state->njd);
    JPCommon_make_label(&state->common);
    const int labels = JPCommon_get_label_size(&state->common);
    if (labels <= 2 || labels > kMaximumPhones) return DataError;
    auto featuresForHts = JPCommon_get_label_feature(&state->common);
    std::vector<std::string> voicedLabels;
    std::vector<char*> labelPointers;
    if (voicedPlan) {
        // Explicit audit variant only. Change the five phoneme-context fields
        // consistently; retain accent/mora/boundary context and the original
        // frontend labels. This predicts voiced-vowel duration rather than
        // stretching a devoiced-vowel prediction by a word-specific constant.
        voicedLabels.reserve(labels); labelPointers.reserve(labels);
        for (int i = 0; i < labels; ++i) {
            voicedLabels.emplace_back(featuresForHts[i]);
            auto& label = voicedLabels.back();
            const auto boundary = label.find('/');
            if (boundary == std::string::npos) return DataError;
            for (size_t j = 0; j < boundary; ++j)
                if (label[j] == 'A' || label[j] == 'E' || label[j] == 'I' || label[j] == 'O' || label[j] == 'U')
                    label[j] = static_cast<char>(label[j] + ('a' - 'A'));
            labelPointers.push_back(&label[0]);
        }
        featuresForHts = labelPointers.data();
    }
    if (!HTS_Engine_generate_state_sequence_from_strings(&state->hts,
            featuresForHts, static_cast<size_t>(labels))) return DataError;
    return Ok;
}

bool supported_plan_model() {
    const auto& model = state->hts.ms;
    return model.num_voices == 1 && model.num_states == 5 && model.num_streams == 3 &&
        model.fullcontext_format && std::strcmp(model.fullcontext_format, "HTS_TTS_JPN") == 0 &&
        model.fullcontext_version && std::strcmp(model.fullcontext_version, "1.0") == 0 &&
        model.stream_type && std::strcmp(model.stream_type, "MCP,LF0,LPF") == 0 &&
        model.stream && model.stream[0] && model.stream[0][1].vector_length == 1 &&
        model.stream[0][1].is_msd == TRUE && model.stream[0][0].is_msd == FALSE &&
        model.stream[0][2].is_msd == FALSE;
}

bool phone_name(const char* label, char* slot) {
    if (!label) return false;
    size_t length = 0;
    while (length <= 4096 && label[length]) ++length;
    if (length > 4096) return false;
    const char* end = label + length;
    const char* first = std::find(label, end, '-');
    if (first == end) return false;
    ++first;
    const char* last = std::find(first, end, '+');
    const auto count = last - first;
    if (last == end || count < 1 || count >= kPhoneSlotBytes) return false;
    for (auto at = first; at != last; ++at) {
        const auto c = static_cast<unsigned char>(*at);
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '_')) return false;
    }
    std::memcpy(slot, first, static_cast<size_t>(count));
    return true;
}
}

// Working-copy HTS file input uses this shim instead of the system ANSI locale.
// The bridge itself is not built with a global fopen replacement.
extern "C" FILE* toj_fopen_utf8(const char* path, const char* mode) {
#ifdef _WIN32
    if (!path || !mode) return nullptr;
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, nullptr, 0);
    if (count < 1 || count > 32768) return nullptr;
    std::vector<wchar_t> wide(static_cast<size_t>(count));
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide.data(), count)) return nullptr;
    wchar_t wideMode[8]{};
    size_t i = 0;
    for (; mode[i] && i < 7; ++i) {
        if (static_cast<unsigned char>(mode[i]) > 127) return nullptr;
        wideMode[i] = static_cast<wchar_t>(mode[i]);
    }
    if (mode[i]) return nullptr;
    return _wfopen(wide.data(), wideMode);
#else
    return std::fopen(path, mode);
#endif
}

#ifdef _WIN32
extern "C" HANDLE toj_create_file_utf8(const char* path, DWORD access, DWORD share,
    LPSECURITY_ATTRIBUTES security, DWORD creation, DWORD flags, HANDLE templateFile) {
    if (!path) return INVALID_HANDLE_VALUE;
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, nullptr, 0);
    if (count < 1 || count > 32768) return INVALID_HANDLE_VALUE;
    std::vector<wchar_t> wide(static_cast<size_t>(count));
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide.data(), count)) return INVALID_HANDLE_VALUE;
    return CreateFileW(wide.data(), access, share, security, creation, flags, templateFile);
}
#endif

TOJ_EXPORT int toj_abi_version() noexcept { return 1; }

// Status 5 is terminal for this native process, including future load/clear.
TOJ_EXPORT int toj_load(const char* dictionaryUtf8, int dictionaryBytes,
    const char* voiceUtf8, int voiceBytes) noexcept {
    Gate gate;
    if (!gate.acquired) return Busy;
    if (poisoned) return Poisoned;
    try {
#ifdef _WIN32
        // Windows hosts must also set this before NativeLibrary.Load: a static
        // runtime may assert during DLL initialization. This is process-local.
        if (_set_error_mode(1) == -1) { poisoned = true; return Poisoned; } // _OUT_TO_STDERR
#endif
        if (!utf8(dictionaryUtf8, dictionaryBytes, kMaximumPathBytes, kMaximumPathBytes) ||
            !utf8(voiceUtf8, voiceBytes, kMaximumPathBytes, kMaximumPathBytes)) return Invalid;
        const std::string directory(dictionaryUtf8, static_cast<size_t>(dictionaryBytes));
        const std::string voice(voiceUtf8, static_cast<size_t>(voiceBytes));
        if (!bounded_files(directory, voice)) return DataError;
        normal_clear();
        state = new State();
        char* voices[] = {const_cast<char*>(voice.c_str())};
        if (!Mecab_load(&state->mecab, directory.c_str()) || !HTS_Engine_load(&state->hts, voices, 1)) {
            normal_clear(); return DataError;
        }
        const char* format = HTS_Engine_get_fullcontext_label_format(&state->hts);
        if (!format || std::strcmp(format, "HTS_TTS_JPN") != 0 ||
            HTS_Engine_get_sampling_frequency(&state->hts) != kRate || HTS_Engine_get_fperiod(&state->hts) != kPeriod) {
            normal_clear(); return DataError;
        }
        // The managed host verifies the model SHA; the basename guard keeps
        // this model-specific duration workaround off other 48-kHz voices.
        const auto separator = voice.find_last_of("/\\");
        state->boundedNitechBreaths = voice.substr(separator == std::string::npos ? 0 : separator + 1) ==
            "nitech_jp_atr503_m001.htsvoice";
        HTS_Engine_set_audio_buff_size(&state->hts, 0);
        return Ok;
    } catch (...) { poisoned = true; return Poisoned; }
}

static int synthesize_mode(const char* textUtf8, int textBytes, int16_t* output,
    int outputCapacity, int* outputCount, int* sampleRate, bool drawnOutMode) noexcept {
    Gate gate;
    if (!gate.acquired) return Busy;
    if (!outputCount || !sampleRate) return Invalid;
    *outputCount = 0; *sampleRate = 0;
    if (poisoned) return Poisoned;
    try {
        if (!state) return NotLoaded;
        if (!utf8(textUtf8, textBytes, kMaximumTextBytes, kMaximumCharacters) || !output ||
            outputCapacity < 1 || outputCapacity > kMaximumSamples ||
            reinterpret_cast<uintptr_t>(output) % alignof(int16_t)) return Invalid;
        const int prepared = generate_state(textUtf8, textBytes, false, drawnOutMode);
        if (prepared != Ok) { state->refresh(); return prepared; }
        const size_t frames = state->hts.sss.total_frame; // Public pinned HTS state: engine getter reads the not-yet-generated waveform.
        if (frames < 1 || frames > static_cast<size_t>(kMaximumSamples / kPeriod) ||
            frames > static_cast<size_t>(outputCapacity / kPeriod)) { state->refresh(); return Capacity; }
        // Bounds are checked before HTS allocates the parameter/waveform arrays.
        if (!HTS_Engine_generate_parameter_sequence(&state->hts) || !HTS_Engine_generate_sample_sequence(&state->hts)) {
            state->refresh(); return DataError;
        }
        const size_t count = HTS_Engine_get_nsamples(&state->hts);
        if (count != frames * kPeriod || count > static_cast<size_t>(outputCapacity)) { poisoned = true; return Poisoned; }
        double peak = 0;
        for (size_t i = 0; i < count; ++i) {
            const double value = HTS_Engine_get_generated_speech(&state->hts, i);
            if (!std::isfinite(value)) { poisoned = true; return Poisoned; }
            peak = std::max(peak, std::abs(value));
        }
        if (peak < 1e-6) { state->refresh(); return DataError; }
        const double gain = std::min(1.0, 0.92 * 32767 / peak);
        for (size_t i = 0; i < count; ++i)
            output[i] = static_cast<int16_t>(std::lround(HTS_Engine_get_generated_speech(&state->hts, i) * gain));
        state->refresh();
        *outputCount = static_cast<int>(count); *sampleRate = kRate;
        return Ok;
    } catch (...) { poisoned = true; return Poisoned; }
}

TOJ_EXPORT int toj_synthesize(const char* textUtf8, int textBytes, int16_t* output,
    int outputCapacity, int* outputCount, int* sampleRate) noexcept {
    return synthesize_mode(textUtf8, textBytes, output, outputCapacity, outputCount, sampleRate, false);
}

// Additive ABI v1: reproduce the original NIT frontend without inserted pauses.
// The ordinary export and all planner calls retain their existing behavior.
TOJ_EXPORT int toj_synthesize_drawn_out(const char* textUtf8, int textBytes, int16_t* output,
    int outputCapacity, int* outputCount, int* sampleRate) noexcept {
    return synthesize_mode(textUtf8, textBytes, output, outputCapacity, outputCount, sampleRate, true);
}

// Additive ABI v1 comparison exit. Caller owns all buffers; no native pointer is
// returned. Slots are 32-byte ASCII/NUL/zero padded. Durations are [start,end)
// 5-ms frames, including sil/pau. LF0 is natural-log Hz on voiced frames and 0
// otherwise. Maximum payload: 8192*(32+4+4)+9000*(8+1)=408680 bytes.
// Counts/rate/period stay zero on failure; payload arrays are committed only
// after validation and successful refresh. A capacity failure is retryable.
TOJ_EXPORT int toj_plan(const char* textUtf8, int textBytes,
    uint8_t* phoneSlots, int phoneCapacity, int32_t* startFrames, int32_t* endFrames,
    double* lf0, uint8_t* voiced, int frameCapacity,
    int32_t* outputPhones, int32_t* outputFrames, int32_t* sampleRate, int32_t* framePeriod) noexcept {
    if (!outputPhones || !outputFrames || !sampleRate || !framePeriod ||
        reinterpret_cast<uintptr_t>(outputPhones) % alignof(int32_t) ||
        reinterpret_cast<uintptr_t>(outputFrames) % alignof(int32_t) ||
        reinterpret_cast<uintptr_t>(sampleRate) % alignof(int32_t) ||
        reinterpret_cast<uintptr_t>(framePeriod) % alignof(int32_t)) return Invalid;
    *outputPhones = 0; *outputFrames = 0; *sampleRate = 0; *framePeriod = 0;
    Gate gate;
    if (!gate.acquired) return Busy;
    if (poisoned) return Poisoned;
    try {
        if (!state) return NotLoaded;
        if (!utf8(textUtf8, textBytes, kMaximumTextBytes, kMaximumCharacters) ||
            !phoneSlots || !startFrames || !endFrames || !lf0 || !voiced ||
            phoneCapacity < 1 || phoneCapacity > kMaximumPhones ||
            frameCapacity < 1 || frameCapacity > kMaximumFrames ||
            reinterpret_cast<uintptr_t>(startFrames) % alignof(int32_t) ||
            reinterpret_cast<uintptr_t>(endFrames) % alignof(int32_t) ||
            reinterpret_cast<uintptr_t>(lf0) % alignof(double)) return Invalid;
        const auto reject = [](int status) { state->refresh(); return status; };
        if (!supported_plan_model()) return reject(DataError);
        const int prepared = generate_state(textUtf8, textBytes,
#ifdef TANAYOMI_AUDIT_VOICED_PLAN
            true
#else
            false
#endif
        );
        if (prepared != Ok) return reject(prepared);
        const auto& sss = state->hts.sss;
        const size_t phones = state->hts.label.size;
        const size_t frames = sss.total_frame;
        if (phones < 1 || phones > kMaximumPhones || phones != static_cast<size_t>(JPCommon_get_label_size(&state->common)) ||
            sss.nstate != 5 || sss.nstream != 3 || !sss.duration ||
            sss.total_state != phones * 5) return reject(DataError);
        if (frames < 1 || frames > kMaximumFrames || phones > static_cast<size_t>(phoneCapacity) ||
            frames > static_cast<size_t>(frameCapacity)) return reject(Capacity);
        std::vector<uint8_t> names(phones * kPhoneSlotBytes, 0);
        std::vector<int32_t> starts(phones), ends(phones);
        size_t frame = 0;
        const HTS_LabelString* label = state->hts.label.head;
        for (size_t phone = 0; phone < phones; ++phone) {
            if (!label || !phone_name(label->name, reinterpret_cast<char*>(names.data() + phone * kPhoneSlotBytes)))
                return reject(DataError);
            starts[phone] = static_cast<int32_t>(frame);
            for (size_t index = 0; index < 5; ++index) {
                const size_t duration = sss.duration[phone * 5 + index];
                if (duration < 1 || duration > frames - frame) return reject(DataError);
                frame += duration;
            }
            ends[phone] = static_cast<int32_t>(frame);
            label = label->next;
        }
        if (frame != frames || label) return reject(DataError);
        // Parameter generation retains context duration/F0 but never allocates
        // the generated waveform or invokes the vocoder/audio output.
        if (!HTS_Engine_generate_parameter_sequence(&state->hts)) return reject(DataError);
        const auto& pss = state->hts.pss;
        if (pss.nstream != 3 || pss.total_frame != frames || !pss.pstream) return reject(DataError);
        const auto& pitch = pss.pstream[1]; // Validated MCP,LF0,LPF model mapping.
        if (pitch.vector_length != 1 || pitch.length > frames || !pitch.msd_flag ||
            (pitch.length && !pitch.par)) return reject(DataError);
        std::vector<double> localLf0(frames, 0);
        std::vector<uint8_t> localVoiced(frames, 0);
        const double minimumLf0 = std::log(40.0), maximumLf0 = std::log(1200.0);
        size_t pitchIndex = 0;
        for (size_t at = 0; at < frames; ++at) {
            if (pitch.msd_flag[at] != TRUE && pitch.msd_flag[at] != FALSE) return reject(DataError);
            if (pitch.msd_flag[at] == FALSE) continue;
            if (pitchIndex >= pitch.length || !pitch.par[pitchIndex]) return reject(DataError);
            const double value = pitch.par[pitchIndex++][0];
            if (!std::isfinite(value) || value < minimumLf0 || value > maximumLf0) return reject(DataError);
            localLf0[at] = value; localVoiced[at] = 1;
        }
        if (pitchIndex != pitch.length) return reject(DataError);
        state->refresh();
        std::memcpy(phoneSlots, names.data(), names.size());
        std::memcpy(startFrames, starts.data(), starts.size() * sizeof(int32_t));
        std::memcpy(endFrames, ends.data(), ends.size() * sizeof(int32_t));
        std::memcpy(lf0, localLf0.data(), localLf0.size() * sizeof(double));
        std::memcpy(voiced, localVoiced.data(), localVoiced.size());
        *outputPhones = static_cast<int32_t>(phones); *outputFrames = static_cast<int32_t>(frames);
        *sampleRate = kRate; *framePeriod = kPeriod;
        return Ok;
    } catch (...) { poisoned = true; return Poisoned; }
}

TOJ_EXPORT int toj_clear() noexcept {
    Gate gate;
    if (!gate.acquired) return Busy;
    if (poisoned) return Poisoned;
    try { normal_clear(); return Ok; }
    catch (...) { poisoned = true; return Poisoned; }
}
