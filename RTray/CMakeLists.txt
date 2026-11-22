cmake_minimum_required(VERSION 4.3)

project(RTray LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

if (WIN32)
  add_compile_definitions(UNICODE _UNICODE _WIN32_WINNT=0x0A00 NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

add_executable(RTray main.cpp)
target_include_directories(RTray PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
target_link_libraries(RTray PRIVATE shell32 advapi32 user32)

if (MSVC)
  target_compile_options(RTray PRIVATE /W4 /permissive-)
else()
  target_compile_options(RTray PRIVATE -Wall -Wextra -Wpedantic)
endif()

# 与 RVM 一致：把 MinGW 运行时 DLL 复制到可执行文件旁边，免得到处依赖 PATH
if (MINGW)
  get_filename_component(RTRAY_MINGW_RUNTIME_DIR "${CMAKE_C_COMPILER}" DIRECTORY)
  foreach(dll libgcc_s_seh-1.dll libstdc++-6.dll libwinpthread-1.dll)
    if (EXISTS "${RTRAY_MINGW_RUNTIME_DIR}/${dll}")
      add_custom_command(TARGET RTray POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different
                "${RTRAY_MINGW_RUNTIME_DIR}/${dll}" "$<TARGET_FILE_DIR:RTray>")
    endif()
  endforeach()
endif()
