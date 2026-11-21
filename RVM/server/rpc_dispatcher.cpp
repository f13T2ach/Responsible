#include "server/rpc_dispatcher.h"

#include <algorithm>
#include <chrono>
#include <utility>

#include "channel/convert.h"
#include "channel/protobuf_codec.h"
#include "core/error.h"
#include "core/version.h"
#include "rvm/v1/control.pb.h"
#include "rvm/v1/rpc.pb.h"
#include "security/auth.h"
#include "server/session_manager.h"
#include "server/shell_runner.h"

namespace rvm::server {
namespace {

constexpr std::size_t kMaxStreamChunk = 60 * 1024;

std::uint64_t NowUnixMs() {
  return static_cast<std::uint64_t>(
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::system_clock::now().time_since_epoch())
          .count());
}

v1::ProbeState ToProto(environment::ProbeState state) {
  switch (state) {
    case environment::ProbeState::kOk:
      return v1::PROBE_STATE_OK;
    case environment::ProbeState::kFailed:
      return v1::PROBE_STATE_FAILED;
    case environment::ProbeState::kNotProbed:
      return v1::PROBE_STATE_NOT_PROBED;
  }
  return v1::PROBE_STATE_NOT_PROBED;
}

v1::ProbeResult ToProto(const environment::ProbeResult& result) {
  v1::ProbeResult message;
  message.set_state(ToProto(result.state));
  message.set_detail(result.detail);
  return message;
}

v1::RpcResponse BaseResponse(std::uint64_t request_id) {
  v1::RpcResponse response;
  response.set_protocol(v1::PROTOCOL_VERSION_1);
  response.set_request_id(request_id);
  return response;
}

v1::RpcResponse ErrorResponse(std::uint64_t request_id, const core::Error& error) {
  v1::RpcResponse response = BaseResponse(request_id);
  *response.mutable_error() = channel::ToProto(error);
  return response;
}

core::Result<std::vector<std::uint8_t>> Serialize(const v1::RpcResponse& response) {
  core::Result<std::string> bytes =
      channel::SerializeMessage(response, channel::kMaxPayloadBytes);
  if (!bytes.ok()) return bytes.error();
  return core::Ok(std::vector<std::uint8_t>(bytes.value().begin(), bytes.value().end()));
}

core::Result<DispatchResult> ErrorResult(std::uint64_t request_id, const core::Error& error,
                                         bool close = false) {
  core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(request_id, error));
  if (!payload.ok()) return payload.error();
  return DispatchResult{payload.Take(), close};
}

core::Result<DispatchResult> ResponseResult(v1::RpcResponse response, bool close = false) {
  core::Result<std::vector<std::uint8_t>> payload = Serialize(response);
  if (!payload.ok()) return payload.error();
  return DispatchResult{payload.Take(), close};
}

core::Error UnsupportedRequest(const core::Engine& engine) {
  const environment::AvailabilityReport& availability = engine.availability();
  if (availability.rvm_available()) {
    return core::NotImplemented("request is not implemented yet");
  }
  return core::Error(core::ErrorCode::kUnavailable, "RVM is unavailable", false,
                     availability.reason());
}

v1::HelloResponse MakeHello(const core::Engine& engine) {
  const environment::AvailabilityReport& availability = engine.availability();

  v1::HelloResponse hello;
  hello.set_protocol(v1::PROTOCOL_VERSION_1);
  hello.set_engine_version(std::string(core::kEngineVersion));
  hello.set_capability(std::string(core::kCapability));
  hello.add_backends(v1::BACKEND_KIND_SANDBOX);

  v1::AvailabilityReport* report = hello.mutable_availability();
  *report->mutable_appcontainer() = ToProto(availability.appcontainer);
  *report->mutable_winfsp() = ToProto(availability.winfsp);
  *report->mutable_vbs() = ToProto(availability.vbs);
  *report->mutable_hvci() = ToProto(availability.hvci);
  report->set_rvm_available(availability.rvm_available());
  if (!availability.rvm_available()) report->set_reason(availability.reason());

  v1::EnvironmentFacts* facts = hello.mutable_environment();
  facts->set_vbs_enabled(availability.vbs.ok());
  facts->set_hvci_enabled(availability.hvci.ok());

  hello.set_rvm_available(availability.rvm_available());
  if (!availability.rvm_available()) hello.set_unavailable_reason(availability.reason());
  return hello;
}

