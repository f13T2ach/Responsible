#include <windows.h>

#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <string_view>

#include "test_registry.h"
#include "workspace/union_view.h"

namespace {

class TempTree {
 public:
  TempTree() {
    wchar_t buffer[MAX_PATH] = {};
    const DWORD length = GetTempPathW(MAX_PATH, buffer);
    std::wstring root(buffer, length);
    path_ = root + L"rvm-union-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
            std::to_wstring(GetTickCount64());
    std::filesystem::create_directories(path_);
  }

  ~TempTree() {
    std::error_code error;
    std::filesystem::remove_all(path_, error);
  }

  TempTree(const TempTree&) = delete;
  TempTree& operator=(const TempTree&) = delete;

  std::wstring Path(std::wstring_view relative) const {
    if (relative.empty()) return path_;
    return path_ + L"\\" + std::wstring(relative);
  }

  void MakeDirectory(std::wstring_view relative) const {
    std::filesystem::create_directories(Path(relative));
  }

  void WriteFile(std::wstring_view relative, std::string_view content) const {
    const std::filesystem::path file(Path(relative));
    if (file.has_parent_path()) std::filesystem::create_directories(file.parent_path());
    std::ofstream stream(file, std::ios::binary | std::ios::trunc);
    stream.write(content.data(), static_cast<std::streamsize>(content.size()));
  }

 private:
  std::wstring path_;
};

struct Fixture {
  TempTree temp;
  rvm::workspace::UnionView view;

  Fixture() : view(Make()) {}

  rvm::workspace::UnionView Make() {
    temp.MakeDirectory(L"base");
    temp.MakeDirectory(L"diff");
    temp.MakeDirectory(L"tombstones");
    auto created = rvm::workspace::UnionView::Create(
        {temp.Path(L"base"), temp.Path(L"diff"), temp.Path(L"tombstones"), {}});
    RVM_CHECK(created.ok());
    return created.Take();
  }

  std::wstring BaseFile(std::wstring_view relative, std::string_view content) const {
    temp.WriteFile((std::wstring(L"base\\") + std::wstring(relative)).c_str(), content);
    return temp.Path((std::wstring(L"base\\") + std::wstring(relative)).c_str());
  }

  std::wstring DiffFile(std::wstring_view relative, std::string_view content) const {
    temp.WriteFile((std::wstring(L"diff\\") + std::wstring(relative)).c_str(), content);
    return temp.Path((std::wstring(L"diff\\") + std::wstring(relative)).c_str());
  }
};

bool HasEntry(const std::vector<rvm::workspace::UnionEntry>& entries, std::wstring_view name) {
  for (const auto& entry : entries) {
    if (entry.relative == name) return true;
  }
  return false;
}

}  // namespace

RVM_TEST(union_view_serves_base_entries) {
  Fixture fixture;
  fixture.BaseFile(L"src\\main.cpp", "int main;");

  const auto root = fixture.view.Stat(L"");
  RVM_CHECK(root.ok());
  RVM_CHECK(root.value().is_directory);

  const auto directory = fixture.view.Stat(L"src");
  RVM_CHECK(directory.ok());
  RVM_CHECK(directory.value().is_directory);
  RVM_CHECK(!directory.value().from_diff);

  const auto file = fixture.view.Stat(L"src\\main.cpp");
  RVM_CHECK(file.ok());
  RVM_CHECK(!file.value().is_directory);
  RVM_CHECK(!file.value().from_diff);
  RVM_CHECK_EQ(file.value().size, std::uint64_t{9});

  const auto listing = fixture.view.List(L"");
  RVM_CHECK(listing.ok());
  RVM_CHECK_EQ(listing.value().size(), std::size_t{1});
  RVM_CHECK(HasEntry(listing.value(), L"src"));
}

RVM_TEST(union_view_diff_overrides_base) {
  Fixture fixture;
  fixture.BaseFile(L"a.txt", "base");
  fixture.DiffFile(L"a.txt", "diff!");

  const auto entry = fixture.view.Stat(L"a.txt");
  RVM_CHECK(entry.ok());
  RVM_CHECK(entry.value().from_diff);
  RVM_CHECK_EQ(entry.value().size, std::uint64_t{5});

  const auto listing = fixture.view.List(L"");
  RVM_CHECK(listing.ok());
  RVM_CHECK_EQ(listing.value().size(), std::size_t{1});
  RVM_CHECK(listing.value()[0].from_diff);
}

RVM_TEST(union_view_create_writes_to_diff_only) {
  Fixture fixture;
  const auto created = fixture.view.CreateEntry(L"notes\\todo.md", false);
  RVM_CHECK(created.ok());
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"diff\\notes\\todo.md")));
  RVM_CHECK(!std::filesystem::exists(fixture.temp.Path(L"base\\notes\\todo.md")));

  const auto entry = fixture.view.Stat(L"notes\\todo.md");
  RVM_CHECK(entry.ok());
  RVM_CHECK(entry.value().from_diff);

  const auto duplicate = fixture.view.CreateEntry(L"notes\\todo.md", false);
  RVM_CHECK(!duplicate.ok());
  RVM_CHECK(duplicate.error().code == rvm::core::ErrorCode::kAlreadyExists);
}

RVM_TEST(union_view_delete_tombstones_base_file) {
  Fixture fixture;
  fixture.BaseFile(L"a.txt", "base");

  const auto removed = fixture.view.RemoveEntry(L"a.txt");
  RVM_CHECK(removed.ok());
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"base\\a.txt")));

  const auto entry = fixture.view.Stat(L"a.txt");
  RVM_CHECK(!entry.ok());
  RVM_CHECK(entry.error().code == rvm::core::ErrorCode::kNotFound);

  const auto listing = fixture.view.List(L"");
  RVM_CHECK(listing.ok());
  RVM_CHECK(!HasEntry(listing.value(), L"a.txt"));
}

