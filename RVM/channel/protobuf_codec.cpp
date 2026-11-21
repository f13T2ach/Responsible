#include "channel/protobuf_codec.h"

#include <limits>

#include "core/error.h"

namespace rvm::channel {

core::Result<std::string> SerializeMessage(const google::protobuf::MessageLite& message,
                                           std::size_t limit) {
  const std::size_t size = static_cast<std::size_t>(message.ByteSizeLong());
  if (size > limit) {
    return core::Error(core::ErrorCode::kProtocolError,
                       "protobuf message exceeds the payload limit");
  }

  std::string bytes;
  bytes.resize(size);
  if (size != 0 && !message.SerializeToArray(bytes.data(), static_cast<int>(size))) {
    return core::Error(core::ErrorCode::kProtocolError, "protobuf serialization failed");
  }
  return core::Ok(std::move(bytes));
}

core::Status ParseMessage(std::span<const std::uint8_t> payload,
                          google::protobuf::MessageLite& message, std::size_t limit) {
  if (payload.size() > limit) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "payload exceeds the protocol limit"));
  }
  if (payload.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "payload exceeds the protobuf size limit"));
  }

  message.Clear();
  if (payload.empty()) return core::Status::Ok();
  if (!message.ParseFromArray(payload.data(), static_cast<int>(payload.size()))) {
    return core::Status::Failure(
        core::Error(core::ErrorCode::kProtocolError, "protobuf deserialization failed"));
  }
  return core::Status::Ok();
}

}  // namespace rvm::channel