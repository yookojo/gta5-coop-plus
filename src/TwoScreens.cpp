// TwoScreens.asi - drop-in replacement for OfflineCoopMod.asi (Offline Coop by AlexDevilish).
//
// The co-op script (scripts\OfflineCoop.dll) alternates the game camera between Player 1 and
// Player 2 every frame and reports which one it used through the shared memory block
// "GTASplitScreenCoop" { int enabled; int cam; int heartbeat; }.  The original ASI pasted the
// middle half of each player's frame side by side into one picture.  This one instead:
//   * Player 1 frames -> the game's own window (main monitor, full size, full refresh rate)
//   * Player 2 frames -> a borderless window on a second screen (the TV), straight GPU copy
// When co-op split is off, the TV window is hidden and the game behaves normally.
//
// Settings (optional) in TwoScreens.ini next to GTA5.exe:
//   [TwoScreens]
//   Monitor=auto          ; or a device name like \\.\DISPLAY3
//   MirrorWhenNotSplit=0  ; 1 = TV shows the normal game view when split-screen is off
//   SwapPlayers=0         ; 1 = if Player 1/2 come out on the wrong screens

#include <windows.h>
#include <tlhelp32.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_5.h>
#include <deque>
#include <mutex>
#include <string>
#include <cstdio>
#include <cstdarg>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "gdi32.lib")

// ---- Script Hook V (import lib built from ScriptHookV.def) ----
__declspec(dllimport) void scriptRegister(HMODULE module, void (*LP_SCRIPT_MAIN)());
__declspec(dllimport) void scriptUnregister(HMODULE module);
__declspec(dllimport) void scriptWait(DWORD time);
__declspec(dllimport) void presentCallbackRegister(void (*cb)(void*));
__declspec(dllimport) void presentCallbackUnregister(void (*cb)(void*));

static HWND g_tvWnd;   // Player 2's window
static volatile HMONITOR g_gameMon;   // monitor the game is on (from its swap chain's output)
static float g_sharp = 0.5f; static bool g_sharpUpscale = true;   // TV upscaler settings (TwoScreens.ini)
static ID3D11Buffer* g_upCb;   // TV upscaler constants (source/target sizes)
static HANDLE g_tvWait;   // signalled when the TV swap chain has a buffer free (never wait on the game's time)
struct SplitShm { volatile LONG enabled; volatile LONG cam; volatile LONG beat; volatile LONG layout; };   // layout: 0 two screens, 1 side by side, 2 top/bottom, 3 side by side (whole view)

static HMODULE   g_self;
static HANDLE    g_map;
static SplitShm* g_shm;
static std::mutex       g_qLock;
static std::deque<int>  g_queue;      // camera tag per game frame, oldest first
static wchar_t   g_dir[MAX_PATH];

// settings
static wchar_t g_monitorName[64] = L"auto";
static bool    g_mirror = false;
static bool    g_swap   = false;

// D3D state (render thread only)
static ID3D11Device*        g_dev;
static ID3D11DeviceContext* g_ctx;
static IDXGISwapChain1*     g_tvChain;
static ID3D11Texture2D*     g_lastP1;
static D3D11_TEXTURE2D_DESC g_desc;
static bool  g_haveP1;
static bool  g_failed;
static LONG  g_presentBeat = -1;
static DWORD g_presentBeatTick;
static bool  g_tvVisible;
static unsigned long long g_p1Frames, g_p2Frames, g_tvDrops;

// TV window (own thread)
static HANDLE g_tvReady;
static RECT   g_tvRect;
#define WM_TV_SHOW (WM_APP + 1)
#define WM_TV_HIDE (WM_APP + 2)

static void Log(const char* fmt, ...) {
    wchar_t path[MAX_PATH]; swprintf_s(path, L"%sTwoScreens.log", g_dir);
    FILE* f = nullptr; if (_wfopen_s(&f, path, L"a") != 0 || !f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "[%02d:%02d:%02d] ", t.wHour, t.wMinute, t.wSecond);
    va_list a; va_start(a, fmt); vfprintf(f, fmt, a); va_end(a);
    fputc('\n', f); fclose(f);
}

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

// ---------------------------------------------------------------- shared memory + frame queue
static void OpenShm() {
    g_map = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SplitShm), "GTASplitScreenCoop");
    bool existed = GetLastError() == ERROR_ALREADY_EXISTS;
    if (!g_map) { Log("ERROR CreateFileMapping %lu", GetLastError()); return; }
    g_shm = (SplitShm*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SplitShm));
    if (!g_shm) { Log("ERROR MapViewOfFile %lu", GetLastError()); return; }
    if (!existed) { g_shm->enabled = 0; g_shm->cam = 1; g_shm->beat = 0; }
    Log("shared memory ready (%s)", existed ? "opened" : "created");
}

// Runs once per game frame on a Script Hook fiber: record which camera this frame used.
static void ScriptMain() {
    LONG last = -1;
    for (;;) {
        if (g_shm && g_shm->enabled == 1) {
            LONG b = g_shm->beat;
            if (b != last) {
                last = b;
                std::lock_guard<std::mutex> l(g_qLock);
                g_queue.push_back(g_shm->cam);
                while (g_queue.size() >= 4) g_queue.pop_front();
            }
        } else {
            std::lock_guard<std::mutex> l(g_qLock);
            g_queue.clear();
        }
        scriptWait(0);
    }
}

