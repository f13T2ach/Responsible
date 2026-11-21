#include "hyperv/hcs_api.h"

#include <windows.h>

#include <cstdio>
#include <type_traits>
#include <utility>
#include <vector>

namespace rvm::hyperv {
namespace {

using CreateOperationFn = void* (*)(void*, void*);
using CloseOperationFn = void (*)(void*);
using WaitForOperationResultFn = HRESULT (*)(void*, DWORD, PWSTR*);
using GetServicePropertiesFn = HRESULT (*)(PCWSTR, PWSTR*);
using CreateComputeSystemFn = HRESULT (*)(PCWSTR, PCWSTR, void*, PCWSTR, void**);
using StartComputeSystemFn = HRESULT (*)(void*, void*, PCWSTR);
using ShutdownComputeSystemFn = HRESULT (*)(void*, void*, PCWSTR);
using TerminateComputeSystemFn = HRESULT (*)(void*, void*, PCWSTR);
using CloseComputeSystemFn = void (*)(void*);
using GetComputeSystemPropertiesFn = HRESULT (*)(void*, PCWSTR, PWSTR*);
using SetupBaseOsLayerFn = HRESULT (*)(PCWSTR, HANDLE, PCWSTR);
using InitializeWritableLayerFn = HRESULT (*)(PCWSTR, PCWSTR, PCWSTR);

std::string WideToUtf8(const wchar_t* value) {
  if (value == nullptr) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string result(static_cast<std::size_t>(size - 1), '\0');
  WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), size, nullptr, nullptr);
  return result;
}

std::string TakeLocalString(PWSTR value) {
  if (value == nullptr) return {};
  std::string text = WideToUtf8(value);
  LocalFree(value);
  return text;
}

std::string Hex32(std::uint32_t value) {
  char buffer[11] = {};
  std::snprintf(buffer, sizeof(buffer), "0x%08X", value);
  return buffer;
}

}  // namespace

class HcsApiImpl {
 public:
  HMODULE core = nullptr;
  HMODULE storage = nullptr;

  CreateOperationFn create_operation = nullptr;
  CloseOperationFn close_operation = nullptr;
  WaitForOperationResultFn wait_for_result = nullptr;
  GetServicePropertiesFn get_service_properties = nullptr;
  CreateComputeSystemFn create_system = nullptr;
  StartComputeSystemFn start_system = nullptr;
  ShutdownComputeSystemFn shutdown_system = nullptr;
  TerminateComputeSystemFn terminate_system = nullptr;
  CloseComputeSystemFn close_system = nullptr;
  GetComputeSystemPropertiesFn get_system_properties = nullptr;

  SetupBaseOsLayerFn setup_base_os_layer = nullptr;
  InitializeWritableLayerFn initialize_writable_layer = nullptr;

  ~HcsApiImpl() {
    if (storage != nullptr) FreeLibrary(storage);
    if (core != nullptr) FreeLibrary(core);
  }
};

namespace {

struct OperationGuard {
  HcsApiImpl* impl = nullptr;
  HcsOperation operation = nullptr;

  OperationGuard() = default;
  OperationGuard(const OperationGuard&) = delete;
  OperationGuard& operator=(const OperationGuard&) = delete;
  OperationGuard(OperationGuard&& other) noexcept
      : impl(other.impl), operation(other.operation) {
    other.impl = nullptr;
    other.operation = nullptr;
  }
  OperationGuard& operator=(OperationGuard&& other) noexcept {
    if (this != &other) {
      Reset();
      impl = other.impl;
      operation = other.operation;
      other.impl = nullptr;
      other.operation = nullptr;
    }
    return *this;
  }

  ~OperationGuard() { Reset(); }

