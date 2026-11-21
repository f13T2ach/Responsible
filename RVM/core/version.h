#pragma once

#include <cstdint>
#include <string_view>

namespace rvm::core {

inline constexpr std::uint32_t kProtocolVersion = 1;
inline constexpr std::string_view kEngineVersion = "0.1.0-p0";
inline constexpr std::string_view kCapability = "responsible.rvm.v1";

}  // namespace rvm::core