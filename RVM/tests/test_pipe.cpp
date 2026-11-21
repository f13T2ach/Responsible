#include <windows.h>

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <thread>
#include <vector>

#include "channel/frame_io.h"
#include "channel/protobuf_codec.h"
#include "core/engine.h"
#include "rvm/v1/control.pb.h"
#include "rvm/v1/rpc.pb.h"
#include "server/pipe_server.h"
#include "test_registry.h"

using rvm::core::Engine;
using rvm::core::EngineConfig;
using rvm::core::EngineState;
using rvm::environment::AvailabilityReport;
using rvm::environment::IAvailabilityProbe;
using rvm::environment::ProbeResult;
using rvm::environment::ProbeState;

namespace {

class UnavailableProbe final : public IAvailabilityProbe {
 public:
  AvailabilityReport Probe() const override {
    AvailabilityReport report;
    report.appcontainer = ProbeResult{ProbeState::kFailed, "userenv.dll is missing"};
    return report;
  }
};

std::vector<std::uint8_t> EncodeRequest(const rvm::v1::RpcRequest& request) {
  const auto encoded = rvm::channel::SerializeMessage(request, rvm::channel::kMaxPayloadBytes);
  RVM_CHECK(encoded.ok());
  return std::vector<std::uint8_t>(encoded.value().begin(), encoded.value().end());
}

rvm::v1::RpcResponse ReadResponse(HANDLE client, std::uint64_t expected_request_id) {
  rvm::channel::FrameHeader header;
  std::vector<std::uint8_t> payload;
  RVM_CHECK(rvm::channel::ReadFrame(client, header, payload).ok());
  RVM_CHECK(header.type == rvm::channel::FrameType::kRpcResponse);
  RVM_CHECK_EQ(header.request_id, expected_request_id);

  rvm::v1::RpcResponse response;
  const auto parsed = rvm::channel::ParseMessage(
      std::span<const std::uint8_t>(payload.data(), payload.size()), response,
      rvm::channel::kMaxPayloadBytes);
  RVM_CHECK(parsed.ok());
  return response;
}

HANDLE ConnectClient(const std::wstring& full_name) {
  for (int attempt = 0; attempt < 300; ++attempt) {
    HANDLE client = CreateFileW(full_name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                OPEN_EXISTING, 0, nullptr);
    if (client != INVALID_HANDLE_VALUE) return client;
    const DWORD error = GetLastError();
    if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) return INVALID_HANDLE_VALUE;
    Sleep(10);
  }
  return INVALID_HANDLE_VALUE;
}

}  // namespace

RVM_TEST(pipe_server_handshake_ping_and_shutdown) {
  auto engine = std::make_unique<Engine>(EngineConfig{},
                                         std::make_unique<UnavailableProbe>());
  RVM_CHECK(engine->Initialize().ok());

  const std::wstring pipe_name =
      L"rvm-test-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
      std::to_wstring(GetTickCount64());
  const std::wstring full_name = L"\\\\.\\pipe\\" + pipe_name;

  rvm::server::PipeServer server(*engine, {pipe_name, "integration-token"});
  rvm::core::Status run_status;
  std::thread server_thread([&] { run_status = server.Run(); });
  struct ServerGuard {
    rvm::server::PipeServer& server;
    std::thread& thread;
    ~ServerGuard() {
      if (!thread.joinable()) return;
      server.Stop();
      thread.join();
    }
  } guard{server, server_thread};

  HANDLE client = ConnectClient(full_name);
  RVM_CHECK(client != INVALID_HANDLE_VALUE);

  rvm::v1::RpcRequest hello;
  hello.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  hello.set_request_id(1);
  hello.mutable_hello()->set_requested_protocol(rvm::v1::PROTOCOL_VERSION_1);
  hello.mutable_hello()->set_client_name("pipe-test");
  hello.mutable_hello()->set_client_pid(GetCurrentProcessId());
  hello.mutable_hello()->set_token("integration-token");
  const std::vector<std::uint8_t> hello_payload = EncodeRequest(hello);
  RVM_CHECK(rvm::channel::WriteFrame(client, rvm::channel::FrameType::kRpcRequest, 1,
                                     hello_payload)
                .ok());

  const rvm::v1::RpcResponse hello_response = ReadResponse(client, 1);
  RVM_CHECK(hello_response.body_case() == rvm::v1::RpcResponse::kHello);
  RVM_CHECK(!hello_response.hello().rvm_available());
  RVM_CHECK(!hello_response.hello().unavailable_reason().empty());

  rvm::v1::RpcRequest ping;
  ping.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  ping.set_request_id(2);
  ping.mutable_ping()->set_nonce(1234);
  const std::vector<std::uint8_t> ping_payload = EncodeRequest(ping);
  RVM_CHECK(rvm::channel::WriteFrame(client, rvm::channel::FrameType::kRpcRequest, 2,
                                     ping_payload)
                .ok());
  RVM_CHECK_EQ(ReadResponse(client, 2).pong().nonce(), 1234u);

  rvm::v1::RpcRequest shutdown;
  shutdown.set_protocol(rvm::v1::PROTOCOL_VERSION_1);
  shutdown.set_request_id(3);
  shutdown.mutable_shutdown()->set_reason("integration test");
  const std::vector<std::uint8_t> shutdown_payload = EncodeRequest(shutdown);
  RVM_CHECK(rvm::channel::WriteFrame(client, rvm::channel::FrameType::kRpcRequest, 3,
                                     shutdown_payload)
                .ok());
  RVM_CHECK(ReadResponse(client, 3).shutdown().accepted());

  CloseHandle(client);
  server_thread.join();

  RVM_CHECK(run_status.ok());
  RVM_CHECK(engine->state() == EngineState::kStopped);
}