  void Reset() {
    if (operation != nullptr && impl != nullptr) impl->close_operation(operation);
    operation = nullptr;
  }
};

core::Result<std::string> WaitForOperation(const HcsApiImpl& impl, HcsOperation operation,
                                           std::uint32_t timeout_ms, std::string_view what) {
  PWSTR raw_result = nullptr;
  const HRESULT hr = impl.wait_for_result(operation, static_cast<DWORD>(timeout_ms), &raw_result);
  std::string detail = TakeLocalString(raw_result);
  if (FAILED(hr)) {
    if (detail.find("timed out") != std::string::npos ||
        detail.find("timeout") != std::string::npos || detail.find("Timeout") != std::string::npos) {
      return core::Error(core::ErrorCode::kEngineTimeout, std::string(what) + " timed out", true,
                         std::move(detail));
    }
    return HcsApi::MapHresult(static_cast<std::int32_t>(hr), what, std::move(detail));
  }
  return detail;
}

core::Result<OperationGuard> NewOperation(HcsApiImpl& impl, std::string_view what) {
  OperationGuard guard;
  guard.impl = &impl;
  guard.operation = impl.create_operation(nullptr, nullptr);
  if (guard.operation == nullptr) {
    return core::Error(core::ErrorCode::kBackendFailed,
                       std::string(what) + " could not create an HCS operation");
  }
  return guard;
}

}  // namespace

HcsApi::HcsApi() = default;
HcsApi::~HcsApi() = default;
HcsApi::HcsApi(HcsApi&& other) noexcept = default;
HcsApi& HcsApi::operator=(HcsApi&& other) noexcept = default;

bool HcsApi::loaded() const { return impl_ != nullptr && impl_->core != nullptr; }
bool HcsApi::has_storage_api() const { return impl_ != nullptr && impl_->storage != nullptr; }

core::Result<HcsApi> HcsApi::Load() {
  auto impl = std::make_unique<HcsApiImpl>();
  impl->core = LoadLibraryW(L"computecore.dll");
  if (impl->core == nullptr) {
    return core::Error(core::ErrorCode::kHcsUnavailable, "computecore.dll could not be loaded",
                       false, "GetLastError=" + std::to_string(GetLastError()));
  }

  std::string missing;
  auto resolve = [&](const char* name, auto& target) {
    target = reinterpret_cast<std::remove_reference_t<decltype(target)>>(
        GetProcAddress(impl->core, name));
    if (target == nullptr) {
      if (!missing.empty()) missing += ", ";
      missing += name;
    }
    return target != nullptr;
  };
  resolve("HcsCreateOperation", impl->create_operation);
  resolve("HcsCloseOperation", impl->close_operation);
  resolve("HcsWaitForOperationResult", impl->wait_for_result);
  resolve("HcsGetServiceProperties", impl->get_service_properties);
  resolve("HcsCreateComputeSystem", impl->create_system);
  resolve("HcsStartComputeSystem", impl->start_system);
  resolve("HcsShutDownComputeSystem", impl->shutdown_system);
  resolve("HcsTerminateComputeSystem", impl->terminate_system);
  resolve("HcsCloseComputeSystem", impl->close_system);
  resolve("HcsGetComputeSystemProperties", impl->get_system_properties);
  if (!missing.empty()) {
    return core::Error(core::ErrorCode::kHcsUnavailable,
                       "computecore.dll is missing required HCS exports", false, missing);
  }

  impl->storage = LoadLibraryW(L"computestorage.dll");
  if (impl->storage != nullptr) {
    impl->setup_base_os_layer = reinterpret_cast<SetupBaseOsLayerFn>(
        GetProcAddress(impl->storage, "HcsSetupBaseOSLayer"));
    impl->initialize_writable_layer = reinterpret_cast<InitializeWritableLayerFn>(
        GetProcAddress(impl->storage, "HcsInitializeWritableLayer"));
  }

  HcsApi api;
  api.impl_ = std::move(impl);
  return api;
}

core::Result<std::string> HcsApi::GetServiceProperties(std::string_view query_json) const {
  if (!loaded()) {
    return core::Error(core::ErrorCode::kHcsUnavailable, "HCS API is not loaded");
  }
  const std::wstring query = std::wstring(query_json.begin(), query_json.end());
  PWSTR raw_result = nullptr;
  const HRESULT hr = impl_->get_service_properties(query.c_str(), &raw_result);
  std::string detail = TakeLocalString(raw_result);
  if (FAILED(hr)) {
    return MapHresult(static_cast<std::int32_t>(hr), "HcsGetServiceProperties", std::move(detail));
  }
  return detail;
}

