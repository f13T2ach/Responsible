#pragma once

#include <string>
#include <string_view>

namespace rvm::core {

enum class ErrorCode {
  kUnspecified = 0,
  kUnavailable = 1,
  kHcsUnavailable = 2,
  kGuestUnreachable = 3,
  kSessionNotFound = 4,
  kMaskedFile = 5,
  kPathOutOfScope = 6,
  kPolicyDenied = 7,
  kApprovalRequired = 8,
  kHashMismatch = 9,
  kSyncConflict = 10,
  kBackendFailed = 11,
  kEngineTimeout = 12,
  kProtocolError = 13,
  kNotImplemented = 14,
  kInvalidArgument = 15,
  kAlreadyExists = 16,
  kBusy = 17,
  kInvalidState = 18,
  kUnauthenticated = 19,
  kNotFound = 20,
};

std::string_view ErrorCodeName(ErrorCode code) noexcept;
bool IsRetryable(ErrorCode code) noexcept;

struct Error {
  ErrorCode code = ErrorCode::kUnspecified;
  std::string message;
  bool retryable = false;
  std::string detail;

  Error() = default;
  Error(ErrorCode code_value, std::string message_value, bool retryable_value = false,
        std::string detail_value = {})
      : code(code_value),
        message(std::move(message_value)),
        retryable(retryable_value),
        detail(std::move(detail_value)) {}
};

inline Error MakeError(ErrorCode code, std::string message) {
  return Error(code, std::move(message), IsRetryable(code));
}

inline Error NotImplemented(std::string message) {
  return Error(ErrorCode::kNotImplemented, std::move(message));
}

inline Error InvalidArgument(std::string message) {
  return Error(ErrorCode::kInvalidArgument, std::move(message));
}

}  // namespace rvm::core