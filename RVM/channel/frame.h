#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

#include "core/result.h"

namespace rvm::channel {

inline constexpr std::uint32_t kFrameMagic = 0x314D5652;
inline constexpr std::uint16_t kFrameVersion = 1;
inline constexpr std::size_t kFrameHeaderSize = 24;
inline constexpr std::uint32_t kMaxPayloadBytes = 8 * 1024 * 1024;

enum class FrameType : std::uint16_t {
  kRpcRequest = 1,
  kRpcResponse = 2,
  kStream = 3,
  kPing = 4,
  kPong = 5,
  kClose = 6,
};

struct FrameHeader {
  std::uint32_t magic = kFrameMagic;
  std::uint16_t header_version = kFrameVersion;
  FrameType type = FrameType::kRpcRequest;
  std::uint32_t payload_size = 0;
  std::uint32_t flags = 0;
  std::uint64_t request_id = 0;
};

const char* FrameTypeName(FrameType type) noexcept;

class FrameCodec {
 public:
  static core::Result<std::array<std::uint8_t, kFrameHeaderSize>> EncodeHeader(
      const FrameHeader& header);
  static core::Result<FrameHeader> DecodeHeader(std::span<const std::uint8_t> bytes);
  static core::Status Validate(const FrameHeader& header);
};

}  // namespace rvm::channel