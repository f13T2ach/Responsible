#include "channel/frame.h"

#include <algorithm>

#include "core/error.h"

namespace rvm::channel {
namespace {

void WriteU16(std::uint8_t* out, std::uint16_t value) {
  out[0] = static_cast<std::uint8_t>(value & 0xFF);
  out[1] = static_cast<std::uint8_t>((value >> 8) & 0xFF);
}

void WriteU32(std::uint8_t* out, std::uint32_t value) {
  for (int i = 0; i < 4; ++i) {
    out[i] = static_cast<std::uint8_t>((value >> (8 * i)) & 0xFF);
  }
}

void WriteU64(std::uint8_t* out, std::uint64_t value) {
  for (int i = 0; i < 8; ++i) {
    out[i] = static_cast<std::uint8_t>((value >> (8 * i)) & 0xFF);
  }
}

std::uint16_t ReadU16(const std::uint8_t* in) {
  return static_cast<std::uint16_t>(in[0]) |
         static_cast<std::uint16_t>(static_cast<std::uint16_t>(in[1]) << 8);
}

std::uint32_t ReadU32(const std::uint8_t* in) {
  std::uint32_t value = 0;
  for (int i = 0; i < 4; ++i) value |= static_cast<std::uint32_t>(in[i]) << (8 * i);
  return value;
}

std::uint64_t ReadU64(const std::uint8_t* in) {
  std::uint64_t value = 0;
  for (int i = 0; i < 8; ++i) value |= static_cast<std::uint64_t>(in[i]) << (8 * i);
  return value;
}

bool IsKnownFrameType(std::uint16_t type) {
  switch (static_cast<FrameType>(type)) {
    case FrameType::kRpcRequest:
    case FrameType::kRpcResponse:
    case FrameType::kStream:
    case FrameType::kPing:
    case FrameType::kPong:
    case FrameType::kClose:
      return true;
  }
  return false;
}

}  // namespace

const char* FrameTypeName(FrameType type) noexcept {
  switch (type) {
    case FrameType::kRpcRequest:
      return "rpc_request";
    case FrameType::kRpcResponse:
      return "rpc_response";
    case FrameType::kStream:
      return "stream";
    case FrameType::kPing:
      return "ping";
    case FrameType::kPong:
      return "pong";
    case FrameType::kClose:
      return "close";
  }
  return "unknown";
}

core::Status FrameCodec::Validate(const FrameHeader& header) {
  if (header.magic != kFrameMagic) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "invalid frame magic"));
  }
  if (header.header_version != kFrameVersion) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "unsupported frame header version"));
  }
  if (!IsKnownFrameType(static_cast<std::uint16_t>(header.type))) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "unknown frame type"));
  }
  if (header.payload_size > kMaxPayloadBytes) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "frame payload exceeds the protocol limit"));
  }
  return core::Status::Ok();
}

core::Result<std::array<std::uint8_t, kFrameHeaderSize>> FrameCodec::EncodeHeader(
    const FrameHeader& header) {
  core::Status status = Validate(header);
  if (!status.ok()) return status.error();

  std::array<std::uint8_t, kFrameHeaderSize> bytes{};
  WriteU32(bytes.data() + 0, header.magic);
  WriteU16(bytes.data() + 4, header.header_version);
  WriteU16(bytes.data() + 6, static_cast<std::uint16_t>(header.type));
  WriteU32(bytes.data() + 8, header.payload_size);
  WriteU32(bytes.data() + 12, header.flags);
  WriteU64(bytes.data() + 16, header.request_id);
  return core::Ok(bytes);
}

core::Result<FrameHeader> FrameCodec::DecodeHeader(std::span<const std::uint8_t> bytes) {
  if (bytes.size() < kFrameHeaderSize) {
    return core::Error(core::ErrorCode::kProtocolError, "truncated frame header");
  }

  FrameHeader header;
  header.magic = ReadU32(bytes.data() + 0);
  header.header_version = ReadU16(bytes.data() + 4);
  header.type = static_cast<FrameType>(ReadU16(bytes.data() + 6));
  header.payload_size = ReadU32(bytes.data() + 8);
  header.flags = ReadU32(bytes.data() + 12);
  header.request_id = ReadU64(bytes.data() + 16);

  core::Status status = Validate(header);
  if (!status.ok()) return status.error();
  return core::Ok(header);
}

}  // namespace rvm::channel