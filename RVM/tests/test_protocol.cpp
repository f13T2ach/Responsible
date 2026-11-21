#include <array>
#include <cstdint>
#include <string>

#include "channel/convert.h"
#include "channel/protobuf_codec.h"
#include "test_registry.h"
#include "rvm/v1/control.pb.h"

RVM_TEST(protobuf_codec_roundtrips_hello_request) {
  rvm::v1::HelloRequest request;
  request.set_requested_protocol(rvm::v1::PROTOCOL_VERSION_1);
  request.set_client_name("responsible");
  request.set_client_pid(4242);
  request.set_token("token-value");

  const auto serialized = rvm::channel::SerializeMessage(request);
  RVM_CHECK(serialized.ok());

  rvm::v1::HelloRequest decoded;
  const auto payload = std::span<const std::uint8_t>(
      reinterpret_cast<const std::uint8_t*>(serialized.value().data()), serialized.value().size());
  RVM_CHECK(rvm::channel::ParseMessage(payload, decoded).ok());
  RVM_CHECK_EQ(decoded.client_name(), std::string("responsible"));
  RVM_CHECK_EQ(decoded.client_pid(), 4242u);
  RVM_CHECK(decoded.requested_protocol() == rvm::v1::PROTOCOL_VERSION_1);
}

RVM_TEST(protobuf_codec_enforces_limits) {
  rvm::v1::HelloRequest request;
  request.set_client_name("responsible");

  RVM_CHECK(!rvm::channel::SerializeMessage(request, 1).ok());

  const auto serialized = rvm::channel::SerializeMessage(request);
  RVM_CHECK(serialized.ok());
  const auto payload = std::span<const std::uint8_t>(
      reinterpret_cast<const std::uint8_t*>(serialized.value().data()), serialized.value().size());
  rvm::v1::HelloRequest decoded;
  RVM_CHECK(!rvm::channel::ParseMessage(payload, decoded, 1).ok());
}

RVM_TEST(error_codes_roundtrip_through_protocol) {
  const std::array<rvm::core::ErrorCode, 20> codes = {
      rvm::core::ErrorCode::kUnspecified,
      rvm::core::ErrorCode::kUnavailable,
      rvm::core::ErrorCode::kHcsUnavailable,
      rvm::core::ErrorCode::kGuestUnreachable,
      rvm::core::ErrorCode::kSessionNotFound,
      rvm::core::ErrorCode::kMaskedFile,
      rvm::core::ErrorCode::kPathOutOfScope,
      rvm::core::ErrorCode::kPolicyDenied,
      rvm::core::ErrorCode::kApprovalRequired,
      rvm::core::ErrorCode::kHashMismatch,
      rvm::core::ErrorCode::kSyncConflict,
      rvm::core::ErrorCode::kBackendFailed,
      rvm::core::ErrorCode::kEngineTimeout,
      rvm::core::ErrorCode::kProtocolError,
      rvm::core::ErrorCode::kNotImplemented,
      rvm::core::ErrorCode::kInvalidArgument,
      rvm::core::ErrorCode::kAlreadyExists,
      rvm::core::ErrorCode::kBusy,
      rvm::core::ErrorCode::kInvalidState,
      rvm::core::ErrorCode::kUnauthenticated,
  };
  for (const rvm::core::ErrorCode code : codes) {
    RVM_CHECK(rvm::channel::FromProto(rvm::channel::ToProto(code)) == code);
  }
}

RVM_TEST(error_message_roundtrip_preserves_fields) {
  const rvm::core::Error error(rvm::core::ErrorCode::kMaskedFile, "masked", false, "detail");
  const rvm::core::Error decoded = rvm::channel::FromProto(rvm::channel::ToProto(error));
  RVM_CHECK(decoded.code == error.code);
  RVM_CHECK_EQ(decoded.message, error.message);
  RVM_CHECK_EQ(decoded.detail, error.detail);
  RVM_CHECK_EQ(decoded.retryable, error.retryable);
}

RVM_TEST(sandbox_policy_roundtrip_preserves_writable_roots) {
  rvm::core::SandboxPolicy policy;
  policy.mode = rvm::core::SandboxMode::kWorkspaceWrite;
  policy.approval = rvm::core::ApprovalMode::kAsk;
  policy.network_access = false;
  policy.writable_roots = {L"C:\\Work", L"D:\\Data"};

  const auto encoded = rvm::channel::ToProto(policy);
  RVM_CHECK(encoded.ok());
  const auto decoded = rvm::channel::FromProto(encoded.value());
  RVM_CHECK(decoded.ok());
  RVM_CHECK(decoded.value().mode == rvm::core::SandboxMode::kWorkspaceWrite);
  RVM_CHECK(decoded.value().approval == rvm::core::ApprovalMode::kAsk);
  RVM_CHECK_EQ(decoded.value().writable_roots.size(), static_cast<std::size_t>(2));
  RVM_CHECK_EQ(decoded.value().writable_roots[0], std::wstring(L"C:\\Work"));
  RVM_CHECK_EQ(decoded.value().writable_roots[1], std::wstring(L"D:\\Data"));
}

RVM_TEST(sandbox_policy_rejects_unspecified_modes) {
  rvm::v1::SandboxPolicy message;
  message.set_mode(rvm::v1::SANDBOX_MODE_UNSPECIFIED);
  message.set_approval(rvm::v1::APPROVAL_MODE_ASK);
  RVM_CHECK(!rvm::channel::FromProto(message).ok());
}

RVM_TEST(utf8_conversion_rejects_invalid_input) {
  RVM_CHECK(!rvm::channel::FromUtf8("\xFF").ok());
  RVM_CHECK(rvm::channel::FromUtf8("hello").ok());
  RVM_CHECK_EQ(rvm::channel::FromUtf8("hello").value(), std::wstring(L"hello"));
}