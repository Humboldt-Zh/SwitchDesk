// SwitchDesk remote management mode for Sphaira 1.0.8. GPL-3.0.
#include "switchdesk/server.hpp"
#include "switchdesk/management_view.hpp"
#include "switchdesk/safety.hpp"
#include "switchdesk/wire.hpp"
#include "switchdesk/build_info.hpp"
#include "app.hpp"
#include "title_info.hpp"
#include "usb/usbds.hpp"
#include "ui/progress_box.hpp"
#include "ui/menus/game_menu.hpp"
#include "yati/yati.hpp"
#include "yati/container/nsp.hpp"
#include "yati/container/xci.hpp"
#include "yati/nx/nca.hpp"
#include "yati/nx/ncm.hpp"
#include "yati/nx/keys.hpp"
#include <yyjson.h>
#include <switch.h>
#include <atomic>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <dirent.h>
#include <fcntl.h>
#include <map>
#include <memory>
#include <ranges>
#include <set>
#include <string>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

namespace switchdesk {
namespace {
using namespace sphaira;
constexpr auto Failure = MAKERESULT(345, 1);
std::atomic_llong progress_done{}, progress_total{};
using File = std::unique_ptr<FILE, decltype(&std::fclose)>;
auto Open(const std::string& path, const char* mode) -> File { return File(std::fopen(path.c_str(), mode), &std::fclose); }
auto Hex(u64 value) -> std::string { char text[17]{}; std::snprintf(text, sizeof(text), "%016llX", static_cast<unsigned long long>(value)); return text; }
auto Uid(AccountUid uid) -> std::string { return Hex(uid.uid[0]) + Hex(uid.uid[1]); }
auto Storage(u8 id) -> const char* { return id == NcmStorageId_SdCard ? "SdCard" : id == NcmStorageId_GameCard ? "GameCard" : "BuiltInUser"; }
auto MetaKind(u8 type) -> const char* { return type == NcmContentMetaType_Application ? "Base" : type == NcmContentMetaType_Patch ? "Update" : "DLC"; }
auto Environment() -> std::string {
    if (!App::IsEmummc()) return "sysMMC";
    const auto& paths = App::GetApp()->m_emummc_paths; std::array<u8, 32> hash{}; sha256CalculateHash(hash.data(), &paths, sizeof(paths));
    char id[13]{}; for (unsigned i = 0; i < 6; ++i) std::snprintf(id + i * 2, 3, "%02X", hash[i]);
    return std::string("emuMMC-") + id;
}
auto DeviceId() -> std::string {
    static std::string value;
    if (!value.empty()) return value;
    u64 hardware{};
    if (R_SUCCEEDED(splGetConfig(SplConfigItem_DeviceId, &hardware)) && hardware != 0) {
        std::array<u8, 32> digest{}; sha256CalculateHash(digest.data(), &hardware, sizeof(hardware));
        for (unsigned i = 0; i < 16; ++i) { char hex[3]{}; std::snprintf(hex, sizeof(hex), "%02X", digest[i]); value += hex; }
        return value;
    }
    mkdir("sdmc:/switch/SwitchDesk", 0777);
    if (auto file = Open("sdmc:/switch/SwitchDesk/device.id", "rb")) { char data[33]{}; if (std::fread(data, 1, 32, file.get()) == 32) value = data; }
    if (value.empty()) { value = Hex(randomGet64()) + Hex(randomGet64()); if (auto file = Open("sdmc:/switch/SwitchDesk/device.id", "wb")) std::fwrite(value.data(), 1, value.size(), file.get()); }
    return value;
}
auto Mkdirs(const std::string& path) -> bool {
    for (std::size_t i = path.find(":/") + 2; i < path.size(); ++i) if (path[i] == '/') {
        if (mkdir(path.substr(0, i).c_str(), 0777) && errno != EEXIST) return false;
    }
    return !mkdir(path.c_str(), 0777) || errno == EEXIST;
}
struct TreeItem { std::string path; bool directory{}; u64 size{}; };
auto Tree(const std::string& root, const std::string& relative, std::vector<TreeItem>& output, unsigned depth = 0) -> bool {
    if (depth > 64 || output.size() > 100000) return false;
    const auto current = root + (relative.empty() ? "" : '/' + relative);
    auto dir = std::unique_ptr<DIR, decltype(&closedir)>(opendir(current.c_str()), &closedir); if (!dir) return false;
    while (auto entry = readdir(dir.get())) {
        const auto name = std::string(entry->d_name); if (name == "." || name == "..") continue;
        const auto path = relative.empty() ? name : relative + '/' + name; if (!ValidRelative(path)) return false;
        struct stat info{}; if (stat((root + '/' + path).c_str(), &info)) return false;
        const auto is_dir = S_ISDIR(info.st_mode); output.push_back({path, is_dir, static_cast<u64>(info.st_size)});
        if (is_dir && !Tree(root, path, output, depth + 1)) return false;
    }
    return true;
}
auto RemoveTree(const std::string& root) -> bool {
    std::vector<TreeItem> entries; if (!Tree(root, "", entries)) return false;
    for (const auto& item : std::views::reverse(entries)) {
        const auto path = root + '/' + item.path;
        if (item.directory ? rmdir(path.c_str()) : unlink(path.c_str())) return false;
    }
    return !rmdir(root.c_str());
}
auto HashFile(const std::string& path) -> std::string {
    auto file = Open(path, "rb"); if (!file) return {};
    auto buffer = std::make_unique<std::array<u8, Chunk>>(); Sha256Context context{}; sha256ContextCreate(&context);
    while (const auto count = std::fread(buffer->data(), 1, buffer->size(), file.get())) sha256ContextUpdate(&context, buffer->data(), count);
    if (std::ferror(file.get())) return {};
    std::array<u8, 32> digest{}; sha256ContextGetHash(&context, digest.data());
    std::string result; for (const auto byte : digest) { char hex[3]; std::snprintf(hex, sizeof(hex), "%02X", byte); result += hex; } return result;
}
struct Handle {
    std::string root, target, temporary, mount, kind;
    File file{nullptr, &std::fclose};
    std::vector<ui::menu::game::NspEntry> exports;
    FsSaveDataInfo save{};
    u64 size{}, written{};
    struct Expected { u64 size{}, written{}; std::string sha; };
    std::map<std::string, Expected> expected;
    std::vector<std::string> directories;
    bool committed{}, live_started{};
    ~Handle() {
        file.reset(); if (!mount.empty()) fsdevUnmountDevice(mount.c_str());
        if (!committed && !temporary.empty() && kind == "upload") unlink(temporary.c_str());
        if (!root.empty() && kind == "restore" && !live_started && root.starts_with("sdmc:/switch/SwitchDesk/staging/")) RemoveTree(root);
    }
};
struct Json {
    std::unique_ptr<yyjson_mut_doc, decltype(&yyjson_mut_doc_free)> doc{yyjson_mut_doc_new(nullptr), &yyjson_mut_doc_free};
    auto Obj() -> yyjson_mut_val* { return yyjson_mut_obj(doc.get()); }
    auto Arr() -> yyjson_mut_val* { return yyjson_mut_arr(doc.get()); }
    void Add(yyjson_mut_val* obj, const char* key, yyjson_mut_val* value) { yyjson_mut_obj_add(obj, yyjson_mut_strcpy(doc.get(), key), value); }
    void Str(yyjson_mut_val* obj, const char* key, const std::string& value) { Add(obj, key, yyjson_mut_strcpy(doc.get(), value.c_str())); }
    void Num(yyjson_mut_val* obj, const char* key, u64 value) { Add(obj, key, yyjson_mut_uint(doc.get(), value)); }
    void Bool(yyjson_mut_val* obj, const char* key, bool value) { Add(obj, key, yyjson_mut_bool(doc.get(), value)); }
    void Push(yyjson_mut_val* arr, yyjson_mut_val* value) { yyjson_mut_arr_append(arr, value); }
    auto Encode(yyjson_mut_val* value) -> std::string { yyjson_mut_doc_set_root(doc.get(), value); size_t size{}; auto data = yyjson_mut_write(doc.get(), 0, &size); if (!data) return {}; std::string output(data, size); std::free(data); return output; }
};
auto String(yyjson_val* object, const char* key) -> std::string { const auto value = yyjson_obj_get(object, key); const auto text = yyjson_get_str(value); return text ? std::string(text, yyjson_get_len(value)) : std::string{}; }
auto Number(yyjson_val* object, const char* key) -> u64 { return yyjson_get_uint(yyjson_obj_get(object, key)); }

struct Server;
struct RemoteSource final : yati::source::Base {
    Server& server; u64 size;
    RemoteSource(Server& owner, u64 length) : server(owner), size(length) { }
    Result Read(void* buffer, s64 offset, s64 count, u64* read) override;
};
struct Server {
    ui::ProgressBox* progress;
    usb::UsbDs usb{UINT64_MAX};
    std::map<std::string, std::unique_ptr<Handle>> handles;
    std::map<std::string, FsSaveDataInfo> saves;
    u64 handle_id{};
    std::string error_code, error_message;
    bool released{}, cancelled{};
    explicit Server(ui::ProgressBox* box) : progress(box) { }
    Result Fail(const std::string& code, const std::string& message) { error_code = code; error_message = message; return Failure; }
    Result Write(Kind kind, std::span<const u8> bytes) {
        auto header = Encode({kind, bytes.size()}); R_TRY(usb.TransferAll(false, header.data(), header.size(), 10000000000ULL));
        if (!bytes.empty()) R_TRY(usb.TransferAll(false, const_cast<u8*>(bytes.data()), bytes.size(), 10000000000ULL)); return 0;
    }
    Result WriteJson(Kind kind, Json& json, yyjson_mut_val* value) { const auto text = json.Encode(value); R_UNLESS(!text.empty(), Failure); return Write(kind, {reinterpret_cast<const u8*>(text.data()), text.size()}); }
    Result Read(Kind& kind, std::vector<u8>& bytes, u64 timeout = UINT64_MAX) {
        std::array<u8, 16> header{}; R_TRY(usb.TransferAll(true, header.data(), header.size(), timeout)); Header parsed{};
        R_UNLESS(Decode(header, parsed), Failure); kind = parsed.kind; bytes.resize(parsed.size);
        if (!bytes.empty()) R_TRY(usb.TransferAll(true, bytes.data(), bytes.size(), 10000000000ULL));
        if (kind == Kind::Cancel) { cancelled = true; return Failure; } return 0;
    }
    Result Run();
    Result Dispatch(const std::string& method, yyjson_val* args, Json& json, yyjson_mut_val*& result, std::vector<u8>& binary);
    Result ListTitles(Json& json, yyjson_mut_val*& result);
    Result ListSaves(Json& json, yyjson_mut_val*& result);
    Result Inspect(RemoteSource& source, const std::string& name, Json& json, yyjson_mut_val*& result);
    Result Mount(Handle& handle, const FsSaveDataInfo& save);
    auto NewHandle(std::unique_ptr<Handle> value) -> std::string { const auto key = std::to_string(++handle_id); handles.emplace(key, std::move(value)); return key; }
};
Result RemoteSource::Read(void* buffer, s64 offset, s64 count, u64* read) {
    *read = 0; R_UNLESS(offset >= 0 && count >= 0 && static_cast<u64>(offset) <= size && static_cast<u64>(count) <= size - offset, Failure);
    while (*read < static_cast<u64>(count)) {
        R_TRY(server.progress->ShouldExitResult()); const auto length = std::min<u64>(Chunk, count - *read);
        Json json; const auto request = json.Obj(); json.Num(request, "offset", offset + *read); json.Num(request, "length", length);
        const auto total = progress_total.load();
        if (total > 0) { const auto update = json.Obj(); json.Num(update, "completed", std::max(0LL, progress_done.load())); json.Num(update, "total", total); json.Str(update, "stage", "安装内容"); R_TRY(server.WriteJson(Kind::Progress, json, update)); }
        R_TRY(server.WriteJson(Kind::ReadAt, json, request)); Kind kind{}; std::vector<u8> bytes; R_TRY(server.Read(kind, bytes, 10000000000ULL));
        R_UNLESS(kind == Kind::Binary && bytes.size() == length, Failure); std::memcpy(static_cast<u8*>(buffer) + *read, bytes.data(), bytes.size()); *read += bytes.size();
    }
    return 0;
}
Result Server::Mount(Handle& handle, const FsSaveDataInfo& save) {
    for (const auto& [id, item] : handles) if (!item->mount.empty()) return Fail("SaveBusy", "另一个存档正在使用，请结束当前操作");
    FsSaveDataAttribute attr{}; attr.application_id = save.application_id; attr.uid = save.uid; attr.save_data_type = save.save_data_type;
    attr.save_data_rank = save.save_data_rank; attr.save_data_index = save.save_data_index;
    FsFileSystem fs{}; const auto rc = fsOpenSaveDataFileSystem(&fs, static_cast<FsSaveDataSpaceId>(save.save_data_space_id), &attr);
    if (R_FAILED(rc)) return Fail("SaveBusy", "无法打开存档；请关闭游戏，并在完整应用模式下运行配套程序");
    if (fsdevMountDevice("sdksave", fs) < 0) { fsFsClose(&fs); return Fail("SaveMount", "挂载存档失败"); }
    handle.mount = "sdksave"; handle.root = "sdksave:/"; handle.save = save; return 0;
}
Result Server::ListTitles(Json& json, yyjson_mut_val*& result) {
    result = json.Arr();
    return title::ForEachApplicationRecord([&](std::span<const NsApplicationRecord> records) {
        for (const auto& record : records) {
            const auto row = json.Obj(); const auto info = title::Get(record.application_id);
            json.Str(row, "titleId", Hex(record.application_id)); json.Str(row, "name", info ? info->lang.name : Hex(record.application_id));
            json.Str(row, "publisher", info ? info->lang.author : "");
            auto control = std::make_unique<NsApplicationControlData>(); u64 actual{};
            json.Str(row, "displayVersion", R_SUCCEEDED(nsGetApplicationControlData(NsApplicationControlSource_CacheOnly, record.application_id, control.get(), sizeof(*control), &actual)) ? control->nacp.display_version : "");
            title::MetaEntries entries; title::GetMetaEntries(record.application_id, entries); const auto components = json.Arr(); bool has_base{}, unreadable{};
            for (const auto& entry : entries) {
                if (entry.meta_type < NcmContentMetaType_Application || entry.meta_type > NcmContentMetaType_DataPatch) continue;
                ui::menu::game::ContentInfoEntry content{}; if (R_FAILED(ui::menu::game::BuildContentEntry(entry, content))) { unreadable = true; continue; }
                if (entry.meta_type == NcmContentMetaType_Application) has_base = true;
                u64 size{}; std::set<std::string> seen;
                for (const auto& item : content.content_infos) { const auto id = std::string(reinterpret_cast<const char*>(&item.content_id), sizeof(item.content_id)); if (!seen.insert(id).second) continue; u64 length{}; ncmContentInfoSizeToU64(&item, &length); size += length; }
                const auto component = json.Obj(); json.Str(component, "titleId", Hex(entry.application_id)); json.Str(component, "kind", MetaKind(entry.meta_type));
                json.Num(component, "version", entry.version); json.Str(component, "storage", Storage(entry.storageID)); json.Num(component, "size", size); json.Push(components, component);
            }
            json.Str(row, "state", has_base ? "已安装" : yyjson_mut_arr_size(components) ? "仅更新/DLC · 缺少本体" : unreadable ? "内容信息不可读取" : "已归档/卡带未插入"); json.Add(row, "components", components); json.Push(result, row);
        }
    });
}
Result Server::ListSaves(Json& json, yyjson_mut_val*& result) {
    result = json.Arr(); saves.clear(); FsSaveDataInfoReader reader{}; R_TRY(fsOpenSaveDataInfoReader(&reader, FsSaveDataSpaceId_User));
    ON_SCOPE_EXIT(fsSaveDataInfoReaderClose(&reader)); std::array<FsSaveDataInfo, 32> entries{}; s64 count{};
    do {
        R_TRY(fsSaveDataInfoReaderRead(&reader, entries.data(), entries.size(), &count));
        for (s64 i = 0; i < count; ++i) {
            const auto& save = entries[i]; if (save.save_data_type != FsSaveDataType_Account && save.save_data_type != FsSaveDataType_Device) continue;
            const auto key = Hex(save.save_data_id) + '-' + std::to_string(save.save_data_type); saves[key] = save;
            const auto row = json.Obj(); const auto info = title::Get(save.application_id); const auto user = Uid(save.uid); std::string user_name = save.save_data_type == FsSaveDataType_Device ? "设备共享" : "未知用户";
            for (const auto& profile : App::GetAccountList()) if (Uid(profile.uid) == user) { user_name = profile.nickname; break; }
            json.Str(row, "key", key); json.Str(row, "titleId", Hex(save.application_id)); json.Str(row, "gameName", info ? info->lang.name : Hex(save.application_id));
            json.Str(row, "userId", user); json.Str(row, "userName", user_name); json.Str(row, "type", save.save_data_type == FsSaveDataType_Account ? "Account" : "Device");
            json.Num(row, "size", save.size); json.Str(row, "environment", Environment()); json.Push(result, row);
        }
    } while (count > 0); return 0;
}
Result Server::Inspect(RemoteSource& source, const std::string& name, Json& json, yyjson_mut_val*& result) {
    std::unique_ptr<yati::container::Base> container;
    auto lower = name; std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (lower.ends_with(".nsp") || lower.ends_with(".nsz")) container = std::make_unique<yati::container::Nsp>(&source);
    if (lower.ends_with(".xci") || lower.ends_with(".xcz")) container = std::make_unique<yati::container::Xci>(&source);
    if (!container) return Fail("PackageType", "仅支持 NSP/XCI/NSZ/XCZ");
    yati::container::Collections collections; R_TRY(container->GetCollections(collections));
    keys::Keys keys{}; R_TRY(keys::parse_keys(keys, true)); u64 required{};
    const auto components = json.Arr(); std::string app_id;
    for (const auto& item : collections) {
        if (item.offset < 0 || item.size < 0 || static_cast<u64>(item.offset) > source.size || static_cast<u64>(item.size) > source.size - item.offset) return Fail("PackageBounds", "安装包内容边界错误");
        if (!item.name.ends_with(".nca") && !item.name.ends_with(".ncz")) continue;
        if (item.size < sizeof(nca::Header)) return Fail("PackageHeader", "NCA 头不完整");
        std::array<u8, sizeof(nca::Header)> data{}; u64 read{}; R_TRY(source.Read(data.data(), item.offset, data.size(), &read)); nca::Header header{};
        R_TRY(nca::DecryptHeader(data.data(), keys, header));
        if (header.size < sizeof(nca::Header) || header.size > static_cast<u64>(INT64_MAX) - required) return Fail("PackageSize", "安装包大小无效");
        required += header.size;
        if (item.name.ends_with(".cnmt.nca")) {
            if (item.size > 64 * 1024 * 1024) return Fail("MetadataSize", "内容清单过大");
            const auto stage = "sdmc:/switch/SwitchDesk/inspect.nca";
            ON_SCOPE_EXIT(unlink(stage));
            auto file = Open(stage, "wb"); if (!file) return Fail("StageSpace", "无法创建内容清单临时文件");
            auto buffer = std::make_unique<std::array<u8, Chunk>>();
            for (u64 offset = 0; offset < static_cast<u64>(item.size);) { const auto count = std::min<u64>(Chunk, item.size - offset); R_TRY(source.Read(buffer->data(), item.offset + offset, count, &read)); if (std::fwrite(buffer->data(), 1, count, file.get()) != count) return Fail("StageWrite", "内容清单写入失败"); offset += count; }
            file.reset(); ncm::PackagedContentMeta meta{}; std::vector<u8> extended; std::vector<NcmPackagedContentInfo> infos;
            // fsp-srv resolves the SD pseudo filesystem path, rather than a newlib mount name.
            R_TRY(nca::ParseCnmt(fs::FsPath("@Sdcard:/switch/SwitchDesk/inspect.nca"), header.program_id, meta, extended, infos));
            const auto component = json.Obj(); json.Str(component, "titleId", Hex(meta.title_id)); json.Str(component, "kind", MetaKind(meta.meta_type)); json.Num(component, "version", meta.title_version);
            json.Str(component, "storage", "Package"); u64 size = item.size;
            for (const auto& content : infos) { u64 length{}; ncmContentInfoSizeToU64(&content.info, &length); size += length; }
            json.Num(component, "size", size); json.Push(components, component); if (app_id.empty()) app_id = Hex(ncm::GetAppId(meta));
        }
    }
    if (yyjson_mut_arr_size(components) == 0 || required == 0 || required > static_cast<u64>(INT64_MAX) - 1024 * 1024) return Fail("PackageMetadata", "未找到有效的内容清单");
    result = json.Obj(); json.Str(result, "name", name); json.Str(result, "gameName", name); json.Str(result, "titleId", app_id);
    json.Num(result, "requiredBytes", required + 1024 * 1024); json.Bool(result, "recognized", true); json.Add(result, "components", components); return 0;
}
Result Server::Dispatch(const std::string& method, yyjson_val* args, Json& json, yyjson_mut_val*& result, std::vector<u8>& binary) {
    result = json.Obj();
    if (method == "hello") {
        if (Number(args, "protocol") != Protocol) return Fail("ProtocolMismatch", "协议版本不兼容");
        ManagementConnection(true);
        json.Num(result, "protocol", Protocol); json.Str(result, "companion", std::string("SwitchDesk/") + BuildVersion + "+" + BuildId); return 0;
    }
    if (method == "device.info") {
        SetSysFirmwareVersion fw{}; R_TRY(setsysGetFirmwareVersion(&fw)); SetSysProductModel model{}; R_TRY(setsysGetProductModel(&model));
        const char* model_name = "Nintendo Switch";
        if (model == SetSysProductModel_Iowa) model_name = "Nintendo Switch 续航版";
        if (model == SetSysProductModel_Hoag) model_name = "Nintendo Switch Lite";
        if (model == SetSysProductModel_Aula) model_name = "Nintendo Switch OLED";
        u64 ams{}; const auto ams_rc = splGetConfig(static_cast<SplConfigItem>(65000), &ams);
        const auto version = std::to_string((ams >> 56) & 255) + '.' + std::to_string((ams >> 48) & 255) + '.' + std::to_string((ams >> 40) & 255);
        UsbDeviceSpeed speed{}; u16 packet{}; usb.GetSpeed(&speed, &packet);
        json.Str(result, "deviceId", DeviceId()); json.Str(result, "model", model_name); json.Str(result, "firmware", fw.display_version);
        json.Str(result, "atmosphere", R_SUCCEEDED(ams_rc) ? version : "不可读取"); json.Str(result, "companion", std::string(BuildVersion) + "+" + BuildId + " / Sphaira 1.0.8");
        json.Str(result, "environment", Environment()); json.Str(result, "usbSpeed", GetUsbDsSpeedStr(speed)); json.Bool(result, "applicationMode", App::IsApplication());
        const auto caps = json.Arr(); for (const auto cap : {"files", "titles"}) json.Push(caps, yyjson_mut_strcpy(json.doc.get(), cap));
        if (App::IsApplication()) for (const auto cap : {"saves", "install", "inspect", "export"}) json.Push(caps, yyjson_mut_strcpy(json.doc.get(), cap));
        json.Add(result, "capabilities", caps); return 0;
    }
    if (method == "device.release") { released = true; return 0; }
    if (method == "storages.list") {
        result = json.Arr();
        for (const auto id : {NcmStorageId_SdCard, NcmStorageId_BuiltInUser}) {
            s64 total{}, free{}; R_TRY(nsGetStorageSize(id, &total, &free)); const auto row = json.Obj();
            json.Str(row, "id", Storage(id)); json.Str(row, "name", id == NcmStorageId_SdCard ? "SD 卡" : (App::IsEmummc() ? "当前虚拟系统用户分区（emuMMC）" : "当前真实系统用户分区（sysMMC）"));
            json.Num(row, "totalBytes", total); json.Num(row, "freeBytes", free); json.Push(result, row);
        } return 0;
    }
    if (method == "titles.list") return ListTitles(json, result);
    if (method == "titles.icon") {
        const auto id = String(args, "titleId"); if (id.size() != 16 || id.find_first_not_of("0123456789abcdefABCDEF") != id.npos) return Fail("TitleId", "游戏标识无效");
        auto control = std::make_unique<NsApplicationControlData>(); u64 actual{};
        R_TRY(nsGetApplicationControlData(NsApplicationControlSource_Storage, std::strtoull(id.c_str(), nullptr, 16), control.get(), sizeof(*control), &actual));
        if (actual <= sizeof(NacpStruct) || actual > sizeof(*control)) return Fail("Icon", "该游戏没有可读取的图标");
        binary.assign(control->icon, control->icon + actual - sizeof(NacpStruct)); json.Num(result, "binaryLength", binary.size()); return 0;
    }
    if (method == "saves.list") { if (!App::IsApplication()) return Fail("ApplicationMode", "存档操作要求完整应用模式");
    return ListSaves(json, result); }
    if (method == "fs.list") {
        const auto path = String(args, "path"); if (!ValidRemote(path)) return Fail("InvalidPath", "路径格式错误");
        auto dir = std::unique_ptr<DIR, decltype(&closedir)>(opendir(("sdmc:" + path).c_str()), &closedir); if (!dir) return Fail("DirectoryOpen", "无法打开目录");
        result = json.Arr();
        while (auto entry = readdir(dir.get())) {
            const auto name = std::string(entry->d_name); if (name == "." || name == "..") continue;
            const auto remote = path == "/" ? '/' + name : path + '/' + name; struct stat info{};
            if (stat(("sdmc:" + remote).c_str(), &info)) continue;
            const auto row = json.Obj(); json.Str(row, "name", name); json.Str(row, "path", remote); json.Bool(row, "isDirectory", S_ISDIR(info.st_mode)); json.Num(row, "size", info.st_size); json.Push(result, row);
        } return 0;
    }
    if (method == "fs.stat") {
        const auto path = String(args, "path"); if (!ValidRemote(path)) return Fail("InvalidPath", "路径无效");
        struct stat info{}; const auto exists = !stat(("sdmc:" + path).c_str(), &info);
        if (!exists && errno != ENOENT) return Fail("Stat", "无法查询目标路径");
        json.Bool(result, "exists", exists); json.Bool(result, "isDirectory", exists && S_ISDIR(info.st_mode)); json.Num(result, "size", exists ? info.st_size : 0); return 0;
    }
    if (method == "fs.mkdir" || method == "fs.rename" || method == "fs.delete") {
        const auto path = String(args, "path"); if (!Writable(path)) return Fail("ProtectedPath", "路径无效或受到系统保护");
        if (method == "fs.mkdir") { if (mkdir(("sdmc:" + path).c_str(), 0777)) return Fail("CreateDirectory", "创建目录失败");
        }
        if (method == "fs.delete") {
            const auto full = "sdmc:" + path; struct stat info{}; if (stat(full.c_str(), &info)) return Fail("MissingFile", "文件不存在");
            if (S_ISDIR(info.st_mode) ? rmdir(full.c_str()) : unlink(full.c_str())) return Fail("DeleteFailed", "删除失败；文件夹必须为空");
        }
        if (method == "fs.rename") {
            const auto target = String(args, "target"); if (!Writable(target)) return Fail("ProtectedPath", "目标路径受到保护");
            struct stat info{}; if (!stat(("sdmc:" + target).c_str(), &info)) return Fail("Exists", "目标名称已存在");
            if (rename(("sdmc:" + path).c_str(), ("sdmc:" + target).c_str())) return Fail("RenameFailed", "重命名失败");
        } return 0;
    }
    if (method == "fs.read.open" || method == "fs.write.open") {
        const auto path = String(args, "path"); if (!ValidRemote(path)) return Fail("InvalidPath", "路径格式错误");
        auto handle = std::make_unique<Handle>(); handle->target = "sdmc:" + path;
        if (method == "fs.read.open") {
            handle->file = Open(handle->target, "rb"); struct stat info{};
            if (!handle->file || fstat(fileno(handle->file.get()), &info) || !S_ISREG(info.st_mode)) return Fail("ReadOpen", "无法打开文件");
            handle->size = info.st_size; handle->kind = "file";
        } else {
            if (!Writable(path)) return Fail("ProtectedPath", "该路径受到系统保护");
            struct stat info{}; if (!stat(handle->target.c_str(), &info)) return Fail("Exists", "目标文件已存在");
            handle->size = Number(args, "size"); s64 total{}, free{}; R_TRY(nsGetStorageSize(NcmStorageId_SdCard, &total, &free));
            if (handle->size > static_cast<u64>(free) || handle->size > INT64_MAX) return Fail("NoSpace", "SD 卡可用空间不足");
            handle->temporary = handle->target + ".sdk-" + Hex(randomGet64()) + ".partial"; handle->kind = "upload";
            const auto fd = open(handle->temporary.c_str(), O_WRONLY | O_CREAT | O_EXCL, 0666); if (fd < 0) return Fail("WriteOpen", "无法创建临时文件");
            handle->file.reset(fdopen(fd, "wb")); if (!handle->file) { close(fd); return Fail("WriteOpen", "打开文件失败"); }
            if (ftruncate(fd, handle->size)) return Fail("FileSystemLimit", "无法预分配文件；请检查容量及 FAT32 单文件大小限制");
        }
        json.Num(result, "size", handle->size); json.Str(result, "handle", NewHandle(std::move(handle))); return 0;
    }
    if (method == "saves.read.open" || method == "saves.restore.begin") {
        if (!App::IsApplication()) return Fail("ApplicationMode", "请关闭游戏并使用完整应用模式运行配套程序");
        const auto key = String(args, "key"); const auto it = saves.find(key); if (it == saves.end()) return Fail("SaveMissing", "存档记录已失效，请刷新列表");
        auto handle = std::make_unique<Handle>(); handle->save = it->second;
        if (method == "saves.read.open") {
            const auto& save = it->second;
            if (yyjson_obj_get(args, "titleId") && (String(args, "titleId") != Hex(save.application_id) ||
                String(args, "userId") != Uid(save.uid) || String(args, "type") != (save.save_data_type == FsSaveDataType_Account ? "Account" : "Device")))
                return Fail("SaveMismatch", "存档身份与所选游戏或用户不一致，请刷新列表");
            R_TRY(Mount(*handle, save)); handle->kind = "save";
        }
        else {
            const auto meta = yyjson_obj_get(args, "manifest"); const auto& save = it->second;
            if (String(meta, "deviceId") != DeviceId() || String(meta, "environment") != Environment() || String(meta, "titleId") != Hex(save.application_id) ||
                String(meta, "userId") != Uid(save.uid) || String(meta, "saveType") != (save.save_data_type == FsSaveDataType_Account ? "Account" : "Device")) return Fail("SaveMismatch", "快照与目标不匹配");
            const auto files = yyjson_obj_get(meta, "files"); if (!yyjson_is_arr(files) || yyjson_arr_size(files) > 100000) return Fail("Manifest", "存档清单无效");
            size_t index{}, max{}; yyjson_val* item{}; u64 required{};
            yyjson_arr_foreach(files, index, max, item) {
                const auto path = String(item, "path"); const auto sha = String(item, "sha256"); const auto length = Number(item, "size");
                if (!ValidRelative(path) || sha.size() != 64 || length > INT64_MAX || required > UINT64_MAX - length || handle->expected.contains(path)) return Fail("Manifest", "存档文件清单无效");
                handle->expected.emplace(path, Handle::Expected{length, 0, sha}); required += length;
            }
            const auto dirs = yyjson_obj_get(meta, "directories");
            yyjson_arr_foreach(dirs, index, max, item) { const auto text = yyjson_get_str(item); if (!text || !ValidRelative(text)) return Fail("Manifest", "存档目录无效"); handle->directories.emplace_back(text); }
            FsSaveDataExtraData extra{}; R_TRY(fsReadSaveDataFileSystemExtraDataBySaveDataSpaceId(&extra, sizeof(extra), static_cast<FsSaveDataSpaceId>(save.save_data_space_id), save.save_data_id));
            if (required > static_cast<u64>(extra.data_size)) return Fail("SaveCapacity", "快照内容超过目标存档容量");
            s64 total{}, free{}; R_TRY(nsGetStorageSize(NcmStorageId_SdCard, &total, &free)); if (required > static_cast<u64>(free)) return Fail("NoSpace", "SD 卡没有足够空间暂存恢复文件");
            handle->kind = "restore"; handle->root = "sdmc:/switch/SwitchDesk/staging/" + Hex(randomGet64());
            if (!Mkdirs(handle->root)) return Fail("StageCreate", "创建恢复暂存目录失败");
        }
        json.Str(result, "handle", NewHandle(std::move(handle))); return 0;
    }
    if (method == "titles.export.open") {
        if (!App::IsApplication()) return Fail("ApplicationMode", "游戏导出要求完整应用模式");
        const auto wanted = String(args, "titleId"); const auto version = Number(args, "version"); const auto storage = String(args, "storage");
        auto handle = std::make_unique<Handle>(); handle->kind = "export"; Result rc = Failure;
        R_TRY(title::ForEachApplicationRecord([&](std::span<const NsApplicationRecord> records) {
            for (const auto& record : records) {
                if (!handle->exports.empty()) break; title::MetaEntries entries; title::GetMetaEntries(record.application_id, entries);
                for (const auto& entry : entries) if (Hex(entry.application_id) == wanted && entry.version == version && storage == Storage(entry.storageID)) {
                    ui::menu::game::Entry game{}; game.app_id = record.application_id; const auto info = title::Get(record.application_id); if (info) game.lang = info->lang;
                    rc = ui::menu::game::BuildNspEntries(game, title::MetaEntries{entry}, handle->exports); break;
                }
            }
        }));
        if (R_FAILED(rc) || handle->exports.size() != 1) return Fail("ExportUnavailable", "无法打包该组件；请检查主机内容、权限及内容解析所需密钥");
        handle->size = handle->exports.front().nsp_size; json.Num(result, "size", handle->size); json.Str(result, "handle", NewHandle(std::move(handle))); return 0;
    }
    if (method == "package.inspect" || method == "titles.install") {
        if (!App::IsApplication()) return Fail("ApplicationMode", "游戏包解析与安装要求完整应用模式");
        const auto name = String(args, "name"); if (name.empty() || name.size() > 255 || name.find_first_of("/\\:") != name.npos) return Fail("InvalidName", "安装包名称无效");
        RemoteSource source(*this, Number(args, "size")); progress_done = 0; progress_total = 0;
        if (method == "package.inspect") return Inspect(source, name, json, result);
        if (!App::IsApplication()) return Fail("ApplicationMode", "安装要求完整应用模式");
        const auto storage = String(args, "storage"); if (storage != "SdCard" && storage != "BuiltInUser") return Fail("Storage", "安装目标无效");
        Json inspection; yyjson_mut_val* package{}; R_TRY(Inspect(source, name, inspection, package));
        const auto needed = yyjson_mut_get_uint(yyjson_mut_obj_get(package, "requiredBytes")); s64 total{}, free{};
        R_TRY(nsGetStorageSize(storage == "SdCard" ? NcmStorageId_SdCard : NcmStorageId_BuiltInUser, &total, &free));
        if (needed > static_cast<u64>(free)) return Fail("NoSpace", "目标存储空间不足");
        yati::ConfigOverride config{}; config.sd_card_install = storage == "SdCard"; config.skip_nca_hash_verify = false;
        config.lower_system_version = false; config.lower_master_key = false; config.convert_to_standard_crypto = false;
        config.convert_to_common_ticket = false;
        const auto rc = yati::InstallFromSource(progress, &source, fs::FsPath(name), config, source.size);
        progress_done = 0; progress_total = 0; return rc;
    }
    // Chunk uploads are consumed before validation so an error cannot leave a binary
    // frame in front of the next request.
    std::vector<u8> upload;
    if (method == "write.chunk") { Kind kind{}; R_TRY(Read(kind, upload, 10000000000ULL)); if (kind != Kind::Binary || upload.size() > Chunk) return Fail("ChunkSize", "文件块无效");
    }
    const auto key = String(args, "handle"); const auto it = handles.find(key);
    if (it == handles.end()) return Fail("UnknownHandle", "操作已结束或连接已变化，请重试");
    auto& handle = *it->second;
    if (method == "handle.close") { handles.erase(it); return 0; }
    if (method == "handle.tree") {
        if (handle.kind != "save") return Fail("HandleType", "操作类型错误");
        std::vector<TreeItem> entries;
        if (!Tree(handle.root, "", entries)) return Fail("SaveRead", "读取存档文件列表失败");
        result = json.Arr();
        for (const auto& item : entries) { const auto row = json.Obj(); json.Str(row, "name", item.path); json.Str(row, "path", item.path); json.Bool(row, "isDirectory", item.directory); json.Num(row, "size", item.size); json.Push(result, row); } return 0;
    }
    if (method == "read.chunk") {
        const auto offset = Number(args, "offset"), length = Number(args, "length"); if (length > Chunk || offset > INT64_MAX) return Fail("ChunkBounds", "文件块越界");
        binary.resize(length); u64 actual{};
        if (handle.kind == "export") { if (offset > handle.size || length > handle.size - offset) return Fail("ChunkBounds", "内容读取越界");
        R_TRY(handle.exports.front().Read(binary.data(), offset, length, &actual)); }
        else if (handle.kind == "file") { if (offset > handle.size || length > handle.size - offset) return Fail("ChunkBounds", "文件读取越界"); if (fseeko(handle.file.get(), offset, SEEK_SET)) return Fail("Seek", "文件定位失败"); actual = std::fread(binary.data(), 1, length, handle.file.get()); }
        else if (handle.kind == "save") {
            const auto path = String(args, "path"); if (!ValidRelative(path)) return Fail("InvalidPath", "存档路径无效"); auto file = Open(handle.root + '/' + path, "rb");
            if (!file || fseeko(file.get(), offset, SEEK_SET)) return Fail("SaveRead", "打开存档文件失败");
            actual = std::fread(binary.data(), 1, length, file.get());
        } else return Fail("HandleType", "操作不可读取");
        if (actual != length) return Fail("ShortRead", "文件内容在读取期间发生变化");
        json.Num(result, "binaryLength", length); return 0;
    }
    if (method == "saves.restore.file") {
        if (handle.kind != "restore") return Fail("HandleType", "操作类型错误");
        const auto path = String(args, "path"); const auto expected = handle.expected.find(path);
        if (!ValidRelative(path) || expected == handle.expected.end() || Number(args, "size") != expected->second.size || !SameDigest(String(args, "sha256"), expected->second.sha)) return Fail("Manifest", "恢复文件与清单不一致");
        const auto target = handle.root + '/' + path; const auto parent = target.substr(0, target.find_last_of('/'));
        if (!Mkdirs(parent)) return Fail("StageCreate", "创建暂存目录失败");
        auto file = Open(target, "wb");
        if (!file) return Fail("StageWrite", "创建暂存文件失败");
        expected->second.written = 0; return 0;
    }
    if (method == "write.chunk") {
        const auto offset = Number(args, "offset");
        if (handle.kind == "upload") {
            if (offset != handle.written || upload.size() > handle.size - handle.written) return Fail("ChunkBounds", "上传块位置无效");
            if (std::fwrite(upload.data(), 1, upload.size(), handle.file.get()) != upload.size()) return Fail("NoSpace", "写入失败，请检查空间");
            handle.written += upload.size(); return 0;
        }
        if (handle.kind == "restore") {
            const auto path = String(args, "path"); const auto expected = handle.expected.find(path);
            if (!ValidRelative(path) || expected == handle.expected.end() || offset != expected->second.written || upload.size() > expected->second.size - offset) return Fail("ChunkBounds", "恢复文件块越界");
            auto file = Open(handle.root + '/' + path, "ab"); if (!file || std::fwrite(upload.data(), 1, upload.size(), file.get()) != upload.size()) return Fail("NoSpace", "暂存恢复文件失败"); expected->second.written += upload.size(); return 0;
        } return Fail("HandleType", "操作不可写入");
    }
    if (method == "fs.write.commit") {
        if (handle.kind != "upload" || handle.written != handle.size) return Fail("Incomplete", "文件尚未传输完整");
        if (std::fflush(handle.file.get()) || fsync(fileno(handle.file.get()))) return Fail("Flush", "写入数据未能保存");
        handle.file.reset();
        struct stat info{}; if (!stat(handle.target.c_str(), &info)) return Fail("Exists", "目标文件已存在");
        if (rename(handle.temporary.c_str(), handle.target.c_str())) return Fail("Commit", "完成文件传输失败");
        handle.committed = true; return 0;
    }
    if (method == "saves.restore.commit") {
        ManagementLocked(true); ON_SCOPE_EXIT(ManagementLocked(false));
        if (handle.kind != "restore") return Fail("HandleType", "操作类型错误");
        for (const auto& [path, expected] : handle.expected)
            if (expected.written != expected.size || !SameDigest(HashFile(handle.root + '/' + path), expected.sha)) return Fail("Integrity", "恢复文件未完整传输或校验失败");
        Handle live; R_TRY(Mount(live, handle.save)); std::vector<TreeItem> entries; if (!Tree(live.root, "", entries)) return Fail("SaveRead", "读取当前存档失败");
        handle.live_started = true;
        for (const auto& item : std::views::reverse(entries)) { const auto path = live.root + '/' + item.path; if (item.directory ? rmdir(path.c_str()) : unlink(path.c_str())) return Fail("RestoreWrite", "清理当前存档失败；电脑 Recovery 快照可用于恢复"); }
        auto fs = fsdevGetDeviceFileSystem("sdksave:"); if (!fs) return Fail("SaveMount", "存档挂载已结束"); R_TRY(fsFsCommit(fs));
        for (const auto& dir : handle.directories) if (!Mkdirs(live.root + '/' + dir)) return Fail("RestoreWrite", "创建存档目录失败");
        auto buffer = std::make_unique<std::array<u8, Chunk>>();
        for (const auto& [path, expected] : handle.expected) {
            const auto target = live.root + '/' + path; if (!Mkdirs(target.substr(0, target.find_last_of('/')))) return Fail("RestoreWrite", "创建存档目录失败");
            auto input = Open(handle.root + '/' + path, "rb"), output = Open(target, "wb"); if (!input || !output) return Fail("RestoreWrite", "无法打开恢复文件");
            u64 copied{}; while (const auto count = std::fread(buffer->data(), 1, buffer->size(), input.get())) { if (std::fwrite(buffer->data(), 1, count, output.get()) != count) return Fail("RestoreWrite", "存档写入失败；保留了电脑 Recovery 快照"); copied += count; }
            if (copied != expected.size || std::ferror(input.get()) || std::fflush(output.get())) return Fail("RestoreWrite", "存档写回不完整");
            output.reset(); R_TRY(fsFsCommit(fs));
        }
        for (const auto& [path, expected] : handle.expected) if (!SameDigest(HashFile(live.root + '/' + path), expected.sha)) return Fail("RestoreVerify", "存档写回校验失败");
        handle.committed = true; RemoveTree(handle.root); return 0;
    }
    return Fail("UnknownMethod", "未知操作");
}
Result Server::Run() {
    R_TRY(setsysInitialize()); ON_SCOPE_EXIT(setsysExit()); R_TRY(splInitialize()); ON_SCOPE_EXIT(splExit()); R_TRY(nsInitialize()); ON_SCOPE_EXIT(nsExit());
    SetSysFirmwareVersion firmware{}; u64 atmosphere{};
    const auto firmware_rc = setsysGetFirmwareVersion(&firmware);
    const auto atmosphere_rc = splGetConfig(static_cast<SplConfigItem>(65000), &atmosphere);
    const auto version = std::to_string((atmosphere >> 56) & 255) + '.' + std::to_string((atmosphere >> 48) & 255) + '.' + std::to_string((atmosphere >> 40) & 255);
    ManagementDevice(R_SUCCEEDED(firmware_rc) ? firmware.display_version : "不可读取", R_SUCCEEDED(atmosphere_rc) ? version : "不可读取", App::IsEmummc() ? "emuMMC / 虚拟系统" : "sysMMC / 真实系统");
    R_TRY(title::Init()); ON_SCOPE_EXIT(title::Exit()); DeviceId();
    R_TRY(usb.Init()); progress->AddCancelEvent(usb.GetCancelEvent()); ON_SCOPE_EXIT(progress->RemoveCancelEvent(usb.GetCancelEvent()));
    R_TRY(usb.IsUsbConnected(UINT64_MAX));
    while (!released && !progress->ShouldExit()) {
        Kind kind{}; std::vector<u8> bytes; R_TRY(Read(kind, bytes)); R_UNLESS(kind == Kind::Request, Failure);
        auto doc = std::unique_ptr<yyjson_doc, decltype(&yyjson_doc_free)>(yyjson_read(reinterpret_cast<const char*>(bytes.data()), bytes.size(), 0), &yyjson_doc_free); R_UNLESS(doc, Failure);
        const auto root = yyjson_doc_get_root(doc.get()); const auto id = Number(root, "id"); const auto method = String(root, "method"); const auto args = yyjson_obj_get(root, "args");
        if (!yyjson_is_obj(args) || method.empty() || handles.size() > 16) return Failure;
        Json json; yyjson_mut_val* result{}; std::vector<u8> binary; error_code.clear(); error_message.clear();
        const bool background = method == "device.info" || method == "titles.icon" || method == "hello";
        if (!background) ManagementTask(method == "titles.list" || method == "storages.list" || method == "saves.list" ? "正在读取主机资料" : method == "titles.install" ? "正在安装内容" : method == "saves.restore.commit" ? "正在写回存档，请保持连接" : method.starts_with("saves.") ? "正在处理存档任务" : method == "read.chunk" ? "正在传输到电脑" : method == "write.chunk" ? "正在接收电脑文件" : "正在处理电脑任务");
        const auto rc = Dispatch(method, args, json, result, binary); if (cancelled) return Failure;
        const auto response = json.Obj(); json.Num(response, "id", id); json.Bool(response, "ok", R_SUCCEEDED(rc));
        if (R_SUCCEEDED(rc)) json.Add(response, "result", result);
        else { const auto error = json.Obj(); json.Str(error, "code", error_code.empty() ? "HorizonError" : error_code); json.Str(error, "message", error_message.empty() ? "主机操作失败，Result=" + Hex(rc) : error_message); json.Add(response, "error", error); }
        R_TRY(WriteJson(Kind::Response, json, response)); if (R_SUCCEEDED(rc) && yyjson_mut_obj_get(result, "binaryLength")) R_TRY(Write(Kind::Binary, binary));
        if (handles.empty() && !background) ManagementTask({});
    }
    return 0;
}
} // namespace
void SetProgress(long long completed, long long total) { progress_done = completed; progress_total = total; ManagementProgress(completed, total); }
void Launch() {
    sphaira::App::SetMtpEnable(false); sphaira::App::SetFtpEnable(false); sphaira::App::SetHddEnable(false);
    sphaira::App::Push<sphaira::ui::ProgressBox>(0, "SwitchDesk PC", "Connect USB to your computer. B to exit.", [](auto progress) -> Result {
        while (!progress->ShouldExit()) {
            bool released{};
            { Server server(progress); const auto rc = server.Run(); released = server.released; if (released || progress->ShouldExit()) return rc; ManagementConnection(false, "连接中断或初始化失败，Result=" + Hex(rc)); }
            progress->SetActionName("SwitchDesk PC").SetTitle("Waiting for USB connection..."); svcSleepThread(250000000);
        } return 0;
    }, [](Result) { sphaira::App::Exit(); });
}
} // namespace switchdesk
