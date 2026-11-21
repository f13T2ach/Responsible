#pragma once

#include <string>
#include <string_view>

#include "core/error.h"
#include "core/policy.h"
#include "core/result.h"
#include "rvm/v1/common.pb.h"

namespace rvm::channel {

v1::ErrorCode ToProto(core::ErrorCode code);
core::ErrorCode FromProto(v1::ErrorCode code);

v1::Error ToProto(const core::Error& error);
core::Error FromProto(const v1::Error& error);

core::Result<std::string> ToUtf8(std::wstring_view value);
core::Result<std::wstring> FromUtf8(std::string_view value);

core::Result<v1::SandboxPolicy> ToProto(const core::SandboxPolicy& policy);
core::Result<core::SandboxPolicy> FromProto(const v1::SandboxPolicy& policy);

}  // namespace rvm::channel