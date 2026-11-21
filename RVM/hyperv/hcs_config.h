#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "core/result.h"

namespace rvm::hyperv {

struct LayerSpec {
  std::string id;
  std::wstring path;
};

struct MappedDirectorySpec {
  std::wstring host_path;
  std::wstring guest_path;
  bool read_only = false;
};

struct ComputeSystemSpec {
  std::wstring name;
  std::vector<LayerSpec> layers;
  std::wstring scratch_path;
  std::vector<MappedDirectorySpec> mapped_directories;
  std::uint32_t memory_mb = 0;
  bool hypervisor_partition = true;
};

std::string JsonEscape(std::string_view value);
std::string WideToUtf8(const std::wstring& value);
bool IsAbsoluteWindowsPath(const std::wstring& path);
bool IsValidLayerId(std::string_view id);
std::string NewGuid();

core::Result<std::string> BuildComputeSystemJson(const ComputeSystemSpec& spec);
core::Result<std::string> BuildLayerDataJson(const std::vector<LayerSpec>& layers);

}  // namespace rvm::hyperv