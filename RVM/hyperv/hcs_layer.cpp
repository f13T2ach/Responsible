#include "hyperv/hcs_layer.h"

#include <windows.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <map>
#include <sstream>
#include <system_error>
#include <utility>

#include "hyperv/hcs_api.h"
#include "hyperv/hcs_config.h"

namespace rvm::hyperv {
namespace fs = std::filesystem;

namespace {

constexpr const wchar_t* kMarkerName = L"rvm-layer.txt";
constexpr const char* kLayoutVersion = "1";

std::string ReadRegistryString(HKEY root, const wchar_t* subkey, const wchar_t* value) {
  DWORD size = 0;
  if (RegGetValueW(root, subkey, value, RRF_RT_REG_SZ, nullptr, nullptr, &size) != ERROR_SUCCESS ||
      size == 0) {
    return {};
  }
  std::wstring buffer(size / sizeof(wchar_t), L'\0');
  if (RegGetValueW(root, subkey, value, RRF_RT_REG_SZ, nullptr, buffer.data(), &size) !=
      ERROR_SUCCESS) {
    return {};
  }
  while (!buffer.empty() && buffer.back() == L'\0') buffer.pop_back();
  return WideToUtf8(buffer);
}

bool WriteMarker(const fs::path& layer_dir, const std::string& id) {
  const fs::path marker = layer_dir / kMarkerName;
  const fs::path temporary = layer_dir / (std::wstring(kMarkerName) + L".tmp");

  std::ofstream stream(temporary, std::ios::binary | std::ios::trunc);
  if (!stream) return false;
  stream << "id=" << id << '\n';
  stream << "windows_build=" << BaseLayerStore::CurrentWindowsBuild() << '\n';
  stream << "layout_version=" << kLayoutVersion << '\n';
  stream << "created_unix_ms="
         << std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::system_clock::now().time_since_epoch())
                .count()
         << '\n';
  stream.close();
  if (!stream) return false;

  return MoveFileExW(temporary.c_str(), marker.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
}

std::map<std::string, std::string> ReadMarker(const fs::path& layer_dir) {
  std::map<std::string, std::string> values;
  std::ifstream stream(layer_dir / kMarkerName, std::ios::binary);
  if (!stream) return values;
  std::string line;
  while (std::getline(stream, line)) {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    const auto equals = line.find('=');
    if (equals == std::string::npos) continue;
    values.emplace(line.substr(0, equals), line.substr(equals + 1));
  }
  return values;
}

bool LayerDirectoryLooksUsable(const fs::path& layer_dir) {
  std::error_code error;
  fs::directory_iterator iterator(layer_dir, error);
  if (error) return false;
  for (const fs::directory_entry& entry : iterator) {
    if (entry.path().filename() == kMarkerName) continue;
    return true;
  }
  return false;
}

core::Result<BaseLayerInfo> ReadLayer(const fs::path& layer_dir) {
  const auto marker = ReadMarker(layer_dir);
  const auto id = marker.find("id");
  const auto build = marker.find("windows_build");
  const auto layout = marker.find("layout_version");
  if (id == marker.end() || build == marker.end() || layout == marker.end()) {
    return core::Error(core::ErrorCode::kInvalidState, "base layer marker is incomplete",
                       false, WideToUtf8(layer_dir.wstring()));
  }
  if (!IsValidLayerId(id->second)) {
    return core::Error(core::ErrorCode::kInvalidState, "base layer id is not a GUID",
                       false, id->second);
  }
  if (layout->second != kLayoutVersion) {
    return core::Error(core::ErrorCode::kInvalidState, "base layer layout version is unsupported",
                       false, layout->second);
  }
  const std::string current_build = BaseLayerStore::CurrentWindowsBuild();
  if (build->second != current_build) {
    return core::Error(core::ErrorCode::kInvalidState,
                       "base layer was generated for a different Windows build", false,
                       build->second + " != " + current_build);
  }
  if (!LayerDirectoryLooksUsable(layer_dir)) {
    return core::Error(core::ErrorCode::kInvalidState, "base layer directory is empty",
                       false, WideToUtf8(layer_dir.wstring()));
  }
  return BaseLayerInfo{layer_dir.wstring(), id->second, build->second};
}

bool IsSafeSessionId(const std::string& session_id) {
  if (session_id.empty() || session_id.size() > 64) return false;
  for (const char ch : session_id) {
    const bool allowed = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                         (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
    if (!allowed) return false;
  }
  return true;
}

}  // namespace

BaseLayerStore::BaseLayerStore(std::wstring root) : root_(std::move(root)) {}

BaseLayerStore BaseLayerStore::Default() { return BaseLayerStore(DefaultRoot()); }

std::wstring BaseLayerStore::DefaultRoot() {
  std::wstring program_data(32768, L'\0');
  const DWORD length = GetEnvironmentVariableW(L"ProgramData", program_data.data(),
                                               static_cast<DWORD>(program_data.size()));
  program_data.resize(length);
  if (program_data.empty()) program_data = L"C:\\ProgramData";
  return (fs::path(program_data) / L"RVM" / L"layers").wstring();
}

std::string BaseLayerStore::CurrentWindowsBuild() {
  std::string build = ReadRegistryString(
      HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", L"CurrentBuildNumber");
  if (build.empty()) build = "unknown";
  return build;
}

bool BaseLayerStore::IsProcessElevated() {
  HANDLE token = nullptr;
  if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) == 0) return false;
  TOKEN_ELEVATION elevation{};
  DWORD returned = 0;
  const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation),
                                      &returned);
  CloseHandle(token);
  return ok != 0 && elevation.TokenIsElevated != 0;
}