// ---------------------------------------------------------------- TV window
struct MonPick { RECT best; long long bestArea; bool found; const wchar_t* want; HMONITOR gameMon; };
// the game's own window (to keep Player 2 off whichever screen the game is on)
static BOOL CALLBACK FindGameWnd(HWND h, LPARAM p) {
    DWORD pid = 0; GetWindowThreadProcessId(h, &pid);
    if (pid != GetCurrentProcessId() || h == g_tvWnd || !IsWindowVisible(h) || GetWindow(h, GW_OWNER)) return TRUE;
    RECT r; GetWindowRect(h, &r); if (r.right - r.left < 640) return TRUE;
    *(HWND*)p = h; return FALSE;
}
static BOOL CALLBACK MonEnum(HMONITOR m, HDC, LPRECT, LPARAM p) {
    MonPick* k = (MonPick*)p;
    MONITORINFOEXW mi = {}; mi.cbSize = sizeof(mi); GetMonitorInfoW(m, &mi);
    long long area = (long long)(mi.rcMonitor.right - mi.rcMonitor.left) * (mi.rcMonitor.bottom - mi.rcMonitor.top);
    if (_wcsicmp(k->want, L"auto") == 0) {
        bool gameHere = k->gameMon ? (m == k->gameMon) : (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
        if (!gameHere && area > k->bestArea) { k->best = mi.rcMonitor; k->bestArea = area; k->found = true; }
    } else if (_wcsicmp(k->want, mi.szDevice) == 0) { k->best = mi.rcMonitor; k->bestArea = area; k->found = true; }
    return TRUE;
}

// choose the second screen again (start-up and whenever screens change)
static volatile bool g_tvMoved, g_noSecondScreen;
static bool PickTvMonitor() {
    MonPick k = {}; k.want = g_monitorName;
    k.gameMon = g_gameMon;   // the monitor the game's own swap chain is showing on (set from the render thread)
    EnumDisplayMonitors(nullptr, nullptr, MonEnum, (LPARAM)&k);
    if (!k.found) { g_noSecondScreen = true; Log("no second screen right now"); return false; }
    bool changed = memcmp(&k.best, &g_tvRect, sizeof(RECT)) != 0;
    g_noSecondScreen = false;
    if (changed) {
        g_tvRect = k.best; g_tvMoved = true;
        Log("Player 2's screen: %ldx%ld at %ld,%ld (the game is on another screen)", g_tvRect.right - g_tvRect.left, g_tvRect.bottom - g_tvRect.top, g_tvRect.left, g_tvRect.top);
        if (g_tvWnd && IsWindowVisible(g_tvWnd))
            SetWindowPos(g_tvWnd, HWND_TOPMOST, g_tvRect.left, g_tvRect.top, g_tvRect.right - g_tvRect.left, g_tvRect.bottom - g_tvRect.top, SWP_NOACTIVATE);
    }
    return true;
}

static LRESULT CALLBACK TvProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    switch (msg) {
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;          // clicking the TV never steals focus from GTA
    case WM_SETCURSOR:     SetCursor(nullptr); return TRUE;
    case WM_TV_SHOW:       SetWindowPos(h, HWND_TOPMOST, g_tvRect.left, g_tvRect.top, g_tvRect.right - g_tvRect.left,
                                        g_tvRect.bottom - g_tvRect.top, SWP_NOACTIVATE | SWP_SHOWWINDOW); return 0;
    case WM_TV_HIDE:       ShowWindow(h, SW_HIDE); return 0;
    case WM_CLOSE:         return 0;                       // only GTA closing ends it
    case WM_ERASEBKGND:    return 1;
    case WM_DISPLAYCHANGE: PickTvMonitor(); return 0;   // a screen was plugged in or removed
    }
    return DefWindowProcW(h, msg, w, l);
}

static DWORD WINAPI TvThread(LPVOID) {
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);   // real pixels on the 300% TV
    if (!PickTvMonitor()) { RECT r = { 0, 0, 1920, 1080 }; g_tvRect = r; }   // keep going: a screen may be plugged in later
    Log("TV screen: %ldx%ld at %ld,%ld", g_tvRect.right - g_tvRect.left, g_tvRect.bottom - g_tvRect.top, g_tvRect.left, g_tvRect.top);

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = TvProc; wc.hInstance = g_self; wc.lpszClassName = L"GTATwoScreensTV";
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    RegisterClassExW(&wc);
    g_tvWnd = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, wc.lpszClassName, L"GTA V - Player 2",
                              WS_POPUP, g_tvRect.left, g_tvRect.top, g_tvRect.right - g_tvRect.left, g_tvRect.bottom - g_tvRect.top,
                              nullptr, nullptr, g_self, nullptr);
    if (!g_tvWnd) Log("ERROR CreateWindow %lu", GetLastError());
    SetEvent(g_tvReady);
    SetTimer(g_tvWnd, 1, 3000, nullptr);
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) {
        if (m.message == WM_TIMER && m.hwnd == g_tvWnd) {
            PickTvMonitor();
            RECT wr; GetWindowRect(g_tvWnd, &wr);   // put the window back if anything moved it
            if (IsWindowVisible(g_tvWnd) && memcmp(&wr, &g_tvRect, sizeof(RECT)) != 0)
                SetWindowPos(g_tvWnd, HWND_TOPMOST, g_tvRect.left, g_tvRect.top, g_tvRect.right - g_tvRect.left, g_tvRect.bottom - g_tvRect.top, SWP_NOACTIVATE);
        }
        TranslateMessage(&m); DispatchMessageW(&m);
    }
    return 0;
}

static void ShowTv(bool show) {
    if (!g_tvWnd || show == g_tvVisible) return;
    g_tvVisible = show;
    PostMessageW(g_tvWnd, show ? WM_TV_SHOW : WM_TV_HIDE, 0, 0);
    Log(show ? "TV on" : "TV off");
}

// ---------------------------------------------------------------- D3D
static DXGI_FORMAT NoSrgb(DXGI_FORMAT f) {
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
    default: return f;
    }
}

static void DropResources() { SafeRelease(g_tvChain); SafeRelease(g_lastP1); g_haveP1 = false; }

