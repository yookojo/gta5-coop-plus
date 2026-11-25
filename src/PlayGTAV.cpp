// PlayGTAV.cpp - drop-in replacement for GTA V's PlayGTAV.exe start-up stub (Epic version).
//
// The original stub runs Redistributables\Rockstar-Games-Launcher.exe "/S /t" AS ADMINISTRATOR on every
// launch, only to check the Rockstar launcher's version, which raises a Windows admin prompt each time.
// This stub skips that check and does what the original does next: start the installed Rockstar Games
// Launcher with the same arguments (Epic sign-in) plus "@args.txt", and wait for it to exit.
// If the Rockstar launcher isn't installed, it hands over to the original stub (PlayGTAV.original.exe).
#include <windows.h>
#include <string>
#include <cstdio>

static std::wstring Dir() {
    wchar_t p[MAX_PATH]; GetModuleFileNameW(nullptr, p, MAX_PATH);
    std::wstring s(p); return s.substr(0, s.find_last_of(L'\\'));
}

static void Log(const std::wstring& m) {
    FILE* f = nullptr;
    if (_wfopen_s(&f, (Dir() + L"\\CoopPlus\\playgtav.log").c_str(), L"a, ccs=UTF-8") == 0 && f) {
        SYSTEMTIME t; GetLocalTime(&t);
        fwprintf(f, L"[%04d-%02d-%02d %02d:%02d:%02d] %s\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, m.c_str());
        fclose(f);
    }
}

static std::wstring LauncherPath() {
    wchar_t buf[MAX_PATH] = {}; DWORD sz = sizeof(buf);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Rockstar Games\\Launcher", L"InstallFolder",
                     RRF_RT_REG_SZ, nullptr, buf, &sz) == ERROR_SUCCESS && buf[0])
        return std::wstring(buf) + L"\\Launcher.exe";
    return L"C:\\Program Files\\Rockstar Games\\Launcher\\Launcher.exe";
}

static int RunAndWait(const std::wstring& exe, std::wstring cmd, const std::wstring& cwd) {
    STARTUPINFOW si = { sizeof(si) }; PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(exe.c_str(), &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, cwd.c_str(), &si, &pi)) {
        Log(L"could not start " + exe + L" (error " + std::to_wstring(GetLastError()) + L")"); return -1;
    }
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 0; GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return (int)code;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::wstring dir = Dir();
    // everything after our own exe name = the arguments Epic passed (sign-in token etc.)
    std::wstring full = GetCommandLineW(), args;
    size_t i = 0;
    if (!full.empty() && full[0] == L'"') { i = full.find(L'"', 1); i = (i == std::wstring::npos) ? full.size() : i + 1; }
    else { i = full.find(L' '); if (i == std::wstring::npos) i = full.size(); }
    args = full.substr(i);

    std::wstring launcher = LauncherPath();
    if (GetFileAttributesW(launcher.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Log(L"Rockstar launcher not found - using the original PlayGTAV (it will install it)");
        std::wstring orig = dir + L"\\PlayGTAV.original.exe";
        return RunAndWait(orig, L"\"" + orig + L"\"" + args, dir);
    }
    std::wstring cmd = L"\"" + launcher + L"\" -skipPatcherCheck -epicAppId=gta5 " + args + L" @args.txt";
    Log(L"starting the Rockstar launcher without the admin check");
    int code = RunAndWait(launcher, cmd, dir);
    Log(L"Rockstar launcher exited with " + std::to_wstring(code));
    return code;
}
