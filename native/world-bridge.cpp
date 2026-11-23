// Tanayomi's caller-owned, flat-array ABI around the pinned WORLD implementation.
// Run this DLL in an expendable worker. C++ exceptions become status codes;
// invalid native pointers, access violations and termination require process isolation.
// ABI v1: 44100 Hz, 5 ms frames, frame-major matrices of fft_size/2+1 doubles.
#include <algorithm>
#include <atomic>
#include <cfenv>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <limits>
#include <new>
#include <thread>
#include <vector>
#if defined(_WIN32) && defined(__x86_64__) && defined(__MINGW32__) && defined(__clang__)
#include <xmmintrin.h>
#endif
#include "world/cheaptrick.h"
#include "world/d4c.h"
#include "world/harvest.h"
#include "world/dio.h"
#include "world/stonemask.h"
#include "world/synthesis.h"

#ifdef _WIN32
#define TW_EXPORT extern "C" __declspec(dllexport) std::int32_t __cdecl
#else
#define TW_EXPORT extern "C" __attribute__((visibility("default"))) std::int32_t
#endif

namespace {
constexpr int kRate = 44100;
constexpr double kFrameMs = 5.0;
constexpr int kMinimumInput = kRate / 10;  // Harvest's very short-input edge is excluded.
constexpr int kMaximumInput = kRate * 45;
constexpr int kMaximumOutput = kRate * 45;
constexpr int kMaximumAnalysisFrames = 9001;
constexpr int kMaximumSynthesisFrames = 9001;
enum Status : std::int32_t { Ok = 0, Argument = 1, Length = 2, Value = 3, Allocation = 4, Native = 5 };

int FftSize() {
    CheapTrickOption option{};
    InitializeCheapTrickOption(kRate, &option);
    return GetFFTSizeForCheapTrick(kRate, &option);
}
int Frames(int samples) { return GetSamplesForHarvest(kRate, samples, kFrameMs); }
bool InputLength(int samples) { return samples >= kMinimumInput && samples <= kMaximumInput; }
bool MatrixLength(int frames, int count, int fft) {
    const std::int64_t product = static_cast<std::int64_t>(frames) * (fft / 2 + 1);
    return product > 0 && product <= std::numeric_limits<std::int32_t>::max() && count == product;
}
bool ValidValues(const double* values, int count, double minimum, double maximum) {
    for (int i = 0; i < count; ++i)
        if (!std::isfinite(values[i]) || values[i] < minimum || values[i] > maximum) return false;
    return true;
}
bool ValidF0(const double* f0, int count) {
    for (int i = 0; i < count; ++i)
        if (!std::isfinite(f0[i]) || (f0[i] != 0.0 && (f0[i] < 40.0 || f0[i] > 1200.0))) return false;
    return true;
}
bool Overlaps(const double* a, int a_count, const double* b, int b_count) {
    const auto first = reinterpret_cast<std::uintptr_t>(a);
    const auto second = reinterpret_cast<std::uintptr_t>(b);
    const auto a_bytes = static_cast<std::uintptr_t>(a_count) * sizeof(double);
    const auto b_bytes = static_cast<std::uintptr_t>(b_count) * sizeof(double);
    if (first > std::numeric_limits<std::uintptr_t>::max() - a_bytes ||
        second > std::numeric_limits<std::uintptr_t>::max() - b_bytes) return true;
    return first < second + b_bytes && second < first + a_bytes;
}
template<class T> std::vector<T*> Rows(T* flat, int frames, int fft) {
    std::vector<T*> rows(static_cast<std::size_t>(frames));
    const auto width = static_cast<std::size_t>(fft / 2 + 1);
    for (int i = 0; i < frames; ++i) rows[i] = flat + static_cast<std::size_t>(i) * width;
    return rows;
}

// Optional audit exports only. The ordinary entry points never enter here.
// This FP representation is pinned to the audited LLVM/MinGW Win64 headers.
#if defined(_WIN32) && defined(__x86_64__) && defined(__MINGW32__) && defined(__clang__)
struct ParallelFpEnvironment {
    std::fenv_t environment{};
    unsigned int mxcsr{};
};
bool CaptureParallelFp(ParallelFpEnvironment& state) noexcept {
    if (std::fegetenv(&state.environment) != 0) return false;
    state.mxcsr = _mm_getcsr();
    return true;
}
bool SupportedParallelFp(const ParallelFpEnvironment& state) noexcept {
    // Only the worker's masked, nearest-rounding/default-SSE controls are
    // eligible. Sticky status bits may be set by the preceding F0 analysis.
    // Nondefault trap/round/flush controls retain the original serial path.
    return (state.mxcsr & ~0x3fu) == 0x1f80u &&
        (state.environment._Fe_ctl & (_MCW_EM | _MCW_RC | _EM_AMBIGUOUS)) == _MCW_EM;
}
bool ApplyParallelFp(const ParallelFpEnvironment& state) noexcept {
    if (std::fesetenv(&state.environment) != 0) return false;
    _mm_setcsr(state.mxcsr);
    ParallelFpEnvironment actual{};
    return CaptureParallelFp(actual) && actual.mxcsr == state.mxcsr &&
        actual.environment._Fe_ctl == state.environment._Fe_ctl &&
        actual.environment._Fe_stat == state.environment._Fe_stat;
}
void JoinParallelThread(std::thread& thread) noexcept {
    try { thread.join(); }
    catch (...) {
        // Returning while native work may still use caller pointers is unsafe.
        // End the expendable worker without an unhandled-exception OS dialog;
        // its owner observes EOF and reaps the process. Never detach this work.
        std::_Exit(4);
    }
}
struct ParallelFpFailure {};
#endif

template<class CheapStage, class D4cStage>
bool RunParallelStages(CheapStage cheap_stage, D4cStage d4c_stage) {
#if defined(_WIN32) && defined(__x86_64__) && defined(__MINGW32__) && defined(__clang__)
    ParallelFpEnvironment caller{};
    if (!CaptureParallelFp(caller) || !SupportedParallelFp(caller)) return false;
    std::atomic<int> ready{0}; // 1: verified FP environment; 2: serial fallback.
    std::atomic<bool> begin{false};
    std::exception_ptr cheap_error, d4c_error;
    bool restore_failed = false;
    std::thread thread;
    try {
        thread = std::thread([&]() noexcept {
            ParallelFpEnvironment original{};
            if (!CaptureParallelFp(original)) {
                ready.store(2, std::memory_order_release);
                return;
            }
            // Verify both application and restoration before either algorithm
            // starts. No caller FP state or output has changed on fallback.
            if (!ApplyParallelFp(caller) || !ApplyParallelFp(original) || !ApplyParallelFp(caller)) {
                (void)ApplyParallelFp(original);
                ready.store(2, std::memory_order_release);
                return;
            }
            ready.store(1, std::memory_order_release);
            while (!begin.load(std::memory_order_acquire)) std::this_thread::yield();
            try { d4c_stage(); }
            catch (...) { d4c_error = std::current_exception(); }
            restore_failed = !ApplyParallelFp(original);
        });
    } catch (...) {
        // The fixed libc++ constructor throws only before it transfers a
        // successfully created thread. Both algorithms remain unstarted.
        return false;
    }
    int state;
    while ((state = ready.load(std::memory_order_acquire)) == 0) std::this_thread::yield();
    if (state == 2) { JoinParallelThread(thread); return false; }
    begin.store(true, std::memory_order_release);
    try { cheap_stage(); }
    catch (...) { cheap_error = std::current_exception(); }
    JoinParallelThread(thread);
    // Match original stage exception priority after all borrowers have ended.
    if (cheap_error) std::rethrow_exception(cheap_error);
    if (d4c_error) std::rethrow_exception(d4c_error);
    if (restore_failed) throw ParallelFpFailure{};
    return true;
#else
    (void)cheap_stage; (void)d4c_stage;
    return false;
#endif
}
}  // namespace

