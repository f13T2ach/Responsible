cmake_minimum_required(VERSION 4.3)

project(RvmEngine LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

option(RVM_BUILD_TESTS "Build RVM tests" ON)
set(RVM_PROTOBUF_SOURCE_DIR "" CACHE PATH "Optional local protobuf source tree")

if (WIN32)
  add_compile_definitions(UNICODE _UNICODE _WIN32_WINNT=0x0A00 NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

find_package(Protobuf CONFIG QUIET)
if (Protobuf_FOUND AND TARGET protobuf::libprotobuf)
  message(STATUS "RVM: using installed protobuf")
  set(RVM_PROTOBUF_LIB protobuf::libprotobuf)
  if (TARGET protobuf::protoc)
    set(RVM_PROTOC_COMMAND "$<TARGET_FILE:protobuf::protoc>")
  elseif (TARGET protoc)
    set(RVM_PROTOC_COMMAND "$<TARGET_FILE:protoc>")
  elseif (Protobuf_PROTOC_EXECUTABLE)
    set(RVM_PROTOC_COMMAND "${Protobuf_PROTOC_EXECUTABLE}")
  else()
    message(FATAL_ERROR "protobuf was found but protoc is unavailable")
  endif()
else()
  message(STATUS "RVM: fetching protobuf v21.12")
  include(FetchContent)
  set(CMAKE_POLICY_VERSION_MINIMUM 3.5 CACHE STRING "" FORCE)
  set(protobuf_BUILD_TESTS OFF CACHE BOOL "" FORCE)
  set(protobuf_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(protobuf_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
  set(protobuf_BUILD_PROTOC_BINARIES ON CACHE BOOL "" FORCE)
  set(protobuf_MSVC_STATIC_RUNTIME OFF CACHE BOOL "" FORCE)
  set(protobuf_WITH_ZLIB OFF CACHE BOOL "" FORCE)
  set(protobuf_BUILD_LIBUPB OFF CACHE BOOL "" FORCE)
  if (RVM_PROTOBUF_SOURCE_DIR)
    FetchContent_Declare(protobuf SOURCE_DIR "${RVM_PROTOBUF_SOURCE_DIR}")
  else()
    FetchContent_Declare(
      protobuf
      URL https://codeload.github.com/protocolbuffers/protobuf/tar.gz/refs/tags/v21.12
      URL_HASH SHA256=22fdaf641b31655d4b2297f9981fa5203b2866f8332d3c6333f6b0107bb320de
      DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
  endif()
  FetchContent_MakeAvailable(protobuf)
  set(RVM_PROTOBUF_LIB protobuf::libprotobuf)
  set(RVM_PROTOC_COMMAND "$<TARGET_FILE:protoc>")
endif()

set(RVM_PROTO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/protocol")
set(RVM_GENERATED_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(GLOB RVM_PROTO_FILES CONFIGURE_DEPENDS "${RVM_PROTO_ROOT}/rvm/v1/*.proto")
if (NOT RVM_PROTO_FILES)
  message(FATAL_ERROR "no protocol files found under ${RVM_PROTO_ROOT}")
endif()

set(RVM_GENERATED_SOURCES "")
set(RVM_GENERATED_HEADERS "")
foreach(proto_file ${RVM_PROTO_FILES})
  get_filename_component(proto_name "${proto_file}" NAME_WE)
  list(APPEND RVM_GENERATED_SOURCES "${RVM_GENERATED_DIR}/rvm/v1/${proto_name}.pb.cc")
  list(APPEND RVM_GENERATED_HEADERS "${RVM_GENERATED_DIR}/rvm/v1/${proto_name}.pb.h")
endforeach()

add_custom_command(
  OUTPUT ${RVM_GENERATED_SOURCES} ${RVM_GENERATED_HEADERS}
  COMMAND "${CMAKE_COMMAND}" -E make_directory "${RVM_GENERATED_DIR}"
  COMMAND "${RVM_PROTOC_COMMAND}" "--proto_path=${RVM_PROTO_ROOT}" "--cpp_out=${RVM_GENERATED_DIR}"
          ${RVM_PROTO_FILES}
  DEPENDS ${RVM_PROTO_FILES}
  COMMENT "Generating RVM v1 protocol"
  VERBATIM)

add_library(rvm_protocol STATIC ${RVM_GENERATED_SOURCES})
target_include_directories(rvm_protocol PUBLIC "${RVM_GENERATED_DIR}")
target_link_libraries(rvm_protocol PUBLIC ${RVM_PROTOBUF_LIB})
if (TARGET protoc)
  add_dependencies(rvm_protocol protoc)
elseif (TARGET protobuf::protoc)
  add_dependencies(rvm_protocol protobuf::protoc)
endif()

function(rvm_enable_warnings target)
  if (MSVC)
    target_compile_options(${target} PRIVATE /W4 /permissive-)
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Wpedantic)
  endif()
endfunction()

add_library(rvm_core STATIC
  core/error.cpp
  core/lifecycle.cpp
  core/log.cpp
  security/auth.cpp
  security/path_policy.cpp
  security/masked_policy.cpp)
target_include_directories(rvm_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
rvm_enable_warnings(rvm_core)

add_library(rvm_runtime STATIC
  core/engine.cpp
  environment/availability.cpp
  environment/system_probe.cpp
  hyperv/hcs_api.cpp
  hyperv/hcs_backend.cpp
  hyperv/hcs_config.cpp
  hyperv/hcs_environment.cpp
  hyperv/hcs_layer.cpp
  sandbox/appcontainer.cpp
  sandbox/backend.cpp
  workspace/union_view.cpp
  workspace/fsp_host.cpp)
target_include_directories(rvm_runtime PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_include_directories(rvm_runtime PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/third_party/winfsp/inc")
target_link_libraries(rvm_runtime PUBLIC rvm_core advapi32 rpcrt4 userenv)
rvm_enable_warnings(rvm_runtime)

add_library(rvm_channel STATIC
  channel/frame.cpp
  channel/frame_io.cpp
  channel/protobuf_codec.cpp
  channel/convert.cpp)
target_include_directories(rvm_channel PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(rvm_channel PUBLIC rvm_core rvm_protocol)
rvm_enable_warnings(rvm_channel)

add_library(rvm_server STATIC
  server/jailbreak_smoke.cpp
  server/network_audit.cpp
  server/pipe_server.cpp
  server/rpc_dispatcher.cpp
  server/session_manager.cpp
  server/shell_runner.cpp)
target_include_directories(rvm_server PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(rvm_server PUBLIC rvm_runtime rvm_channel advapi32 iphlpapi ws2_32)
rvm_enable_warnings(rvm_server)

add_executable(RvmEngine main.cpp cli/rvm_cli.cpp client/shell_bridge.cpp)
target_link_libraries(RvmEngine PRIVATE rvm_server bcrypt)
rvm_enable_warnings(RvmEngine)

if (MINGW)
  get_filename_component(RVM_MINGW_RUNTIME_DIR "${CMAKE_C_COMPILER}" DIRECTORY)
  function(rvm_bundle_runtime target)
    foreach(dll libgcc_s_seh-1.dll libstdc++-6.dll libwinpthread-1.dll)
      if (EXISTS "${RVM_MINGW_RUNTIME_DIR}/${dll}")
        add_custom_command(TARGET ${target} POST_BUILD
          COMMAND "${CMAKE_COMMAND}" -E copy_if_different
                  "${RVM_MINGW_RUNTIME_DIR}/${dll}" "$<TARGET_FILE_DIR:${target}>")
      endif()
    endforeach()
  endfunction()
  rvm_bundle_runtime(RvmEngine)
endif()

if (RVM_BUILD_TESTS)
  enable_testing()
  add_executable(rvm_tests
    tests/test_main.cpp
    tests/test_state_machines.cpp
    tests/test_path_policy.cpp
    tests/test_masked_policy.cpp
    tests/test_frame.cpp
    tests/test_protocol.cpp
    tests/test_availability.cpp
    tests/test_auth.cpp
    tests/test_dispatcher.cpp
    tests/test_pipe.cpp
    tests/test_hcs.cpp
    tests/test_capability.cpp
    tests/test_shell_environment.cpp
    tests/test_sessions.cpp
    tests/test_union_view.cpp
    tests/test_workspace_archive.cpp)
  target_include_directories(rvm_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(rvm_tests PRIVATE rvm_server rvm_core rvm_runtime rvm_channel
                                           rvm_protocol)
  rvm_enable_warnings(rvm_tests)
  if (COMMAND rvm_bundle_runtime)
    rvm_bundle_runtime(rvm_tests)
  endif()
  add_test(NAME rvm_tests COMMAND rvm_tests)
endif()