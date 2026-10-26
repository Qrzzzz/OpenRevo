#pragma once

// Keep integer command IDs in sync with Core/TrayActions.cs. No hardware code here.
constexpr UINT OpenCommand = 1, ExitCommand = 2, PerformanceCommand = 3, SettingsCommand = 4;
constexpr UINT OfficeCommand = 10, BalancedCommand = 11, TurboCommand = 12, CustomCommand = 13;
constexpr UINT FanBoostCommand = 14, FanAutoCommand = 15, RestoreCommand = 16, KeepCommand = 17;
constexpr UINT RefreshCommand = 18, DiagnosticsCommand = 19, UpdatesCommand = 20;

inline bool IsTrayAction(UINT command)
{
    return command == OpenCommand || command == PerformanceCommand || command == SettingsCommand
        || (command >= OfficeCommand && command <= UpdatesCommand);
}

inline HMENU CreateTrayMenu(bool hasUi, DWORD state)
{
    const auto menu = CreatePopupMenu();
    if (!menu) return nullptr;
    AppendMenuW(menu, MF_STRING, OpenCommand, L"打开设备概览(&O)");
    AppendMenuW(menu, MF_STRING, PerformanceCommand, L"性能与散热(&P)");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING | MF_DISABLED, 0,
        hasUi ? L"快捷控制 · 按实时状态启用" : L"快捷控制 · 打开后核验并执行");
    auto add = [hasUi, state](HMENU target, UINT id, const wchar_t* label) {
        const bool coldAllowed = id >= OfficeCommand && id <= FanAutoCommand;
        AppendMenuW(target, MF_STRING | ((hasUi ? (state & (1u << id)) != 0 : coldAllowed) ? MF_ENABLED : MF_GRAYED), id, label);
    };
    const auto modes = CreatePopupMenu();
    const auto fans = CreatePopupMenu();
    if (!modes || !fans)
    {
        if (modes) DestroyMenu(modes);
        if (fans) DestroyMenu(fans);
        DestroyMenu(menu); return nullptr;
    }
    add(modes, OfficeCommand, L"办公模式(&O)");
    add(modes, BalancedCommand, L"均衡模式(&B)");
    add(modes, TurboCommand, L"狂暴模式(&T)");
    add(modes, CustomCommand, L"原厂已保存的自定义(&C)");
    const auto mode = (state >> 24) & 7;
    if (hasUi && mode >= 1 && mode <= 4)
        CheckMenuRadioItem(modes, OfficeCommand, CustomCommand, OfficeCommand + mode - 1, MF_BYCOMMAND);
    add(fans, FanBoostCommand, L"开启一键强冷(&B)");
    add(fans, FanAutoCommand, L"恢复自动调速(&A)");
    if (hasUi && (state & ((1u << 28) | (1u << 29))))
        CheckMenuRadioItem(fans, FanBoostCommand, FanAutoCommand, (state & (1u << 29)) ? FanBoostCommand : FanAutoCommand, MF_BYCOMMAND);
    AppendMenuW(menu, MF_STRING | MF_POPUP, reinterpret_cast<UINT_PTR>(modes), L"性能模式(&M)");
    AppendMenuW(menu, MF_STRING | MF_POPUP, reinterpret_cast<UINT_PTR>(fans), L"风扇调速(&F)");
    add(menu, RestoreCommand, L"撤销上次模式切换(&U)");
    add(menu, KeepCommand, L"结束撤销记录(&K)");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, DiagnosticsCommand, L"保存诊断报告(&D)");
    AppendMenuW(menu, MF_STRING, UpdatesCommand, L"检查 GitHub 更新(&G)");
    AppendMenuW(menu, MF_STRING, SettingsCommand, L"启动与后台设置(&S)");
    add(menu, RefreshCommand, L"刷新设备数据(&R)");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, ExitCommand, L"退出 OpenRevo(&X)");
    SetMenuDefaultItem(menu, OpenCommand, FALSE);
    return menu;
}
