#include <ctranslate2/models/whisper.h>
#include <ctranslate2/models/model_reader.h>
#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <stdexcept>

// Only this C ABI crosses the managed boundary. All exceptions stay inside the DLL.
#define API extern "C" __declspec(dllexport)
using namespace ctranslate2;
static thread_local std::string last_error;
template<class F> int guarded(F action) noexcept {
  try { action(); last_error.clear(); return 0; }
  catch (const std::exception& e) { last_error = e.what(); return -1; }
  catch (...) { last_error = "Unknown native error"; return -1; }
}
class UnicodeReader : public models::ModelReader {
  std::string path;
public:
  explicit UnicodeReader(const char* value) : path(value) {}
  std::string get_model_id() const override { return path; }
  std::unique_ptr<std::istream> get_file(const std::string& name, bool binary) override {
    auto stream = std::make_unique<std::ifstream>(std::filesystem::u8path(path) / name,
        std::ios::in | (binary ? std::ios::binary : std::ios::openmode(0)));
    if (!*stream) return nullptr;
    return stream;
  }
};
struct Context {
  std::unique_ptr<models::Whisper> model;
  StorageView encoded;
  std::string precision;
};
API const char* ct2_error() noexcept { return last_error.c_str(); }
API int ct2_abi() noexcept { return 1; }
API int ct2_create(const char* path, const char* device, const char* precision, int threads, void** output) noexcept {
  if (output) *output = nullptr;
  return guarded([&] {
    if (!path || !device || !precision || !output || threads < 1) throw std::invalid_argument("Invalid create arguments");
    const std::string backend(device);
    if (backend != "cpu" && backend != "cuda") throw std::invalid_argument("Only CPU and CUDA are supported");
    models::ModelLoader loader(std::make_shared<UnicodeReader>(path));
    loader.device = backend == "cuda" ? Device::CUDA : Device::CPU;
    loader.compute_type = str_to_compute_type(precision);
    auto replicas = loader.load();
    auto context = std::make_unique<Context>();
    context->precision = compute_type_to_str(replicas.at(0)->effective_compute_type());
    ReplicaPoolConfig config; config.num_threads_per_replica = threads;
    context->model = std::make_unique<models::Whisper>(replicas, config);
    *output = context.release();
  });
}
API void ct2_destroy(void* handle) noexcept { delete static_cast<Context*>(handle); }
API int ct2_info(void* handle, int* mels, int* multilingual, char* precision, int capacity) noexcept {
  return guarded([&] {
    if (!handle || !mels || !multilingual || !precision) throw std::invalid_argument("Invalid info arguments");
    auto& c = *static_cast<Context*>(handle);
    if (capacity <= static_cast<int>(c.precision.size())) throw std::invalid_argument("Precision buffer too small");
    *mels = static_cast<int>(c.model->n_mels()); *multilingual = c.model->is_multilingual();
    std::memcpy(precision, c.precision.c_str(), c.precision.size() + 1);
  });
}
API int ct2_encode(void* handle, const float* features, int mels, int frames) noexcept {
  return guarded([&] {
    if (!handle || !features || frames != 3000) throw std::invalid_argument("Expected 3000 feature frames");
    auto& c = *static_cast<Context*>(handle);
    if (mels != c.model->n_mels()) throw std::invalid_argument("Mel count does not match model");
    StorageView input({1, mels, frames}, std::vector<float>(features, features + mels * frames), Device::CPU);
    c.encoded = c.model->encode(input, false).get();
  });
}
API int ct2_detect(void* handle, char* language, int capacity) noexcept {
  return guarded([&] {
    if (!handle || !language) throw std::invalid_argument("Invalid detection arguments");
    auto& c = *static_cast<Context*>(handle);
    auto probabilities = c.model->detect_language(c.encoded).at(0).get();
    const auto& value = probabilities.at(0).first;
    if (capacity <= static_cast<int>(value.size())) throw std::invalid_argument("Language buffer too small");
    std::memcpy(language, value.c_str(), value.size() + 1);
  });
}
API int ct2_generate(void* handle, const int* prompt, int prompt_count, float temperature,
    int* tokens, int capacity, int* count, float* score, float* no_speech) noexcept {
  return guarded([&] {
    if (!handle || !prompt || !tokens || !count || !score || !no_speech || prompt_count < 1 || prompt_count > 224 || capacity < 448)
      throw std::invalid_argument("Invalid generation arguments");
    auto& c = *static_cast<Context*>(handle);
    models::WhisperOptions options;
    options.beam_size = temperature == 0 ? 5 : 1;
    options.sampling_topk = temperature == 0 ? 1 : 0;
    options.sampling_temperature = temperature == 0 ? 1 : temperature;
    options.return_scores = true; options.return_no_speech_prob = true;
    auto result = c.model->generate(c.encoded,
        std::vector<std::vector<size_t>>{std::vector<size_t>(prompt, prompt + prompt_count)}, options).at(0).get();
    const auto& sequence = result.sequences_ids.at(0);
    if (sequence.size() > static_cast<size_t>(capacity)) throw std::runtime_error("Output buffer too small");
    *count = static_cast<int>(sequence.size());
    std::transform(sequence.begin(), sequence.end(), tokens, [](size_t token) { return static_cast<int>(token); });
    *score = result.scores.at(0); *no_speech = result.no_speech_prob;
  });
}
API int ct2_align(void* handle, const int* start, int start_count, const int* text, int text_count,
    int frames, int* pairs, int capacity, int* count) noexcept {
  return guarded([&] {
    if (!handle || !start || !text || !pairs || !count || start_count < 1 || text_count < 1 || text_count > 448 || frames < 1 || frames > 3000)
      throw std::invalid_argument("Invalid alignment arguments");
    auto& c = *static_cast<Context*>(handle);
    auto result = c.model->align(c.encoded, std::vector<size_t>(start, start + start_count),
        {std::vector<size_t>(text, text + text_count)}, {static_cast<size_t>(frames)}, 7).at(0).get();
    if (result.alignments.size() * 2 > static_cast<size_t>(capacity)) throw std::runtime_error("Alignment buffer too small");
    *count = static_cast<int>(result.alignments.size());
    for (size_t i = 0; i < result.alignments.size(); ++i) {
      pairs[i * 2] = static_cast<int>(result.alignments[i].first);
      pairs[i * 2 + 1] = static_cast<int>(result.alignments[i].second);
    }
  });
}