static bool EnsureResources(const D3D11_TEXTURE2D_DESC& d) {
    if (g_tvMoved) { g_tvMoved = false; SafeRelease(g_tvChain); SafeRelease(g_upCb); if (g_tvWait) { CloseHandle(g_tvWait); g_tvWait = nullptr; } }
    if (g_tvChain && g_lastP1 && d.Width == g_desc.Width && d.Height == g_desc.Height && d.Format == g_desc.Format) return true;
    DropResources();
    g_desc = d;
    if (!g_tvWnd) return false;

    D3D11_TEXTURE2D_DESC t = d;
    t.BindFlags = 0; t.MiscFlags = 0; t.CPUAccessFlags = 0; t.Usage = D3D11_USAGE_DEFAULT; t.MipLevels = 1; t.ArraySize = 1;
    HRESULT hr = g_dev->CreateTexture2D(&t, nullptr, &g_lastP1);
    if (FAILED(hr)) { Log("ERROR CreateTexture2D 0x%08lX", hr); return false; }

    IDXGIDevice* dxgiDev = nullptr; IDXGIAdapter* adapter = nullptr; IDXGIFactory2* factory = nullptr;
    hr = g_dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDev);
    if (SUCCEEDED(hr)) hr = dxgiDev->GetAdapter(&adapter);
    if (SUCCEEDED(hr)) hr = adapter->GetParent(__uuidof(IDXGIFactory2), (void**)&factory);
    if (SUCCEEDED(hr)) {
        DXGI_SWAP_CHAIN_DESC1 sd = {};
        UINT tvW = (UINT)(g_tvRect.right - g_tvRect.left), tvH = (UINT)(g_tvRect.bottom - g_tvRect.top);
        bool native = g_sharpUpscale && tvW > d.Width && tvH > d.Height;
        sd.Width = native ? tvW : d.Width; sd.Height = native ? tvH : d.Height; sd.Format = NoSrgb(d.Format);
        sd.SampleDesc.Count = 1; sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount = 3;
        sd.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;   // lets us check for a free buffer without waiting
        sd.Scaling = DXGI_SCALING_STRETCH; sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD; sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        hr = factory->CreateSwapChainForHwnd(g_dev, g_tvWnd, &sd, nullptr, nullptr, &g_tvChain);
        if (SUCCEEDED(hr)) {
            factory->MakeWindowAssociation(g_tvWnd, DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);
            IDXGISwapChain2* c2 = nullptr;
            if (SUCCEEDED(g_tvChain->QueryInterface(__uuidof(IDXGISwapChain2), (void**)&c2))) { c2->SetMaximumFrameLatency(1); g_tvWait = c2->GetFrameLatencyWaitableObject(); c2->Release(); }
        }
    }
    SafeRelease(factory); SafeRelease(adapter); SafeRelease(dxgiDev);
    if (FAILED(hr)) { Log("ERROR TV swap chain 0x%08lX (format %d)", hr, (int)d.Format); DropResources(); return false; }
    Log("TV output ready: game picture %ux%u format %d samples %u", d.Width, d.Height, (int)d.Format, d.SampleDesc.Count);
    return true;
}

// ---------------------------------------------------------------- GPU routing by frame tag
static const char* kShader = R"(
Texture2D<float4> Src : register(t0);
Texture2D<float4> Alt : register(t1);
Texture2D<float4> P2t : register(t2);
SamplerState Lin : register(s0);
cbuffer C : register(b0) { int Mode; int Layout; int2 Pad; };
float4 VS(uint id : SV_VertexID) : SV_Position {
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
int Tag() {
    float3 c = Src.Load(int3(2, 2, 0)).rgb;
    if (c.r > 0.35 && c.b > 0.35 && c.g < 0.12 && abs(c.r - c.b) < 0.15) return 2;   // magenta (even if darkened) = Player 2's camera
    if (c.g > 0.35 && c.r < 0.12 && c.b < 0.12) return 1;                           // green = Player 1's camera
    return 0;
}
float4 PS(float4 pos : SV_Position) : SV_Target {
    int t = Tag();
    int2 p = int2(pos.xy);
    int2 q = int2(p.x < 6 ? p.x + 6 : p.x, p.y < 6 ? p.y + 6 : p.y);   // paint over the tag
    if (Mode == 1) { if (t == 2) discard; return Src.Load(int3(q, 0)); }        // remember Player 1's picture
    if (Mode == 2) { if (t != 2) discard; return Src.Load(int3(q, 0)); }        // Player 2's picture -> TV
    if (Layout == 0) {
        if (t == 2) return Alt.Load(int3(p, 0));                                 // main screen keeps Player 1
        return Src.Load(int3(q, 0));
    }
    // one screen for both players
    uint w, h; Src.GetDimensions(w, h); int hw = (int)w / 2, hh = (int)h / 2;
    if (Layout == 1) {                    // side by side, the middle of each view
        if (abs(p.x - hw) < 2) return float4(0, 0, 0, 1);
        return p.x < hw ? Alt.Load(int3(p.x + hw / 2, p.y, 0)) : P2t.Load(int3(p.x - hw / 2, p.y, 0));
    }
    if (Layout == 2) {                    // top and bottom, the middle of each view
        if (abs(p.y - hh) < 2) return float4(0, 0, 0, 1);
        return p.y < hh ? Alt.Load(int3(p.x, p.y + hh / 2, 0)) : P2t.Load(int3(p.x, p.y - hh / 2, 0));
    }
    // Layout 3: side by side, each whole view scaled down (nothing cut off, black bars above/below)
    bool right = p.x >= hw;
    float2 uv = float2((p.x - (right ? hw : 0) + 0.5) / hw, (p.y - h * 0.25 + 0.5) / hh);
    if (uv.y < 0 || uv.y > 1 || abs(p.x - hw) < 2) return float4(0, 0, 0, 1);
    return right ? P2t.SampleLevel(Lin, uv, 0) : Alt.SampleLevel(Lin, uv, 0);
}

// ---- TV upscale: Catmull-Rom bicubic (9 bilinear taps), clamped to the local range (no halos),
// then contrast-adaptive sharpening (after AMD FidelityFX CAS).
Texture2D<float4> Up : register(t3);
cbuffer U : register(b1) { float2 SrcSize; float2 DstSize; float Sharp; float3 Pad2; };
float3 Tap(float2 uv) { return Up.SampleLevel(Lin, uv, 0).rgb; }
float3 CatRom(float2 uv) {
    float2 p = uv * SrcSize, t1 = floor(p - 0.5) + 0.5, f = p - t1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f)), w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f)), w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2, t0 = (t1 - 1) / SrcSize, t3 = (t1 + 2) / SrcSize, t12 = (t1 + w2 / w12) / SrcSize;
    float3 r = Tap(float2(t0.x, t0.y)) * w0.x * w0.y + Tap(float2(t12.x, t0.y)) * w12.x * w0.y + Tap(float2(t3.x, t0.y)) * w3.x * w0.y
             + Tap(float2(t0.x, t12.y)) * w0.x * w12.y + Tap(float2(t12.x, t12.y)) * w12.x * w12.y + Tap(float2(t3.x, t12.y)) * w3.x * w12.y
             + Tap(float2(t0.x, t3.y)) * w0.x * w3.y + Tap(float2(t12.x, t3.y)) * w12.x * w3.y + Tap(float2(t3.x, t3.y)) * w3.x * w3.y;
    return r;
}
float4 PSUp(float4 pos : SV_Position) : SV_Target {
    float2 uv = pos.xy / DstSize, px = 1.0 / SrcSize;
    float3 c = CatRom(uv);
    float3 n = Tap(uv + float2(0, -px.y)), s = Tap(uv + float2(0, px.y)), e = Tap(uv + float2(px.x, 0)), w = Tap(uv + float2(-px.x, 0));
    float3 mn = min(min(min(n, s), min(e, w)), Tap(uv)), mx = max(max(max(n, s), max(e, w)), Tap(uv));
    c = clamp(c, mn, mx);
    float3 amp = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 1e-4)));
    float3 wgt = -amp * lerp(0.125, 0.2, Sharp);
    return float4(saturate((c + (n + s + e + w) * wgt) / (1.0 + 4.0 * wgt)), 1);
}
)";

