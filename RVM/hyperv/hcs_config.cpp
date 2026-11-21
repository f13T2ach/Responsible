#include "hyperv/hcs_config.h"

#include <windows.h>
#include <rpc.h>

#include <cctype>
#include <cwctype>

namespace rvm::hyperv {
namespace {

constexpr std::string_view kSchemaVersion = R"({"Major":2,"Minor":1})";

bool IsHex(char value) {
  return std::isxdigit(static_cast<unsigned char>(value)) != 0;
}

std::string Quote(std::string_view value) {
  return "\"" + JsonEscape(value) + "\"";
}

core::Status ValidateLayerPath(const std::wstring& path) {
  if (!IsAbsoluteWindowsPath(path)) {
    return core::Status::Failure(core::Error(core::ErrorCode::kInvalidArgument,
                       "HCS layer paths must be absolute local paths",
                       false, WideToUtf8(path)));
  }
  return core::Status::Ok();
}

}  // namespace

std::string WideToUtf8(const std::wstring& value) {
  if (value.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                                       nullptr, 0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string result(static_cast<std::size_t>(size), '\0');
  WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size,
                      nullptr, nullptr);
  return result;
}

std::string JsonEscape(std::string_view value) {
  std::string out;
  out.reserve(value.size() + 8);
  for (const char ch : value) {
    switch (ch) {
      case '"':
        out += "\\\"";
        break;
      case '\\':
        out += "\\\\";
        break;
      case '\b':
        out += "\\b";
        break;
      case '\f':
        out += "\\f";
        break;
      case '\n':
        out += "\\n";
        break;
      case '\r':
        out += "\\r";
        break;
      case '\t':
        out += "\\t";
        break;
      default:
        if (static_cast<unsigned char>(ch) < 0x20) {
          static constexpr char kHex[] = "0123456789abcdef";
          out += "\\u00";
          out += kHex[(static_cast<unsigned char>(ch) >> 4) & 0x0F];
          out += kHex[static_cast<unsigned char>(ch) & 0x0F];
        } else {
          out += ch;
        }
        break;
    }
  }
  return out;
}

bool IsAbsoluteWindowsPath(const std::wstring& path) {
  if (path.size() < 3) return false;
  if (path[0] == L'\\' || path[0] == L'/') return false;
  if (!std::iswalpha(static_cast<wint_t>(path[0]))) return false;
  if (path[1] != L':') return false;
  if (path[2] != L'\\' && path[2] != L'/') return false;
  for (const wchar_t ch : path) {
    if (ch < 0x20) return false;
  }
  return true;
}

bool IsValidLayerId(std::string_view id) {
  if (id.size() != 36) return false;
  constexpr std::size_t kDashes[] = {8, 13, 18, 23};
  std::size_t dash_index = 0;
  for (std::size_t i = 0; i < id.size(); ++i) {
    if (dash_index < 4 && i == kDashes[dash_index]) {
      if (id[i] != '-') return false;
      ++dash_index;
      continue;
    }
    if (!IsHex(id[i])) return false;
  }
  return dash_index == 4;
}

std::string NewGuid() {
  UUID uuid{};
  if (UuidCreate(&uuid) != RPC_S_OK) return {};
  RPC_CSTR text = nullptr;
  if (UuidToStringA(&uuid, &text) != RPC_S_OK || text == nullptr) return {};
  std::string result(reinterpret_cast<const char*>(text));
  RpcStringFreeA(&text);
  return result;
}

core::Result<std::string> BuildLayerDataJson(const std::vector<LayerSpec>& layers) {
  if (layers.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "a writable layer needs at least one antecedent layer");
  }
  std::string json = "{\"SchemaVersion\":" + std::string(kSchemaVersion) + ",\"Layers\":[";
  for (std::size_t i = 0; i < layers.size(); ++i) {
    if (i != 0) json += ',';
    if (!IsValidLayerId(layers[i].id)) {
      return core::Error(core::ErrorCode::kInvalidArgument, "layer id must be a GUID",
                         false, layers[i].id);
    }
    const core::Status path_status = ValidateLayerPath(layers[i].path);
    if (!path_status.ok()) return path_status.error();
    json += "{\"Id\":";
    json += Quote(layers[i].id);
    json += ",\"Path\":";
    json += Quote(WideToUtf8(layers[i].path));
    json += ",\"PathType\":\"AbsolutePath\"}";
  }
  json += "]}";
  return json;
}

