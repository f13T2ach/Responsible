#pragma once

#include <cstddef>
#include <span>
#include <string>

#include <google/protobuf/message_lite.h>

#include "channel/frame.h"
#include "core/result.h"

namespace rvm::channel {

core::Result<std::string> SerializeMessage(const google::protobuf::MessageLite& message,
                                           std::size_t limit = kMaxPayloadBytes);
core::Status ParseMessage(std::span<const std::uint8_t> payload,
                          google::protobuf::MessageLite& message,
                          std::size_t limit = kMaxPayloadBytes);

}  // namespace rvm::channel