static bool g_gpuRoute = true;
static ID3D11VertexShader* g_vs; static ID3D11PixelShader* g_ps, *g_psUp;
static ID3D11Buffer* g_cb[3][4]; static ID3D11SamplerState* g_samp; static ID3D11ShaderResourceView* g_p2SRV;
static ID3D11Texture2D* g_src; static ID3D11ShaderResourceView *g_srcSRV, *g_altSRV; static ID3D11RenderTargetView* g_altRTV;
static ID3D11Texture2D* g_lastP2; static ID3D11RenderTargetView* g_p2RTV;   // newest Player 2 picture, shown on every TV frame
static ID3D11RasterizerState* g_rs; static ID3D11BlendState* g_bs; static ID3D11DepthStencilState* g_ds;
static UINT g_routeW, g_routeH; static DXGI_FORMAT g_routeFmt;

typedef HRESULT(WINAPI* D3DCompileFn)(LPCVOID, SIZE_T, LPCSTR, const void*, void*, LPCSTR, LPCSTR, UINT, UINT, ID3DBlob**, ID3DBlob**);

static bool InitShaders() {
    HMODULE dc = LoadLibraryW(L"d3dcompiler_47.dll");
    D3DCompileFn compile = dc ? (D3DCompileFn)GetProcAddress(dc, "D3DCompile") : nullptr;
    if (!compile) { Log("no shader compiler - using frame counting"); return false; }
    ID3DBlob *vsb = nullptr, *psb = nullptr, *err = nullptr;
    if (FAILED(compile(kShader, strlen(kShader), "route", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vsb, &err)) ||
        FAILED(compile(kShader, strlen(kShader), "route", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &psb, &err))) {
        Log("shader compile failed: %s", err ? (const char*)err->GetBufferPointer() : "?"); SafeRelease(err); SafeRelease(vsb); return false;
    }
    bool ok = SUCCEEDED(g_dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &g_vs)) &&
              SUCCEEDED(g_dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &g_ps));
    vsb->Release(); psb->Release();
    ID3DBlob* upb = nullptr;
    if (ok && SUCCEEDED(compile(kShader, strlen(kShader), "route", nullptr, nullptr, "PSUp", "ps_5_0", (1 << 15) /* optimization level 3 */, 0, &upb, &err)))
    { g_dev->CreatePixelShader(upb->GetBufferPointer(), upb->GetBufferSize(), nullptr, &g_psUp); upb->Release(); }
    else { Log("TV upscaler unavailable: %s", err ? (const char*)err->GetBufferPointer() : "?"); SafeRelease(err); }
    for (int m = 0; m < 3 && ok; m++) for (int l = 0; l < 4 && ok; l++) {
        int data[4] = { m + 1, l, 0, 0 };
        D3D11_BUFFER_DESC bd = { 16, D3D11_USAGE_IMMUTABLE, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0 };
        D3D11_SUBRESOURCE_DATA sd = { data, 0, 0 };
        ok = SUCCEEDED(g_dev->CreateBuffer(&bd, &sd, &g_cb[m][l]));
    }
    D3D11_SAMPLER_DESC smp = {}; smp.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; smp.AddressU = smp.AddressV = smp.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP; smp.MaxLOD = D3D11_FLOAT32_MAX;
    ok = ok && SUCCEEDED(g_dev->CreateSamplerState(&smp, &g_samp));
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
    D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    D3D11_DEPTH_STENCIL_DESC dsd = {};
    ok = ok && SUCCEEDED(g_dev->CreateRasterizerState(&rd, &g_rs)) && SUCCEEDED(g_dev->CreateBlendState(&bld, &g_bs)) &&
         SUCCEEDED(g_dev->CreateDepthStencilState(&dsd, &g_ds));
    Log(ok ? "frame-tag routing ready" : "frame-tag routing setup failed");
    return ok;
}

static bool EnsureRouteTargets(const D3D11_TEXTURE2D_DESC& d) {
    if (g_src && g_routeW == d.Width && g_routeH == d.Height && g_routeFmt == d.Format) return true;
    SafeRelease(g_srcSRV); SafeRelease(g_src); SafeRelease(g_altSRV); SafeRelease(g_altRTV); SafeRelease(g_lastP1); SafeRelease(g_p2RTV); SafeRelease(g_p2SRV); SafeRelease(g_lastP2);
    D3D11_TEXTURE2D_DESC t = d;
    t.Usage = D3D11_USAGE_DEFAULT; t.CPUAccessFlags = 0; t.MiscFlags = 0; t.MipLevels = 1; t.ArraySize = 1;
    t.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(g_dev->CreateTexture2D(&t, nullptr, &g_src)) || FAILED(g_dev->CreateShaderResourceView(g_src, nullptr, &g_srcSRV))) return false;
    t.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    if (FAILED(g_dev->CreateTexture2D(&t, nullptr, &g_lastP1)) || FAILED(g_dev->CreateShaderResourceView(g_lastP1, nullptr, &g_altSRV)) ||
        FAILED(g_dev->CreateRenderTargetView(g_lastP1, nullptr, &g_altRTV))) return false;
    if (FAILED(g_dev->CreateTexture2D(&t, nullptr, &g_lastP2)) || FAILED(g_dev->CreateRenderTargetView(g_lastP2, nullptr, &g_p2RTV)) ||
        FAILED(g_dev->CreateShaderResourceView(g_lastP2, nullptr, &g_p2SRV))) return false;
    g_routeW = d.Width; g_routeH = d.Height; g_routeFmt = d.Format;
    return true;
}

