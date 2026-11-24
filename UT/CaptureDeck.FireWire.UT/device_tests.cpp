#include "capdvhs/directshow.hpp"

#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <utility>

namespace {

void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

AM_MEDIA_TYPE transport_type(bool stride = false) {
    AM_MEDIA_TYPE type{};
    type.majortype = MEDIATYPE_Stream;
    type.subtype = stride ? MEDIASUBTYPE_MPEG2_TRANSPORT_STRIDE : MEDIASUBTYPE_MPEG2_TRANSPORT;
    type.formattype = FORMAT_None;
    type.lSampleSize = 188 * 256;
    return type;
}

class RefCounter final : public IUnknown {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown) return E_NOINTERFACE;
        *value = static_cast<IUnknown*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { ++add_calls; return ++references; }
    ULONG STDMETHODCALLTYPE Release() override { ++release_calls; return --references; }
    ULONG references = 1;
    unsigned add_calls = 0;
    unsigned release_calls = 0;
};

void layout_tests() {
    auto type = transport_type();
    capdvhs::PacketLayout layout{99, 99, 99};
    require(capdvhs::media_layout(type, layout) && layout == capdvhs::PacketLayout{0, 188, 188},
            "Ordinary MPEG2_TRANSPORT must use 188-byte packets");
    type = transport_type(true);
    require(capdvhs::media_layout(type, layout) && layout == capdvhs::PacketLayout{0, 188, 188},
            "STRIDE with no format block must use the 188-byte defaults");
    std::array<std::uint32_t, 4> fields{4, 188, 192, 0x12345678};
    type.pbFormat = reinterpret_cast<BYTE*>(fields.data());
    type.cbFormat = static_cast<ULONG>(sizeof(fields));
    type.lSampleSize = 192 * 256;
    require(capdvhs::media_layout(type, layout) && layout == capdvhs::PacketLayout{4, 188, 192},
            "The observed 16-byte format block must be accepted");
    type.cbFormat = 12;
    require(capdvhs::media_layout(type, layout), "The documented 12-byte format block must be accepted");
    type.cbFormat = 11;
    require(!capdvhs::media_layout(type, layout), "Truncated stride format must be rejected");
    type.cbFormat = 12;
    type.pbFormat = nullptr;
    require(!capdvhs::media_layout(type, layout), "Null stride format must be rejected");
    type.pbFormat = reinterpret_cast<BYTE*>(fields.data());
    fields[0] = 5;
    require(!capdvhs::media_layout(type, layout), "Offset outside the packet must be rejected");
    fields[0] = std::numeric_limits<std::uint32_t>::max();
    require(!capdvhs::media_layout(type, layout), "Overflowing offset must be rejected");
    fields = {4, 189, 192, 0};
    require(!capdvhs::media_layout(type, layout), "A non-188-byte payload must be rejected");
    fields = {0, 188, 204, 0};
    require(!capdvhs::media_layout(type, layout), "Unsupported strides must be rejected");
    fields = {0, 188, 187, 0};
    require(!capdvhs::media_layout(type, layout), "A stride smaller than the payload must be rejected");
    fields = {1, 188, 188, 0};
    require(!capdvhs::media_layout(type, layout), "188-byte packets cannot have a prefix");
    fields = {0, 188, 192, 0};
    require(capdvhs::media_layout(type, layout), "An in-bounds stride offset must be represented exactly");
    type.majortype = MEDIATYPE_Video;
    require(!capdvhs::media_layout(type, layout), "A video media type cannot be captured as TS");
    type = transport_type();
    type.subtype = MEDIASUBTYPE_NULL;
    require(!capdvhs::media_layout(type, layout), "An unknown stream subtype must be rejected");
    type = transport_type();
    type.cbFormat = 12;
    type.pbFormat = reinterpret_cast<BYTE*>(fields.data());
    require(!capdvhs::media_layout(type, layout), "Ordinary transport with an unexpected format block must be rejected");
}

