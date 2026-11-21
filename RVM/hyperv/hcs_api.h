#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

#include "core/result.h"

namespace rvm::hyperv {

// Opaque HCS handles. They are only valid with the HcsApi instance that created them.
using HcsOperation = void*;
using HcsSystem = void*;

class HcsApiImpl;

class HcsApi {
 public:
  static core::Result<HcsApi> Load();

  HcsApi();
  ~HcsApi();
  HcsApi(HcsApi&& other) noexcept;
  HcsApi& operator=(HcsApi&& other) noexcept;
  HcsApi(const HcsApi&) = delete;
  HcsApi& operator=(const HcsApi&) = delete;

  bool loaded() const;
  bool has_storage_api() const;

  core::Result<std::string> GetServiceProperties(std::string_view query_json) const;

  core::Result<HcsSystem> CreateComputeSystem(const std::wstring& id, std::string_view config,
                                              std::uint32_t timeout_ms) const;
  core::Status StartComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const;
  core::Status ShutdownComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const;
  core::Status TerminateComputeSystem(HcsSystem system, std::uint32_t timeout_ms) const;
  core::Result<std::string> GetComputeSystemProperties(HcsSystem system,
                                                       std::string_view query_json) const;
  void CloseComputeSystem(HcsSystem system) const noexcept;

  // computestorage.dll: builds a host-derived read-only base OS layer.
  core::Status SetupBaseOsLayer(const std::wstring& layer_path, std::string_view options_json) const;
  core::Status InitializeWritableLayer(const std::wstring& layer_path,
                                       std::string_view layer_data_json) const;

  static core::Error MapHresult(std::int32_t hresult, std::string_view operation,
                                std::string detail = {});

 private:
  std::unique_ptr<HcsApiImpl> impl_;
};

}  // namespace rvm::hyperv