core::Result<HcsSystem> HcsApi::CreateComputeSystem(const std::wstring& id, std::string_view config,
                                                    std::uint32_t timeout_ms) const {
  if (!loaded()) {
    return core::Error(core::ErrorCode::kHcsUnavailable, "HCS API is not loaded");
  }
  const std::wstring wide_config(config.begin(), config.end());
  auto operation = NewOperation(*impl_, "HcsCreateComputeSystem");
  if (!operation.ok()) return operation.error();

  HcsSystem system = nullptr;
  const HRESULT hr = impl_->create_system(id.c_str(), wide_config.c_str(), operation.value().operation,
                                          nullptr, &system);
  if (FAILED(hr)) {
    return MapHresult(static_cast<std::int32_t>(hr), "HcsCreateComputeSystem");
  }
  const auto waited = WaitForOperation(*impl_, operation.value().operation, timeout_ms,
                                       "HcsCreateComputeSystem");
  if (!waited.ok()) {
    if (system != nullptr) impl_->close_system(system);
    return waited.error();
  }
  if (system == nullptr) {
    return core::Error(core::ErrorCode::kBackendFailed,
                       "HcsCreateComputeSystem returned no system handle");
  }
  return system;
}

core::Status HcsApi::StartComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const {
  if (!loaded() || system == nullptr) {
    return core::Status::Failure(core::Error(core::ErrorCode::kInvalidState, "HCS compute system is not open"));
  }
  auto operation = NewOperation(*impl_, "HcsStartComputeSystem");
  if (!operation.ok()) return core::Status::Failure(operation.error());

  const HRESULT hr = impl_->start_system(system, operation.value().operation, nullptr);
  if (FAILED(hr)) {
    return core::Status::Failure(MapHresult(static_cast<std::int32_t>(hr), "HcsStartComputeSystem"));
  }
  const auto waited = WaitForOperation(*impl_, operation.value().operation, timeout_ms,
                                       "HcsStartComputeSystem");
  return waited.ok() ? core::Status::Ok() : core::Status::Failure(waited.error());
}

core::Status HcsApi::ShutdownComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const {
  if (!loaded() || system == nullptr) return core::Status::Ok();
  auto operation = NewOperation(*impl_, "HcsShutDownComputeSystem");
  if (!operation.ok()) return core::Status::Failure(operation.error());

  const HRESULT hr = impl_->shutdown_system(system, operation.value().operation, nullptr);
  if (FAILED(hr)) {
    return core::Status::Failure(MapHresult(static_cast<std::int32_t>(hr), "HcsShutDownComputeSystem"));
  }
  const auto waited = WaitForOperation(*impl_, operation.value().operation, timeout_ms,
                                       "HcsShutDownComputeSystem");
  return waited.ok() ? core::Status::Ok() : core::Status::Failure(waited.error());
}

core::Status HcsApi::TerminateComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const {
  if (!loaded() || system == nullptr) return core::Status::Ok();
  auto operation = NewOperation(*impl_, "HcsTerminateComputeSystem");
  if (!operation.ok()) return core::Status::Failure(operation.error());

  const HRESULT hr = impl_->terminate_system(system, operation.value().operation, nullptr);
  if (FAILED(hr)) {
    return core::Status::Failure(MapHresult(static_cast<std::int32_t>(hr), "HcsTerminateComputeSystem"));
  }
  const auto waited = WaitForOperation(*impl_, operation.value().operation, timeout_ms,
                                       "HcsTerminateComputeSystem");
  return waited.ok() ? core::Status::Ok() : core::Status::Failure(waited.error());
}

core::Result<std::string> HcsApi::GetComputeSystemProperties(HcsSystem system,
                                                             std::string_view query_json) const {
  if (!loaded() || system == nullptr) {
    return core::Error(core::ErrorCode::kInvalidState, "HCS compute system is not open");
  }
  const std::wstring query = std::wstring(query_json.begin(), query_json.end());
  PWSTR raw_result = nullptr;
  const HRESULT hr = impl_->get_system_properties(system, query.c_str(), &raw_result);
  std::string detail = TakeLocalString(raw_result);
  if (FAILED(hr)) {
    return MapHresult(static_cast<std::int32_t>(hr), "HcsGetComputeSystemProperties",
                      std::move(detail));
  }
  return detail;
}

void HcsApi::CloseComputeSystem(HcsSystem system) const noexcept {
  if (impl_ != nullptr && impl_->close_system != nullptr && system != nullptr) {
    impl_->close_system(system);
  }
}

