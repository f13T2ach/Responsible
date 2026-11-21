#pragma once

#include <string>
#include <string_view>

namespace rvm::security {

bool ConstantTimeEquals(std::string_view left, std::string_view right);
std::string TrimTokenLine(std::string_view line);

}  // namespace rvm::security