TW_EXPORT tw_world_abi_version() noexcept { return 1; }

// Status is authoritative. On a nonzero result every output buffer is invalid.
TW_EXPORT tw_world_layout(std::int32_t sample_count, std::int32_t* frames,
    std::int32_t* fft_size) noexcept {
    if (!frames || !fft_size || frames == fft_size) return Argument;
    *frames = *fft_size = 0;
    if (!InputLength(sample_count)) return Length;
    try {
        const int count = Frames(sample_count);
        const int fft = FftSize();
        if (count < 2 || count > kMaximumAnalysisFrames || fft < 2 || fft > 4096) return Native;
        *frames = count; *fft_size = fft;
        return Ok;
    } catch (const std::bad_alloc&) { return Allocation; }
    catch (...) { return Native; }
}

static std::int32_t Analyze(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count,
    double* spectrum, std::int32_t spectrum_count, double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size, bool fast, bool parallel) noexcept {
    if (!samples || !f0 || !time_axis || !spectrum || !aperiodicity) return Argument;
    if (!InputLength(sample_count)) return Length;
    try {
        const int frames = Frames(sample_count);
        const int fft = FftSize();
        if (fft_size != fft) return Argument;
        if (frames < 2 || frames > kMaximumAnalysisFrames || f0_count != frames || time_count != frames ||
            !MatrixLength(frames, spectrum_count, fft) || !MatrixLength(frames, aperiodicity_count, fft)) return Length;
        const double* arrays[] = { samples, f0, time_axis, spectrum, aperiodicity };
        const int counts[] = { sample_count, f0_count, time_count, spectrum_count, aperiodicity_count };
        for (int i = 0; i < 5; ++i)
            for (int j = i + 1; j < 5; ++j)
                if (Overlaps(arrays[i], counts[i], arrays[j], counts[j])) return Argument;
        if (!ValidValues(samples, sample_count, -1.0, 1.0)) return Value;

        auto spectral_rows = Rows(spectrum, frames, fft);
        auto aperiodic_rows = Rows(aperiodicity, frames, fft);
        HarvestOption harvest{};
        InitializeHarvestOption(&harvest);
        harvest.frame_period = kFrameMs;
        if (fast) {
            DioOption dio{};
            InitializeDioOption(&dio);
            dio.frame_period = kFrameMs;
            dio.speed = 1;
            std::vector<double> initial(frames);
            if (GetSamplesForDIO(kRate, sample_count, kFrameMs) != frames) return Native;
            Dio(samples, sample_count, kRate, &dio, time_axis, initial.data());
            StoneMask(samples, sample_count, kRate, time_axis, initial.data(), frames, f0);
        } else Harvest(samples, sample_count, kRate, &harvest, time_axis, f0);
        if (!ValidF0(f0, frames)) return Value;
        for (int i = 0; i < frames; ++i)
            if (!std::isfinite(time_axis[i]) || std::abs(time_axis[i] - i * kFrameMs / 1000.0) > 1e-9) return Value;
        const auto cheap_stage = [&]() {
            CheapTrickOption cheaptrick{};
            InitializeCheapTrickOption(kRate, &cheaptrick);
            CheapTrick(samples, sample_count, kRate, time_axis, f0, frames, &cheaptrick, spectral_rows.data());
        };
        const auto d4c_stage = [&]() {
            D4COption d4c{};
            InitializeD4COption(&d4c);
            D4C(samples, sample_count, kRate, time_axis, f0, frames, fft, &d4c, aperiodic_rows.data());
        };
        if (!parallel || !RunParallelStages(cheap_stage, d4c_stage)) {
            cheap_stage();
            d4c_stage();
        }
        return ValidValues(spectrum, spectrum_count, 0.0, 1e12) &&
            ValidValues(aperiodicity, aperiodicity_count, 0.0, 1.0) ? Ok : Value;
    } catch (const std::bad_alloc&) { return Allocation; }
    catch (...) { return Native; }
}

