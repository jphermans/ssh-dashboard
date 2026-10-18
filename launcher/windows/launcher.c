/*
 * SSH Dashboard launcher for Windows.
 *
 * A ~100 KB native executable. It detects an installed Python 3.9+/Tk 8.6,
 * tells the user which version it will use, can install Python through winget
 * when none is found, and otherwise links to the official download page.
 * No Python runtime is embedded.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>
#include <stdio.h>
#include <wchar.h>

#define APP_TITLE L"SSH Dashboard"
#define DOWNLOAD_URL L"https://www.python.org/downloads/windows/"
#define SCRIPT_NAME L"ssh_dashboard.py"

typedef struct {
    wchar_t exe[MAX_PATH];
    int launcher;            /* uses the "py" launcher (-3) */
    wchar_t version[32];
    wchar_t tk[32];
    int has_tk;
} Python;

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

static void join_path(wchar_t *out, size_t count, const wchar_t *base,
                      const wchar_t *name) {
    StringCchPrintfW(out, count, L"%s\\%s", base, name);
}

static int read_ascii(const wchar_t *path, wchar_t *out, size_t count) {
    FILE *file = _wfopen(path, L"rb");
    char buffer[1024];
    size_t read;
    if (file == NULL) {
        return 0;
    }
    read = fread(buffer, 1, sizeof(buffer) - 1, file);
    fclose(file);
    buffer[read] = 0;
    while (read > 0 && (buffer[read - 1] == '\n' || buffer[read - 1] == '\r')) {
        buffer[--read] = 0;
    }
    if (read == 0) {
        return 0;
    }
    return MultiByteToWideChar(CP_UTF8, 0, buffer, -1, out, (int)count) > 0;
}

static void write_ascii(const wchar_t *path, const wchar_t *text) {
    FILE *file = _wfopen(path, L"wb");
    char buffer[4096];
    int length;
    if (file == NULL) {
        return;
    }
    length = WideCharToMultiByte(CP_UTF8, 0, text, -1, buffer, sizeof(buffer), NULL, NULL);
    if (length > 1) {
        fwrite(buffer, 1, (size_t)(length - 1), file);
    }
    fclose(file);
}

static void ensure_dir(const wchar_t *path) {
    wchar_t buffer[MAX_PATH];
    wchar_t *cursor;
    StringCchCopyW(buffer, MAX_PATH, path);
    for (cursor = buffer + 3; *cursor != L'\0'; ++cursor) {
        if (*cursor == L'\\') {
            *cursor = L'\0';
            CreateDirectoryW(buffer, NULL);
            *cursor = L'\\';
        }
    }
    CreateDirectoryW(buffer, NULL);
}

static void local_appdata(wchar_t *out, size_t count) {
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", out, (DWORD)count) == 0) {
        StringCchCopyW(out, count, L".");
    }
}

/* ---------------------------------------------------------------- probe */

static int run_capture(const wchar_t *exe, int launcher, const wchar_t *code,
                       const wchar_t *probe_path) {
    wchar_t args[16384];
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    DWORD wait_result;

    SetEnvironmentVariableW(L"SSHDASH_PROBE", probe_path);

    if (FAILED(StringCchPrintfW(args, ARRAYSIZE(args), launcher
                                ? L"-3 -c \"%s\"" : L"-c \"%s\"", code))) {
        return 0;
    }
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    ZeroMemory(&process, sizeof(process));

    if (!CreateProcessW(exe, args, NULL, NULL, FALSE, CREATE_NO_WINDOW,
                        NULL, NULL, &startup, &process)) {
        return 0;
    }
    wait_result = WaitForSingleObject(process.hProcess, 20000);
    if (wait_result == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 1);
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return wait_result == WAIT_OBJECT_0;
}

