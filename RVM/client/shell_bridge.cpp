#include "client/shell_bridge.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cwchar>
#include <string>
#include <thread>
#include <vector>

#include "channel/convert.h"
#include "channel/frame_io.h"
#include "channel/protobuf_codec.h"
#include "core/error.h"
#include "rvm/v1/rpc.pb.h"

namespace rvm::client {
namespace {

constexpr int kExitUnavailable = 127;
constexpr int kExitTimedOut = 124;
constexpr int kExitCancelled = 130;
constexpr std::uint64_t kHelloRequestId = 1;
constexpr std::uint64_t kExecRequestId = 2;
constexpr std::uint32_t kDefaultTimeoutMs = 600000;

void Report(const std::string& message) {
  const HANDLE error = GetStdHandle(STD_ERROR_HANDLE);
  std::string line = "RVM_SANDBOX_UNAVAILABLE: " + message + "\n";
  DWORD written = 0;
  WriteFile(error, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
}

void WriteStream(HANDLE handle, const std::string& data) {
  std::size_t offset = 0;
  while (offset < data.size()) {
    DWORD written = 0;
    const DWORD chunk =
        static_cast<DWORD>(std::min<std::size_t>(data.size() - offset, 64 * 1024));
    if (!WriteFile(handle, data.data() + offset, chunk, &written, nullptr) || written == 0) return;
    offset += written;
  }
}

bool SendRequest(HANDLE pipe, const v1::RpcRequest& request) {
  auto payload = channel::SerializeMessage(request);
  if (!payload.ok()) return false;
  const std::vector<std::uint8_t> bytes(payload.value().begin(), payload.value().end());
  return channel::WriteFrame(pipe, channel::FrameType::kRpcRequest, request.request_id(), bytes)
      .ok();
}

std::wstring EnvironmentValue(const wchar_t* name) {
  const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
  if (size == 0) return {};
  std::wstring value(size, L'\0');
  const DWORD copied = GetEnvironmentVariableW(name, value.data(), size);
  if (copied == 0 || copied >= size) return {};
  value.resize(copied);
  return value;
}

std::string WideToUtf8OrEmpty(const std::wstring& value) {
  auto converted = channel::ToUtf8(value);
  return converted.ok() ? converted.Take() : std::string();
}

HANDLE ConnectPipe(const std::wstring& pipe_name) {
  const std::wstring path = L"\\\\.\\pipe\\" + pipe_name;
  for (int attempt = 0; attempt < 50; attempt++) {
    HANDLE pipe = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                              OPEN_EXISTING, 0, nullptr);
    if (pipe != INVALID_HANDLE_VALUE) return pipe;
    const DWORD error = GetLastError();
    if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) return INVALID_HANDLE_VALUE;
    if (error == ERROR_PIPE_BUSY) WaitNamedPipeW(path.c_str(), 100);
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
  }
  return INVALID_HANDLE_VALUE;
}

}  // namespace

