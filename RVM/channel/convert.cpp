#include "channel/convert.h"

#include <windows.h>

#include <utility>

namespace rvm::channel {

v1::ErrorCode ToProto(core::ErrorCode code) {
  switch (code) {
    case core::ErrorCode::kUnspecified:
      return v1::ERROR_CODE_UNSPECIFIED;
    case core::ErrorCode::kUnavailable:
      return v1::RVM_UNAVAILABLE;
    case core::ErrorCode::kHcsUnavailable:
      return v1::RVM_HCS_UNAVAILABLE;
    case core::ErrorCode::kGuestUnreachable:
      return v1::RVM_GUEST_UNREACHABLE;
    case core::ErrorCode::kSessionNotFound:
      return v1::RVM_SESSION_NOT_FOUND;
    case core::ErrorCode::kMaskedFile:
      return v1::RVM_MASKED_FILE;
    case core::ErrorCode::kPathOutOfScope:
      return v1::RVM_PATH_OUT_OF_SCOPE;
    case core::ErrorCode::kPolicyDenied:
      return v1::RVM_POLICY_DENIED;
    case core::ErrorCode::kApprovalRequired:
      return v1::RVM_APPROVAL_REQUIRED;
    case core::ErrorCode::kHashMismatch:
      return v1::RVM_HASH_MISMATCH;
    case core::ErrorCode::kSyncConflict:
      return v1::RVM_SYNC_CONFLICT;
    case core::ErrorCode::kBackendFailed:
      return v1::RVM_BACKEND_FAILED;
    case core::ErrorCode::kEngineTimeout:
      return v1::RVM_ENGINE_TIMEOUT;
    case core::ErrorCode::kProtocolError:
      return v1::RVM_PROTOCOL_ERROR;
    case core::ErrorCode::kNotImplemented:
      return v1::RVM_NOT_IMPLEMENTED;
    case core::ErrorCode::kInvalidArgument:
      return v1::RVM_INVALID_ARGUMENT;
    case core::ErrorCode::kAlreadyExists:
      return v1::RVM_ALREADY_EXISTS;
    case core::ErrorCode::kBusy:
      return v1::RVM_BUSY;
    case core::ErrorCode::kInvalidState:
      return v1::RVM_INVALID_STATE;
    case core::ErrorCode::kUnauthenticated:
      return v1::RVM_UNAUTHENTICATED;
    case core::ErrorCode::kNotFound:
      return v1::RVM_NOT_FOUND;
  }
  return v1::ERROR_CODE_UNSPECIFIED;
}

core::ErrorCode FromProto(v1::ErrorCode code) {
  switch (code) {
    case v1::ERROR_CODE_UNSPECIFIED:
      return core::ErrorCode::kUnspecified;
    case v1::RVM_UNAVAILABLE:
      return core::ErrorCode::kUnavailable;
    case v1::RVM_HCS_UNAVAILABLE:
      return core::ErrorCode::kHcsUnavailable;
    case v1::RVM_GUEST_UNREACHABLE:
      return core::ErrorCode::kGuestUnreachable;
    case v1::RVM_SESSION_NOT_FOUND:
      return core::ErrorCode::kSessionNotFound;
    case v1::RVM_MASKED_FILE:
      return core::ErrorCode::kMaskedFile;
    case v1::RVM_PATH_OUT_OF_SCOPE:
      return core::ErrorCode::kPathOutOfScope;
    case v1::RVM_POLICY_DENIED:
      return core::ErrorCode::kPolicyDenied;
    case v1::RVM_APPROVAL_REQUIRED:
      return core::ErrorCode::kApprovalRequired;
    case v1::RVM_HASH_MISMATCH:
      return core::ErrorCode::kHashMismatch;
    case v1::RVM_SYNC_CONFLICT:
      return core::ErrorCode::kSyncConflict;
    case v1::RVM_BACKEND_FAILED:
      return core::ErrorCode::kBackendFailed;
    case v1::RVM_ENGINE_TIMEOUT:
      return core::ErrorCode::kEngineTimeout;
    case v1::RVM_PROTOCOL_ERROR:
      return core::ErrorCode::kProtocolError;
    case v1::RVM_NOT_IMPLEMENTED:
      return core::ErrorCode::kNotImplemented;
    case v1::RVM_INVALID_ARGUMENT:
      return core::ErrorCode::kInvalidArgument;
    case v1::RVM_ALREADY_EXISTS:
      return core::ErrorCode::kAlreadyExists;
    case v1::RVM_BUSY:
      return core::ErrorCode::kBusy;
    case v1::RVM_INVALID_STATE:
      return core::ErrorCode::kInvalidState;
    case v1::RVM_UNAUTHENTICATED:
      return core::ErrorCode::kUnauthenticated;
    case v1::RVM_NOT_FOUND:
      return core::ErrorCode::kNotFound;
  }
  return core::ErrorCode::kProtocolError;
}

v1::Error ToProto(const core::Error& error) {
  v1::Error message;
  message.set_code(ToProto(error.code));
  message.set_message(error.message);
  message.set_retryable(error.retryable);
  message.set_detail(error.detail);
  return message;
}

core::Error FromProto(const v1::Error& error) {
  return core::Error(FromProto(error.code()), error.message(), error.retryable(), error.detail());
}

core::Result<std::string> ToUtf8(std::wstring_view value) {
  if (value.empty()) return core::Ok(std::string());
  const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                       static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
  if (size <= 0) {
    return core::Error(core::ErrorCode::kInvalidArgument,
                       "wide string cannot be encoded as UTF-8");
  }
  std::string bytes(static_cast<std::size_t>(size), '\0');
  WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                      static_cast<int>(value.size()), bytes.data(), size, nullptr, nullptr);
  return core::Ok(std::move(bytes));
}