core::Status EmitOutput(const StreamSink& stream, std::uint64_t request_id,
                        std::uint64_t session_id, std::uint32_t process_id,
                        std::uint64_t& sequence, bool is_error, bool truncated,
                        const std::string& data) {
  for (std::size_t offset = 0; offset < data.size(); offset += kMaxStreamChunk) {
    const std::size_t size = std::min(kMaxStreamChunk, data.size() - offset);
    v1::StreamFrame frame;
    frame.set_request_id(request_id);
    frame.set_session_id(session_id);
    v1::ShellOutputChunk* chunk = frame.mutable_output();
    chunk->set_session_id(session_id);
    chunk->set_process_id(process_id);
    chunk->set_stream(is_error ? v1::SHELL_STREAM_STDERR : v1::SHELL_STREAM_STDOUT);
    chunk->set_data(data.substr(offset, size));
    chunk->set_sequence(sequence++);
    chunk->set_truncated(truncated);
    const core::Status written = stream(frame);
    if (!written.ok()) return written;
  }
  return core::Status::Ok();
}

}  // namespace

RpcDispatcher::RpcDispatcher(core::Engine& engine, std::string expected_token,
                             SessionManager* sessions)
    : engine_(engine), expected_token_(std::move(expected_token)), sessions_(sessions) {}

