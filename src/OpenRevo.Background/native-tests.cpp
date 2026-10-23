#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include "tray-renderer.h"

int main()
{
    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    { std::puts("FAIL native tray manifest must enable PerMonitorV2 before UI creation"); return 1; }
    for (const int size : { 16, 20, 24, 28, 32, 40, 48, 64 })
    {
        const auto pixels = RenderTrayPixels(size);
        size_t transparent = 0, opaque = 0, edge = 0, white = 0;
        for (const auto pixel : pixels)
        {
            const auto alpha = pixel >> 24;
            if (alpha == 0) ++transparent;
            else if (alpha == 255) ++opaque;
            else ++edge;
            if (pixel == 0xffffffff) ++white;
            if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha)
            { std::puts("FAIL icon alpha must be premultiplied"); return 1; }
        }
        if (pixels.size() != static_cast<size_t>(size) * size || !transparent || !opaque || !edge || !white
            || pixels.front() != 0 || pixels.back() != 0)
        { std::puts("FAIL scaled icon must preserve transparent corners, smooth edges and O stroke"); return 1; }
    }
    std::puts("PASS PerMonitorV2 manifest and antialiased tray icon at 8 physical sizes");
    return 0;
}
