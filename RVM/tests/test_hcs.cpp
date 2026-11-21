#include <cstdint>
#include <filesystem>
#include <string>

#include "hyperv/hcs_api.h"
#include "hyperv/hcs_config.h"
#include "hyperv/hcs_layer.h"
#include "test_registry.h"

using rvm::hyperv::BaseLayerStore;
using rvm::hyperv::BuildComputeSystemJson;
using rvm::hyperv::BuildLayerDataJson;
using rvm::hyperv::ComputeSystemSpec;
using rvm::hyperv::HcsApi;
using rvm::hyperv::IsAbsoluteWindowsPath;
using rvm::hyperv::IsValidLayerId;
using rvm::hyperv::JsonEscape;
using rvm::hyperv::LayerSpec;
using rvm::hyperv::MappedDirectorySpec;
using rvm::hyperv::NewGuid;

namespace {

constexpr const char* kLayerId = "01234567-89ab-cdef-0123-456789abcdef";

bool Contains(const std::string& haystack, const std::string& needle) {
  return haystack.find(needle) != std::string::npos;
}

}  // namespace

RVM_TEST(hcs_json_escape_handles_quotes_backslashes_and_controls) {
  RVM_CHECK_EQ(JsonEscape("a\"b\\c\n"), std::string("a\\\"b\\\\c\\n"));
  RVM_CHECK_EQ(JsonEscape(std::string(1, '\x01')), std::string("\\u0001"));
  RVM_CHECK_EQ(JsonEscape("plain"), std::string("plain"));
}

RVM_TEST(hcs_path_validation_rejects_relative_and_unc_paths) {
  RVM_CHECK(IsAbsoluteWindowsPath(L"C:\\Work"));
  RVM_CHECK(IsAbsoluteWindowsPath(L"D:/Data"));
  RVM_CHECK(!IsAbsoluteWindowsPath(L"relative\\directory"));
  RVM_CHECK(!IsAbsoluteWindowsPath(L"\\\\server\\share"));
  RVM_CHECK(!IsAbsoluteWindowsPath(L"\\\\?\\C:\\Work"));
  RVM_CHECK(!IsAbsoluteWindowsPath(L"C:relative"));
  RVM_CHECK(!IsAbsoluteWindowsPath(L""));
}

RVM_TEST(hcs_layer_id_validation_requires_guid_shape) {
  RVM_CHECK(IsValidLayerId(kLayerId));
  RVM_CHECK(!IsValidLayerId("not-a-guid"));
  RVM_CHECK(!IsValidLayerId("0123456789abcdef0123456789abcdef"));
  RVM_CHECK(!IsValidLayerId("01234567-89ab-cdef-0123-456789abcde"));
}

RVM_TEST(hcs_new_guid_is_canonical) {
  const std::string guid = NewGuid();
  RVM_CHECK(IsValidLayerId(guid));
}

RVM_TEST(hcs_compute_system_json_contains_container_isolation) {
  ComputeSystemSpec spec;
  spec.name = L"rvm-smoke";
  spec.layers.push_back({kLayerId, L"C:\\ProgramData\\RVM\\layers\\base\\x"});
  spec.scratch_path = L"C:\\ProgramData\\RVM\\layers\\sessions\\s1\\scratch";
  spec.mapped_directories.push_back(
      MappedDirectorySpec{L"D:\\Work", L"C:\\RVM\\Workspace", false});
  spec.memory_mb = 2048;

  const auto json = BuildComputeSystemJson(spec);
  RVM_CHECK(json.ok());
  const std::string& text = json.value();
  RVM_CHECK(Contains(text, R"("SchemaVersion":{"Major":2,"Minor":1})"));
  RVM_CHECK(Contains(text, R"("Owner":"responsible-rvm")"));
  RVM_CHECK(Contains(text, R"("ShouldTerminateOnLastHandleClosed":true)"));
  RVM_CHECK(Contains(text, R"("SystemType":"Container")"));
  RVM_CHECK(Contains(text, R"("HvPartition":true)"));
  RVM_CHECK(Contains(text, "\"Id\":\"" + std::string(kLayerId) + "\""));
  RVM_CHECK(Contains(text, R"("PathType":"AbsolutePath")"));
  RVM_CHECK(Contains(text, R"("HostPathType":"AbsolutePath")"));
  RVM_CHECK(Contains(text, R"("ContainerPath":"C:\\RVM\\Workspace")"));
  RVM_CHECK(Contains(text, R"("ReadOnly":false)"));
  RVM_CHECK(Contains(text, R"("SizeInMB":2048)"));
}