TW_EXPORT tw_world_analyze(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count,
    double* spectrum, std::int32_t spectrum_count, double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size) noexcept {
    return Analyze(samples, sample_count, f0, f0_count, time_axis, time_count,
        spectrum, spectrum_count, aperiodicity, aperiodicity_count, fft_size, false, false);
}
TW_EXPORT tw_world_analyze_dio(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count,
    double* spectrum, std::int32_t spectrum_count, double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size) noexcept {
    return Analyze(samples, sample_count, f0, f0_count, time_axis, time_count,
        spectrum, spectrum_count, aperiodicity, aperiodicity_count, fft_size, true, false);
}

// Optional ABI-v1 extensions. No existing export selects parallel work.
TW_EXPORT tw_world_analyze_parallel(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count,
    double* spectrum, std::int32_t spectrum_count, double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size) noexcept {
    return Analyze(samples, sample_count, f0, f0_count, time_axis, time_count,
        spectrum, spectrum_count, aperiodicity, aperiodicity_count, fft_size, false, true);
}
TW_EXPORT tw_world_analyze_dio_parallel(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count,
    double* spectrum, std::int32_t spectrum_count, double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size) noexcept {
    return Analyze(samples, sample_count, f0, f0_count, time_axis, time_count,
        spectrum, spectrum_count, aperiodicity, aperiodicity_count, fft_size, true, true);
}

