/*
 * SSH Dashboard launcher for Windows.
 *
 * A ~100 KB native executable that finds an installed Python (3.9+, Tk 8.6),
 * starts ssh_dashboard.py with it, and offers the Python download page when no
 * interpreter is available. No Python runtime is embedded.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>
#include <wchar.h>

#define APP_TITLE L"SSH Dashboard"
#define DOWNLOAD_URL L"https://www.python.org/downloads/windows/"
#define SCRIPT_NAME L"ssh_dashboard.py"

static int file_exists(const wchar_t *path) {
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES &&
           !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

static void show_error(const wchar_t *message) {
    MessageBoxW(NULL, message, APP_TITLE, MB_OK | MB_ICONWARNING);
}

static int find_in_path(const wchar_t *name, wchar_t *out, DWORD size) {
    return SearchPathW(NULL, name, NULL, size, out, NULL) > 0;
}

static int run(const wchar_t *executable, const wchar_t *arguments, const wchar_t *cwd) {
    wchar_t command[32768];
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;

    if (FAILED(StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" %s",
                                executable, arguments))) {
        return 0;
    }
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    ZeroMemory(&process, sizeof(process));

    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, cwd,
                        &startup, &process)) {
        return 0;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 1;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line,
                    int show_command) {
    wchar_t base_dir[MAX_PATH];
    wchar_t script[MAX_PATH * 2];
    wchar_t arguments[32768];
    wchar_t python[MAX_PATH];

    (void)instance;
    (void)previous;
    (void)command_line;
    (void)show_command;

    if (!GetModuleFileNameW(NULL, base_dir, MAX_PATH)) {
        show_error(L"Could not determine the application folder.");
        return 1;
    }
    wchar_t *slash = wcsrchr(base_dir, L'\\');
    if (slash != NULL) {
        *slash = L'\0';
    }
    if (FAILED(StringCchPrintfW(script, ARRAYSIZE(script), L"%s\\%s",
                                base_dir, SCRIPT_NAME))) {
        show_error(L"Could not build the application path.");
        return 1;
    }
    if (!file_exists(script)) {
        show_error(L"ssh_dashboard.py is missing next to SSH Dashboard.exe.\n\n"
                   L"Keep the whole folder together when moving the app.");
        return 1;
    }

    SetEnvironmentVariableW(L"PYTHONDONTWRITEBYTECODE", L"1");

    /* The python.org launcher handles multiple installed versions. */
    if (find_in_path(L"pyw.exe", python, MAX_PATH)) {
        if (SUCCEEDED(StringCchPrintfW(arguments, ARRAYSIZE(arguments),
                                       L"-3 \"%s\"", script)) &&
            run(python, arguments, base_dir)) {
            return 0;
        }
    }
    if (find_in_path(L"pythonw.exe", python, MAX_PATH)) {
        if (SUCCEEDED(StringCchPrintfW(arguments, ARRAYSIZE(arguments),
                                       L"\"%s\"", script)) &&
            run(python, arguments, base_dir)) {
            return 0;
        }
    }
    if (find_in_path(L"python.exe", python, MAX_PATH)) {
        if (SUCCEEDED(StringCchPrintfW(arguments, ARRAYSIZE(arguments),
                                       L"\"%s\"", script)) &&
            run(python, arguments, base_dir)) {
            return 0;
        }
    }

    if (MessageBoxW(NULL,
            L"SSH Dashboard needs Python 3.9 or newer with Tk 8.6.\n\n"
            L"Open the Python download page now?",
            L"Python required", MB_OKCANCEL | MB_ICONWARNING) == IDOK) {
        ShellExecuteW(NULL, L"open", DOWNLOAD_URL, NULL, NULL, SW_SHOWNORMAL);
    }
    return 1;
}
