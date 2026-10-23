#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

inline std::vector<std::uint32_t> RenderTrayPixels(int size)
{
    constexpr int samples = 8;
    constexpr int sampleCount = samples * samples;
    std::vector<std::uint32_t> pixels(static_cast<size_t>(size) * size);
    for (int y = 0; y < size; ++y)
        for (int x = 0; x < size; ++x)
        {
            int covered = 0; double red = 0, green = 0, blue = 0;
            for (int sy = 0; sy < samples; ++sy)
                for (int sx = 0; sx < samples; ++sx)
                {
                    const double px = (x + (sx + .5) / samples) / size * 2 - 1;
                    const double py = (y + (sy + .5) / samples) / size * 2 - 1;
                    const double dx = std::max(std::abs(px) - .62, 0.0);
                    const double dy = std::max(std::abs(py) - .62, 0.0);
                    if (dx * dx + dy * dy > .27 * .27) continue;
                    ++covered;
                    const double radius = px * px + py * py;
                    if (radius >= .35 * .35 && radius <= .55 * .55)
                    { red += 255; green += 255; blue += 255; }
                    else
                    {
                        const double gradient = (py + 1) / 2;
                        red += 34 - 20 * gradient;
                        green += 182 - 70 * gradient;
                        blue += 165 - 52 * gradient;
                    }
                }
            // Premultiplied BGRA with real alpha at the rounded edge and smooth O strokes.
            pixels[static_cast<size_t>(y) * size + x] =
                (static_cast<std::uint32_t>(covered * 255 / sampleCount) << 24)
                | (static_cast<std::uint32_t>(red / sampleCount) << 16)
                | (static_cast<std::uint32_t>(green / sampleCount) << 8)
                | static_cast<std::uint32_t>(blue / sampleCount);
        }
    return pixels;
}