// Saves and restores the pipeline state we touch, so the game never notices.
struct SavedState {
    ID3D11InputLayout* il = nullptr; D3D11_PRIMITIVE_TOPOLOGY topo; ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr;
    ID3D11GeometryShader* gs = nullptr; ID3D11HullShader* hs = nullptr; ID3D11DomainShader* ds = nullptr;
    ID3D11ShaderResourceView* srv[3] = {}; ID3D11SamplerState* smp = nullptr; ID3D11Buffer* cb = nullptr; ID3D11RasterizerState* rs = nullptr;
    D3D11_VIEWPORT vp[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]; UINT nvp = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    ID3D11BlendState* bs = nullptr; FLOAT bf[4]; UINT mask; ID3D11DepthStencilState* dss = nullptr; UINT sref;
    ID3D11RenderTargetView* rtv[8] = {}; ID3D11DepthStencilView* dsv = nullptr;
    void Save(ID3D11DeviceContext* c) {
        c->IAGetInputLayout(&il); c->IAGetPrimitiveTopology(&topo); c->VSGetShader(&vs, nullptr, nullptr); c->PSGetShader(&ps, nullptr, nullptr);
        c->GSGetShader(&gs, nullptr, nullptr); c->HSGetShader(&hs, nullptr, nullptr); c->DSGetShader(&ds, nullptr, nullptr);
        c->PSGetShaderResources(0, 3, srv); c->PSGetSamplers(0, 1, &smp); c->PSGetConstantBuffers(0, 1, &cb); c->RSGetState(&rs); c->RSGetViewports(&nvp, vp);
        c->OMGetBlendState(&bs, bf, &mask); c->OMGetDepthStencilState(&dss, &sref); c->OMGetRenderTargets(8, rtv, &dsv);
    }
    void Restore(ID3D11DeviceContext* c) {
        c->IASetInputLayout(il); c->IASetPrimitiveTopology(topo); c->VSSetShader(vs, nullptr, 0); c->PSSetShader(ps, nullptr, 0);
        c->GSSetShader(gs, nullptr, 0); c->HSSetShader(hs, nullptr, 0); c->DSSetShader(ds, nullptr, 0);
        c->PSSetShaderResources(0, 3, srv); c->PSSetSamplers(0, 1, &smp); c->PSSetConstantBuffers(0, 1, &cb); c->RSSetState(rs); c->RSSetViewports(nvp, vp);
        c->OMSetBlendState(bs, bf, mask); c->OMSetDepthStencilState(dss, sref); c->OMSetRenderTargets(8, rtv, dsv);
        SafeRelease(il); SafeRelease(vs); SafeRelease(ps); SafeRelease(gs); SafeRelease(hs); SafeRelease(ds); SafeRelease(srv[0]); SafeRelease(srv[1]); SafeRelease(srv[2]); SafeRelease(smp);
        SafeRelease(cb); SafeRelease(rs); SafeRelease(bs); SafeRelease(dss); for (auto& r : rtv) SafeRelease(r); SafeRelease(dsv);
    }
};

// Diagnostics: every few frames copy the tag pixel to a small CPU-readable texture and read it back a
// few frames later (never waits on the GPU). Logged as counts of magenta / green / other.
static ID3D11Texture2D* g_probe[4]; static int g_probeFrame, g_probeIdx; static bool g_probeUsed[4];
static unsigned g_seenP2, g_seenP1, g_seenOther; static unsigned char g_lastOther[4];
static void ProbeTag(const D3D11_TEXTURE2D_DESC& d) {
    if (++g_probeFrame % 7) return;
    if (!g_probe[0]) {
        D3D11_TEXTURE2D_DESC t = {}; t.Width = 1; t.Height = 1; t.MipLevels = 1; t.ArraySize = 1; t.Format = d.Format; t.SampleDesc.Count = 1;
        t.Usage = D3D11_USAGE_STAGING; t.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        for (auto& p : g_probe) if (FAILED(g_dev->CreateTexture2D(&t, nullptr, &p))) return;
    }
    int i = g_probeIdx; g_probeIdx = (g_probeIdx + 1) % 4;
    if (g_probeUsed[i]) {
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(g_ctx->Map(g_probe[i], 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m))) {
            unsigned char* px = (unsigned char*)m.pData;
            unsigned char r = px[0], g = px[1], b = px[2];
            if (d.Format == DXGI_FORMAT_B8G8R8A8_UNORM || d.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) { unsigned char t2 = r; r = b; b = t2; }
            if (r > 200 && g < 60 && b > 200) g_seenP2++; else if (r < 60 && g > 200 && b < 60) g_seenP1++;
            else { g_seenOther++; g_lastOther[0] = r; g_lastOther[1] = g; g_lastOther[2] = b; }
            g_ctx->Unmap(g_probe[i], 0);
        }
    }
    D3D11_BOX box = { 2, 2, 0, 3, 3, 1 };
    g_ctx->CopySubresourceRegion(g_probe[i], 0, 0, 0, 0, g_src, 0, &box);
    g_probeUsed[i] = true;
}

