#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <string>

using Microsoft::WRL::ComPtr;

// 将已经完成的 PNG 帧编码为 H.264 MP4；输入帧不删除，输出使用调用方指定的临时路径。
// 参数：帧目录、临时 MP4、首帧编号、帧数、帧率。失败时返回非零并输出 HRESULT。
static HRESULT Encode(const wchar_t* directory, const wchar_t* output, int start, int count, int fps)
{
    ComPtr<IWICImagingFactory> imaging;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                 IID_PPV_ARGS(&imaging));
    if (FAILED(hr)) return hr;
    ComPtr<IMFSinkWriter> writer;
    DWORD stream = 0;
    UINT width = 0, height = 0;
    for (int index = 0; index < count; ++index)
    {
        wchar_t filename[64];
        swprintf_s(filename, L"帧-%06d.png", start + index);
        const std::wstring path = std::wstring(directory) + L"\\" + filename;
        ComPtr<IWICBitmapDecoder> decoder;
        hr = imaging->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ,
                                               WICDecodeMetadataCacheOnDemand, &decoder);
        if (FAILED(hr)) return hr;
        ComPtr<IWICBitmapFrameDecode> frame;
        if (FAILED(hr = decoder->GetFrame(0, &frame))) return hr;
        UINT frameWidth = 0, frameHeight = 0;
        if (FAILED(hr = frame->GetSize(&frameWidth, &frameHeight))) return hr;
        if (index == 0)
        {
            width = frameWidth; height = frameHeight;
            if (!width || !height || (width & 1) || (height & 1) ||
                static_cast<uint64_t>(width) * height * 4 > UINT32_MAX)
                return E_INVALIDARG;
            if (GetFileAttributesW(output) != INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
            if (FAILED(hr = MFCreateSinkWriterFromURL(output, nullptr, nullptr, &writer))) return hr;
            ComPtr<IMFMediaType> outType;
            if (FAILED(hr = MFCreateMediaType(&outType))) return hr;
            if (FAILED(hr = outType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video))) return hr;
            if (FAILED(hr = outType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264))) return hr;
            const uint64_t requestedBitrate = static_cast<uint64_t>(width) * height * fps / 5;
            const UINT32 bitrate = static_cast<UINT32>(std::clamp<uint64_t>(requestedBitrate, 1000000, 100000000));
            if (FAILED(hr = outType->SetUINT32(MF_MT_AVG_BITRATE, bitrate))) return hr;
            if (FAILED(hr = outType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive))) return hr;
            if (FAILED(hr = MFSetAttributeSize(outType.Get(), MF_MT_FRAME_SIZE, width, height))) return hr;
            if (FAILED(hr = MFSetAttributeRatio(outType.Get(), MF_MT_FRAME_RATE, fps, 1))) return hr;
            if (FAILED(hr = MFSetAttributeRatio(outType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1))) return hr;
            if (FAILED(hr = writer->AddStream(outType.Get(), &stream))) return hr;
            ComPtr<IMFMediaType> inType;
            if (FAILED(hr = MFCreateMediaType(&inType))) return hr;
            if (FAILED(hr = inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video))) return hr;
            if (FAILED(hr = inType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32))) return hr;
            // WIC 按从上到下的行顺序提供图像；显式声明正 stride，避免竖直翻转。
            if (FAILED(hr = inType->SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4))) return hr;
            if (FAILED(hr = inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive))) return hr;
            if (FAILED(hr = MFSetAttributeSize(inType.Get(), MF_MT_FRAME_SIZE, width, height))) return hr;
            if (FAILED(hr = MFSetAttributeRatio(inType.Get(), MF_MT_FRAME_RATE, fps, 1))) return hr;
            if (FAILED(hr = MFSetAttributeRatio(inType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1))) return hr;
            if (FAILED(hr = writer->SetInputMediaType(stream, inType.Get(), nullptr))) return hr;
            if (FAILED(hr = writer->BeginWriting())) return hr;
        }
        if (frameWidth != width || frameHeight != height) return E_INVALIDARG;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(hr = imaging->CreateFormatConverter(&converter))) return hr;
        if (FAILED(hr = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA,
                                              WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) return hr;
        const DWORD stride = width * 4, bytes = stride * height;
        ComPtr<IMFMediaBuffer> buffer;
        if (FAILED(hr = MFCreateMemoryBuffer(bytes, &buffer))) return hr;
        BYTE* pixels = nullptr;
        if (FAILED(hr = buffer->Lock(&pixels, nullptr, nullptr))) return hr;
        hr = converter->CopyPixels(nullptr, stride, bytes, pixels);
        const HRESULT unlock = buffer->Unlock();
        if (FAILED(hr)) return hr;
        if (FAILED(unlock)) return unlock;
        if (FAILED(hr = buffer->SetCurrentLength(bytes))) return hr;
        ComPtr<IMFSample> sample;
        if (FAILED(hr = MFCreateSample(&sample))) return hr;
        if (FAILED(hr = sample->AddBuffer(buffer.Get()))) return hr;
        const LONGLONG time = static_cast<LONGLONG>(index) * 10000000 / fps;
        const LONGLONG next = static_cast<LONGLONG>(index + 1) * 10000000 / fps;
        if (FAILED(hr = sample->SetSampleTime(time))) return hr;
        if (FAILED(hr = sample->SetSampleDuration(next - time))) return hr;
        if (FAILED(hr = writer->WriteSample(stream, sample.Get()))) return hr;
    }
    return writer ? writer->Finalize() : E_INVALIDARG;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 6) return 2;
    wchar_t* end = nullptr;
    const long start = wcstol(argv[3], &end, 10); if (*end || start < 0) return 2;
    const long count = wcstol(argv[4], &end, 10); if (*end || count < 1) return 2;
    const long fps = wcstol(argv[5], &end, 10); if (*end || fps < 1 || fps > 240) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION);
    if (SUCCEEDED(hr))
    {
        hr = Encode(argv[1], argv[2], start, count, fps);
        MFShutdown();
    }
    CoUninitialize();
    if (FAILED(hr)) { fwprintf(stderr, L"Media Foundation 错误 0x%08lX\n", static_cast<unsigned long>(hr)); return 1; }
    return 0;
}
