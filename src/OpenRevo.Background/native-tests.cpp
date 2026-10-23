#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include "tray-icon.h"

int main()
{
    if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
    { std::puts("FAIL native tray manifest must enable PerMonitorV2 before UI creation"); return 1; }
    for (const int size : { 16, 20, 24, 28, 32, 40, 48, 64, 128, 256 })
    {
        const auto pixels = ReadLucideIconPixels(size);
        size_t transparent = 0, opaque = 0, edge = 0, white = 0;
        for (const auto pixel : pixels)
        {
            const auto alpha = pixel >> 24;
            if (alpha == 0) ++transparent;
            else if (alpha == 255) ++opaque;
            else ++edge;
            if (alpha == 255 && ((pixel >> 16) & 255) >= 220 && ((pixel >> 8) & 255) >= 220 && (pixel & 255) >= 220) ++white;
            if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha)
            { std::puts("FAIL icon alpha must be premultiplied"); return 1; }
        }
        if (pixels.size() != static_cast<size_t>(size) * size || !transparent || !opaque || !edge || !white
            || pixels.front() != 0 || pixels.back() != 0)
        { std::printf("FAIL Lucide icon %d px: pixels=%zu transparent=%zu opaque=%zu edge=%zu light-strokes=%zu\n", size, pixels.size(), transparent, opaque, edge, white); return 1; }
    }
    std::puts("PASS PerMonitorV2 manifest and embedded Lucide fan icon at 10 physical sizes");
    return 0;
}