static bool g_singleScreen;
static bool GpuRoute(ID3D11Texture2D* bb, const D3D11_TEXTURE2D_DESC& d) {
    static int state = 0;   // 0 untried, 1 ready, -1 unavailable
    if (state == 0) state = InitShaders() ? 1 : -1;
    if (state < 0) { g_gpuRoute = false; return false; }
    if (!EnsureRouteTargets(d)) { Log("frame-tag routing: could not create textures"); g_gpuRoute = false; return false; }

    ID3D11Texture2D* tvbb = nullptr; ID3D11RenderTargetView *bbRTV = nullptr;
    if (FAILED(g_tvChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&tvbb)) || FAILED(g_dev->CreateRenderTargetView(bb, nullptr, &bbRTV))) {
        SafeRelease(tvbb); SafeRelease(bbRTV);
        Log("frame-tag routing: could not draw to the screens - using frame counting"); g_gpuRoute = false; return false;
    }

    SavedState s; s.Save(g_ctx);
    g_ctx->CopyResource(g_src, bb);
    ProbeTag(d);
    D3D11_VIEWPORT vp = { 0, 0, (float)d.Width, (float)d.Height, 0, 1 };
    g_ctx->IASetInputLayout(nullptr); g_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g_ctx->VSSetShader(g_vs, nullptr, 0); g_ctx->PSSetShader(g_ps, nullptr, 0);
    g_ctx->GSSetShader(nullptr, nullptr, 0); g_ctx->HSSetShader(nullptr, nullptr, 0); g_ctx->DSSetShader(nullptr, nullptr, 0);
    g_ctx->RSSetState(g_rs); g_ctx->RSSetViewports(1, &vp); g_ctx->OMSetBlendState(g_bs, nullptr, 0xFFFFFFFF); g_ctx->OMSetDepthStencilState(g_ds, 0);

    ID3D11ShaderResourceView* none[3] = {};
    int lay = g_shm ? (int)g_shm->layout : 0; if (lay < 0 || lay > 3) lay = 0;
    // 1) remember the latest Player 1 picture
    g_ctx->OMSetRenderTargets(1, &g_altRTV, nullptr);
    g_ctx->PSSetShaderResources(0, 1, &g_srcSRV); g_ctx->PSSetConstantBuffers(0, 1, &g_cb[0][lay]);
    g_ctx->Draw(3, 0);
    // 2) Player 2 pictures go to the TV
    g_ctx->OMSetRenderTargets(1, &g_p2RTV, nullptr); g_ctx->PSSetConstantBuffers(0, 1, &g_cb[1][lay]);
    g_ctx->Draw(3, 0);
    // 3) the main screen always shows Player 1
    g_ctx->OMSetRenderTargets(1, &bbRTV, nullptr);
    ID3D11ShaderResourceView* all3[3] = { g_srcSRV, g_altSRV, g_p2SRV };
    g_ctx->PSSetShaderResources(0, 3, all3); g_ctx->PSSetSamplers(0, 1, &g_samp); g_ctx->PSSetConstantBuffers(0, 1, &g_cb[2][lay]);
    g_ctx->Draw(3, 0);
    g_ctx->PSSetShaderResources(0, 3, none);
    g_singleScreen = lay != 0;
    bool upscaled = false;
    // only touch the TV when it has a free buffer; otherwise skip this TV update instead of stalling the game
    bool tvFree = !g_singleScreen && (!g_tvWait || WaitForSingleObject(g_tvWait, 0) == WAIT_OBJECT_0);
    if (!g_singleScreen && !tvFree) g_tvDrops++;
    if (tvFree) {
        D3D11_TEXTURE2D_DESC td; tvbb->GetDesc(&td);
        if (td.Width != d.Width || td.Height != d.Height) {
            if (g_psUp) {
                if (!g_upCb) {
                    float cbd[8] = { (float)d.Width, (float)d.Height, (float)td.Width, (float)td.Height, g_sharp, 0, 0, 0 };
                    D3D11_BUFFER_DESC bd = { 32, D3D11_USAGE_IMMUTABLE, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0 }; D3D11_SUBRESOURCE_DATA sdat = { cbd, 0, 0 };
                    g_dev->CreateBuffer(&bd, &sdat, &g_upCb);
                }
                ID3D11RenderTargetView* tvRTV = nullptr;
                if (g_upCb && SUCCEEDED(g_dev->CreateRenderTargetView(tvbb, nullptr, &tvRTV))) {
                    D3D11_VIEWPORT tvp = { 0, 0, (float)td.Width, (float)td.Height, 0, 1 };
                    g_ctx->RSSetViewports(1, &tvp); g_ctx->OMSetRenderTargets(1, &tvRTV, nullptr);
                    g_ctx->PSSetShader(g_psUp, nullptr, 0); g_ctx->PSSetShaderResources(3, 1, &g_p2SRV); g_ctx->PSSetConstantBuffers(1, 1, &g_upCb);
                    g_ctx->PSSetSamplers(0, 1, &g_samp); g_ctx->Draw(3, 0);
                    ID3D11ShaderResourceView* n1 = nullptr; g_ctx->PSSetShaderResources(3, 1, &n1);
                    tvRTV->Release(); upscaled = true;
                }
            }
        }
    }
    s.Restore(g_ctx);

    bbRTV->Release();
    if (tvFree) {
        if (!upscaled) g_ctx->CopyResource(tvbb, g_lastP2);    // TV always shows the newest Player 2 picture
        HRESULT hr = g_tvChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING) g_tvDrops++;
    }
    tvbb->Release();
    g_p1Frames++;
    return true;
}

// ---------------------------------------------------------------- hide Player 2's controller from the game
// CoopPlus writes "GTAXInputBlock" {magic 'KLBX', version 2, slot, heartbeat, sequence}. While the
// heartbeat keeps changing, the game is told that slot has no controller (so only CoopPlus reads it).
// Done by redirecting the game's own XInput imports (import address table), no code patching.
struct BlockShm { volatile LONG magic, ver, slot, beat, seq; };
static BlockShm* g_block;
typedef DWORD(WINAPI* XGetStateFn)(DWORD, void*);
typedef DWORD(WINAPI* XGetCapsFn)(DWORD, DWORD, void*);
static XGetStateFn o_getState, o_getStateEx, o_setState; static XGetCapsFn o_getCaps;
static volatile LONG g_blockSeenBeat = -1; static volatile DWORD g_blockSeenTick; static volatile LONG g_blockedReads;

static int BlockedSlot() {
    if (!g_block || g_block->magic != 0x58424C4B) return -1;
    LONG b = g_block->beat; DWORD now = GetTickCount();
    if (b != g_blockSeenBeat) { g_blockSeenBeat = b; g_blockSeenTick = now; }
    if (now - g_blockSeenTick > 1000) return -1;          // CoopPlus stopped (or crashed): give the pad back
    return g_block->slot;
}
static DWORD WINAPI H_GetState(DWORD i, void* s)   { if ((int)i == BlockedSlot()) { InterlockedIncrement(&g_blockedReads); return ERROR_DEVICE_NOT_CONNECTED; } return o_getState(i, s); }
static DWORD WINAPI H_GetStateEx(DWORD i, void* s) { if ((int)i == BlockedSlot()) { InterlockedIncrement(&g_blockedReads); return ERROR_DEVICE_NOT_CONNECTED; } return o_getStateEx(i, s); }
static DWORD WINAPI H_SetState(DWORD i, void* v) { if ((int)i == BlockedSlot()) return ERROR_DEVICE_NOT_CONNECTED; return o_setState(i, v); }
static DWORD WINAPI H_GetCaps(DWORD i, DWORD f, void* c) { if ((int)i == BlockedSlot()) return ERROR_DEVICE_NOT_CONNECTED; return o_getCaps(i, f, c); }

static void PatchSlot(ULONGLONG* slot, void* mine, void** orig) {
    DWORD old;
    if (!VirtualProtect(slot, sizeof(*slot), PAGE_READWRITE, &old)) return;
    *orig = (void*)*slot; *slot = (ULONGLONG)mine;
    VirtualProtect(slot, sizeof(*slot), old, &old);
}

