#include "environment/system_probe.h"

#include <windows.h>

#include <array>
#include <sstream>

namespace rvm::environment {
namespace {

std::string LastErrorMessage(DWORD code) {
  if (code == ERROR_SUCCESS) return {};
  LPWSTR buffer = nullptr;
  const DWORD length = FormatMessageW(
      FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
          FORMAT_MESSAGE_IGNORE_INSERTS,
      nullptr, code, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
      reinterpret_cast<LPWSTR>(&buffer), 0, nullptr);
  std::string message;
  if (length != 0 && buffer != nullptr) {
    const int utf8_length =
        WideCharToMultiByte(CP_UTF8, 0, buffer, static_cast<int>(length), nullptr, 0, nullptr,
                            nullptr);
    if (utf8_length > 0) {
      message.resize(static_cast<std::size_t>(utf8_length));
      WideCharToMultiByte(CP_UTF8, 0, buffer, static_cast<int>(length), message.data(),
                          utf8_length, nullptr, nullptr);
    }
  }
  if (buffer != nullptr) LocalFree(buffer);
  while (!message.empty() && (message.back() == '\r' || message.back() == '\n')) {
    message.pop_back();
  }
  return message;
}

std::string ReadRegistryString(HKEY root, const std::wstring& subkey,
                               const std::wstring& value_name) {
  DWORD size = 0;
  if (RegGetValueW(root, subkey.c_str(), value_name.c_str(), RRF_RT_REG_SZ, nullptr, nullptr,
                   &size) != ERROR_SUCCESS ||
      size == 0) {
    return {};
  }
  std::wstring value(size / sizeof(wchar_t), L'\0');
  if (RegGetValueW(root, subkey.c_str(), value_name.c_str(), RRF_RT_REG_SZ, nullptr, value.data(),
                   &size) != ERROR_SUCCESS) {
    return {};
  }
  while (!value.empty() && value.back() == L'\0') value.pop_back();
  const int utf8_length = WideCharToMultiByte(CP_UTF8, 0, value.data(),
                                              static_cast<int>(value.size()), nullptr, 0, nullptr,
                                              nullptr);
  if (utf8_length <= 0) return {};
  std::string text(static_cast<std::size_t>(utf8_length), '\0');
  WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), text.data(),
                      utf8_length, nullptr, nullptr);
  return text;
}

ProbeResult ProbeRegistryDword(const std::wstring& subkey, const std::wstring& value_name) {
  DWORD value = 0;
  DWORD size = sizeof(value);
  const LSTATUS status =
      RegGetValueW(HKEY_LOCAL_MACHINE, subkey.c_str(), value_name.c_str(), RRF_RT_REG_DWORD,
                   nullptr, &value, &size);
  if (status != ERROR_SUCCESS) {
    return {ProbeState::kFailed,
            "registry value is unavailable (" + std::to_string(status) + ")"};
  }
  if (value == 1) return {ProbeState::kOk, "enabled"};
  return {ProbeState::kFailed, "disabled"};
}

}  // namespace

HcsApiProbe ProbeHcsApi() {
  HcsApiProbe probe;
  HMODULE module = LoadLibraryW(L"computecore.dll");
  if (module == nullptr) {
    probe.detail = "computecore.dll is not available (" +
                   std::to_string(GetLastError()) + " " +
                   LastErrorMessage(GetLastError()) + ")";
    return probe;
  }

  static constexpr std::array<const char*, 16> kRequiredExports = {
      "HcsCreateComputeSystem",
      "HcsOpenComputeSystem",
      "HcsCloseComputeSystem",
      "HcsStartComputeSystem",
      "HcsShutDownComputeSystem",
      "HcsTerminateComputeSystem",
      "HcsCreateProcess",
      "HcsOpenProcess",
      "HcsCloseProcess",
      "HcsGetComputeSystemProperties",
      "HcsGetProcessProperties",
      "HcsCreateOperation",
      "HcsCloseOperation",
      "HcsWaitForOperationResult",
      "HcsGetOperationResult",
      "HcsEnumerateComputeSystems",
  };

  for (const char* name : kRequiredExports) {
    if (GetProcAddress(module, name) == nullptr) probe.missing.emplace_back(name);
  }
  FreeLibrary(module);

  probe.available = probe.missing.empty();
  probe.detail = probe.available ? "all required HCS exports are present"
                                 : "one or more required HCS exports are missing";
  return probe;
}

