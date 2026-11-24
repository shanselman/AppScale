// Win32Harness — client-area capture to PNG via PrintWindow + WIC.
#include "Harness.h"

#include <wincodec.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

HRESULT SaveClientAreaPng(HWND hwnd, const std::wstring& path, UINT* width, UINT* height) {
    RECT cr{};
    GetClientRect(hwnd, &cr);
    const int w = cr.right, h = cr.bottom;
    if (w <= 0 || h <= 0) return E_UNEXPECTED;
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;  // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC screen = GetDC(nullptr);
    HBITMAP dib = CreateDIBSection(screen, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    HDC mem = CreateCompatibleDC(screen);
    ReleaseDC(nullptr, screen);
    if (!dib || !mem) return E_OUTOFMEMORY;
    HGDIOBJ old = SelectObject(mem, dib);
    BOOL printed = PrintWindow(hwnd, mem, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
    GdiFlush();
    HRESULT hr = printed ? S_OK : HRESULT_FROM_WIN32(GetLastError());

    if (SUCCEEDED(hr)) {
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapEncoder> encoder;
        ComPtr<IWICBitmapFrameEncode> frame;
        hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        if (SUCCEEDED(hr)) hr = factory->CreateStream(&stream);
        if (SUCCEEDED(hr)) hr = stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE);
        if (SUCCEEDED(hr)) hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
        if (SUCCEEDED(hr)) hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
        if (SUCCEEDED(hr)) hr = encoder->CreateNewFrame(&frame, nullptr);
        if (SUCCEEDED(hr)) hr = frame->Initialize(nullptr);
        if (SUCCEEDED(hr)) hr = frame->SetSize(static_cast<UINT>(w), static_cast<UINT>(h));
        // PrintWindow leaves the alpha byte undefined; PNG gets opaque BGRA.
        for (int i = 0; i < w * h; ++i) static_cast<BYTE*>(bits)[i * 4 + 3] = 0xFF;
        WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppBGRA;
        if (SUCCEEDED(hr)) hr = frame->SetPixelFormat(&fmt);
        if (SUCCEEDED(hr) && fmt != GUID_WICPixelFormat32bppBGRA) hr = E_UNEXPECTED;
        if (SUCCEEDED(hr))
            hr = frame->WritePixels(static_cast<UINT>(h), static_cast<UINT>(w) * 4, static_cast<UINT>(w * h * 4), static_cast<BYTE*>(bits));
        if (SUCCEEDED(hr)) hr = frame->Commit();
        if (SUCCEEDED(hr)) hr = encoder->Commit();
    }
    SelectObject(mem, old);
    DeleteDC(mem);
    DeleteObject(dib);
    if (width) *width = static_cast<UINT>(w);
    if (height) *height = static_cast<UINT>(h);
    return hr;
}
