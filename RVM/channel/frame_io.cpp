#include "channel/frame_io.h"

#include <string>

#include "core/error.h"

namespace rvm::channel {
namespace {

core::Status ReadExact(HANDLE handle, std::uint8_t* data, std::size_t size) {
  std::size_t offset = 0;
  while (offset < size) {
    DWORD read = 0;
    const DWORD remaining = static_cast<DWORD>(size - offset);
    if (!ReadFile(handle, data + offset, remaining, &read, nullptr)) {
      const DWORD error = GetLastError();
      if (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED) {
        return core::Status::Failure(
            core::Error(core::ErrorCode::kUnavailable, "peer closed the channel"));
      }
      return core::Status::Failure(core::Error(
          core::ErrorCode::kProtocolError, "channel read failed", false,
          "ReadFile failed with error " + std::to_string(error)));
    }
    if (read == 0) {
      return core::Status::Failure(
          core::Error(core::ErrorCode::kUnavailable, "peer closed the channel"));
    }
    offset += read;
  }
  return core::Status::Ok();
}

core::Status WriteExact(HANDLE handle, const std::uint8_t* data, std::size_t size) {
  std::size_t offset = 0;
  while (offset < size) {
    DWORD written = 0;
    const DWORD remaining = static_cast<DWORD>(size - offset);
    if (!WriteFile(handle, data + offset, remaining, &written, nullptr)) {
      const DWORD error = GetLastError();
      return core::Status::Failure(core::Error(
          core::ErrorCode::kUnavailable, "channel write failed", false,
          "WriteFile failed with error " + std::to_string(error)));
    }
    if (written == 0) {
      return core::Status::Failure(
          core::Error(core::ErrorCode::kUnavailable, "channel write made no progress"));
    }
    offset += written;
  }
  return core::Status::Ok();
}

}  // namespace

core::Status ReadFrame(HANDLE handle, FrameHeader& header, std::vector<std::uint8_t>& payload) {
  std::array<std::uint8_t, kFrameHeaderSize> bytes{};
  core::Status status = ReadExact(handle, bytes.data(), bytes.size());
  if (!status.ok()) return status;

  core::Result<FrameHeader> decoded = FrameCodec::DecodeHeader(bytes);
  if (!decoded.ok()) return core::Status::Failure(decoded.error());
  header = decoded.value();

  payload.resize(header.payload_size);
  if (header.payload_size == 0) return core::Status::Ok();
  return ReadExact(handle, payload.data(), payload.size());
}

core::Status WriteFrame(HANDLE handle, FrameType type, std::uint64_t request_id,
                        std::span<const std::uint8_t> payload) {
  FrameHeader header;
  header.type = type;
  header.request_id = request_id;
  header.payload_size = static_cast<std::uint32_t>(payload.size());

  core::Result<std::array<std::uint8_t, kFrameHeaderSize>> encoded = FrameCodec::EncodeHeader(header);
  if (!encoded.ok()) return core::Status::Failure(encoded.error());

  core::Status status = WriteExact(handle, encoded.value().data(), encoded.value().size());
  if (!status.ok()) return status;
  if (payload.empty()) return core::Status::Ok();
  return WriteExact(handle, payload.data(), payload.size());
}

}  // namespace rvm::channel