// Optional ABI-v1 extension: calibration needs no spectral/AP matrices.
// Status is authoritative; every output is invalid after a nonzero result.
static std::int32_t AnalyzeF0(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count, bool fast) noexcept {
    if (!samples || !f0 || !time_axis) return Argument;
    if (!InputLength(sample_count)) return Length;
    try {
        const int frames = Frames(sample_count);
        if (frames < 2 || frames > kMaximumAnalysisFrames || f0_count != frames || time_count != frames) return Length;
        if (Overlaps(samples, sample_count, f0, f0_count) ||
            Overlaps(samples, sample_count, time_axis, time_count) || Overlaps(f0, f0_count, time_axis, time_count)) return Argument;
        if (!ValidValues(samples, sample_count, -1.0, 1.0)) return Value;
        HarvestOption harvest{};
        InitializeHarvestOption(&harvest);
        harvest.frame_period = kFrameMs;
        if (fast) {
            DioOption dio{};
            InitializeDioOption(&dio);
            dio.frame_period = kFrameMs;
            dio.speed = 1;
            std::vector<double> initial(frames);
            if (GetSamplesForDIO(kRate, sample_count, kFrameMs) != frames) return Native;
            Dio(samples, sample_count, kRate, &dio, time_axis, initial.data());
            StoneMask(samples, sample_count, kRate, time_axis, initial.data(), frames, f0);
        } else Harvest(samples, sample_count, kRate, &harvest, time_axis, f0);
        if (!ValidF0(f0, frames)) return Value;
        for (int i = 0; i < frames; ++i)
            if (!std::isfinite(time_axis[i]) || std::abs(time_axis[i] - i * kFrameMs / 1000.0) > 1e-9) return Value;
        return Ok;
    } catch (const std::bad_alloc&) { return Allocation; }
    catch (...) { return Native; }
}

TW_EXPORT tw_world_analyze_f0(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count) noexcept {
    return AnalyzeF0(samples, sample_count, f0, f0_count, time_axis, time_count, false);
}
TW_EXPORT tw_world_analyze_dio_f0(const double* samples, std::int32_t sample_count,
    double* f0, std::int32_t f0_count, double* time_axis, std::int32_t time_count) noexcept {
    return AnalyzeF0(samples, sample_count, f0, f0_count, time_axis, time_count, true);
}

TW_EXPORT tw_world_synthesize(const double* f0, std::int32_t frame_count,
    const double* spectrum, std::int32_t spectrum_count, const double* aperiodicity,
    std::int32_t aperiodicity_count, std::int32_t fft_size, double* output,
    std::int32_t output_count) noexcept {
    if (!f0 || !spectrum || !aperiodicity || !output) return Argument;
    if (frame_count < 2 || frame_count > kMaximumSynthesisFrames || output_count < 1 || output_count > kMaximumOutput) return Length;
    try {
        const int fft = FftSize();
        if (fft_size != fft) return Argument;
        if (Frames(output_count) != frame_count || !MatrixLength(frame_count, spectrum_count, fft) ||
            !MatrixLength(frame_count, aperiodicity_count, fft)) return Length;
        if (Overlaps(output, output_count, f0, frame_count) || Overlaps(output, output_count, spectrum, spectrum_count) ||
            Overlaps(output, output_count, aperiodicity, aperiodicity_count)) return Argument;
        if (!ValidF0(f0, frame_count) || !ValidValues(spectrum, spectrum_count, 0.0, 1e12) ||
            !ValidValues(aperiodicity, aperiodicity_count, 0.0, 1.0)) return Value;
        auto spectral_rows = Rows(spectrum, frame_count, fft);
        auto aperiodic_rows = Rows(aperiodicity, frame_count, fft);
        Synthesis(f0, frame_count, spectral_rows.data(), aperiodic_rows.data(), fft, kFrameMs, kRate, output_count, output);
        for (int i = 0; i < output_count; ++i) if (!std::isfinite(output[i])) return Value;
        return Ok;
    } catch (const std::bad_alloc&) { return Allocation; }
    catch (...) { return Native; }
}
