#pragma once
#include <vector>
#include <cstdint>

constexpr int LucideIconResource = 101;
inline std::vector<std::uint32_t> ReadIconPixels(HICON icon, int size)
{
    ICONINFO info{};
    std::vector<std::uint32_t> pixels;
    if (icon && GetIconInfo(icon, &info))
    {
        BITMAP bitmap{};
        if (GetObjectW(info.hbmColor, sizeof(bitmap), &bitmap) && bitmap.bmWidth == size && bitmap.bmHeight == size)
        {
            pixels.resize(static_cast<size_t>(size) * size);
            BITMAPINFO format{}; format.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            format.bmiHeader.biWidth = size; format.bmiHeader.biHeight = -size;
            format.bmiHeader.biPlanes = 1; format.bmiHeader.biBitCount = 32;
            const auto dc = GetDC(nullptr);
            if (!GetDIBits(dc, info.hbmColor, 0, size, pixels.data(), &format, DIB_RGB_COLORS)) pixels.clear();
            ReleaseDC(nullptr, dc);
        }
        if (info.hbmColor) DeleteObject(info.hbmColor);
        if (info.hbmMask) DeleteObject(info.hbmMask);
    }
    return pixels;
}

inline HICON LoadLucideIcon(int size)
{
    const auto original = static_cast<HICON>(LoadImageW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(LucideIconResource),
        IMAGE_ICON, size, size, LR_DEFAULTCOLOR));
    auto pixels = ReadIconPixels(original, size);
    bool straightAlpha = false;
    for (const auto pixel : pixels)
    {
        const auto alpha = pixel >> 24;
        if (((pixel >> 16) & 255) > alpha || ((pixel >> 8) & 255) > alpha || (pixel & 255) > alpha) straightAlpha = true;
    }
    if (!straightAlpha) return original;
    // PNG-backed ICOs may decode to straight BGRA. Normalize once for shell compositing.
    for (auto& pixel : pixels)
    {
        const auto alpha = pixel >> 24;
        pixel = (alpha << 24) | ((((pixel >> 16) & 255) * alpha / 255) << 16)
            | ((((pixel >> 8) & 255) * alpha / 255) << 8) | ((pixel & 255) * alpha / 255);
    }
    BITMAPINFO format{}; format.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    format.bmiHeader.biWidth = size; format.bmiHeader.biHeight = -size;
    format.bmiHeader.biPlanes = 1; format.bmiHeader.biBitCount = 32;
    void* bits = nullptr;
    const auto color = CreateDIBSection(nullptr, &format, DIB_RGB_COLORS, &bits, nullptr, 0);
    const auto mask = CreateBitmap(size, size, 1, 1, nullptr);
    HICON result = nullptr;
    if (color && mask)
    {
        memcpy(bits, pixels.data(), pixels.size() * sizeof(pixels[0]));
        ICONINFO info{}; info.fIcon = TRUE; info.hbmColor = color; info.hbmMask = mask;
        result = CreateIconIndirect(&info);
    }
    if (color) DeleteObject(color);
    if (mask) DeleteObject(mask);
    if (!result) return original;
    DestroyIcon(original);
    return result;
}

inline std::vector<std::uint32_t> ReadLucideIconPixels(int size)
{
    const auto icon = LoadLucideIcon(size);
    const auto pixels = ReadIconPixels(icon, size);
    if (icon) DestroyIcon(icon);
    return pixels;
}
