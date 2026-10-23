#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include "tray-icon.h"
#include "tray-menu.h"

// No CLR, WinUI, device connection, timers or polling in the resident process.
constexpr wchar_t WindowClass[] = L"OpenRevo.Background.v1";
constexpr wchar_t UiProperty[] = L"OpenRevo.Ui.v1";
constexpr UINT TrayMessage = WM_APP + 1;
constexpr UINT StopMessage = WM_APP + 2;
constexpr UINT OpenMessage = WM_APP + 3;
constexpr UINT ExitAllMessage = WM_APP + 4;
UINT showUiMessage, exitUiMessage, trayActionMessage, trayStateMessage, taskbarCreated;
HICON trayIcon;
HANDLE launchingUi;
bool iconAdded;

HWND FindUi()
{
    HWND result = nullptr;
    EnumWindows([](HWND window, LPARAM context) -> BOOL {
        if (!GetPropW(window, UiProperty)) return TRUE;
        *reinterpret_cast<HWND*>(context) = window;
        return FALSE;
    }, reinterpret_cast<LPARAM>(&result));
    return result;
}

void ReportError(const wchar_t* message)
{
    MessageBoxW(nullptr, message, L"OpenRevo", MB_OK | MB_ICONWARNING);
}

void OpenUi(UINT action = 0)
{
    if (action != 0 && !IsTrayAction(action)) return;
    if (auto ui = FindUi())
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(ui, &pid);
        AllowSetForegroundWindow(pid);
        PostMessageW(ui, action == 0 ? showUiMessage : trayActionMessage, action, 0);
        return;
    }
    if (launchingUi)
    {
        if (WaitForSingleObject(launchingUi, 0) == WAIT_TIMEOUT) return;
        CloseHandle(launchingUi); launchingUi = nullptr;
    }
    std::vector<wchar_t> path(32768);
    const DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (count == 0 || count >= path.size()) { ReportError(L"无法确定程序位置，请重新打开完整发行包。"); return; }
    const std::wstring full(path.data(), count);
    const auto folder = full.substr(0, full.find_last_of(L"\\/"));
    const auto executable = folder + L"\\OpenRevo.exe";
    std::wstring command = L"\"" + executable + L"\"";
    if (action != 0) command += L" --tray-action " + std::to_wstring(action);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
        nullptr, folder.c_str(), &startup, &process))
    { ReportError(L"无法打开 OpenRevo。请将完整发行包解压到同一个文件夹。"); return; }
    CloseHandle(process.hThread);
    launchingUi = process.hProcess;
}

HICON CreateTrayIcon()
{
    const auto taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
    const UINT dpi = taskbar ? GetDpiForWindow(taskbar) : GetDpiForSystem();
    const int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
    return LoadAppIcon(size);
}

void RefreshTrayIcon(HWND window)
{
    const auto replacement = CreateTrayIcon();
    if (!replacement) return;
    NOTIFYICONDATAW icon{}; icon.cbSize = sizeof(icon); icon.hWnd = window; icon.uID = 1;
    icon.uFlags = NIF_ICON; icon.hIcon = replacement;
    Shell_NotifyIconW(NIM_MODIFY, &icon);
    if (trayIcon) DestroyIcon(trayIcon);
    trayIcon = replacement;
}

bool AddIcon(HWND window)
{
    NOTIFYICONDATAW icon{};
    icon.cbSize = sizeof(icon); icon.hWnd = window; icon.uID = 1;
    icon.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    icon.uCallbackMessage = TrayMessage; icon.hIcon = trayIcon;
    wcscpy_s(icon.szTip, L"OpenRevo · 点击打开 · 轻量后台");
    iconAdded = Shell_NotifyIconW(NIM_ADD, &icon) != FALSE;
    if (iconAdded) { icon.uVersion = NOTIFYICON_VERSION_4; Shell_NotifyIconW(NIM_SETVERSION, &icon); }
    return iconAdded;
}

void ShowMenu(HWND window, LPARAM coordinates)
{
    POINT point{ static_cast<short>(LOWORD(coordinates)), static_cast<short>(HIWORD(coordinates)) };
    NOTIFYICONIDENTIFIER identifier{}; identifier.cbSize = sizeof(identifier); identifier.hWnd = window; identifier.uID = 1;
    RECT iconBounds{};
    const bool hasBounds = SUCCEEDED(Shell_NotifyIconGetRect(&identifier, &iconBounds));
    if (point.x == -1 && point.y == -1)
    {
        if (hasBounds) point = { iconBounds.right, iconBounds.top };
        else GetCursorPos(&point);
    }
    // The owner must be on the menu's monitor so PerMonitorV2 chooses its actual DPI.
    SetWindowPos(window, nullptr, point.x, point.y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    const auto ui = FindUi();
    DWORD_PTR state = 0;
    if (ui) SendMessageTimeoutW(ui, trayStateMessage, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &state);
    const auto menu = CreateTrayMenu(ui != nullptr, static_cast<DWORD>(state));
    if (!menu) return;
    TPMPARAMS placement{}; placement.cbSize = sizeof(placement);
    if (hasBounds) placement.rcExclude = iconBounds;
    SetForegroundWindow(window);
    const auto command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_RIGHTALIGN
        | TPM_BOTTOMALIGN | TPM_WORKAREA, point.x, point.y, window, hasBounds ? &placement : nullptr);
    DestroyMenu(menu);
    PostMessageW(window, WM_NULL, 0, 0);
    if (IsTrayAction(command)) OpenUi(command);
    if (command == ExitCommand) PostMessageW(window, ExitAllMessage, 0, 0);
    NOTIFYICONDATAW icon{}; icon.cbSize = sizeof(icon); icon.hWnd = window; icon.uID = 1;
    Shell_NotifyIconW(NIM_SETFOCUS, &icon);
}

LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == taskbarCreated)
    {
        iconAdded = false;
        const auto replacement = CreateTrayIcon();
        if (replacement) { if (trayIcon) DestroyIcon(trayIcon); trayIcon = replacement; }
        if (!AddIcon(window)) DestroyWindow(window);
        return 0;
    }
    switch (message)
    {
    case WM_DPICHANGED: case WM_DISPLAYCHANGE: case WM_SETTINGCHANGE:
        if (iconAdded) RefreshTrayIcon(window);
        return 0;
    case TrayMessage:
        if (LOWORD(lParam) == NIN_SELECT || LOWORD(lParam) == NIN_KEYSELECT) OpenUi();
        else if (LOWORD(lParam) == WM_CONTEXTMENU) ShowMenu(window, static_cast<LPARAM>(wParam));
        return 0;
    case OpenMessage: OpenUi(); return 0;
    case ExitAllMessage:
        if (auto ui = FindUi()) PostMessageW(ui, exitUiMessage, 0, 0);
        // Wait for an in-flight launch to become ready only on an explicit exit.
        else if (launchingUi && WaitForSingleObject(launchingUi, 0) == WAIT_TIMEOUT)
        {
            WaitForInputIdle(launchingUi, 3000);
            if (auto launchingWindow = FindUi()) PostMessageW(launchingWindow, exitUiMessage, 0, 0);
        }
        DestroyWindow(window); return 0;
    case StopMessage: case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_ENDSESSION: if (wParam) DestroyWindow(window); return 0;
    case WM_DESTROY:
        if (iconAdded)
        {
            NOTIFYICONDATAW icon{}; icon.cbSize = sizeof(icon); icon.hWnd = window; icon.uID = 1;
            Shell_NotifyIconW(NIM_DELETE, &icon); iconAdded = false;
        }
        PostQuitMessage(0); return 0;
    default: return DefWindowProcW(window, message, wParam, lParam);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    int count = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    const std::wstring argument = arguments && count == 2 ? arguments[1] : L"";
    if (arguments) LocalFree(arguments);
    if (count > 2 || (!argument.empty() && argument != L"--autostart" && argument != L"--show"
        && argument != L"--stop" && argument != L"--exit")) return 2;
    if (argument == L"--stop" || argument == L"--exit")
    {
        const auto existing = FindWindowW(WindowClass, nullptr);
        if (existing) PostMessageW(existing, argument == L"--stop" ? StopMessage : ExitAllMessage, 0, 0);
        return 0;
    }
    auto mutex = CreateMutexW(nullptr, TRUE, L"Local\\OpenRevo.Background.v1");
    if (!mutex) return 3;
    const bool alreadyRunning = GetLastError() == ERROR_ALREADY_EXISTS;
    auto ready = CreateEventW(nullptr, TRUE, FALSE, L"Local\\OpenRevo.Background.Ready.v1");
    if (!ready) { CloseHandle(mutex); return 3; }
    if (alreadyRunning)
    {
        WaitForSingleObject(ready, 3000);
        if (argument == L"--show")
            if (auto existing = FindWindowW(WindowClass, nullptr)) PostMessageW(existing, OpenMessage, 0, 0);
        CloseHandle(ready); CloseHandle(mutex); return 0;
    }
    ResetEvent(ready);
    showUiMessage = RegisterWindowMessageW(L"OpenRevo.Show.v1");
    exitUiMessage = RegisterWindowMessageW(L"OpenRevo.Exit.v1");
    trayActionMessage = RegisterWindowMessageW(L"OpenRevo.TrayAction.v1");
    trayStateMessage = RegisterWindowMessageW(L"OpenRevo.TrayState.v1");
    taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WindowProcedure; windowClass.hInstance = instance; windowClass.lpszClassName = WindowClass;
    trayIcon = CreateTrayIcon();
    const auto registered = RegisterClassW(&windowClass);
    const auto window = registered ? CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, L"OpenRevo 轻量后台", 0,
        0, 0, 0, 0, nullptr, nullptr, instance, nullptr) : nullptr;
    int result = 0;
    if (!window || !trayIcon || !AddIcon(window))
    {
        ReportError(L"托盘暂不可用，OpenRevo 后台已退出。可以直接打开主程序。");
        if (window) DestroyWindow(window);
        result = 4;
    }
    else
    {
        SetEvent(ready);
        if (argument == L"--show") OpenUi();
        MSG message{};
        BOOL status;
        while ((status = GetMessageW(&message, nullptr, 0, 0)) > 0)
        { TranslateMessage(&message); DispatchMessageW(&message); }
        if (status == -1) { DestroyWindow(window); result = 5; }
    }
    if (launchingUi) CloseHandle(launchingUi);
    if (trayIcon) DestroyIcon(trayIcon);
    CloseHandle(ready); ReleaseMutex(mutex); CloseHandle(mutex);
    return result;
}