core::Result<DispatchResult> RpcDispatcher::Handle(std::span<const std::uint8_t> request,
                                                   std::uint32_t peer_pid,
                                                   const StreamSink& stream,
                                                   const std::function<bool()>& peer_alive) {
  v1::RpcRequest rpc;
  if (!channel::ParseMessage(request, rpc, channel::kMaxPayloadBytes).ok()) {
    core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
        0, core::Error(core::ErrorCode::kProtocolError, "request is not a valid RpcRequest")));
    if (!payload.ok()) return payload.error();
    return DispatchResult{payload.Take(), true};
  }

  const std::uint64_t request_id = rpc.request_id();
  if (rpc.protocol() != v1::PROTOCOL_VERSION_1) {
    core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
        request_id,
        core::Error(core::ErrorCode::kProtocolError, "unsupported protocol version")));
    if (!payload.ok()) return payload.error();
    return DispatchResult{payload.Take(), true};
  }

  if (!authenticated_) {
    if (rpc.body_case() != v1::RpcRequest::kHello) {
      core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
          request_id,
          core::Error(core::ErrorCode::kUnauthenticated, "handshake is required")));
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), true};
    }

    const v1::HelloRequest& hello = rpc.hello();
    if (hello.requested_protocol() != v1::PROTOCOL_VERSION_1) {
      core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
          request_id,
          core::Error(core::ErrorCode::kProtocolError, "requested protocol is not supported")));
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), true};
    }
    if (!security::ConstantTimeEquals(hello.token(), expected_token_)) {
      core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
          request_id, core::Error(core::ErrorCode::kUnauthenticated, "invalid session token")));
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), true};
    }
    if (hello.client_pid() == 0 || hello.client_pid() != peer_pid) {
      core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
          request_id,
          core::Error(core::ErrorCode::kUnauthenticated, "client process id does not match")));
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), true};
    }

    authenticated_ = true;
    v1::RpcResponse response = BaseResponse(request_id);
    *response.mutable_hello() = MakeHello(engine_);
    core::Result<std::vector<std::uint8_t>> payload = Serialize(response);
    if (!payload.ok()) return payload.error();
    return DispatchResult{payload.Take(), false};
  }

  switch (rpc.body_case()) {
    case v1::RpcRequest::kHello: {
      core::Result<std::vector<std::uint8_t>> payload = Serialize(ErrorResponse(
          request_id, core::Error(core::ErrorCode::kInvalidState, "already authenticated")));
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), false};
    }
    case v1::RpcRequest::kPing: {
      v1::RpcResponse response = BaseResponse(request_id);
      response.mutable_pong()->set_nonce(rpc.ping().nonce());
      core::Result<std::vector<std::uint8_t>> payload = Serialize(response);
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), false};
    }
    case v1::RpcRequest::kShutdown: {
      core::Status status = engine_.BeginStop();
      if (status.ok()) status = engine_.CompleteStop();
      if (!status.ok()) {
        core::Result<std::vector<std::uint8_t>> payload =
            Serialize(ErrorResponse(request_id, status.error()));
        if (!payload.ok()) return payload.error();
        return DispatchResult{payload.Take(), true};
      }
      v1::RpcResponse response = BaseResponse(request_id);
      response.mutable_shutdown()->set_accepted(true);
      core::Result<std::vector<std::uint8_t>> payload = Serialize(response);
      if (!payload.ok()) return payload.error();
      return DispatchResult{payload.Take(), true};
    }
    case v1::RpcRequest::kCreateSession: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      core::Result<v1::Session> created = sessions_->Create(rpc.create_session());
      if (!created.ok()) return ErrorResult(request_id, created.error());
      v1::RpcResponse response = BaseResponse(request_id);
      *response.mutable_create_session()->mutable_session() = created.value();
      return ResponseResult(std::move(response));
    }
    case v1::RpcRequest::kCloseSession: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      const core::Status closed =
          sessions_->Close(rpc.close_session().session_id(), rpc.close_session().reason());
      if (!closed.ok()) return ErrorResult(request_id, closed.error());
      v1::RpcResponse response = BaseResponse(request_id);
      response.mutable_close_session()->set_closed(true);
      return ResponseResult(std::move(response));
    }
    case v1::RpcRequest::kGetSession: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      core::Result<v1::Session> session = sessions_->Get(rpc.get_session().session_id());
      if (!session.ok()) return ErrorResult(request_id, session.error());
      v1::RpcResponse response = BaseResponse(request_id);
      *response.mutable_get_session()->mutable_session() = session.value();
      return ResponseResult(std::move(response));
    }
    case v1::RpcRequest::kListSessions: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      const std::vector<v1::Session> records = sessions_->List();
      v1::RpcResponse response = BaseResponse(request_id);
      auto* listed = response.mutable_list_sessions()->mutable_sessions();
      listed->Add(records.begin(), records.end());
      return ResponseResult(std::move(response));
    }
    case v1::RpcRequest::kShellExec: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      const v1::ShellExecRequest& request = rpc.shell_exec();
      core::Result<ShellContext> context = sessions_->DescribeForShell(request.session_id());
      if (!context.ok()) return ErrorResult(request_id, context.error());
      if (!stream) {
        return ErrorResult(request_id, core::Error(core::ErrorCode::kProtocolError,
                                                   "streaming execution requires a stream channel"));
      }

      core::Result<ShellOutcome> outcome =
          RunShell(context.value(), request, [&peer_alive] { return !peer_alive(); });
      if (!outcome.ok()) return ErrorResult(request_id, outcome.error());

      const ShellOutcome& shell = outcome.value();
      std::uint64_t sequence = 0;
      v1::StreamFrame started_frame;
      started_frame.set_request_id(request_id);
      started_frame.set_session_id(request.session_id());
      v1::ShellExecStarted* started = started_frame.mutable_exec_started();
      started->set_session_id(request.session_id());
      started->set_process_id(shell.process_id);
      started->set_started_unix_ms(NowUnixMs() - shell.duration_ms);
      if (!stream(started_frame).ok()) return core::Ok(DispatchResult{{}, true});
      if (!EmitOutput(stream, request_id, request.session_id(), shell.process_id, sequence,
                      false, shell.output_truncated, shell.result.output)
               .ok() ||
          !EmitOutput(stream, request_id, request.session_id(), shell.process_id, sequence,
                      true, shell.error_truncated, shell.result.error)
               .ok()) {
        return core::Ok(DispatchResult{{}, true});
      }

      v1::StreamFrame exit_frame;
      exit_frame.set_request_id(request_id);
      exit_frame.set_session_id(request.session_id());
      v1::ShellExited* exited = exit_frame.mutable_exited();
      exited->set_session_id(request.session_id());
      exited->set_process_id(shell.process_id);
      exited->set_has_exit_code(true);
      exited->set_exit_code(static_cast<std::int32_t>(shell.result.exit_code));
      exited->set_timed_out(shell.result.timed_out);
      exited->set_cancelled(shell.result.cancelled);
      exited->set_duration_ms(shell.duration_ms);
      v1::ShellSandboxFacts* facts = exited->mutable_sandbox();
      facts->set_enforcement(context.value().restricted_token ? "partial" : "full");
      if (shell.result.timed_out || shell.result.cancelled) {
        exited->set_error(shell.result.timed_out ? "command timed out" : "command cancelled");
      }
      if (!stream(exit_frame).ok()) return core::Ok(DispatchResult{{}, true});
      return core::Ok(DispatchResult{{}, false});
    }
    case v1::RpcRequest::kSetToolAuthorizations: {
      if (sessions_ == nullptr) return ErrorResult(request_id, UnsupportedRequest(engine_));
      const core::Status applied =
          sessions_->SetAuthorizedTools(rpc.set_tool_authorizations());
      if (!applied.ok()) return ErrorResult(request_id, applied.error());
      v1::RpcResponse response = BaseResponse(request_id);
      response.mutable_empty();
      return ResponseResult(std::move(response));
    }
    case v1::RpcRequest::kShellKill:
      return ErrorResult(request_id, core::NotImplemented(
                                         "shell_kill is not implemented; cancel by closing "
                                         "the requesting client connection"));
    case v1::RpcRequest::BODY_NOT_SET: {
      return ErrorResult(request_id,
                         core::Error(core::ErrorCode::kProtocolError, "request body is empty"));
    }
    default:
      return ErrorResult(request_id, UnsupportedRequest(engine_));
  }
}

}  // namespace rvm::server