core::Result<std::string> BuildComputeSystemJson(const ComputeSystemSpec& spec) {
  if (spec.name.empty() || spec.name.size() > 128) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "compute system name must be 1..128 characters");
  }
  for (const wchar_t ch : spec.name) {
    const bool allowed = std::iswalnum(static_cast<wint_t>(ch)) != 0 || ch == L'-' || ch == L'_';
    if (!allowed) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "compute system name may only contain letters, digits, '-' and '_'",
                         false, WideToUtf8(spec.name));
    }
  }
  if (spec.layers.empty()) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "compute system needs at least one base layer");
  }
  if (!IsAbsoluteWindowsPath(spec.scratch_path)) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "container scratch path must be an absolute local path");
  }

  std::string json = "{";
  json += "\"SchemaVersion\":" + std::string(kSchemaVersion);
  json += ",\"Owner\":\"responsible-rvm\"";
  json += ",\"ShouldTerminateOnLastHandleClosed\":true";
  json += ",\"SystemType\":\"Container\"";
  json += ",\"Name\":" + Quote(WideToUtf8(spec.name));
  json += ",\"Container\":{";
  json += "\"HvPartition\":";
  json += spec.hypervisor_partition ? "true" : "false";

  json += ",\"Storage\":{\"Layers\":[";
  for (std::size_t i = 0; i < spec.layers.size(); ++i) {
    if (i != 0) json += ',';
    if (!IsValidLayerId(spec.layers[i].id)) {
      return core::Error(core::ErrorCode::kInvalidArgument, "layer id must be a GUID",
                         false, spec.layers[i].id);
    }
    const core::Status path_status = ValidateLayerPath(spec.layers[i].path);
    if (!path_status.ok()) return path_status.error();
    json += "{\"Id\":";
    json += Quote(spec.layers[i].id);
    json += ",\"Path\":";
    json += Quote(WideToUtf8(spec.layers[i].path));
    json += ",\"PathType\":\"AbsolutePath\"}";
  }
  json += "]";
  json += ",\"Path\":" + Quote(WideToUtf8(spec.scratch_path));
  json += "}";

  if (!spec.mapped_directories.empty()) {
    json += ",\"MappedDirectories\":[";
    for (std::size_t i = 0; i < spec.mapped_directories.size(); ++i) {
      const MappedDirectorySpec& mapping = spec.mapped_directories[i];
      const core::Status host_status = ValidateLayerPath(mapping.host_path);
      if (!host_status.ok()) return host_status.error();
      if (!IsAbsoluteWindowsPath(mapping.guest_path)) {
        return core::Error(core::ErrorCode::kInvalidArgument,
                           "guest mapped directory path must be absolute",
                           false, WideToUtf8(mapping.guest_path));
      }
      if (i != 0) json += ',';
      json += "{\"HostPath\":" + Quote(WideToUtf8(mapping.host_path));
      json += ",\"HostPathType\":\"AbsolutePath\"";
      json += ",\"ContainerPath\":" + Quote(WideToUtf8(mapping.guest_path));
      json += ",\"ReadOnly\":";
      json += mapping.read_only ? "true" : "false";
      json += "}";
    }
    json += "]";
  }

  if (spec.memory_mb != 0) {
    if (spec.memory_mb < 256) {
      return core::Error(core::ErrorCode::kInvalidArgument,
                         "container memory must be at least 256 MB");
    }
    json += ",\"Memory\":{\"SizeInMB\":";
    json += std::to_string(spec.memory_mb);
    json += "}";
  }

  json += "}}";
  return json;
}

}  // namespace rvm::hyperv