int RunShellBridge(int argc, wchar_t** argv) {
  std::wstring pipe_name;
  std::string session_text;
  std::uint32_t timeout_ms = kDefaultTimeoutMs;
  std::vector<std::wstring> command;
  bool after_separator = false;
  for (int i = 2; i < argc; i++) {
    const std::wstring arg = argv[i];
    if (after_separator) {
      command.push_back(arg);
      continue;
    }
    if (arg == L"--") {
      after_separator = true;
    } else if (arg == L"--pipe" && i + 1 < argc) {
      pipe_name = argv[++i];
    } else if (arg == L"--session" && i + 1 < argc) {
      session_text = WideToUtf8OrEmpty(argv[++i]);
    } else if (arg == L"--timeout-ms" && i + 1 < argc) {
      timeout_ms = static_cast<std::uint32_t>(_wtoi(argv[++i]));
    } else {
      Report("unknown shell bridge argument: " + WideToUtf8OrEmpty(arg));
      return kExitUnavailable;
    }
  }
  if (pipe_name.empty() || command.empty()) {
    Report("shell bridge needs --pipe and a command after --");
    return kExitUnavailable;
  }
  std::uint64_t session_id = 0;
  {
    auto parsed = std::strtoull(session_text.c_str(), nullptr, 10);
    if (parsed == 0) {
      Report("shell bridge needs a numeric --session");
      return kExitUnavailable;
    }
    session_id = parsed;
  }
  const std::wstring token = EnvironmentValue(L"RESPONSIBLE_RVM_TOKEN");
  if (token.empty()) {
    Report("RESPONSIBLE_RVM_TOKEN is not set");
    return kExitUnavailable;
  }

  const HANDLE pipe = ConnectPipe(pipe_name);
  if (pipe == INVALID_HANDLE_VALUE) {
    Report("cannot connect to the RVM engine pipe");
    return kExitUnavailable;
  }

  v1::RpcRequest hello;
  hello.set_protocol(v1::PROTOCOL_VERSION_1);
  hello.set_request_id(kHelloRequestId);
  v1::HelloRequest* hello_body = hello.mutable_hello();
  hello_body->set_requested_protocol(v1::PROTOCOL_VERSION_1);
  hello_body->set_client_name("responsible-rvm-shell-bridge");
  hello_body->set_client_pid(GetCurrentProcessId());
  hello_body->set_token(WideToUtf8OrEmpty(token));
  if (!SendRequest(pipe, hello)) {
    Report("failed to send the handshake");
    CloseHandle(pipe);
    return kExitUnavailable;
  }

  v1::RpcResponse hello_response;
  {
    channel::FrameHeader header;
    std::vector<std::uint8_t> payload;
    const core::Status read = channel::ReadFrame(pipe, header, payload);
    if (!read.ok() ||
        !channel::ParseMessage(payload, hello_response, channel::kMaxPayloadBytes).ok()) {
      Report("failed to read the handshake response");
      CloseHandle(pipe);
      return kExitUnavailable;
    }
  }
  if (hello_response.has_error()) {
    Report(hello_response.error().message());
    CloseHandle(pipe);
    return kExitUnavailable;
  }
  if (hello_response.has_hello() && !hello_response.hello().rvm_available()) {
    Report(hello_response.hello().unavailable_reason().empty()
               ? "RVM is unavailable"
               : hello_response.hello().unavailable_reason());
    CloseHandle(pipe);
    return kExitUnavailable;
  }

  wchar_t workdir[MAX_PATH] = {};
  const DWORD workdir_size = GetCurrentDirectoryW(MAX_PATH, workdir);

  v1::RpcRequest exec;
  exec.set_protocol(v1::PROTOCOL_VERSION_1);
  exec.set_request_id(kExecRequestId);
  v1::ShellExecRequest* request = exec.mutable_shell_exec();
  request->set_session_id(session_id);
  request->set_workdir(WideToUtf8OrEmpty(std::wstring(workdir, workdir_size)));
  request->set_timeout_ms(timeout_ms);
  for (const std::wstring& arg : command) request->add_argv(WideToUtf8OrEmpty(arg));
  if (!SendRequest(pipe, exec)) {
    Report("failed to send the shell request");
    CloseHandle(pipe);
    return kExitUnavailable;
  }

  int exit_code = 1;
  for (;;) {
    channel::FrameHeader header;
    std::vector<std::uint8_t> payload;
    const core::Status status = channel::ReadFrame(pipe, header, payload);
    if (!status.ok()) {
      Report("the engine connection closed before the command finished");
      CloseHandle(pipe);
      return kExitUnavailable;
    }
    if (header.type == channel::FrameType::kRpcResponse) {
      v1::RpcResponse response;
      if (channel::ParseMessage(payload, response, channel::kMaxPayloadBytes).ok() &&
          response.has_error()) {
        const std::string detail = response.error().detail();
        Report(detail.empty() ? response.error().message()
                              : response.error().message() + " (" + detail + ")");
        CloseHandle(pipe);
        return kExitUnavailable;
      }
      continue;
    }
    if (header.type != channel::FrameType::kStream) continue;

    v1::StreamFrame frame;
    if (!channel::ParseMessage(payload, frame, channel::kMaxPayloadBytes).ok()) {
      Report("malformed stream frame from the engine");
      CloseHandle(pipe);
      return kExitUnavailable;
    }
    if (frame.has_output()) {
      const v1::ShellOutputChunk& chunk = frame.output();
      const HANDLE target =
          chunk.stream() == v1::SHELL_STREAM_STDERR ? GetStdHandle(STD_ERROR_HANDLE)
                                                    : GetStdHandle(STD_OUTPUT_HANDLE);
      WriteStream(target, chunk.data());
    } else if (frame.has_exited()) {
      const v1::ShellExited& exited = frame.exited();
      exit_code = exited.has_exit_code() ? static_cast<int>(exited.exit_code()) : 1;
      if (exited.timed_out()) exit_code = kExitTimedOut;
      if (exited.cancelled()) exit_code = kExitCancelled;
      break;
    }
  }

  FlushFileBuffers(GetStdHandle(STD_OUTPUT_HANDLE));
  FlushFileBuffers(GetStdHandle(STD_ERROR_HANDLE));
  CloseHandle(pipe);
  return exit_code;
}

}  // namespace rvm::client