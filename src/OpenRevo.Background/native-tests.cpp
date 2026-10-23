#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include "tray-icon.h"
#include "tray-menu.h"

int main()
{
    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    { std::puts("FAIL native tray manifest must enable PerMonitorV2 before UI creation"); return 1; }
    for (const int size : { 16, 20, 24, 28, 32, 40, 48, 64, 128, 256 })
    {
        const auto pixels = ReadAppIconPixels(size);
        size_t transparent = 0, opaque = 0, edge = 0, highlights = 0;
        for (const auto pixel : pixels)
        {
            const auto alpha = pixel >> 24;
            if (alpha == 0) ++transparent;
            else if (alpha == 255) ++opaque;
            else ++edge;
            // Generated enamel may be partially translucent; assess its unassociated color.
            if (alpha >= 128 && ((pixel >> 8) & 255) * 255 >= 160 * alpha && (pixel & 255) * 255 >= 140 * alpha) ++highlights;
            if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha)
            { std::puts("FAIL icon alpha must be premultiplied"); return 1; }
        }
        if (pixels.size() != static_cast<size_t>(size) * size || !transparent || !opaque || !edge || !highlights
            || pixels.front() != 0 || pixels.back() != 0)
        { std::printf("FAIL app icon %d px: pixels=%zu transparent=%zu opaque=%zu edge=%zu highlights=%zu\n", size, pixels.size(), transparent, opaque, edge, highlights); return 1; }
    }
    std::puts("PASS PerMonitorV2 manifest and embedded generated app icon at 10 physical sizes");
    for (const bool hasUi : {false, true})
    {
        const DWORD state = (1u << BalancedCommand) | (1u << FanBoostCommand) | (1u << RestoreCommand) | (1u << RefreshCommand)
            | (1u << 24) | (1u << 28);
        const auto menu = CreateTrayMenu(hasUi, state);
        if (!menu || GetMenuItemCount(menu) != 15) { std::puts("FAIL tray menu structure"); return 1; }
        const auto modes = GetSubMenu(menu, 4); const auto fans = GetSubMenu(menu, 5);
        if (!modes || !fans || GetMenuItemCount(modes) != 4 || GetMenuItemCount(fans) != 2)
        { std::puts("FAIL tray mode/fan groups"); return 1; }
        auto disabled = [](HMENU target, UINT command) { return (GetMenuState(target, command, MF_BYCOMMAND) & (MF_DISABLED | MF_GRAYED)) != 0; };
        if (disabled(modes, BalancedCommand) || disabled(fans, FanBoostCommand)
            || disabled(modes, OfficeCommand) != hasUi || disabled(fans, FanAutoCommand) != hasUi
            || disabled(menu, RestoreCommand) == hasUi || !disabled(menu, KeepCommand))
        { std::puts("FAIL cold/warm menu availability"); return 1; }
        if (hasUi && (!(GetMenuState(modes, OfficeCommand, MF_BYCOMMAND) & MF_CHECKED)
            || !(GetMenuState(fans, FanAutoCommand, MF_BYCOMMAND) & MF_CHECKED)))
        { std::puts("FAIL current mode/fan selection"); return 1; }
        DestroyMenu(menu);
    }
    const auto disconnected = CreateTrayMenu(true, 0);
    if (!disconnected || !(GetMenuState(GetSubMenu(disconnected, 4), OfficeCommand, MF_BYCOMMAND) & MF_GRAYED)
        || !(GetMenuState(disconnected, RefreshCommand, MF_BYCOMMAND) & MF_GRAYED))
    { std::puts("FAIL disconnected controls must be disabled"); return 1; }
    DestroyMenu(disconnected);
    for (UINT command = 0; command < 256; command++)
        if (IsTrayAction(command) != (command == 1 || command == 3 || command == 4 || (command >= 10 && command <= 20)))
        { std::puts("FAIL desktop action whitelist"); return 1; }
    std::puts("PASS native cold/warm/disconnected menus, current-state radio marks and action whitelist");
    return 0;
}
