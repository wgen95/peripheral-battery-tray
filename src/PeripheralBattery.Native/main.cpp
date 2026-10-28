#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <gdiplus.h>
#include <dwmapi.h>
#include <algorithm>
#include <fstream>
#include <sstream>
#include <cstring>
#include "json.h"
#include "policy.h"
#include "version.h"
using namespace Gdiplus;

static const GUID IconGuid = { 0x6319a943, 0x4e29, 0x4c5e, {0xa8,0xda,0xa0,0xe0,0x18,0x53,0x5d,0xa5} };
static constexpr UINT Callback = WM_APP + 1;
static constexpr UINT_PTR Poll = 1, Debounce = 2, Watch = 3, PopupWatch = 4, Idle = 5, TrayRetry = 6;
static HINSTANCE instance; static HWND mainWindow, popupWindow; static HFONT popupFont;
static HICON icon; static bool registeredIcon, closing, suspended, pinned, menuOpen, dragged, refreshPending;
static POINT pressed{}; static bool hasPress; static UINT popupDpi = 96;
static ULONGLONG keyboardUntil, manualUntil, outsideSince, workerStarted;
static HANDLE worker, uiProcesses[2]{}, signals[6]{}, childJob;
static std::wstring root, executableDirectory, workerOutput, workerInput;
static UINT taskbarCreated;
static unsigned trayFailures;
static RECT popupAnchor{};
static std::map<HANDLE, bool> keyboardDevices;
static Policy::LowBatteryEpisode lowEpisodes[2];
static Json settings = Json::obj(), snapshot = Json::obj(), history = Json::obj();
static std::string lastIconKey;
static void dismissPopup();