void ownership_tests() {
    RefCounter counter;
    std::array<std::uint32_t, 4> fields{4, 188, 192, 7};
    auto source = transport_type(true);
    source.cbFormat = static_cast<ULONG>(sizeof(fields));
    source.pbFormat = reinterpret_cast<BYTE*>(fields.data());
    source.pUnk = &counter;
    {
        capdvhs::MediaType first(source);
        require(first.value.pbFormat != source.pbFormat, "A copied media format must own a distinct allocation");
        require(std::memcmp(first.value.pbFormat, source.pbFormat, sizeof(fields)) == 0,
                "All format bytes, including extensions, must be preserved");
        require(counter.references == 2, "Copying must AddRef the media type's IUnknown");
        fields[3] = 8;
        std::uint32_t copied_extension = 0;
        std::memcpy(&copied_extension, first.value.pbFormat + 12, sizeof(copied_extension));
        require(copied_extension == 7, "Changing the original block must not change a clone");
        capdvhs::MediaType second(first);
        require(second.value.pbFormat != first.value.pbFormat && counter.references == 3,
                "Copy construction must deep-copy and AddRef");
        capdvhs::MediaType moved(std::move(first));
        require(first.value.pbFormat == nullptr && first.value.pUnk == nullptr && counter.references == 3,
                "Moving must transfer ownership without changing references");
        second = std::move(moved);
        require(moved.value.pbFormat == nullptr && moved.value.pUnk == nullptr && counter.references == 2,
                "Move assignment must release the old allocation and transfer ownership");
        second = second;
        require(counter.references == 2, "Self-copy must preserve ownership");
        second = std::move(second);
        require(counter.references == 2, "Self-move must preserve ownership");
        capdvhs::MediaType assigned;
        assigned = second;
        require(counter.references == 3 && assigned.value.pbFormat != second.value.pbFormat,
                "Copy assignment must deep-copy the format");
        AM_MEDIA_TYPE malformed = source;
        malformed.pbFormat = nullptr;
        bool rejected = false;
        try { assigned = capdvhs::MediaType(malformed); }
        catch (const std::exception&) { rejected = true; }
        require(rejected && assigned.value.pbFormat && counter.references == 3,
                "A malformed copy must fail without destroying the previous destination");
    }
    require(counter.references == 1 && counter.add_calls == counter.release_calls,
            "Every media type AddRef must be balanced by Release");
    capdvhs::MediaType empty;
    capdvhs::MediaType copied(empty);
    require(copied.value.cbFormat == 0 && !copied.value.pbFormat && !copied.value.pUnk,
            "An empty media type must be safe to clone and destroy");
}

void json_tests() {
    auto type = transport_type();
    const auto json = capdvhs::media_json(type);
    require(json.find("\"stride\":188") != std::string::npos, "Media JSON must expose the effective packet layout");
    std::array<std::uint32_t, 4> unsupported{4, 188, 204, 9};
    type = transport_type(true);
    type.cbFormat = static_cast<ULONG>(sizeof(unsupported));
    type.pbFormat = reinterpret_cast<BYTE*>(unsupported.data());
    const auto rejected_json = capdvhs::media_json(type);
    require(rejected_json.find("\"supported\":false") != std::string::npos &&
            rejected_json.find("\"format_size\":16") != std::string::npos &&
            rejected_json.find("04000000bc000000cc00000009000000") != std::string::npos,
            "Unsupported layouts must retain their complete format bytes in probe JSON");
    require(capdvhs::guid_string(MEDIATYPE_Stream) == "{E436EB83-524F-11CE-9F53-0020AF0BA770}",
            "GUID formatting must be canonical and complete");
}

} // namespace

int main() {
    try {
        layout_tests();
        ownership_tests();
        json_tests();
        std::cout << "Device media-layout, ownership, and JSON tests passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