static void PatchXInputExports();
static void InstallXInputBlock() {
    HANDLE map = CreateFileMappingA(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(BlockShm), "GTAXInputBlock");
    if (map) g_block = (BlockShm*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(BlockShm));
    BYTE* base = (BYTE*)GetModuleHandleW(nullptr);
    auto dos = (IMAGE_DOS_HEADER*)base; auto nt = (IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
    auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    int patched = 0;
    for (auto imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); dir.VirtualAddress && imp->Name; imp++) {
        const char* dll = (const char*)(base + imp->Name);
        if (_strnicmp(dll, "xinput", 6) != 0) continue;
        auto names = (IMAGE_THUNK_DATA64*)(base + (imp->OriginalFirstThunk ? imp->OriginalFirstThunk : imp->FirstThunk));
        auto iat = (IMAGE_THUNK_DATA64*)(base + imp->FirstThunk);
        for (; names->u1.AddressOfData; names++, iat++) {
            bool byOrdinal = IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal);
            WORD ord = byOrdinal ? (WORD)IMAGE_ORDINAL64(names->u1.Ordinal) : 0;
            const char* fn = byOrdinal ? "" : (const char*)((IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData))->Name;
            if ((!strcmp(fn, "XInputGetState") || ord == 2) && !o_getState) { PatchSlot(&iat->u1.Function, (void*)H_GetState, (void**)&o_getState); patched++; }
            else if (ord == 100 && !o_getStateEx)              { PatchSlot(&iat->u1.Function, (void*)H_GetStateEx, (void**)&o_getStateEx); patched++; }
            else if ((!strcmp(fn, "XInputSetState") || ord == 3) && !o_setState) { PatchSlot(&iat->u1.Function, (void*)H_SetState, (void**)&o_setState); patched++; }
            else if ((!strcmp(fn, "XInputGetCapabilities") || ord == 4) && !o_getCaps) { PatchSlot(&iat->u1.Function, (void*)H_GetCaps, (void**)&o_getCaps); patched++; }
        }
        Log("controller blocker: %s - %d game imports redirected", dll, patched);
    }
    if (!patched) Log("controller blocker: the game imports no XInput functions (nothing to block)");
    PatchXInputExports();
}

// The game also reaches XInput without its import table, so the functions themselves are redirected:
// their first bytes become "jump to our handler". Our handlers never call the patched code; real reads
// go to a private, unpatched copy of xinput1_4 (<GTA>\CoopPlus\xinput_p2.dll).
static HMODULE g_privXInput;
static XGetStateFn p_getState, p_getStateEx, p_setState; static XGetCapsFn p_getCaps;
static DWORD WINAPI J_GetState(DWORD i, void* s)   { if ((int)i == BlockedSlot()) { InterlockedIncrement(&g_blockedReads); return ERROR_DEVICE_NOT_CONNECTED; } return p_getState ? p_getState(i, s) : ERROR_DEVICE_NOT_CONNECTED; }
static DWORD WINAPI J_GetStateEx(DWORD i, void* s) { if ((int)i == BlockedSlot()) { InterlockedIncrement(&g_blockedReads); return ERROR_DEVICE_NOT_CONNECTED; } return p_getStateEx ? p_getStateEx(i, s) : J_GetState(i, s); }
static DWORD WINAPI J_SetState(DWORD i, void* v)   { if ((int)i == BlockedSlot()) return ERROR_DEVICE_NOT_CONNECTED; return p_setState ? p_setState(i, v) : ERROR_DEVICE_NOT_CONNECTED; }
static DWORD WINAPI J_GetCaps(DWORD i, DWORD f, void* c) { if ((int)i == BlockedSlot()) return ERROR_DEVICE_NOT_CONNECTED; return p_getCaps ? p_getCaps(i, f, c) : ERROR_DEVICE_NOT_CONNECTED; }

static bool WriteJump(void* at, void* to) {
    unsigned char code[12] = { 0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xE0 };   // mov rax, imm64 ; jmp rax
    memcpy(code + 2, &to, 8);
    DWORD old;
    if (!VirtualProtect(at, sizeof(code), PAGE_EXECUTE_READWRITE, &old)) return false;
    memcpy(at, code, sizeof(code));
    VirtualProtect(at, sizeof(code), old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, sizeof(code));
    return true;
}

static void PatchXInputExports() {
    if (!g_privXInput) {
        wchar_t priv[MAX_PATH], sys[MAX_PATH];
        swprintf_s(priv, L"%sCoopPlus\\xinput_p2.dll", g_dir);
        if (GetFileAttributesW(priv) == INVALID_FILE_ATTRIBUTES) {
            wchar_t dir[MAX_PATH]; swprintf_s(dir, L"%sCoopPlus", g_dir); CreateDirectoryW(dir, nullptr);
            GetSystemDirectoryW(sys, MAX_PATH); wcscat_s(sys, L"\\xinput1_4.dll"); CopyFileW(sys, priv, TRUE);
        }
        g_privXInput = LoadLibraryW(priv);
        if (!g_privXInput) { Log("controller blocker: could not load %ls", priv); return; }
        p_getState = (XGetStateFn)GetProcAddress(g_privXInput, "XInputGetState");
        p_getStateEx = (XGetStateFn)GetProcAddress(g_privXInput, (LPCSTR)100);
        p_setState = (XGetStateFn)GetProcAddress(g_privXInput, "XInputSetState");
        p_getCaps = (XGetCapsFn)GetProcAddress(g_privXInput, "XInputGetCapabilities");
    }
    // every XInput module the game has loaded (the game folder's XINPUT1_4.dll and the system one)
    const wchar_t* names[] = { L"xinput1_4.dll", L"xinput1_3.dll", L"xinput9_1_0.dll" };
    int patched = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    MODULEENTRY32W me = { sizeof(me) };
    for (BOOL ok = Module32FirstW(snap, &me); ok; ok = Module32NextW(snap, &me)) {
        if (me.hModule == g_privXInput) continue;
        bool isX = false; for (auto n : names) if (!_wcsicmp(me.szModule, n)) isX = true;
        if (!isX) continue;
        struct { LPCSTR name; void* to; } targets[] = {
            { "XInputGetState", (void*)J_GetState }, { (LPCSTR)100, (void*)J_GetStateEx },
            { "XInputSetState", (void*)J_SetState }, { "XInputGetCapabilities", (void*)J_GetCaps } };
        int here = 0;
        for (auto& t : targets) { void* f = (void*)GetProcAddress(me.hModule, t.name); if (f && WriteJump(f, t.to)) { here++; patched++; } }
        Log("controller blocker: %ls (%ls) - %d functions redirected", me.szModule, me.szExePath, here);
    }
    if (snap != INVALID_HANDLE_VALUE) CloseHandle(snap);
    if (!patched) Log("controller blocker: no XInput module loaded yet");
}