RVM_TEST(union_view_recreated_directory_stays_empty) {
  Fixture fixture;
  fixture.BaseFile(L"docs\\a.txt", "a");
  fixture.BaseFile(L"docs\\b.txt", "b");

  RVM_CHECK(fixture.view.RemoveEntry(L"docs").ok());

  const auto hidden = fixture.view.Stat(L"docs");
  RVM_CHECK(!hidden.ok());

  RVM_CHECK(fixture.view.CreateEntry(L"docs", true).ok());
  const auto listing = fixture.view.List(L"docs");
  RVM_CHECK(listing.ok());
  RVM_CHECK_EQ(listing.value().size(), std::size_t{0});

  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"base\\docs\\a.txt")));
}

RVM_TEST(union_view_list_merges_without_duplicates) {
  Fixture fixture;
  fixture.BaseFile(L"shared.txt", "base");
  fixture.BaseFile(L"only-base.txt", "base");
  fixture.DiffFile(L"shared.txt", "diff");
  fixture.DiffFile(L"only-diff.txt", "diff");

  const auto listing = fixture.view.List(L"");
  RVM_CHECK(listing.ok());
  RVM_CHECK_EQ(listing.value().size(), std::size_t{3});
  RVM_CHECK(HasEntry(listing.value(), L"shared.txt"));
  RVM_CHECK(HasEntry(listing.value(), L"only-base.txt"));
  RVM_CHECK(HasEntry(listing.value(), L"only-diff.txt"));

  for (const auto& entry : listing.value()) {
    if (entry.relative == L"shared.txt") RVM_CHECK(entry.from_diff);
  }
}

RVM_TEST(union_view_rename_copies_up_and_hides_source) {
  Fixture fixture;
  fixture.BaseFile(L"a.txt", "payload");

  const auto renamed = fixture.view.Rename(L"a.txt", L"b.txt", false);
  RVM_CHECK(renamed.ok());

  const auto destination = fixture.view.Stat(L"b.txt");
  RVM_CHECK(destination.ok());
  RVM_CHECK(destination.value().from_diff);
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"diff\\b.txt")));
  RVM_CHECK(!std::filesystem::exists(fixture.temp.Path(L"base\\b.txt")));

  const auto source = fixture.view.Stat(L"a.txt");
  RVM_CHECK(!source.ok());

  const auto duplicate = fixture.view.Rename(L"b.txt", L"b.txt", false);
  RVM_CHECK(duplicate.ok());
}

RVM_TEST(union_view_rename_replaces_when_requested) {
  Fixture fixture;
  fixture.BaseFile(L"a.txt", "source");
  fixture.BaseFile(L"b.txt", "target");

  const auto refused = fixture.view.Rename(L"a.txt", L"b.txt", false);
  RVM_CHECK(!refused.ok());
  RVM_CHECK(refused.error().code == rvm::core::ErrorCode::kAlreadyExists);

  const auto replaced = fixture.view.Rename(L"a.txt", L"b.txt", true);
  RVM_CHECK(replaced.ok());
  const auto destination = fixture.view.Stat(L"b.txt");
  RVM_CHECK(destination.ok());
  RVM_CHECK(destination.value().from_diff);
  RVM_CHECK_EQ(destination.value().size, std::uint64_t{6});
}

RVM_TEST(union_view_rejects_unsafe_paths) {
  Fixture fixture;
  const wchar_t* const unsafe[] = {
      L"..\\escape.txt", L"a\\..\\b", L"C:\\x", L"a:b", L"CON", L"nul.txt",
      L"trailing.", L"trailing ", L"a\\\\b", L"a/*?b",
  };
  for (const wchar_t* path : unsafe) {
    const auto entry = fixture.view.Stat(path);
    RVM_CHECK(!entry.ok());
    RVM_CHECK(entry.error().code == rvm::core::ErrorCode::kInvalidArgument);
  }

  const auto root = fixture.view.Stat(L"\\");
  RVM_CHECK(root.ok());

  const auto created = fixture.view.CreateEntry(L"bad:name", false);
  RVM_CHECK(!created.ok());
}

RVM_TEST(union_view_copy_up_preserves_base) {
  Fixture fixture;
  fixture.BaseFile(L"src\\file.txt", "base-content");

  const auto copied = fixture.view.CopyUp(L"src\\file.txt");
  RVM_CHECK(copied.ok());
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"diff\\src\\file.txt")));
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"base\\src\\file.txt")));

  const auto entry = fixture.view.Stat(L"src\\file.txt");
  RVM_CHECK(entry.ok());
  RVM_CHECK(entry.value().from_diff);
}

RVM_TEST(union_view_rename_directory_copies_tree) {
  Fixture fixture;
  fixture.BaseFile(L"pkg\\a.txt", "a");
  fixture.BaseFile(L"pkg\\nested\\b.txt", "b");

  const auto renamed = fixture.view.Rename(L"pkg", L"pkg2", false);
  RVM_CHECK(renamed.ok());
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"diff\\pkg2\\a.txt")));
  RVM_CHECK(std::filesystem::exists(fixture.temp.Path(L"diff\\pkg2\\nested\\b.txt")));
  RVM_CHECK(!fixture.view.Stat(L"pkg").ok());
  RVM_CHECK(fixture.view.Stat(L"pkg2\\nested\\b.txt").ok());
}