RVM_TEST(hcs_compute_system_json_rejects_invalid_specs) {
  ComputeSystemSpec spec;
  spec.name = L"rvm-smoke";
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());

  spec.layers.push_back({kLayerId, L"C:\\base"});
  spec.scratch_path = L"relative\\scratch";
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());

  spec.scratch_path = L"C:\\scratch";
  spec.memory_mb = 64;
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());

  spec.memory_mb = 0;
  spec.name = L"bad name";
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());

  spec.name = L"rvm-smoke";
  spec.layers[0].id = "not-a-guid";
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());

  spec.layers[0].id = kLayerId;
  spec.layers[0].path = L"\\\\server\\share\\base";
  RVM_CHECK(!BuildComputeSystemJson(spec).ok());
}

RVM_TEST(hcs_layer_data_json_lists_antecedent_layers) {
  const auto json = BuildLayerDataJson({LayerSpec{kLayerId, L"C:\\base"}});
  RVM_CHECK(json.ok());
  RVM_CHECK(Contains(json.value(), R"("SchemaVersion":{"Major":2,"Minor":1})"));
  RVM_CHECK(Contains(json.value(), "\"Id\":\"" + std::string(kLayerId) + "\""));
  RVM_CHECK(!BuildLayerDataJson({}).ok());
}

RVM_TEST(hcs_hresult_mapping_is_explicit) {
  const auto hyperv_missing = HcsApi::MapHresult(static_cast<std::int32_t>(0x80370102u), "create");
  RVM_CHECK(hyperv_missing.code == rvm::core::ErrorCode::kHcsUnavailable);
  RVM_CHECK(Contains(hyperv_missing.detail, "0x80370102"));

  const auto denied = HcsApi::MapHresult(static_cast<std::int32_t>(0x80070005u), "create");
  RVM_CHECK(denied.code == rvm::core::ErrorCode::kPolicyDenied);

  const auto hcs_denied = HcsApi::MapHresult(static_cast<std::int32_t>(0x8037011Bu), "probe");
  RVM_CHECK(hcs_denied.code == rvm::core::ErrorCode::kPolicyDenied);
  RVM_CHECK(Contains(hcs_denied.message, "Hyper-V Administrators"));

  const auto allocation = HcsApi::MapHresult(static_cast<std::int32_t>(0x8037011Au), "probe");
  RVM_CHECK(allocation.code == rvm::core::ErrorCode::kBackendFailed);

  const auto already = HcsApi::MapHresult(static_cast<std::int32_t>(0x8037010Du), "create");
  RVM_CHECK(already.code == rvm::core::ErrorCode::kAlreadyExists);

  const auto unknown = HcsApi::MapHresult(static_cast<std::int32_t>(0x80004005u), "create");
  RVM_CHECK(unknown.code == rvm::core::ErrorCode::kBackendFailed);
  RVM_CHECK(Contains(unknown.detail, "0x80004005"));
}

RVM_TEST(base_layer_store_reports_missing_layer_and_manages_scratch) {
  const std::filesystem::path root =
      std::filesystem::temp_directory_path() / ("rvm-test-" + NewGuid());
  const BaseLayerStore store(root.wstring());

  RVM_CHECK(!store.Find().ok());
  const auto ensured = store.Ensure(false);
  RVM_CHECK(!ensured.ok());
  RVM_CHECK(ensured.error().code == rvm::core::ErrorCode::kHcsUnavailable);

  const auto scratch = store.PrepareScratch("sess-1");
  RVM_CHECK(scratch.ok());
  RVM_CHECK(std::filesystem::is_directory(scratch.value()));
  RVM_CHECK(!store.PrepareScratch("../escape").ok());

  RVM_CHECK(store.RemoveScratch(scratch.value()).ok());
  RVM_CHECK(!std::filesystem::exists(scratch.value()));

  RVM_CHECK(!store.RemoveScratch(L"C:\\Windows").ok());

  std::error_code ignored;
  std::filesystem::remove_all(root, ignored);
}