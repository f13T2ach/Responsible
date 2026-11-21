#include <array>
#include <cstdint>
#include <span>

#include "channel/frame.h"
#include "test_registry.h"

using rvm::channel::FrameCodec;
using rvm::channel::FrameHeader;
using rvm::channel::FrameType;

RVM_TEST(frame_header_roundtrip_preserves_fields) {
  FrameHeader header;
  header.type = FrameType::kStream;
  header.payload_size = 123;
  header.flags = 7;
  header.request_id = 42;

  const auto encoded = FrameCodec::EncodeHeader(header);
  RVM_CHECK(encoded.ok());
  const auto bytes = encoded.value();
  RVM_CHECK_EQ(bytes[0], static_cast<std::uint8_t>('R'));
  RVM_CHECK_EQ(bytes[1], static_cast<std::uint8_t>('V'));
  RVM_CHECK_EQ(bytes[2], static_cast<std::uint8_t>('M'));
  RVM_CHECK_EQ(bytes[3], static_cast<std::uint8_t>('1'));
  RVM_CHECK_EQ(bytes[8], static_cast<std::uint8_t>(123));

  const auto decoded = FrameCodec::DecodeHeader(std::span<const std::uint8_t>(bytes));
  RVM_CHECK(decoded.ok());
  RVM_CHECK(decoded.value().magic == rvm::channel::kFrameMagic);
  RVM_CHECK(decoded.value().header_version == rvm::channel::kFrameVersion);
  RVM_CHECK(decoded.value().type == FrameType::kStream);
  RVM_CHECK_EQ(decoded.value().payload_size, 123u);
  RVM_CHECK_EQ(decoded.value().flags, 7u);
  RVM_CHECK_EQ(decoded.value().request_id, 42u);
}

RVM_TEST(frame_decoder_rejects_short_or_corrupt_headers) {
  const std::array<std::uint8_t, 5> short_bytes{};
  RVM_CHECK(!FrameCodec::DecodeHeader(std::span<const std::uint8_t>(short_bytes)).ok());

  FrameHeader header;
  header.payload_size = 0;
  const auto encoded = FrameCodec::EncodeHeader(header);
  RVM_CHECK(encoded.ok());
  auto bytes = encoded.value();

  bytes[0] = 0;
  RVM_CHECK(!FrameCodec::DecodeHeader(std::span<const std::uint8_t>(bytes)).ok());

  bytes = encoded.value();
  bytes[4] = 2;
  RVM_CHECK(!FrameCodec::DecodeHeader(std::span<const std::uint8_t>(bytes)).ok());

  bytes = encoded.value();
  bytes[6] = 99;
  RVM_CHECK(!FrameCodec::DecodeHeader(std::span<const std::uint8_t>(bytes)).ok());
}

RVM_TEST(frame_header_rejects_oversized_payloads) {
  FrameHeader header;
  header.payload_size = rvm::channel::kMaxPayloadBytes;
  RVM_CHECK(FrameCodec::Validate(header).ok());

  header.payload_size = rvm::channel::kMaxPayloadBytes + 1;
  const auto status = FrameCodec::Validate(header);
  RVM_CHECK(!status.ok());
  RVM_CHECK(status.error().code == rvm::core::ErrorCode::kProtocolError);
  RVM_CHECK(!FrameCodec::EncodeHeader(header).ok());
}

RVM_TEST(frame_header_rejects_unknown_type) {
  FrameHeader header;
  header.type = static_cast<FrameType>(999);
  RVM_CHECK(!FrameCodec::Validate(header).ok());
}