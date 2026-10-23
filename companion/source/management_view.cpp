#include "switchdesk/management_view.hpp"
#include "switchdesk/build_info.hpp"
#include "ui/progress_box.hpp"
#include "ui/option_box.hpp"
#include "ui/nvg_util.hpp"
#include "app.hpp"
#include <algorithm>
#include <mutex>
#include <utility>

namespace switchdesk {
namespace {
struct State {
    bool connected{}, locked{}, details{};
    std::string stage, error, firmware{"读取中"}, atmosphere{"读取中"}, environment{"读取中"};
    long long completed{}, total{};
};
std::mutex state_mutex;
State state;
auto Snapshot() -> State { const std::lock_guard lock(state_mutex); return state; }
constexpr auto Ink = 0x22334A;
auto Colour(unsigned rgb) -> NVGcolor { return nvgRGB((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
void Text(NVGcontext* vg, float x, float y, float size, unsigned colour, const std::string& text) {
    sphaira::ui::gfx::drawText(vg, x, y, size, Colour(colour), text.c_str());
}
void Rect(NVGcontext* vg, float x, float y, float w, float h, unsigned colour, float radius = 0) {
    sphaira::ui::gfx::drawRect(vg, x, y, w, h, Colour(colour), radius);
}
void Line(NVGcontext* vg, float x, float y, float x2, float y2, unsigned colour, float width = 1) {
    nvgBeginPath(vg); nvgMoveTo(vg, x, y); nvgLineTo(vg, x2, y2); nvgStrokeColor(vg, Colour(colour)); nvgStrokeWidth(vg, width); nvgStroke(vg);
}
}
void ManagementDevice(std::string firmware, std::string atmosphere, std::string environment) {
    const std::lock_guard lock(state_mutex); state.firmware = std::move(firmware); state.atmosphere = std::move(atmosphere); state.environment = std::move(environment);
}
void ManagementConnection(bool connected, std::string error) {
    const std::lock_guard lock(state_mutex); state.connected = connected; state.error = std::move(error); state.stage.clear(); state.completed = state.total = 0;
}
void ManagementTask(std::string stage) {
    const std::lock_guard lock(state_mutex); state.stage = std::move(stage); state.completed = state.total = 0;
}
void ManagementProgress(long long completed, long long total) {
    const std::lock_guard lock(state_mutex); state.completed = completed; state.total = total;
}
void ManagementLocked(bool locked) { const std::lock_guard lock(state_mutex); state.locked = locked; }
void ConfigureManagement(sphaira::ui::ProgressBox* box) {
    using namespace sphaira;
    box->SetAction(Button::X, Action{[] { const std::lock_guard lock(state_mutex); state.details = !state.details; }});
    box->SetAction(Button::B, Action{[box] {
        const auto current = Snapshot();
        if (current.locked) return;
        if (current.stage.empty()) { box->RequestExit(); box->SetPop(); return; }
        App::Push<ui::OptionBox>("结束当前任务并退出配套程序？", "继续任务", "退出", 0, [box](auto selection) {
            if (selection && *selection && !Snapshot().locked) { box->RequestExit(); box->SetPop(); }
        });
    }});
}
void DrawManagement(NVGcontext* vg) {
    const auto current = Snapshot();
    const bool applet = !sphaira::App::IsApplication();
    const bool busy = !current.stage.empty();
    const auto title = current.locked ? "正在写回存档。" : busy ? "正在处理电脑任务。" : current.connected ? "已连接电脑。" : "等待电脑连接。";
    const auto description = current.locked ? "请保持连接，等待写回完成。\n此阶段无法取消或退出。" : applet ? "当前为 Applet 模式，可浏览基础资料与 SD 文件。\n存档、安装和导出需要完整应用模式。" : current.connected ? "在 Windows 端管理游戏、备份存档和传输文件。\n请保持配套程序运行。" : "打开电脑上的 SwitchDesk，再用 USB 数据线连接。\n首次连接可能需要配置 WinUSB 驱动。";
    nvgSave(vg);
    Rect(vg, 0, 0, 1280, 720, 0xF7F9FE);
    Rect(vg, 50, 34, 36, 36, Ink, 9); Rect(vg, 59, 43, 6, 19, 0xFFFFFF, 2); Rect(vg, 70, 47, 6, 12, 0xFFFFFF, 2);
    Text(vg, 100, 39, 27, Ink, "SwitchDesk");
    Text(vg, 580, 46, 14, 0x71819C, std::string(BuildVersion) + " · 澄空 / AIR");
    Text(vg, 1070, 45, 17, 0x71819C, applet ? "Applet 模式" : "USB 管理模式");
    Line(vg, 50, 94, 1230, 94, 0xE0E7F3);
    Text(vg, 78, 182, 15, 0x2860DD, current.locked ? "WRITE / 请保持连接" : busy ? "TASK / 正在处理" : current.connected ? "CONNECTED / 连接就绪" : "CONNECT / 准备就绪");
    Text(vg, 78, 225, 45, Ink, title);
    sphaira::ui::gfx::drawTextBox(vg, 78, 303, 20, 720, Colour(0x71819C), description);
    if (busy || current.locked) {
        Text(vg, 78, 414, 19, Ink, current.stage);
        Rect(vg, 78, 459, 620, 8, 0xE0E7F3, 4);
        if (current.total > 0) {
            const auto fraction = std::clamp(static_cast<double>(current.completed) / static_cast<double>(current.total), 0.0, 1.0);
            Rect(vg, 78, 459, static_cast<float>(620 * fraction), 8, 0x2860DD, 4);
            Text(vg, 640, 414, 18, 0x2860DD, std::to_string(static_cast<int>(fraction * 100)) + "%");
        } else Text(vg, 78, 487, 16, 0x71819C, "正在处理 · 完成时间暂不可估计");
    } else {
        nvgBeginPath(vg); nvgRoundedRect(vg, 96, 407, 65, 44, 16); nvgRoundedRect(vg, 339, 400, 64, 44, 6); nvgStrokeWidth(vg, 4); nvgStrokeColor(vg, Colour(0x2860DD)); nvgStroke(vg);
        Line(vg, 112, 408, 112, 451, 0x2860DD, 3); Line(vg, 145, 408, 145, 451, 0x2860DD, 3);
        Line(vg, 370, 445, 370, 460, 0x2860DD, 4); Line(vg, 354, 460, 386, 460, 0x2860DD, 4);
        Line(vg, 205, 416, 295, 416, 0xE0E7F3, 2); Text(vg, 240, 432, 13, 0x2860DD, "USB");
        Rect(vg, 78, 516, 360, 42, 0xEAF0FF, 7);
        Text(vg, 93, 526, 17, 0x2860DD, current.connected ? "USB 管理会话已建立" : "等待电脑完成驱动和连接检查");
    }
    Rect(vg, 886, 151, 317, 430, 0xFFFFFF, 11);
    Text(vg, 913, 181, 14, 0x71819C, current.details ? "DETAILS / 连接详情" : "CONSOLE / 主机信息");
    const std::pair<std::string, std::string> rows[] = {
        {"系统环境", current.environment}, {"系统固件", current.firmware}, {"Atmosphère", current.atmosphere},
        {current.details ? "源码标识" : "配套程序", current.details ? BuildId : std::string(BuildVersion) + " / 协议 1"}
    };
    for (unsigned i = 0; i < 4; ++i) {
        const auto y = 233.F + i * 91.F;
        Text(vg, 913, y, 17, 0x71819C, rows[i].first); Text(vg, 913, y + 30, 18, Ink, rows[i].second);
        if (i < 3) Line(vg, 913, y + 68, 1175, y + 68, 0xE0E7F3);
    }
    if (!current.error.empty()) { nvgIntersectScissor(vg, 78, 575, 780, 40); Text(vg, 78, 580, 15, 0xB64141, current.error); nvgResetScissor(vg); }
    Line(vg, 50, 638, 1230, 638, 0xE0E7F3);
    Text(vg, 50, 674, 15, 0x71819C, current.locked ? "写回期间不可取消 · 请保持电源与数据线连接" : "主机端负责连接，电脑端完成管理操作");
    Text(vg, 930, 672, 18, 0x2860DD, "X  连接详情");
    Text(vg, 1086, 672, 18, current.locked ? 0x71819C : Ink, current.locked ? "B  已锁定" : "B  退出");
    nvgRestore(vg);
}
}