static std::wstring wide(const std::string& s) {
    if (s.empty()) return {}; int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), nullptr, 0);
    if (!n) throw std::runtime_error("Invalid UTF-8"); std::wstring w(n, 0); MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s.data(), (int)s.size(), w.data(), n); return w;
}
static std::string narrow(const std::wstring& w) {
    if (w.empty()) return {}; int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0); WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr); return s;
}
static std::string readFile(const std::wstring& path) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot read file");
    DWORD size = GetFileSize(h, nullptr); if (size == INVALID_FILE_SIZE || size > 4 * 1024 * 1024) { CloseHandle(h); throw std::runtime_error("File size limit"); }
    std::string s(size, 0); DWORD got = 0; BOOL ok = ReadFile(h, s.data(), size, &got, nullptr); CloseHandle(h);
    if (!ok || got != size) throw std::runtime_error("Incomplete file"); return s;
}
static void writeFile(const std::wstring& path, const std::string& data) {
    auto temp = path + L".tmp"; HANDLE h = CreateFileW(temp.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create file"); DWORD written = 0;
    BOOL ok = WriteFile(h, data.data(), (DWORD)data.size(), &written, nullptr); CloseHandle(h);
    if (!ok || written != data.size() || !MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temp.c_str()); throw std::runtime_error("Cannot write file"); }
}
static std::string timestamp(ULONGLONG futureMilliseconds = 0) {
    FILETIME ft; GetSystemTimeAsFileTime(&ft); ULARGE_INTEGER v; v.LowPart = ft.dwLowDateTime; v.HighPart = ft.dwHighDateTime; v.QuadPart += futureMilliseconds * 10000;
    ft.dwLowDateTime = v.LowPart; ft.dwHighDateTime = v.HighPart; SYSTEMTIME t{}; FileTimeToSystemTime(&ft, &t);
    char s[40]; snprintf(s, sizeof(s), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds); return s;
}
static void log(const std::string& message) {
    auto path = root + L"\\app.log"; WIN32_FILE_ATTRIBUTE_DATA a{};
    if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &a) && a.nFileSizeLow > 1024 * 1024) MoveFileExW(path.c_str(), (path + L".1").c_str(), MOVEFILE_REPLACE_EXISTING);
    HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
    if (h != INVALID_HANDLE_VALUE) { auto s = timestamp() + " " + message + "\r\n"; DWORD n; WriteFile(h, s.data(), (DWORD)s.size(), &n, nullptr); CloseHandle(h); }
}
static Json emptyReading(const char* name, const char* category, const char* state, const char* detail) {
    Json r = Json::obj(); r["Name"] = name; r["Category"] = category; r["State"] = state; r["Percent"] = Json();
    r["Method"] = "未采用读取接口"; r["Detail"] = detail; r["AttemptedAt"] = timestamp(); return r;
}
static std::wstring display(const Json& r) {
    auto state = r.at("State").text(); int n = r.at("Percent").integer(-1);
    if (state == "Valid" && n >= 0 && n <= 100) return std::to_wstring(n) + L"%";
    if (state == "Idle") return L"待使用"; if (state == "Sleeping") return L"休眠中";
    if (state == "Disconnected") return L"接收器断连"; if (state == "WirelessDisconnected") return L"设备断连";
    if (state == "ReadFailed") return L"读取失败"; if (state == "Unsupported") return L"暂不支持";
    if (state == "LowBatteryWarning") return L"低电量警告"; return L"电量未知";
}
static int percent(const Json& r) { int n = r.at("Percent").integer(-1); return r.at("State").text() == "Valid" && n >= 0 && n <= 100 ? n : -1; }
static int threshold() { return settings.at("LowThreshold").integer(20); }
static bool isLow(const Json& r) { int p = percent(r); return p >= 0 ? p <= threshold() : r.at("State").text() == "LowBatteryWarning"; }
static bool hoverEnabled() { return settings.at("HoverPanel").flag(true); }
static ULONGLONG queryUntil() { return std::max(keyboardUntil, manualUntil); }
static bool keyboardActivityPath(const std::string& path) {
    auto rawPath = Policy::hidPath(path);
    if (rawPath.find("VID_0C45&PID_FEFA") == std::string::npos) return false;
    std::string target; unsigned candidates = 0; auto selected = settings.at("KeyboardReceiver").text();
    for (const auto& d : snapshot.at("Devices").array) {
        if (d.at("Vid").integer() != 0x0c45 || d.at("Pid").integer() != 0xfefa ||
            d.at("UsagePage").integer() != 0xff60 || d.at("Usage").integer() != 0x61 ||
            d.at("InputLength").integer() != 65 || d.at("OutputLength").integer() != 65) continue;
        if (!selected.empty() && selected != d.at("ReceiverKey").text()) continue;
        ++candidates; target = Policy::physicalReceiver(d.at("ReceiverKey").text());
    }
    if (candidates != 1 || target.empty()) return false;
    for (const auto& d : snapshot.at("Devices").array) {
        if (d.at("UsagePage").integer() == 1 && d.at("Usage").integer() == 6 &&
            Policy::hidPath(d.at("Path").text()) == rawPath &&
            Policy::physicalReceiver(d.at("ReceiverKey").text()) == target) return true;
    }
    return false;
}
static bool isTargetKeyboard(HRAWINPUT input) {
    // RID_HEADER never retrieves key codes, scan codes, or the keyboard payload.
    RAWINPUTHEADER header{}; UINT bytes = sizeof(header);
    if (GetRawInputData(input, RID_HEADER, &header, &bytes, sizeof(header)) != sizeof(header) ||
        header.dwType != RIM_TYPEKEYBOARD || !header.hDevice) return false;
    auto cached = keyboardDevices.find(header.hDevice); if (cached != keyboardDevices.end()) return cached->second;
    UINT characters = 0; bool matches = false;
    if (GetRawInputDeviceInfoW(header.hDevice, RIDI_DEVICENAME, nullptr, &characters) == 0 && characters > 0 && characters < 32768) {
        std::wstring path(characters, 0);
        if (GetRawInputDeviceInfoW(header.hDevice, RIDI_DEVICENAME, path.data(), &characters) != (UINT)-1) {
            path.resize(wcslen(path.c_str())); matches = keyboardActivityPath(narrow(path));
        }
    }
    keyboardDevices[header.hDevice] = matches; return matches;
}
static void loadSettings() {
    try { settings = Json::parse(readFile(root + L"\\settings.json")); if (settings.type != Json::Object) throw std::runtime_error("Not an object"); }
    catch (...) { settings = Json::obj(); }
    settings["PollSeconds"] = std::clamp(settings.at("PollSeconds").integer(60), 15, 600);
    settings["LowThreshold"] = std::clamp(settings.at("LowThreshold").integer(20), 1, 50);
    if (mainWindow) SetTimer(mainWindow, Poll, settings.at("PollSeconds").integer(60) * 1000, nullptr);
}
static Color normalColor(const std::string& theme) {
    if (theme == "Teal" || theme == "1") return Color(255, 54, 199, 165); if (theme == "Blue" || theme == "2") return Color(255, 67, 144, 246);
    if (theme == "Green" || theme == "3") return Color(255, 70, 180, 103); if (theme == "Purple" || theme == "4") return Color(255, 167, 119, 238);
    if (theme == "Orange" || theme == "5") return Color(255, 237, 152, 53); return Color(255, 24, 24, 27);
}
static std::string themeName() { auto& t = settings.at("ColorTheme"); return t.type == Json::Number ? std::to_string(t.integer()) : t.text("Monochrome"); }
static void drawRing(Graphics& g, RectF bounds, const Json& reading) {
    Pen edge(Color(210, 127, 133, 143), 7); int p = percent(reading); Color red(255, 243, 75, 80);
    if (p < 0) {
        REAL d1[]{ 1.2f, 1.6f }, d2[]{ 1.68f, 2.24f }; edge.SetDashPattern(d1, 2);
        Pen unknown(isLow(reading) ? red : Color(255, 145, 151, 161), 5); unknown.SetDashPattern(d2, 2);
        g.DrawEllipse(&edge, bounds); g.DrawEllipse(&unknown, bounds); return;
    }
    g.DrawEllipse(&edge, bounds); if (isLow(reading)) { Pen rim(red, 7); g.DrawEllipse(&rim, bounds); }
    Pen consumed(Color(255, 250, 250, 252), 5); g.DrawEllipse(&consumed, bounds); if (p == 0) return;
    Pen remaining(isLow(reading) ? red : normalColor(themeName()), 5);
    if (p == 100) g.DrawEllipse(&remaining, bounds); else { REAL sweep = p * 3.6f; g.DrawArc(&remaining, bounds, -90 - sweep / 2, sweep); }
}
static NOTIFYICONDATAW notifyData() {
    NOTIFYICONDATAW d{}; d.cbSize = sizeof(d); d.hWnd = mainWindow; d.uID = 1; d.guidItem = IconGuid;
    d.uCallbackMessage = Callback; d.hIcon = icon; d.uFlags = NIF_GUID | NIF_MESSAGE | NIF_ICON | NIF_TIP;
    if (!hoverEnabled() && !IsWindowVisible(popupWindow)) d.uFlags |= NIF_SHOWTIP;
    auto tip = L"A950 " + display(snapshot.at("Mouse")) + L"\nS75 " + display(snapshot.at("Keyboard"));
    wcsncpy_s(d.szTip, tip.c_str(), _TRUNCATE); return d;
}
static void scheduleTrayRetry() {
    if (closing || !mainWindow) return;
    registeredIcon = false;
    SetTimer(mainWindow, TrayRetry, Policy::trayRetryMilliseconds(trayFailures), nullptr);
    trayFailures = std::min(trayFailures + 1, 5u);
}
static void modifyTray() {
    if (registeredIcon) { auto d = notifyData(); if (!Shell_NotifyIconW(NIM_MODIFY, &d)) scheduleTrayRetry(); }
}
static void addTray() {
    if (closing) return;
    auto d = notifyData(); registeredIcon = Shell_NotifyIconW(NIM_ADD, &d) != FALSE;
    if (!registeredIcon) registeredIcon = Shell_NotifyIconW(NIM_MODIFY, &d) != FALSE;
    if (registeredIcon) {
        d.uVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIconW(NIM_SETVERSION, &d)) { Shell_NotifyIconW(NIM_DELETE, &d); scheduleTrayRetry(); return; }
        trayFailures = 0; KillTimer(mainWindow, TrayRetry);
    } else { if (!trayFailures) log("托盘暂不可用，自动重试注册"); scheduleTrayRetry(); }
}
static void notifyLowBattery() {
    bool offered[2]{}; std::wstring message; unsigned i = 0;
    for (const char* key : {"Mouse", "Keyboard"}) {
        const auto& r = snapshot.at(key);
        offered[i] = lowEpisodes[i].observe(percent(r), r.at("State").text() == "LowBatteryWarning", threshold(), settings.at("LowBatteryNotifications").flag(true));
        if (offered[i]) { if (!message.empty()) message += L"\n"; message += wide(r.at("Name").text()) + L" " + display(r); } ++i;
    }
    if (message.empty() || !registeredIcon) return;
    auto d = notifyData(); d.uFlags = NIF_GUID | NIF_INFO | NIF_REALTIME;
    d.dwInfoFlags = NIIF_WARNING | NIIF_NOSOUND | NIIF_RESPECT_QUIET_TIME;
    wcsncpy_s(d.szInfoTitle, L"外设电量偏低", _TRUNCATE); wcsncpy_s(d.szInfo, message.c_str(), _TRUNCATE);
    if (Shell_NotifyIconW(NIM_MODIFY, &d)) for (unsigned index = 0; index < 2; ++index) if (offered[index]) lowEpisodes[index].acknowledge();
}
static void render() {
    auto& m = snapshot.at("Mouse"); auto& k = snapshot.at("Keyboard"); bool outerKeyboard = settings.at("KeyboardOuterRing").flag(true);
    std::string key = std::to_string(percent(m)) + ":" + std::to_string(percent(k)) + ":" + std::to_string(isLow(m)) + ":" + std::to_string(isLow(k)) + ":" + std::to_string(outerKeyboard) + themeName();
    if (!icon || key != lastIconKey) {
        Bitmap bitmap(64, 64, PixelFormat32bppARGB); Graphics g(&bitmap); g.SetSmoothingMode(SmoothingModeAntiAlias);
        drawRing(g, RectF(5, 5, 54, 54), outerKeyboard ? k : m); drawRing(g, RectF(18, 18, 28, 28), outerKeyboard ? m : k);
        HICON next{}; if (bitmap.GetHICON(&next) != Ok) throw std::runtime_error("Cannot render tray icon");
        HICON previous = icon; icon = next; modifyTray(); if (previous) DestroyIcon(previous); lastIconKey = key;
    } else modifyTray();
    if (IsWindowVisible(popupWindow)) InvalidateRect(popupWindow, nullptr, TRUE);
}
static void publishSnapshot() {
    try { writeFile(root + L"\\live-history.json", history.dump()); writeFile(root + L"\\live-snapshot.json", snapshot.dump()); }
    catch (const std::exception& e) { log(e.what()); }
}
static HANDLE launchBackend(const std::wstring& arguments, bool showUI = false) {
    auto path = executableDirectory + L"\\PeripheralBattery.Backend.exe"; auto command = L"\"" + path + L"\" " + arguments;
    STARTUPINFOW si{}; si.cb = sizeof(si); si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = showUI ? SW_SHOWNORMAL : SW_HIDE; PROCESS_INFORMATION pi{};
    if (!CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, executableDirectory.c_str(), &si, &pi)) throw std::runtime_error("Cannot start query/UI component");
    if (!AssignProcessToJobObject(childJob, pi.hProcess)) { TerminateProcess(pi.hProcess, 1); CloseHandle(pi.hThread); CloseHandle(pi.hProcess); throw std::runtime_error("Cannot contain helper process"); }
    ResumeThread(pi.hThread); CloseHandle(pi.hThread); return pi.hProcess;
}
static void finishWorker(bool timedOut = false);
static void cancelWorker() {
    refreshPending = false;
    if (worker) {
        if (WaitForSingleObject(worker, 0) == WAIT_TIMEOUT) { TerminateProcess(worker, 1); WaitForSingleObject(worker, 2000); }
        CloseHandle(worker); worker = nullptr;
    }
    KillTimer(mainWindow, Watch);
    if (!workerInput.empty()) DeleteFileW(workerInput.c_str());
    if (!workerOutput.empty()) DeleteFileW(workerOutput.c_str());
}
static void requestRefresh(bool manual = false) {
    if (manual) { manualUntil = GetTickCount64() + 30000; SetTimer(mainWindow, Idle, 30000, nullptr); }
    if (closing || suspended) return;
    if (worker) { refreshPending = true; return; }
    workerOutput = root + L"\\native-probe-" + std::to_wstring(GetCurrentProcessId()) + L".json"; workerInput = workerOutput + L".settings";
    Json request = Json::obj(); request["Settings"] = settings;
    ULONGLONG now = GetTickCount64(); request["KeyboardQueryUntil"] = queryUntil() > now ? Json(timestamp(queryUntil() - now)) : Json();
    try { writeFile(workerInput, request.dump()); worker = launchBackend(L"--worker \"" + workerOutput + L"\" \"" + workerInput + L"\""); workerStarted = GetTickCount64(); SetTimer(mainWindow, Watch, 100, nullptr); }
    catch (const std::exception& e) { log(e.what()); finishWorker(true); }
}
static void finishWorker(bool timedOut) {
    bool good = false; Json next;
    if (worker) {
        if (timedOut && WaitForSingleObject(worker, 0) == WAIT_TIMEOUT) { TerminateProcess(worker, 1); WaitForSingleObject(worker, 2000); }
        DWORD exitCode = 1; GetExitCodeProcess(worker, &exitCode); CloseHandle(worker); worker = nullptr;
        if (!timedOut && exitCode == 0) {
            try { next = Json::parse(readFile(workerOutput)); good = next.at("Mouse").type == Json::Object && next.at("Keyboard").type == Json::Object; }
            catch (...) { }
        }
    }
    KillTimer(mainWindow, Watch); if (!workerInput.empty()) DeleteFileW(workerInput.c_str()); if (!workerOutput.empty()) DeleteFileW(workerOutput.c_str());
    if (closing || suspended) return;
    if (good) {
        keyboardDevices.clear();
        if (queryUntil() <= GetTickCount64() && percent(next.at("Keyboard")) >= 0) next["Keyboard"] = emptyReading("S75", "键盘", "Idle", "键盘闲置，暂停查询以免干扰熄灯省电；按键后恢复。");
        for (const char* key : {"Mouse", "Keyboard"}) {
            auto& r = next.at(key); auto& old = snapshot.at(key);
            if (percent(r) >= 0) history[r.at("Name").text()] = r;
            if (old.at("State").text() != r.at("State").text() || percent(old) != percent(r)) log(r.at("Name").text() + ": " + narrow(display(r)));
        }
        snapshot = std::move(next);
    } else {
        snapshot["Mouse"] = emptyReading("A950", "鼠标", "ReadFailed", "读取失败或超过 8 秒，未采用旧百分比。");
        snapshot["Keyboard"] = queryUntil() > GetTickCount64()
            ? emptyReading("S75", "键盘", "ReadFailed", "读取失败或超过 8 秒，未采用旧百分比。")
            : emptyReading("S75", "键盘", "Idle", "键盘闲置，暂停查询以免干扰熄灯省电；按键后恢复。");
        snapshot["CapturedAt"] = timestamp(); log("查询进程失败或超时");
    }
    render(); publishSnapshot(); if (good) notifyLowBattery();
    if(refreshPending) { refreshPending=false; requestRefresh(); }
}
static void reloadSettings() {
    auto mouse = settings.at("MouseReceiver").text(), keyboard = settings.at("KeyboardReceiver").text();
    loadSettings();
    bool changedMouse = mouse != settings.at("MouseReceiver").text(), changedKeyboard = keyboard != settings.at("KeyboardReceiver").text();
    if (changedMouse || changedKeyboard) {
        // A worker belongs to the receiver selection captured at its launch.
        // Never accept that result after the user switches to another receiver.
        cancelWorker(); keyboardDevices.clear();
        if (changedKeyboard) { keyboardUntil = manualUntil = 0; KillTimer(mainWindow, Idle); lowEpisodes[1] = {}; snapshot["Keyboard"] = emptyReading("S75", "键盘", "Idle", "接收器选择已更新，按键后恢复读取。"); }
        if (changedMouse) { lowEpisodes[0] = {}; snapshot["Mouse"] = emptyReading("A950", "鼠标", "Unknown", "接收器选择已更新，等待读取。"); }
        snapshot["CapturedAt"] = timestamp(); publishSnapshot(); requestRefresh();
    }
    if (!settings.at("LowBatteryNotifications").flag(true)) {
        unsigned i = 0;
        for (const char* key : {"Mouse", "Keyboard"}) { const auto& r = snapshot.at(key); lowEpisodes[i++].observe(percent(r), r.at("State").text() == "LowBatteryWarning", threshold(), false); }
        if (registeredIcon) { auto d = notifyData(); d.uFlags = NIF_GUID | NIF_INFO; d.szInfo[0] = 0; Shell_NotifyIconW(NIM_MODIFY, &d); }
    }
    render();
}
static void saveQuickSetting(const char* key, const Json& value) {
    try {
        auto path = root + L"\\settings.json"; Json latest = settings;
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) latest = Json::parse(readFile(path));
        if (latest.type != Json::Object) throw std::runtime_error("Invalid settings document");
        latest[key] = value; writeFile(path, latest.dump()); reloadSettings();
    } catch (const std::exception& e) { log(e.what()); MessageBoxW(mainWindow, L"设置未能保存，请稍后再试。", L"无线外设电量", MB_OK | MB_ICONWARNING); }
}
static void dismissPopup() { if (popupWindow) ShowWindow(popupWindow, SW_HIDE); KillTimer(mainWindow, PopupWatch); pinned = false; outsideSince = 0; modifyTray(); }
static void openPopup(bool pin) {
    if (IsWindowVisible(popupWindow)) { if (pin && pinned) { dismissPopup(); return; } pinned = pinned || pin; return; }
    if (!pin && (GetAsyncKeyState(VK_LBUTTON) < 0 || menuOpen || !hoverEnabled())) return;
    NOTIFYICONIDENTIFIER id{}; id.cbSize = sizeof(id); id.hWnd = mainWindow; id.uID = 1; id.guidItem = IconGuid; RECT iconRect{}; POINT point{};
    popupAnchor = {};
    if (Shell_NotifyIconGetRect(&id, &iconRect) == S_OK) { point = {(iconRect.left + iconRect.right) / 2, iconRect.top}; popupAnchor = iconRect; } else GetCursorPos(&point);
    MONITORINFO monitor{ sizeof(monitor) }; GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor);
    SetWindowPos(popupWindow, nullptr, point.x, point.y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
    popupDpi = GetDpiForWindow(popupWindow); if (!popupDpi) popupDpi = 96;
    int width = MulDiv(136, popupDpi, 96), height = MulDiv(58, popupDpi, 96);
    int x = std::clamp((int)point.x - width / 2, (int)monitor.rcWork.left, (int)monitor.rcWork.right - width);
    int y = std::clamp((int)point.y - height - MulDiv(12, popupDpi, 96), (int)monitor.rcWork.top, (int)monitor.rcWork.bottom - height);
    pinned = pin; outsideSince = 0;
    SetWindowPos(popupWindow, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW); SetTimer(mainWindow, PopupWatch, 100, nullptr); modifyTray();
}
static BOOL CALLBACK recallUIWindow(HWND window, LPARAM processId) {
    DWORD id = 0; GetWindowThreadProcessId(window, &id);
    if (id == (DWORD)processId && IsWindowVisible(window) && !GetWindow(window, GW_OWNER)) {
        ShowWindowAsync(window, IsIconic(window) ? SW_RESTORE : SW_SHOW); SetForegroundWindow(window); return FALSE;
    }
    return TRUE;
}
static void openUI(int index) {
    dismissPopup(); if (uiProcesses[index] && WaitForSingleObject(uiProcesses[index], 0) == WAIT_TIMEOUT) { EnumWindows(recallUIWindow, (LPARAM)GetProcessId(uiProcesses[index])); return; }
    if (uiProcesses[index]) CloseHandle(uiProcesses[index]);
    uiProcesses[index] = launchBackend(index == 0 ? L"--settings-window" : L"--diagnostics-window", true);
}
static BOOL CALLBACK closeUIWindow(HWND window, LPARAM processId) {
    DWORD id=0; GetWindowThreadProcessId(window,&id); if(id==(DWORD)processId) PostMessageW(window,WM_CLOSE,0,0); return TRUE;
}
static void quit() {
    if (closing) return; closing = true;
    if (worker) finishWorker(true);
    for(auto process:uiProcesses) if(process && WaitForSingleObject(process,0)==WAIT_TIMEOUT) {
        EnumWindows(closeUIWindow,(LPARAM)GetProcessId(process)); WaitForSingleObject(process,1500);
    }
    dismissPopup(); if (registeredIcon) { auto d = notifyData(); Shell_NotifyIconW(NIM_DELETE, &d); registeredIcon = false; }
    log("正常退出，原生托盘与查询任务已结束"); DestroyWindow(popupWindow); DestroyWindow(mainWindow);
}
static void menu() {
    dismissPopup(); menuOpen = true; HMENU m = CreatePopupMenu();
    const wchar_t* names[]{ L"显示电量", L"立即刷新", L"设备诊断", L"设置", L"查看日志", L"退出" };
    for (int i = 0; i < 4; ++i) AppendMenuW(m, MF_STRING, i + 1, names[i]);
    HMENU colors = CreatePopupMenu(), rings = CreatePopupMenu();
    const wchar_t* colorLabels[]{ L"黑白（默认）", L"青绿 / 白", L"蓝 / 白", L"绿 / 白", L"紫 / 白", L"橙 / 白" };
    const char* colorNames[]{ "Monochrome", "Teal", "Blue", "Green", "Purple", "Orange" };
    auto theme = themeName();
    for (int i = 0; i < 6; ++i) AppendMenuW(colors, MF_STRING | ((theme == colorNames[i] || theme == std::to_string(i)) ? MF_CHECKED : 0), 100 + i, colorLabels[i]);
    bool keyboardOuter = settings.at("KeyboardOuterRing").flag(true);
    AppendMenuW(rings, MF_STRING | (keyboardOuter ? MF_CHECKED : 0), 110, L"外环 S75 · 内环 A950");
    AppendMenuW(rings, MF_STRING | (!keyboardOuter ? MF_CHECKED : 0), 111, L"外环 A950 · 内环 S75");
    AppendMenuW(m, MF_POPUP, (UINT_PTR)colors, L"托盘配色"); AppendMenuW(m, MF_POPUP, (UINT_PTR)rings, L"内外环设备");
    AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
    for (int i = 4; i < 6; ++i) AppendMenuW(m, MF_STRING, i + 1, names[i]);
    POINT p; GetCursorPos(&p); SetForegroundWindow(mainWindow);
    UINT result = TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.x, p.y, 0, mainWindow, nullptr);
    DestroyMenu(m); menuOpen = false; PostMessageW(mainWindow, WM_NULL, 0, 0);
    auto d = notifyData(); Shell_NotifyIconW(NIM_SETFOCUS, &d);
    if (result == 1) openPopup(true); if (result == 2) requestRefresh(true); if (result == 3) openUI(1); if (result == 4) openUI(0);
    if (result == 5) ShellExecuteW(nullptr, L"open", L"notepad.exe", (L"\"" + root + L"\\app.log\"").c_str(), nullptr, SW_SHOWNORMAL);
    if (result == 6) quit();
    if (result >= 100 && result < 106) saveQuickSetting("ColorTheme", Json(colorNames[result - 100]));
    if (result == 110 || result == 111) saveQuickSetting("KeyboardOuterRing", Json(result == 110));
}
static LRESULT CALLBACK popupProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (msg == WM_LBUTTONDOWN) { dismissPopup(); return 0; }
    if (msg == WM_DPICHANGED) { popupDpi = HIWORD(w); if (popupFont) { DeleteObject(popupFont); popupFont = nullptr; } }
    if (msg == WM_PAINT) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(h, &ps); RECT r; GetClientRect(h, &r);
        HBRUSH bg = CreateSolidBrush(RGB(37,37,38)); FillRect(dc, &r, bg); DeleteObject(bg);
        if (!popupFont) popupFont = CreateFontW(-MulDiv(10, popupDpi, 72), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        auto oldFont = SelectObject(dc, popupFont); SetBkMode(dc, TRANSPARENT);
        HPEN pen = CreatePen(PS_SOLID, 1, RGB(109,109,111)); auto oldPen = SelectObject(dc, pen); auto oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH)); RoundRect(dc,0,0,r.right,r.bottom,MulDiv(12,popupDpi,96),MulDiv(12,popupDpi,96));
        SelectObject(dc,oldBrush); SelectObject(dc,oldPen); DeleteObject(pen);
        int i = 0; for (const char* key : {"Mouse", "Keyboard"}) {
            auto& reading = snapshot.at(key); int top = MulDiv(8 + i * 21, popupDpi, 96), pad = MulDiv(10, popupDpi, 96), row = MulDiv(21, popupDpi, 96);
            RECT label{pad,top,r.right-pad-MulDiv(70,popupDpi,96),top+row}; auto name = wide(reading.at("Name").text()); SetTextColor(dc,RGB(246,246,247)); DrawTextW(dc,name.c_str(),-1,&label,DT_SINGLELINE|DT_VCENTER|DT_END_ELLIPSIS|DT_NOPREFIX);
            RECT status{r.right-pad-MulDiv(70,popupDpi,96),top,r.right-pad,top+row}; auto text=display(reading);
            SetTextColor(dc,percent(reading)>=0 ? isLow(reading) ? RGB(255,171,111) : RGB(246,246,247) : RGB(185,187,194)); DrawTextW(dc,text.c_str(),-1,&status,DT_SINGLELINE|DT_VCENTER|DT_RIGHT|DT_END_ELLIPSIS|DT_NOPREFIX); ++i;
        }
        SelectObject(dc,oldFont); EndPaint(h,&ps); return 0;
    }
    return DefWindowProcW(h,msg,w,l);
}
static LRESULT CALLBACK mainProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    try {
        if (msg == taskbarCreated) { dismissPopup(); registeredIcon = false; trayFailures = 0; addTray(); return 0; }
        if (msg == Callback) {
            UINT code = LOWORD(l); POINT point{ (short)LOWORD(w), (short)HIWORD(w) };
            if (code == WM_LBUTTONDOWN) { pressed = point; hasPress = true; dragged = false; if (!pinned) dismissPopup(); }
            if (code == WM_MOUSEMOVE && GetAsyncKeyState(VK_LBUTTON) < 0) { if (hasPress && (abs(point.x-pressed.x)>GetSystemMetrics(SM_CXDRAG)/2 || abs(point.y-pressed.y)>GetSystemMetrics(SM_CYDRAG)/2)) dragged=true; dismissPopup(); }
            if (code == NIN_SELECT || code == NIN_KEYSELECT) { bool moved = hasPress && (abs(point.x-pressed.x)>GetSystemMetrics(SM_CXDRAG)/2 || abs(point.y-pressed.y)>GetSystemMetrics(SM_CYDRAG)/2); if(code==NIN_KEYSELECT || (!dragged&&!moved)) openPopup(true); hasPress=false; dragged=false; }
            if (code == NIN_POPUPOPEN) openPopup(false); if (code == WM_CONTEXTMENU) menu(); return 0;
        }
        if (msg == WM_INPUT && !suspended && isTargetKeyboard((HRAWINPUT)l)) { ULONGLONG now=GetTickCount64(); bool resumed=keyboardUntil<=now; keyboardUntil=now+30000; SetTimer(h,Idle,30000,nullptr); if(resumed) SetTimer(h,Debounce,1800,nullptr); }
        if (msg == WM_INPUT_DEVICE_CHANGE || msg == WM_DEVICECHANGE) {
            // Activity from a removed/replaced receiver never authorizes the new
            // receiver. Wait for fresh physical keyboard input after reconnect.
            keyboardDevices.clear(); keyboardUntil = manualUntil = 0; KillTimer(h,Idle); cancelWorker();
            snapshot["Mouse"] = emptyReading("A950", "鼠标", "Unknown", "设备连接发生变化，等待读取。");
            snapshot["Keyboard"] = emptyReading("S75", "键盘", "Idle", "设备连接发生变化，按键后恢复读取。");
            snapshot["CapturedAt"] = timestamp(); render(); publishSnapshot(); SetTimer(h,Debounce,1800,nullptr);
        }
        if (msg == WM_DISPLAYCHANGE) dismissPopup();
        if (msg == WM_POWERBROADCAST) {
            if (w == PBT_APMSUSPEND) { suspended=true; keyboardUntil=manualUntil=0; keyboardDevices.clear(); KillTimer(h,Idle); cancelWorker(); dismissPopup(); snapshot["Mouse"]=emptyReading("A950","鼠标","Unknown","系统休眠，等候恢复读取"); snapshot["Keyboard"]=emptyReading("S75","键盘","Idle","系统休眠，按键后恢复读取"); render(); publishSnapshot(); }
            if (w == PBT_APMRESUMEAUTOMATIC || w == PBT_APMRESUMESUSPEND) { suspended=false; SetTimer(h,Debounce,1800,nullptr); }
        }
        if (msg == WM_TIMER) {
            if(w==Poll) requestRefresh(); if(w==Debounce) { KillTimer(h,Debounce); requestRefresh(); }
            if(w==TrayRetry) { KillTimer(h,TrayRetry); addTray(); }
            if(w==Watch && worker && (WaitForSingleObject(worker,0)==WAIT_OBJECT_0 || GetTickCount64()-workerStarted>8000)) finishWorker(GetTickCount64()-workerStarted>8000);
            if(w==Idle) {
                ULONGLONG now=GetTickCount64();
                if(queryUntil()>now) SetTimer(h,Idle,(UINT)std::max<ULONGLONG>(10,queryUntil()-now),nullptr);
                else { KillTimer(h,Idle); if(percent(snapshot.at("Keyboard"))>=0) {snapshot["Keyboard"]=emptyReading("S75","键盘","Idle","键盘闲置，暂停查询以免干扰熄灯省电；按键后恢复。"); render(); publishSnapshot();} }
            }
            if(w==PopupWatch) { POINT p; GetCursorPos(&p); RECT bounds; GetWindowRect(popupWindow,&bounds); bool inside=PtInRect(&bounds,p)!=FALSE || PtInRect(&popupAnchor,p)!=FALSE;
                if(GetAsyncKeyState(VK_ESCAPE)<0 || (!inside&&GetAsyncKeyState(VK_LBUTTON)<0)) dismissPopup();
                else if(pinned||inside) outsideSince=0;
                else { if(!outsideSince)outsideSince=GetTickCount64(); if(GetTickCount64()-outsideSince>1400) dismissPopup(); }
            }
        }
        if (msg == WM_CLOSE) { quit(); return 0; } if(msg==WM_DESTROY) { PostQuitMessage(0); return 0; }
    } catch(const std::exception& e) { log(e.what()); }
    return DefWindowProcW(h,msg,w,l);
}
static bool sendCommand(int command) { auto name=L"Local\\PeripheralBattery.Command.v1."+std::to_wstring(command); HANDLE h=OpenEventW(EVENT_MODIFY_STATE,FALSE,name.c_str()); if(!h)return false; bool ok=SetEvent(h)!=FALSE; CloseHandle(h); return ok; }
static int selfTest(const std::wstring& path) {
    int checks=0; auto check=[&](bool ok){if(!ok)throw std::runtime_error("Native self-test failed");++checks;};
    auto j=Json::parse("{\"x\":\"\\u4e2d\\u6587\\uD83D\\uDE00\",\"a\":[true,null,-12],\"path\":\"C:\\\\test\"}");
    check(j.at("x").text()=="中文😀"); check(j.at("a").array[2].integer()==-12); check(Json::parse(j.dump()).at("path").text()=="C:\\test");
    for(const char* bad : {"{", "[1,]", "{\"x\":01}", "\"\\uD800\"", "true false"}) { bool rejected=false;try{Json::parse(bad);}catch(...){rejected=true;}check(rejected); }
    auto preserved = Json::parse("{\"future\":{\"decimal\":1.50,\"large\":9007199254740993,\"exponent\":1e30},\"ColorTheme\":\"Blue\"}");
    preserved["ColorTheme"] = "Teal"; auto saved = Json::parse(preserved.dump());
    check(saved.at("future").at("decimal").dump() == "1.50"); check(saved.at("future").at("large").dump() == "9007199254740993");
    check(saved.at("future").at("exponent").dump() == "1e30"); check(saved.at("ColorTheme").text() == "Teal");
    check(Json::parse("1.5").integer(-1) == -1); check(Json(600).dump() == "600");
    settings=Json::obj(); settings["LowThreshold"]=20; settings["ColorTheme"]="Monochrome";
    for(const char* theme : {"Monochrome","Teal","Blue","Green","Purple","Orange"}) {
        settings["ColorTheme"]=theme; auto r=emptyReading("A950","鼠标","Valid",""); r["Percent"]=50;
        Bitmap image(64,64,PixelFormat32bppARGB); Graphics g(&image);g.SetSmoothingMode(SmoothingModeAntiAlias);drawRing(g,RectF(5,5,54,54),r);g.Flush(); Color top,bottom;image.GetPixel(32,5,&top);image.GetPixel(32,59,&bottom);
        auto c=normalColor(theme); check(abs((int)top.GetR()-c.GetR())<20); check(bottom.GetR()>230&&bottom.GetG()>230&&bottom.GetB()>230);
        r["Percent"]=15;check(isLow(r));r["State"]="Idle";check(percent(r)==-1);
    }
    check(Policy::physicalReceiver("USB\\VID_0C45&PID_FEFA&MI_03\\6&EXAMPLE&0&0003") == "USB\\VID_0C45&PID_FEFA\\6&EXAMPLE&0");
    check(Policy::physicalReceiver("usb\\vid_0c45&pid_fefa&mi_00\\6&example&0&0000") == Policy::physicalReceiver("USB\\VID_0C45&PID_FEFA&MI_03\\6&EXAMPLE&0&0003"));
    check(Policy::physicalReceiver("USB\\VID_0C45&PID_FEFA&MI_03\\6&EXAMPLE&0&0002").empty());
    check(Policy::physicalReceiver("HID\\VID_0C45&PID_FEFA&MI_03\\6&EXAMPLE&0&0003").empty());
    check(Policy::physicalReceiver("USB\\VID_0C45&PID_FEFA&MI_G1\\6&EXAMPLE&0&00G1").empty());
    check(Policy::hidPath("\\\\?\\hid#vid_0c45&pid_fefa#example#{guid}\\kbd") == Policy::hidPath("\\\\?\\HID#VID_0C45&PID_FEFA#EXAMPLE#{GUID}"));
    auto device = [](const char* path, const char* receiver, int usagePage, int usage) {
        Json d = Json::obj(); d["Path"] = path; d["ReceiverKey"] = receiver; d["Vid"] = 0x0c45; d["Pid"] = 0xfefa;
        d["UsagePage"] = usagePage; d["Usage"] = usage; d["InputLength"] = 65; d["OutputLength"] = 65; return d;
    };
    const char* firstKey = "USB\\VID_0C45&PID_FEFA&MI_03\\6&FIRST&0&0003";
    const char* firstPath = "\\\\?\\hid#vid_0c45&pid_fefa&mi_00#first#{guid}\\kbd";
    const char* secondKey = "USB\\VID_0C45&PID_FEFA&MI_03\\6&SECOND&0&0003";
    const char* secondPath = "\\\\?\\hid#vid_0c45&pid_fefa&mi_00#second#{guid}\\kbd";
    snapshot = Json::obj(); snapshot["Devices"] = Json::arr(); settings["KeyboardReceiver"] = Json();
    check(!keyboardActivityPath(firstPath));
    snapshot["Devices"].array.push_back(device("command-first", firstKey, 0xff60, 0x61));
    snapshot["Devices"].array.push_back(device(firstPath, "USB\\VID_0C45&PID_FEFA&MI_00\\6&FIRST&0&0000", 1, 6));
    check(keyboardActivityPath(firstPath));
    check(!keyboardActivityPath("\\\\?\\hid#vid_046d&pid_c31c#other#{guid}\\kbd"));
    check(!keyboardActivityPath(secondPath));
    snapshot["Devices"].array.push_back(device("command-second", secondKey, 0xff60, 0x61));
    snapshot["Devices"].array.push_back(device(secondPath, "USB\\VID_0C45&PID_FEFA&MI_00\\6&SECOND&0&0000", 1, 6));
    check(!keyboardActivityPath(firstPath)); check(!keyboardActivityPath(secondPath));
    settings["KeyboardReceiver"] = firstKey; check(keyboardActivityPath(firstPath)); check(!keyboardActivityPath(secondPath));
    settings["KeyboardReceiver"] = secondKey; check(!keyboardActivityPath(firstPath)); check(keyboardActivityPath(secondPath));
    settings["KeyboardReceiver"] = "offline"; check(!keyboardActivityPath(firstPath));
    for (unsigned failures : {0u, 1u, 2u, 5u, 100u}) check(Policy::trayRetryMilliseconds(failures) >= 1000 && Policy::trayRetryMilliseconds(failures) <= 30000);
    Policy::LowBatteryEpisode low;
    check(!low.observe(80, false, 20, true)); check(low.observe(20, false, 20, true)); low.acknowledge();
    check(!low.observe(19, false, 20, true)); check(!low.observe(-1, false, 20, true));
    check(!low.observe(25, false, 20, true)); check(!low.observe(20, false, 20, true));
    check(!low.observe(26, false, 20, true)); check(low.observe(20, false, 20, true)); low.acknowledge();
    check(!low.observe(18, false, 10, true)); check(!low.observe(18, false, 20, true));
    Policy::LowBatteryEpisode muted;
    check(!muted.observe(10, false, 20, false)); check(!muted.observe(10, false, 20, true));
    check(!muted.observe(-1, false, 20, true)); check(!muted.observe(60, false, 20, false)); check(muted.observe(0, false, 20, true));
    Policy::LowBatteryEpisode warning, other;
    check(warning.observe(-1, true, 20, true)); warning.acknowledge(); check(!warning.observe(-1, true, 20, true));
    check(other.observe(5, false, 20, true)); check(!warning.observe(-1, false, 20, true));
    Policy::LowBatteryEpisode highThreshold;
    check(highThreshold.observe(99, false, 99, true)); highThreshold.acknowledge();
    check(!highThreshold.observe(100, false, 99, true)); check(!highThreshold.observe(99, false, 99, true));
    writeFile(path, "{\"Passed\":"+std::to_string(checks)+",\"TrayRegistered\":false,\"HardwareQueried\":false}"); return 0;
}
int WINAPI wWinMain(HINSTANCE h, HINSTANCE, PWSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    instance=h; int argc=0; auto argv=CommandLineToArgvW(GetCommandLineW(),&argc); std::vector<std::wstring> args(argv,argv+argc);LocalFree(argv);
    wchar_t exe[32768];GetModuleFileNameW(nullptr,exe,32768);executableDirectory=std::wstring(exe);executableDirectory.resize(executableDirectory.find_last_of(L"\\/"));
    PWSTR local=nullptr;if(FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&local)))return 1;root=std::wstring(local)+L"\\PeripheralBattery";CoTaskMemFree(local);CreateDirectoryW(root.c_str(),nullptr);
    ULONG_PTR gdiplus=0; GdiplusStartupInput input; if(GdiplusStartup(&gdiplus,&input,nullptr)!=Ok)return 1;
    int result=0; HANDLE mutex=nullptr;
    try {
        if(argc==3&&args[1]==L"--self-test") { result=selfTest(args[2]); GdiplusShutdown(gdiplus);return result; }
        childJob=CreateJobObjectW(nullptr,nullptr); JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if(!childJob || !SetInformationJobObject(childJob,JobObjectExtendedLimitInformation,&limits,sizeof(limits)))throw std::runtime_error("Cannot create helper process lifetime boundary");
        if(argc==3&&args[1]==L"--diagnose") { HANDLE child=launchBackend(L"--diagnose \""+args[2]+L"\""); if(WaitForSingleObject(child,12000)==WAIT_TIMEOUT){TerminateProcess(child,1);result=1;}else{DWORD code;GetExitCodeProcess(child,&code);result=(int)code;}CloseHandle(child);CloseHandle(childJob);GdiplusShutdown(gdiplus);return result; }
        int command=argc>1&&args[1]==L"--quit"?3:argc>1&&args[1]==L"--diagnostics"?1:argc>1&&args[1]==L"--settings"?2:0;
        mutex=CreateMutexW(nullptr,TRUE,L"Local\\PeripheralBattery.Tray.v1"); if(!mutex)throw std::runtime_error("Cannot create instance mutex");
        if(GetLastError()==ERROR_ALREADY_EXISTS){result=sendCommand(command)?0:1;CloseHandle(mutex);CloseHandle(childJob);GdiplusShutdown(gdiplus);return result;}
        if(command==3){CloseHandle(mutex);CloseHandle(childJob);GdiplusShutdown(gdiplus);return 0;}
        WNDCLASSW wc{};wc.hInstance=h;wc.lpfnWndProc=mainProc;wc.lpszClassName=L"PeripheralBattery.Native";RegisterClassW(&wc);
        wc.lpfnWndProc=popupProc;wc.lpszClassName=L"PeripheralBattery.Popup";wc.style=CS_DROPSHADOW;wc.hCursor=LoadCursorW(nullptr,IDC_ARROW);RegisterClassW(&wc);
        taskbarCreated=RegisterWindowMessageW(L"TaskbarCreated"); mainWindow=CreateWindowExW(WS_EX_TOOLWINDOW,L"PeripheralBattery.Native",L"PeripheralBattery.Tray",WS_POPUP,0,0,0,0,nullptr,nullptr,h,nullptr);
        popupWindow=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST,L"PeripheralBattery.Popup",L"无线外设电量",WS_POPUP,0,0,136,58,nullptr,nullptr,h,nullptr);
        int corner=2;DwmSetWindowAttribute(popupWindow,33,&corner,sizeof(corner));
        if(!mainWindow||!popupWindow)throw std::runtime_error("Cannot create tray window");
        RAWINPUTDEVICE keyboard{1,6,RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,mainWindow};if(!RegisterRawInputDevices(&keyboard,1,sizeof(keyboard)))log("键盘活动通知不可用，自动键盘查询保持暂停");
        for(int i=0;i<6;++i){auto name=L"Local\\PeripheralBattery.Command.v1."+std::to_wstring(i);signals[i]=CreateEventW(nullptr,FALSE,FALSE,name.c_str());if(!signals[i])throw std::runtime_error("Cannot create command channel");}
        loadSettings();snapshot["CapturedAt"]=timestamp();snapshot["Devices"]=Json::arr();snapshot["Mouse"]=emptyReading("A950","鼠标","Unknown","等待读取");snapshot["Keyboard"]=emptyReading("S75","键盘","Idle","键盘闲置，按键后恢复读取");
        render();addTray();log("启动 v" APP_VERSION " · 原生低内存托盘");requestRefresh();
        if(command==1)openUI(1);else if(command==2)openUI(0);else if(argc>1&&args[1]==L"--show")openPopup(true);
        bool running=true;while(running){DWORD wait=MsgWaitForMultipleObjects(6,signals,FALSE,INFINITE,QS_ALLINPUT);
            if(wait<WAIT_OBJECT_0+6){int c=(int)(wait-WAIT_OBJECT_0);if(c==0)openPopup(true);if(c==1)openUI(1);if(c==2)openUI(0);if(c==3)quit();if(c==4)requestRefresh(true);if(c==5)reloadSettings();}
            if(wait==WAIT_FAILED)throw std::runtime_error("Message wait failed");MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){if(msg.message==WM_QUIT){running=false;break;}TranslateMessage(&msg);DispatchMessageW(&msg);}
        }
    }catch(const std::exception& e){log(e.what());MessageBoxW(nullptr,wide(e.what()).c_str(),L"无线外设电量",MB_OK|MB_ICONERROR);result=1;}
    closing=true;if(worker)finishWorker(true);if(registeredIcon){auto d=notifyData();Shell_NotifyIconW(NIM_DELETE,&d);}for(auto signal:signals)if(signal)CloseHandle(signal);for(auto ui:uiProcesses)if(ui)CloseHandle(ui);
    if(childJob)CloseHandle(childJob);if(icon)DestroyIcon(icon);if(popupFont)DeleteObject(popupFont);if(mutex)CloseHandle(mutex);GdiplusShutdown(gdiplus);return result;
}
