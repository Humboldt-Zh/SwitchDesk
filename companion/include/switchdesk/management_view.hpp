#pragma once
#include <string>
struct NVGcontext;
namespace sphaira::ui { struct ProgressBox; }
namespace switchdesk {
void ConfigureManagement(sphaira::ui::ProgressBox* box);
void DrawManagement(NVGcontext* vg);
void ManagementDevice(std::string firmware, std::string atmosphere, std::string environment);
void ManagementConnection(bool connected, std::string error = {});
void ManagementTask(std::string stage);
void ManagementProgress(long long completed, long long total);
void ManagementLocked(bool locked);
}
