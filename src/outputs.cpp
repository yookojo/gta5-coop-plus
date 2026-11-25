// Lists DXGI adapters/outputs in the order GTA's AdapterIndex/OutputIndex settings use.
#include <dxgi.h>
#include <cstdio>
#pragma comment(lib, "dxgi.lib")
int main() {
    IDXGIFactory1* f = nullptr;
    if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**)&f))) return 1;
    IDXGIAdapter1* a = nullptr;
    for (UINT i = 0; f->EnumAdapters1(i, &a) != DXGI_ERROR_NOT_FOUND; ++i) {
        DXGI_ADAPTER_DESC1 ad; a->GetDesc1(&ad);
        wprintf(L"adapter %u: %ls\n", i, ad.Description);
        IDXGIOutput* o = nullptr;
        for (UINT j = 0; a->EnumOutputs(j, &o) != DXGI_ERROR_NOT_FOUND; ++j) {
            DXGI_OUTPUT_DESC od; o->GetDesc(&od);
            wprintf(L"  output %u: %ls  %ld,%ld %ldx%ld\n", j, od.DeviceName, od.DesktopCoordinates.left, od.DesktopCoordinates.top,
                    od.DesktopCoordinates.right - od.DesktopCoordinates.left, od.DesktopCoordinates.bottom - od.DesktopCoordinates.top);
            o->Release();
        }
        a->Release();
    }
    f->Release();
    return 0;
}