// Called by Script Hook V on the render thread right before every Present.
static void OnPresent(void* chainPtr) {
    if (g_failed) return;
    IDXGISwapChain* chain = (IDXGISwapChain*)chainPtr;
    static int monCheck = 0;
    if (monCheck++ % 120 == 0) {   // which monitor is the game on? (cheap, about once a second)
        IDXGIOutput* out = nullptr;
        if (SUCCEEDED(chain->GetContainingOutput(&out)) && out) {
            DXGI_OUTPUT_DESC od;
            if (SUCCEEDED(out->GetDesc(&od)) && od.Monitor != g_gameMon) { g_gameMon = od.Monitor; if (g_tvWnd) PostMessageW(g_tvWnd, WM_DISPLAYCHANGE, 0, 0); }
            out->Release();
        }
    }

    bool active = false;
    if (g_shm && g_shm->enabled == 1) {
        DWORD now = GetTickCount(); LONG b = g_shm->beat;
        if (b != g_presentBeat) { g_presentBeat = b; g_presentBeatTick = now; }
        active = now - g_presentBeatTick <= 250;
    }
    int tag = 0;
    if (active) { std::lock_guard<std::mutex> l(g_qLock); if (!g_queue.empty()) { tag = g_queue.front(); g_queue.pop_front(); } }
    if (g_swap && tag) tag = 3 - tag;
    if (!active && !g_mirror) { ShowTv(false); g_haveP1 = false; return; }

    if (!g_dev) {
        if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), (void**)&g_dev))) { Log("ERROR game device is not D3D11"); g_failed = true; return; }
        g_dev->GetImmediateContext(&g_ctx);
        Log("hooked into the game's graphics device");
    }
    if (!g_tvWnd) { if (WaitForSingleObject(g_tvReady, 0) != WAIT_OBJECT_0 || !g_tvWnd) return; }

    ID3D11Texture2D* bb = nullptr;
    if (FAILED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) || !bb) return;
    D3D11_TEXTURE2D_DESC d; bb->GetDesc(&d);
    if (!EnsureResources(d)) { bb->Release(); g_failed = true; Log("giving up - game stays normal"); return; }

    // Preferred path: each frame carries a colour tag in its top-left corner (drawn by CoopPlus) and
    // the GPU routes it, so a stutter can never send a frame to the wrong screen.
    if (active && g_gpuRoute && d.SampleDesc.Count == 1 && GpuRoute(bb, d)) {
        ShowTv(!g_singleScreen && !g_noSecondScreen);
        bb->Release();
        static DWORD lastStat2; DWORD now2 = GetTickCount();
        if (now2 - lastStat2 > 3000) {
            if (lastStat2) Log("last 3 s: %llu frames, tag samples P2 %u / P1 %u / none %u (last odd colour %u,%u,%u), TV skipped %llu, P2 pad hidden from game %ld times",
                               g_p1Frames, g_seenP2, g_seenP1, g_seenOther, g_lastOther[0], g_lastOther[1], g_lastOther[2], g_tvDrops,
                               InterlockedExchange(&g_blockedReads, 0));
            g_p1Frames = g_tvDrops = 0; g_seenP2 = g_seenP1 = g_seenOther = 0; lastStat2 = now2;
        }
        return;
    }

    auto toTv = [&](ID3D11Texture2D* src) {
        static bool resized = false;
        if (!resized) { resized = true; g_tvChain->ResizeBuffers(0, d.Width, d.Height, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT); }
        ID3D11Texture2D* tvbb = nullptr;
        if (SUCCEEDED(g_tvChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&tvbb))) {
            if (d.SampleDesc.Count > 1) g_ctx->ResolveSubresource(tvbb, 0, src, 0, NoSrgb(d.Format));
            else g_ctx->CopyResource(tvbb, src);
            tvbb->Release();
            HRESULT hr = g_tvChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);   // never hold up the main screen
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING) g_tvDrops++;
        }
    };

    if (!active) {                       // mirror mode
        toTv(bb);
    } else if (tag == 1) {               // Player 1's camera: keep it for the main screen
        g_ctx->CopyResource(g_lastP1, bb); g_haveP1 = true; g_p1Frames++;
    } else if (tag == 2) {               // Player 2's camera: send to TV, main screen re-shows Player 1
        toTv(bb); g_p2Frames++;
        if (g_haveP1) g_ctx->CopyResource(bb, g_lastP1);
    }
    if (active) ShowTv(true);
    bb->Release();

    static DWORD lastStat;
    DWORD now = GetTickCount();
    if (active && now - lastStat > 10000) {
        if (lastStat) Log("last 10 s: P1 frames %llu, P2 frames %llu, TV skipped %llu", g_p1Frames, g_p2Frames, g_tvDrops);
        g_p1Frames = g_p2Frames = g_tvDrops = 0; lastStat = now;
    }
}

// ---------------------------------------------------------------- entry
static void LoadSettings() {
    wchar_t ini[MAX_PATH]; swprintf_s(ini, L"%sTwoScreens.ini", g_dir);
    GetPrivateProfileStringW(L"TwoScreens", L"Monitor", L"auto", g_monitorName, 64, ini);
    g_mirror = GetPrivateProfileIntW(L"TwoScreens", L"MirrorWhenNotSplit", 0, ini) != 0;
    g_swap   = GetPrivateProfileIntW(L"TwoScreens", L"SwapPlayers", 0, ini) != 0;
    g_sharpUpscale = GetPrivateProfileIntW(L"TwoScreens", L"SharpUpscale", 1, ini) != 0;
    g_sharp = GetPrivateProfileIntW(L"TwoScreens", L"Sharpness", 50, ini) / 100.0f;
}

BOOL APIENTRY DllMain(HMODULE mod, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = mod;
        DisableThreadLibraryCalls(mod);
        GetModuleFileNameW(mod, g_dir, MAX_PATH);
        if (wchar_t* s = wcsrchr(g_dir, L'\\')) s[1] = 0;
        LoadSettings();
        Log("---- TwoScreens loaded (Monitor=%ls Mirror=%d Swap=%d)", g_monitorName, g_mirror, g_swap);
        OpenShm();
        InstallXInputBlock();
        g_tvReady = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        CloseHandle(CreateThread(nullptr, 0, TvThread, nullptr, 0, nullptr));
        scriptRegister(mod, ScriptMain);
        presentCallbackRegister(OnPresent);
    } else if (reason == DLL_PROCESS_DETACH) {
        presentCallbackUnregister(OnPresent);
        scriptUnregister(mod);
    }
    return TRUE;
}