static int probe(const wchar_t *exe, int launcher, Python *result) {
    wchar_t temp_dir[MAX_PATH];
    wchar_t probe_path[MAX_PATH];
    wchar_t code[1024];
    wchar_t line[256];
    wchar_t *separator;

    ZeroMemory(result, sizeof(*result));
    StringCchCopyW(result->exe, MAX_PATH, exe);
    result->launcher = launcher;

    GetTempPathW(MAX_PATH, temp_dir);
    StringCchPrintfW(probe_path, MAX_PATH, L"%ssshdash-probe-%lu.txt",
                     temp_dir, (unsigned long)GetCurrentProcessId());
    DeleteFileW(probe_path);

    StringCchPrintfW(code, ARRAYSIZE(code),
        L"import os,sys;open(os.environ['SSHDASH_PROBE'],'w').write("
        L"sys.version.split()[0]+'|'+str(__import__('tkinter').TkVersion))");
    if (run_capture(exe, launcher, code, probe_path) &&
        read_ascii(probe_path, line, ARRAYSIZE(line))) {
        separator = wcschr(line, L'|');
        if (separator != NULL) {
            *separator = L'\0';
            StringCchCopyW(result->version, ARRAYSIZE(result->version), line);
            StringCchCopyW(result->tk, ARRAYSIZE(result->tk), separator + 1);
            result->has_tk = 1;
            DeleteFileW(probe_path);
            return 1;
        }
    }

    /* Python exists but Tk could not be imported: capture the version only. */
    StringCchPrintfW(code, ARRAYSIZE(code),
        L"import os,sys;open(os.environ['SSHDASH_PROBE'],'w').write("
        L"sys.version.split()[0]+'|')");
    if (run_capture(exe, launcher, code, probe_path) &&
        read_ascii(probe_path, line, ARRAYSIZE(line))) {
        separator = wcschr(line, L'|');
        if (separator != NULL) {
            *separator = L'\0';
            StringCchCopyW(result->version, ARRAYSIZE(result->version), line);
            DeleteFileW(probe_path);
            return 1;
        }
    }
    DeleteFileW(probe_path);
    return 0;
}

static int version_supported(const wchar_t *version) {
    int major = 0;
    int minor = 0;
    if (swscanf(version, L"%d.%d", &major, &minor) != 2) {
        return 0;
    }
    return major > 3 || (major == 3 && minor >= 9);
}

static int find_python(Python *result, Python *almost) {
    wchar_t path[MAX_PATH * 2];
    wchar_t appdata[MAX_PATH];
    wchar_t saved[MAX_PATH * 2];
    Python candidate;
    int i;

    local_appdata(appdata, MAX_PATH);
    StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\SSHDashboard", appdata);
    StringCchPrintfW(saved, ARRAYSIZE(saved), L"%s\\python-path.txt", path);
    if (read_ascii(saved, candidate.exe, MAX_PATH) && file_exists(candidate.exe)) {
        if (probe(candidate.exe, 0, &candidate)) {
            if (candidate.has_tk && version_supported(candidate.version)) {
                *result = candidate;
                return 1;
            }
            *almost = candidate;
        }
    }

    if (find_in_path(L"py.exe", path, MAX_PATH)) {
        if (probe(path, 1, &candidate)) {
            if (candidate.has_tk && version_supported(candidate.version)) {
                *result = candidate;
                return 1;
            }
            if (almost->version[0] == 0) {
                *almost = candidate;
            }
        }
    }

    for (i = 0; i < 3; ++i) {
        const wchar_t *names[3] = { L"python.exe", L"pythonw.exe", L"python3.exe" };
        if (find_in_path(names[i], path, MAX_PATH)) {
            if (probe(path, 0, &candidate)) {
                if (candidate.has_tk && version_supported(candidate.version)) {
                    *result = candidate;
                    return 1;
                }
                if (almost->version[0] == 0) {
                    *almost = candidate;
                }
            }
        }
    }

    /* winget installs per-user Python outside PATH until a new shell starts. */
    for (i = 0; i < 2; ++i) {
        wchar_t base[MAX_PATH];
        const wchar_t *versions[3] = { L"314", L"313", L"312" };
        int v;
        if (i == 0) {
            StringCchCopyW(base, MAX_PATH, appdata);
            StringCchCatW(base, MAX_PATH, L"\\Programs\\Python");
        } else {
            DWORD length = GetEnvironmentVariableW(L"ProgramFiles", base, MAX_PATH);
            if (length == 0) {
                continue;
            }
        }
        for (v = 0; v < 3; ++v) {
            StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\Python%s\\python.exe",
                             base, versions[v]);
            if (file_exists(path) && probe(path, 0, &candidate)) {
                if (candidate.has_tk && version_supported(candidate.version)) {
                    *result = candidate;
                    return 1;
                }
                if (almost->version[0] == 0) {
                    *almost = candidate;
                }
            }
        }
    }
    return 0;
}