core::Status HcsApi::SetupBaseOsLayer(const std::wstring& layer_path,
                                      std::string_view options_json) const {
  if (impl_ == nullptr || impl_->setup_base_os_layer == nullptr) {
    return core::Status::Failure(core::Error(core::ErrorCode::kHcsUnavailable,
                       "computestorage.dll does not export HcsSetupBaseOSLayer"));
  }
  const std::wstring options(options_json.begin(), options_json.end());
  const HRESULT hr =
      impl_->setup_base_os_layer(layer_path.c_str(), nullptr, options.empty() ? nullptr : options.c_str());
  if (FAILED(hr)) {
    return core::Status::Failure(MapHresult(static_cast<std::int32_t>(hr), "HcsSetupBaseOSLayer"));
  }
  return core::Status::Ok();
}

core::Status HcsApi::InitializeWritableLayer(const std::wstring& layer_path,
                                             std::string_view layer_data_json) const {
  if (impl_ == nullptr || impl_->initialize_writable_layer == nullptr) {
    return core::Status::Failure(core::Error(core::ErrorCode::kHcsUnavailable,
                       "computestorage.dll does not export HcsInitializeWritableLayer"));
  }
  const std::wstring layer_data(layer_data_json.begin(), layer_data_json.end());
  const HRESULT hr =
      impl_->initialize_writable_layer(layer_path.c_str(), layer_data.c_str(), nullptr);
  if (FAILED(hr)) {
    return core::Status::Failure(MapHresult(static_cast<std::int32_t>(hr), "HcsInitializeWritableLayer"));
  }
  return core::Status::Ok();
}

core::Error HcsApi::MapHresult(std::int32_t hresult, std::string_view operation,
                               std::string detail) {
  const std::uint32_t code = static_cast<std::uint32_t>(hresult);
  core::ErrorCode error_code = core::ErrorCode::kBackendFailed;
  std::string message = std::string(operation) + " failed";

  switch (code) {
    case 0x80070005u:  // E_ACCESSDENIED
    case 0x8037011Bu:  // HCS_E_ACCESS_DENIED
      error_code = core::ErrorCode::kPolicyDenied;
      message =
          std::string(operation) + " requires administrator or Hyper-V Administrators privileges";
      break;
    case 0x80370102u:  // HCS_E_HYPERV_NOT_INSTALLED
      error_code = core::ErrorCode::kHcsUnavailable;
      message = "Hyper-V is not installed for HCS";
      break;
    case 0x80370103u:  // HCS_E_INVALID_STATE
      error_code = core::ErrorCode::kInvalidState;
      break;
    case 0x8037010Cu:  // HCS_E_SYSTEM_NOT_FOUND
      error_code = core::ErrorCode::kSessionNotFound;
      break;
    case 0x8037010Du:  // HCS_E_SYSTEM_ALREADY_EXISTS
      error_code = core::ErrorCode::kAlreadyExists;
      break;
    case 0x80370114u:  // HCS_E_SERVICE_NOT_AVAILABLE
    case 0x8037011Du:  // HCS_E_SERVICE_NOT_REACHABLE
      error_code = core::ErrorCode::kHcsUnavailable;
      message = "the vmcompute service is not available";
      break;
    case 0x80370106u:  // HCS_E_CONNECT_FAILED
    case 0x80370108u:  // HCS_E_CONNECTION_CLOSED
      error_code = core::ErrorCode::kUnavailable;
      break;
    case 0x80370107u:  // HCS_E_CONNECTION_TIMEOUT
      error_code = core::ErrorCode::kEngineTimeout;
      break;
    case 0x8037011Au:  // HCS_E_OPERATION_RESULT_ALLOCATION_FAILED
      message = std::string(operation) + " could not allocate the operation result";
      break;
    case 0x80070057u:  // E_INVALIDARG
      error_code = core::ErrorCode::kInvalidArgument;
      break;
    default:
      break;
  }

  std::string full_detail = "hresult=" + Hex32(code);
  if (!detail.empty()) {
    full_detail += " ";
    full_detail += detail;
  }
  return core::Error(error_code, std::move(message), core::IsRetryable(error_code),
                     std::move(full_detail));
}

}  // namespace rvm::hyperv