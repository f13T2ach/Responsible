#pragma once

#include <windows.h>

#include <cstdint>
#include <span>
#include <vector>

#include "channel/frame.h"
#include "core/result.h"

namespace rvm::channel {

core::Status ReadFrame(HANDLE handle, FrameHeader& header, std::vector<std::uint8_t>& payload);
core::Status WriteFrame(HANDLE handle, FrameType type, std::uint64_t request_id,
                        std::span<const std::uint8_t> payload);

}  // namespace rvm::channel