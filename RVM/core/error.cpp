#include "core/error.h"

namespace rvm::core {

std::string_view ErrorCodeName(ErrorCode code) noexcept {
  switch (code) {
    case ErrorCode::kUnspecified:
      return "ERROR_CODE_UNSPECIFIED";
    case ErrorCode::kUnavailable:
      return "RVM_UNAVAILABLE";
    case ErrorCode::kHcsUnavailable:
      return "RVM_HCS_UNAVAILABLE";
    case ErrorCode::kGuestUnreachable:
      return "RVM_GUEST_UNREACHABLE";
    case ErrorCode::kSessionNotFound:
      return "RVM_SESSION_NOT_FOUND";
    case ErrorCode::kMaskedFile:
      return "RVM_MASKED_FILE";
    case ErrorCode::kPathOutOfScope:
      return "RVM_PATH_OUT_OF_SCOPE";
    case ErrorCode::kPolicyDenied:
      return "RVM_POLICY_DENIED";
    case ErrorCode::kApprovalRequired:
      return "RVM_APPROVAL_REQUIRED";
    case ErrorCode::kHashMismatch:
      return "RVM_HASH_MISMATCH";
    case ErrorCode::kSyncConflict:
      return "RVM_SYNC_CONFLICT";
    case ErrorCode::kBackendFailed:
      return "RVM_BACKEND_FAILED";
    case ErrorCode::kEngineTimeout:
      return "RVM_ENGINE_TIMEOUT";
    case ErrorCode::kProtocolError:
      return "RVM_PROTOCOL_ERROR";
    case ErrorCode::kNotImplemented:
      return "RVM_NOT_IMPLEMENTED";
    case ErrorCode::kInvalidArgument:
      return "RVM_INVALID_ARGUMENT";
    case ErrorCode::kAlreadyExists:
      return "RVM_ALREADY_EXISTS";
    case ErrorCode::kBusy:
      return "RVM_BUSY";
    case ErrorCode::kInvalidState:
      return "RVM_INVALID_STATE";
    case ErrorCode::kUnauthenticated:
      return "RVM_UNAUTHENTICATED";
    case ErrorCode::kNotFound:
      return "RVM_NOT_FOUND";
  }
  return "ERROR_CODE_UNKNOWN";
}

bool IsRetryable(ErrorCode code) noexcept {
  switch (code) {
    case ErrorCode::kBusy:
    case ErrorCode::kEngineTimeout:
      return true;
    default:
      return false;
  }
}

}  // namespace rvm::core