/* ------------------------------------------------------------- launching */

static int launch(const Python *python, const wchar_t *script, const wchar_t *cwd) {
    wchar_t command[32768];
    wchar_t executable[MAX_PATH];
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;

    StringCchCopyW(executable, MAX_PATH, python->exe);
    {
        wchar_t sibling[MAX_PATH];
        wchar_t *slash = wcsrchr(executable, L'\\');
        if (slash != NULL) {
            *slash = L'\0';
            StringCchPrintfW(sibling, MAX_PATH,
                             python->launcher ? L"%s\\pyw.exe" : L"%s\\pythonw.exe",
                             executable);
            if (file_exists(sibling)) {
                StringCchCopyW(executable, MAX_PATH, sibling);
            }
        }
    }

    if (python->launcher) {
        StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" -3 \"%s\"",
                         executable, script);
    } else {
        StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" \"%s\"",
                         executable, script);
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

/* ------------------------------------------------------------- installer */

static int install_python(const wchar_t *base_dir) {
    wchar_t script[MAX_PATH * 2];
    wchar_t command[4096];
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    DWORD wait_result;

    join_path(script, ARRAYSIZE(script), base_dir, L"install-python.ps1");
    if (!file_exists(script)) {
        return 0;
    }
    StringCchPrintfW(command, ARRAYSIZE(command),
                     L"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%s\"",
                     script);

    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    ZeroMemory(&process, sizeof(process));
    if (!CreateProcessW(L"powershell.exe", command, NULL, NULL, FALSE,
                        CREATE_NEW_CONSOLE, NULL, base_dir, &startup, &process)) {
        return 0;
    }
    wait_result = WaitForSingleObject(process.hProcess, 15 * 60 * 1000);
    if (wait_result == WAIT_TIMEOUT) {
        TerminateProcess(process.hProcess, 1);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return 0;
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 1;
}

static int load_installed_python(Python *result) {
    wchar_t appdata[MAX_PATH];
    wchar_t path[MAX_PATH * 2];
    wchar_t saved[MAX_PATH * 2];
    Python candidate;

    local_appdata(appdata, MAX_PATH);
    StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\SSHDashboard", appdata);
    StringCchPrintfW(saved, ARRAYSIZE(saved), L"%s\\python-path.txt", path);
    if (read_ascii(saved, candidate.exe, MAX_PATH) && file_exists(candidate.exe) &&
        probe(candidate.exe, 0, &candidate) && candidate.has_tk) {
        *result = candidate;
        return 1;
    }
    return find_python(result, &candidate);
}

/* ------------------------------------------------------------------ main */

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line,
                    int show_command) {
    wchar_t base_dir[MAX_PATH];
    wchar_t script[MAX_PATH * 2];
    wchar_t appdata[MAX_PATH];
    wchar_t support_dir[MAX_PATH];
    wchar_t ack_path[MAX_PATH * 2];
    wchar_t stamp[1024];
    wchar_t message[2048];
    wchar_t ack[1024];
    Python python;
    Python almost;
    int answer;

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
    join_path(script, ARRAYSIZE(script), base_dir, SCRIPT_NAME);
    if (!file_exists(script)) {
        show_error(L"ssh_dashboard.py is missing next to SSH Dashboard.exe.\n\n"
                   L"Keep the whole folder together when moving the app.");
        return 1;
    }

    SetEnvironmentVariableW(L"PYTHONDONTWRITEBYTECODE", L"1");

    local_appdata(appdata, MAX_PATH);
    StringCchPrintfW(support_dir, ARRAYSIZE(support_dir), L"%s\\SSHDashboard", appdata);
    ensure_dir(support_dir);
    StringCchPrintfW(ack_path, ARRAYSIZE(ack_path), L"%s\\python-ack.txt", support_dir);

    ZeroMemory(&python, sizeof(python));
    ZeroMemory(&almost, sizeof(almost));

    if (!find_python(&python, &almost)) {
        if (almost.version[0] != 0) {
            /* Python exists but Tk is missing or it is too old. */
            StringCchPrintfW(message, ARRAYSIZE(message),
                L"Python %s was found at\n%s\n\n"
                L"but it cannot run SSH Dashboard: Tk 8.6 is missing or the "
                L"version is older than 3.9.\n\n"
                L"Install the official Python build from python.org — it "
                L"includes Tk. Open the download page?",
                almost.version, almost.exe);
            if (MessageBoxW(NULL, message, L"Python setup",
                            MB_OKCANCEL | MB_ICONWARNING) == IDOK) {
                ShellExecuteW(NULL, L"open", DOWNLOAD_URL, NULL, NULL, SW_SHOWNORMAL);
            }
            return 1;
        }

        answer = MessageBoxW(NULL,
            L"SSH Dashboard needs Python 3.9 or newer with Tk 8.6.\n\n"
            L"Install Python now?\n"
            L"This downloads the official Python build (about 25 MB) and "
            L"includes Tk.",
            L"Python required", MB_YESNO | MB_ICONQUESTION);
        if (answer == IDYES) {
            if (install_python(base_dir) && load_installed_python(&python)) {
                StringCchPrintfW(message, ARRAYSIZE(message),
                    L"Python %s with Tk %s installed.\n\nStarting SSH Dashboard.",
                    python.version, python.tk);
                MessageBoxW(NULL, message, APP_TITLE, MB_OK | MB_ICONINFORMATION);
            } else {
                MessageBoxW(NULL,
                    L"Automatic installation did not finish.\n\n"
                    L"Install Python from python.org and open SSH Dashboard again.",
                    APP_TITLE, MB_OK | MB_ICONWARNING);
                ShellExecuteW(NULL, L"open", DOWNLOAD_URL, NULL, NULL, SW_SHOWNORMAL);
                return 1;
            }
        } else {
            ShellExecuteW(NULL, L"open", DOWNLOAD_URL, NULL, NULL, SW_SHOWNORMAL);
            return 1;
        }
    }

    /* Tell the user which interpreter is used, once per version. */
    StringCchPrintfW(stamp, ARRAYSIZE(stamp), L"%s|%s|%s",
                     python.exe, python.version, python.tk);
    if (!read_ascii(ack_path, ack, ARRAYSIZE(ack)) || wcscmp(ack, stamp) != 0) {
        StringCchPrintfW(message, ARRAYSIZE(message),
            L"Python %s with Tk %s detected.\n\n"
            L"SSH Dashboard will use:\n%s\n\n"
            L"This confirmation appears once per Python version.",
            python.version, python.tk, python.exe);
        if (MessageBoxW(NULL, message, L"Python detected",
                        MB_OKCANCEL | MB_ICONINFORMATION) != IDOK) {
            return 0;
        }
        write_ascii(ack_path, stamp);
    }

    if (!launch(&python, script, base_dir)) {
        show_error(L"Could not start Python. Reinstall Python from python.org "
                   L"and try again.");
        return 1;
    }
    return 0;
}
