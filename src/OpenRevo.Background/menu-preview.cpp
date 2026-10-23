// Development-only host for the exact production popup menu. Never controls hardware.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "tray-menu.h"

LRESULT CALLBACK PreviewProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_CREATE)
    {
        CreateWindowW(L"STATIC", L"生产菜单预览：只检查布局和状态，不执行硬件操作。", WS_CHILD | WS_VISIBLE,
            20, 20, 600, 30, window, nullptr, nullptr, nullptr);
        const wchar_t* labels[] = { L"关窗后菜单", L"连接中菜单", L"断开后菜单" };
        for (int i = 0; i < 3; i++) CreateWindowW(L"BUTTON", labels[i], WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            20 + i * 190, 65, 175, 45, window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(100 + i)), nullptr, nullptr);
        return 0;
    }
    if (message == WM_COMMAND && LOWORD(wParam) >= 100 && LOWORD(wParam) <= 102)
    {
        const auto scenario = LOWORD(wParam) - 100;
        const DWORD state = scenario == 1 ? (1u << BalancedCommand) | (1u << TurboCommand) | (1u << CustomCommand)
            | (1u << FanBoostCommand) | (1u << RestoreCommand) | (1u << KeepCommand) | (1u << RefreshCommand)
            | (1u << 24) | (1u << 28) : 0;
        const auto menu = CreateTrayMenu(scenario != 0, state);
        POINT point{ 20 + scenario * 190, 115 }; ClientToScreen(window, &point);
        const auto command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, window, nullptr);
        DestroyMenu(menu);
        if (command) SetWindowTextW(window, L"OpenRevo 菜单预览 · 已选择，未执行任何设备操作");
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    WNDCLASSW type{}; type.lpfnWndProc = PreviewProcedure; type.hInstance = instance;
    type.lpszClassName = L"OpenRevo.MenuPreview"; type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    if (!RegisterClassW(&type)) return 1;
    const auto window = CreateWindowW(type.lpszClassName, L"OpenRevo 托盘菜单预览（开发检查）", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 680, 680, nullptr, nullptr, instance, nullptr);
    if (!window) return 2;
    ShowWindow(window, SW_SHOW);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
    return 0;
}