std::wstring BaseLayerStore::base_root() const {
  return (fs::path(root_) / L"base").wstring();
}

std::wstring BaseLayerStore::sessions_root() const {
  return (fs::path(root_) / L"sessions").wstring();
}

core::Result<BaseLayerInfo> BaseLayerStore::Find() const {
  std::error_code error;
  const fs::path root = base_root();
  fs::directory_iterator iterator(root, error);
  if (error) {
    if (error == std::errc::no_such_file_or_directory) {
      return core::Error(core::ErrorCode::kHcsUnavailable,
                         "no host-derived HCS base layer is prepared",
                         false, WideToUtf8(root.wstring()));
    }
    return core::Error(core::ErrorCode::kBackendFailed, "could not enumerate the base layer store",
                       false, error.message());
  }

  std::string last_problem;
  for (const fs::directory_entry& entry : iterator) {
    if (!entry.is_directory()) continue;
    auto layer = ReadLayer(entry.path());
    if (layer.ok()) return layer;
    last_problem = layer.error().detail;
  }

  std::string detail = WideToUtf8(root.wstring());
  if (!last_problem.empty()) detail += " (" + last_problem + ")";
  return core::Error(core::ErrorCode::kHcsUnavailable,
                     "no usable host-derived HCS base layer is prepared", false,
                     std::move(detail));
}

core::Result<BaseLayerInfo> BaseLayerStore::Ensure(bool allow_create) const {
  auto existing = Find();
  if (existing.ok()) return existing;

  if (!allow_create) {
    return core::Error(
        core::ErrorCode::kHcsUnavailable,
        "RVM needs a host-derived base OS layer; run RvmEngine --prepare-base-layer from an "
        "elevated prompt",
        false, existing.error().detail);
  }

  if (!IsProcessElevated()) {
    return core::Error(core::ErrorCode::kPolicyDenied,
                       "preparing the HCS base OS layer requires elevation");
  }

  auto loaded = HcsApi::Load();
  if (!loaded.ok()) return loaded.error();
  HcsApi& hcs = loaded.value();
  if (!hcs.has_storage_api()) {
    return core::Error(core::ErrorCode::kHcsUnavailable,
                       "computestorage.dll is unavailable; the Windows container base layer "
                       "component is not installed");
  }

  const std::string id = NewGuid();
  if (id.empty()) {
    return core::Error(core::ErrorCode::kBackendFailed, "could not generate a layer id");
  }
  const fs::path layer_dir = fs::path(base_root()) / id;
  std::error_code error;
  fs::create_directories(layer_dir, error);
  if (error) {
    return core::Error(core::ErrorCode::kBackendFailed,
                       "could not create the base layer directory", false, error.message());
  }

  const core::Status setup = hcs.SetupBaseOsLayer(layer_dir.wstring(), R"({"Type":"Container"})");
  if (!setup.ok()) {
    std::error_code cleanup;
    fs::remove_all(layer_dir, cleanup);
    core::ErrorCode code = setup.error().code;
    std::string message = setup.error().message;
    if (setup.error().detail.find("0x80070003") != std::string::npos) {
      code = core::ErrorCode::kHcsUnavailable;
      message =
          "the Windows container base OS layer component is not installed; it requires the "
          "Windows Sandbox/Containers feature (Windows Pro or Enterprise)";
    }
    return core::Error(code, std::move(message), setup.error().retryable,
                       setup.error().detail + " layer_dir=" + WideToUtf8(layer_dir.wstring()));
  }
  if (!WriteMarker(layer_dir, id)) {
    return core::Error(core::ErrorCode::kBackendFailed,
                       "base layer was generated but its marker could not be written",
                       false, WideToUtf8(layer_dir.wstring()));
  }
  return BaseLayerInfo{layer_dir.wstring(), id, CurrentWindowsBuild()};
}

core::Result<std::wstring> BaseLayerStore::PrepareScratch(const std::string& session_id) const {
  if (!IsSafeSessionId(session_id)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "session id may only contain letters, digits, '-' and '_'");
  }
  const fs::path scratch = fs::path(sessions_root()) / session_id / L"scratch";
  std::error_code error;
  fs::create_directories(scratch, error);
  if (error) {
    return core::Error(core::ErrorCode::kBackendFailed, "could not create the session scratch space",
                       false, error.message());
  }
  return scratch.wstring();
}

core::Status BaseLayerStore::RemoveScratch(const std::wstring& scratch_path) const {
  const fs::path root = fs::path(sessions_root()).lexically_normal();
  const fs::path scratch = fs::path(scratch_path).lexically_normal();
  const fs::path relative = scratch.lexically_relative(root);
  if (relative.empty() || relative == L"." || relative.native().starts_with(L"..")) {
    return core::Status::Failure(core::Error(core::ErrorCode::kPathOutOfScope,
                       "refusing to remove a scratch path outside the layer store", false,
                       WideToUtf8(scratch.wstring())));
  }
  std::error_code error;
  fs::remove_all(scratch, error);
  if (error) {
    return core::Status::Failure(core::Error(core::ErrorCode::kBackendFailed,
                       "could not remove the session scratch space", false, error.message()));
  }
  return core::Status::Ok();
}

}  // namespace rvm::hyperv