ServiceProbe ProbeService(const std::wstring& name) {
  ServiceProbe probe;
  SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT);
  if (manager == nullptr) {
    probe.detail = "OpenSCManager failed (" + std::to_string(GetLastError()) + ")";
    return probe;
  }

  SC_HANDLE service = OpenServiceW(manager, name.c_str(), SERVICE_QUERY_STATUS);
  if (service == nullptr) {
    const DWORD error = GetLastError();
    probe.detail = "service is not installed (" + std::to_string(error) + ")";
    CloseServiceHandle(manager);
    return probe;
  }

  probe.found = true;
  SERVICE_STATUS_PROCESS status{};
  DWORD bytes_needed = 0;
  if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO,
                            reinterpret_cast<LPBYTE>(&status), sizeof(status), &bytes_needed)) {
    probe.detail = "QueryServiceStatusEx failed (" + std::to_string(GetLastError()) + ")";
  } else {
    probe.running = status.dwCurrentState == SERVICE_RUNNING;
    probe.detail = probe.running ? "service is running" : "service is not running";
  }

  CloseServiceHandle(service);
  CloseServiceHandle(manager);
  return probe;
}

ProbeResult ProbeHypervisor() {
  using WHvGetCapabilityFn = HRESULT(WINAPI*)(UINT32, void*, UINT32, UINT32*);

  HMODULE module = LoadLibraryW(L"WinHvPlatform.dll");
  if (module != nullptr) {
    const auto get_capability =
        reinterpret_cast<WHvGetCapabilityFn>(GetProcAddress(module, "WHvGetCapability"));
    if (get_capability != nullptr) {
      // WHvCapabilityCodeHypervisorPresent = 0; the buffer is a BOOL.
      BOOL present = FALSE;
      UINT32 written = 0;
      const HRESULT result =
          get_capability(0, &present, sizeof(present), &written);
      FreeLibrary(module);
      if (SUCCEEDED(result) && written == sizeof(present)) {
        return present ? ProbeResult{ProbeState::kOk, "the Windows hypervisor is running"}
                       : ProbeResult{ProbeState::kFailed, "the Windows hypervisor is not running"};
      }
    } else {
      FreeLibrary(module);
    }
  }

  if (IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED)) {
    return {ProbeState::kOk,
            "firmware virtualization is enabled (hypervisor presence not confirmed)"};
  }
  return {ProbeState::kFailed, "firmware virtualization is not enabled"};
}

ProbeResult ProbeVbs() {
  return ProbeRegistryDword(L"SYSTEM\\CurrentControlSet\\Control\\DeviceGuard",
                            L"EnableVirtualizationBasedSecurity");
}

ProbeResult ProbeHvci() {
  return ProbeRegistryDword(
      L"SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity",
      L"Enabled");
}

AppContainerProbe ProbeAppContainerApi() {
  AppContainerProbe probe;
  HMODULE module = LoadLibraryW(L"userenv.dll");
  if (module == nullptr) {
    probe.detail = "userenv.dll is not available (" + std::to_string(GetLastError()) + ")";
    return probe;
  }

  static constexpr std::array<const char*, 3> kRequiredExports = {
      "CreateAppContainerProfile",
      "DeleteAppContainerProfile",
      "DeriveAppContainerSidFromAppContainerName",
  };
  std::string missing;
  for (const char* name : kRequiredExports) {
    if (GetProcAddress(module, name) != nullptr) continue;
    if (!missing.empty()) missing += ", ";
    missing += name;
  }
  FreeLibrary(module);

  probe.available = missing.empty();
  probe.detail = probe.available ? "AppContainer profile APIs are available"
                                 : "missing exports: " + missing;
  return probe;
}

ProbeResult ProbeWinFsp() {
  static constexpr std::array<const wchar_t*, 3> kCandidates = {
      L"C:\\Windows\\System32\\winfsp-x64.dll",
      L"C:\\Windows\\System32\\winfsp-x86.dll",
      L"C:\\Program Files (x86)\\WinFsp\\bin\\winfsp-x64.dll",
  };
  for (const wchar_t* candidate : kCandidates) {
    if (GetFileAttributesW(candidate) != INVALID_FILE_ATTRIBUTES) {
      return {ProbeState::kOk, "WinFsp is installed"};
    }
  }
  return {ProbeState::kFailed, "WinFsp is not installed; the workspace view is not available yet"};
}

std::string CurrentWindowsBuild() {
  std::string build =
      ReadRegistryString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
                         L"CurrentBuildNumber");
  if (build.empty()) build = "unknown";
  return build;
}

}  // namespace rvm::environment