core::Result<std::wstring> FromUtf8(std::string_view value) {
  if (value.empty()) return core::Ok(std::wstring());
  const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                                       static_cast<int>(value.size()), nullptr, 0);
  if (size <= 0) {
    return core::Error(core::ErrorCode::kProtocolError, "string is not valid UTF-8");
  }
  std::wstring text(static_cast<std::size_t>(size), L'\0');
  MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                      static_cast<int>(value.size()), text.data(), size);
  return core::Ok(std::move(text));
}

core::Result<v1::SandboxPolicy> ToProto(const core::SandboxPolicy& policy) {
  v1::SandboxPolicy message;
  switch (policy.mode) {
    case core::SandboxMode::kReadOnly:
      message.set_mode(v1::SANDBOX_MODE_READ_ONLY);
      break;
    case core::SandboxMode::kWorkspaceWrite:
      message.set_mode(v1::SANDBOX_MODE_WORKSPACE_WRITE);
      break;
    case core::SandboxMode::kDangerFullAccess:
      message.set_mode(v1::SANDBOX_MODE_DANGER_FULL_ACCESS);
      break;
  }
  message.set_network_access(policy.network_access);
  message.set_approval(policy.approval == core::ApprovalMode::kAsk ? v1::APPROVAL_MODE_ASK
                                                                  : v1::APPROVAL_MODE_NEVER);
  for (const std::wstring& root : policy.writable_roots) {
    core::Result<std::string> encoded = ToUtf8(root);
    if (!encoded.ok()) return encoded.error();
    message.add_writable_roots(std::move(encoded.value()));
  }
  return core::Ok(std::move(message));
}

core::Result<core::SandboxPolicy> FromProto(const v1::SandboxPolicy& policy) {
  core::SandboxPolicy result;
  switch (policy.mode()) {
    case v1::SANDBOX_MODE_READ_ONLY:
      result.mode = core::SandboxMode::kReadOnly;
      break;
    case v1::SANDBOX_MODE_WORKSPACE_WRITE:
      result.mode = core::SandboxMode::kWorkspaceWrite;
      break;
    case v1::SANDBOX_MODE_DANGER_FULL_ACCESS:
      result.mode = core::SandboxMode::kDangerFullAccess;
      break;
    case v1::SANDBOX_MODE_UNSPECIFIED:
    default:
      return core::Error(core::ErrorCode::kProtocolError, "sandbox mode is not specified");
  }

  switch (policy.approval()) {
    case v1::APPROVAL_MODE_ASK:
      result.approval = core::ApprovalMode::kAsk;
      break;
    case v1::APPROVAL_MODE_NEVER:
      result.approval = core::ApprovalMode::kNever;
      break;
    case v1::APPROVAL_MODE_UNSPECIFIED:
    default:
      return core::Error(core::ErrorCode::kProtocolError, "approval mode is not specified");
  }

  result.network_access = policy.network_access();
  result.writable_roots.reserve(static_cast<std::size_t>(policy.writable_roots_size()));
  for (const std::string& root : policy.writable_roots()) {
    core::Result<std::wstring> decoded = FromUtf8(root);
    if (!decoded.ok()) return decoded.error();
    result.writable_roots.push_back(std::move(decoded.value()));
  }
  return core::Ok(std::move(result));
}

}  // namespace rvm::channel