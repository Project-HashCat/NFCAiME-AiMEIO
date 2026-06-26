#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0603
#endif

#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <regex>
#include <string>
#include <thread>
#include <vector>

#ifndef NFCAIME_AES_KEY_HEX
#define NFCAIME_AES_KEY_HEX ""
#endif

namespace {

constexpr size_t kAimeSize = 10;
constexpr uint64_t kCardTtlMs = 5000;
constexpr wchar_t kIniPath[] = L".\\segatools.ini";

struct CardCache {
    bool aimePresent = false;
    bool felicaPresent = false;
    uint8_t aimeData[kAimeSize] = {};
    uint8_t felicaData[8] = {};
    uint64_t expiresAtTick = 0;
};

struct ClientConfig {
    std::wstring serverUrl;
    std::wstring sessionKey;
};

std::mutex g_cacheMutex;
CardCache g_cache;
bool g_aimePresent = false;
bool g_felicaPresent = false;
uint8_t g_aimeId[kAimeSize] = {};
uint8_t g_felicaId[8] = {};

std::atomic_bool g_clientStarted{false};
std::atomic_bool g_clientRunning{false};
std::thread g_clientThread;

std::wstring trim(std::wstring value) {
    const wchar_t* spaces = L" \t\r\n";
    const auto start = value.find_first_not_of(spaces);
    if (start == std::wstring::npos) {
        return L"";
    }
    const auto end = value.find_last_not_of(spaces);
    return value.substr(start, end - start + 1);
}

std::wstring trim_slashes(std::wstring value) {
    while (!value.empty() && (value.back() == L'/' || value.back() == L'\\')) {
        value.pop_back();
    }
    return value;
}

std::wstring sanitize_session_key(std::wstring value) {
    value = trim(value);
    std::wstring result;
    for (wchar_t ch : value) {
        const bool ok =
            (ch >= L'a' && ch <= L'z') ||
            (ch >= L'A' && ch <= L'Z') ||
            (ch >= L'0' && ch <= L'9') ||
            ch == L'-' ||
            ch == L'_';
        if (ok) {
            result.push_back(ch);
        }
    }
    if (result.size() > 80) {
        result.resize(80);
    }
    return result;
}

std::wstring read_ini_string(const wchar_t* key, const wchar_t* fallback = L"") {
    wchar_t buffer[512] = {};
    GetPrivateProfileStringW(L"aimeio", key, fallback, buffer, ARRAYSIZE(buffer), kIniPath);
    return trim(buffer);
}

ClientConfig load_config() {
    ClientConfig config;
    config.serverUrl = read_ini_string(L"serverUrl");
    config.sessionKey = sanitize_session_key(read_ini_string(L"session-key"));
    if (config.sessionKey.empty()) {
        config.sessionKey = sanitize_session_key(read_ini_string(L"sessionKey"));
    }

    // Backward compatibility with the older PC Kit format where serverUrl ended with /{instanceId}.
    if (config.sessionKey.empty()) {
        std::wstring value = trim_slashes(config.serverUrl);
        const auto pos = value.find_last_of(L"/\\");
        if (pos != std::wstring::npos) {
            config.sessionKey = sanitize_session_key(value.substr(pos + 1));
            config.serverUrl = value.substr(0, pos);
        }
    }

    if (config.serverUrl.find(L"://") == std::wstring::npos) {
        config.serverUrl = L"https://" + config.serverUrl;
    }
    config.serverUrl = trim_slashes(config.serverUrl);
    return config;
}

std::wstring http_upgrade_endpoint(const ClientConfig& config) {
    std::wstring url = config.serverUrl;
    if (url.rfind(L"ws://", 0) == 0) {
        url.replace(0, 5, L"http://");
    } else if (url.rfind(L"wss://", 0) == 0) {
        url.replace(0, 6, L"https://");
    }
    return trim_slashes(url) + L"/" + config.sessionKey;
}

std::string json_string(const std::string& json, const char* key) {
    const std::regex pattern(std::string("\"") + key + "\"\\s*:\\s*\"([^\"]*)\"");
    std::smatch match;
    return std::regex_search(json, match, pattern) ? match[1].str() : "";
}

bool json_true(const std::string& json, const char* key) {
    const std::regex pattern(std::string("\"") + key + "\"\\s*:\\s*true");
    return std::regex_search(json, pattern);
}

std::string normalize_digits(const std::string& value) {
    std::string result;
    std::copy_if(value.begin(), value.end(), std::back_inserter(result), [](char ch) {
        return ch >= '0' && ch <= '9';
    });
    return result;
}

std::string normalize_hex(const std::string& value) {
    std::string result;
    std::copy_if(value.begin(), value.end(), std::back_inserter(result), [](char ch) {
        return (ch >= '0' && ch <= '9') ||
               (ch >= 'a' && ch <= 'f') ||
               (ch >= 'A' && ch <= 'F');
    });
    return result;
}

bool parse_hex_bytes(const std::string& value, uint8_t* output, size_t outputSize) {
    if (value.size() != outputSize * 2) {
        return false;
    }
    for (size_t i = 0; i < outputSize; i++) {
        const std::string byteText = value.substr(i * 2, 2);
        char* end = nullptr;
        const long byteValue = std::strtol(byteText.c_str(), &end, 16);
        if (end == nullptr || *end != '\0' || byteValue < 0 || byteValue > 255) {
            return false;
        }
        output[i] = static_cast<uint8_t>(byteValue);
    }
    return true;
}

bool hex_to_bytes(const std::string& value, std::vector<uint8_t>& output) {
    const std::string normalized = normalize_hex(value);
    if (normalized.empty() || normalized.size() % 2 != 0) {
        return false;
    }
    output.assign(normalized.size() / 2, 0);
    return parse_hex_bytes(normalized, output.data(), output.size());
}

bool decrypt_aes_gcm(
    const std::vector<uint8_t>& key,
    const std::vector<uint8_t>& nonce,
    const std::vector<uint8_t>& ciphertext,
    const std::vector<uint8_t>& tag,
    std::string& plaintext
) {
    if (key.size() != 32 || nonce.size() != 12 || tag.size() != 16 || ciphertext.empty()) {
        return false;
    }

    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_KEY_HANDLE keyHandle = nullptr;
    std::vector<uint8_t> keyObject;
    bool ok = false;

    do {
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_AES_ALGORITHM, nullptr, 0) != 0) {
            break;
        }
        if (BCryptSetProperty(
                alg,
                BCRYPT_CHAINING_MODE,
                reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)),
                sizeof(BCRYPT_CHAIN_MODE_GCM),
                0) != 0) {
            break;
        }

        DWORD objectLength = 0;
        DWORD resultLength = 0;
        if (BCryptGetProperty(
                alg,
                BCRYPT_OBJECT_LENGTH,
                reinterpret_cast<PUCHAR>(&objectLength),
                sizeof(objectLength),
                &resultLength,
                0) != 0) {
            break;
        }
        keyObject.assign(objectLength, 0);
        if (BCryptGenerateSymmetricKey(
                alg,
                &keyHandle,
                keyObject.data(),
                static_cast<ULONG>(keyObject.size()),
                const_cast<PUCHAR>(key.data()),
                static_cast<ULONG>(key.size()),
                0) != 0) {
            break;
        }

        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = const_cast<PUCHAR>(nonce.data());
        authInfo.cbNonce = static_cast<ULONG>(nonce.size());
        authInfo.pbTag = const_cast<PUCHAR>(tag.data());
        authInfo.cbTag = static_cast<ULONG>(tag.size());

        std::vector<uint8_t> decrypted(ciphertext.size(), 0);
        ULONG decryptedLength = 0;
        if (BCryptDecrypt(
                keyHandle,
                const_cast<PUCHAR>(ciphertext.data()),
                static_cast<ULONG>(ciphertext.size()),
                &authInfo,
                nullptr,
                0,
                decrypted.data(),
                static_cast<ULONG>(decrypted.size()),
                &decryptedLength,
                0) != 0) {
            break;
        }

        plaintext.assign(reinterpret_cast<const char*>(decrypted.data()), decryptedLength);
        ok = true;
    } while (false);

    if (keyHandle != nullptr) {
        BCryptDestroyKey(keyHandle);
    }
    if (alg != nullptr) {
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    return ok;
}

bool decrypt_payload(const std::string& json, std::string& plaintext) {
    std::vector<uint8_t> key;
    std::vector<uint8_t> nonce;
    std::vector<uint8_t> ciphertext;
    std::vector<uint8_t> tag;

    const std::string nonceText = json_string(json, "nonce").empty()
        ? json_string(json, "iv")
        : json_string(json, "nonce");

    return hex_to_bytes(NFCAIME_AES_KEY_HEX, key) &&
        hex_to_bytes(nonceText, nonce) &&
        hex_to_bytes(json_string(json, "ciphertext"), ciphertext) &&
        hex_to_bytes(json_string(json, "tag"), tag) &&
        decrypt_aes_gcm(key, nonce, ciphertext, tag, plaintext);
}

void store_payload(const std::string& json) {
    const std::string type = json_string(json, "type");
    if (type == "encrypted" || json_true(json, "encrypted")) {
        std::string plaintext;
        if (decrypt_payload(json, plaintext)) {
            store_payload(plaintext);
        }
        return;
    }

    const std::string privateCode = normalize_digits(json_string(json, "privateAccessCode"));
    const std::string officialCode = normalize_digits(json_string(json, "officialAccessCode"));
    const std::string value = json_string(json, "value");
    const std::string idm = type == "felica"
        ? normalize_hex(value)
        : normalize_hex(json_string(json, "idm"));

    std::string aimeCode;
    if (type == "card") {
        aimeCode = privateCode.size() == 20 ? privateCode : officialCode;
    } else if (type == "aime") {
        aimeCode = normalize_digits(value);
    }

    CardCache next;
    next.aimePresent = aimeCode.size() == 20 && parse_hex_bytes(aimeCode, next.aimeData, kAimeSize);
    next.felicaPresent = idm.size() == 16 && parse_hex_bytes(idm, next.felicaData, 8);
    next.expiresAtTick = GetTickCount64() + kCardTtlMs;

    if (!next.aimePresent && !next.felicaPresent) {
        return;
    }

    std::lock_guard<std::mutex> lock(g_cacheMutex);
    g_cache = next;
}

bool crack_url(const std::wstring& url, URL_COMPONENTS& components, std::vector<wchar_t>& host, std::vector<wchar_t>& path) {
    host.assign(256, L'\0');
    path.assign(1024, L'\0');
    ZeroMemory(&components, sizeof(components));
    components.dwStructSize = sizeof(components);
    components.lpszHostName = host.data();
    components.dwHostNameLength = static_cast<DWORD>(host.size());
    components.lpszUrlPath = path.data();
    components.dwUrlPathLength = static_cast<DWORD>(path.size());
    components.dwSchemeLength = static_cast<DWORD>(-1);
    return WinHttpCrackUrl(url.c_str(), static_cast<DWORD>(url.size()), 0, &components) == TRUE;
}

HINTERNET connect_websocket(const std::wstring& endpoint, HINTERNET& session, HINTERNET& connect) {
    URL_COMPONENTS components;
    std::vector<wchar_t> host;
    std::vector<wchar_t> path;
    if (!crack_url(endpoint, components, host, path)) {
        return nullptr;
    }

    const bool secure = components.nScheme == INTERNET_SCHEME_HTTPS ||
        endpoint.rfind(L"wss://", 0) == 0;
    INTERNET_PORT port = components.nPort;
    if (port == 0) {
        port = secure ? INTERNET_DEFAULT_HTTPS_PORT : INTERNET_DEFAULT_HTTP_PORT;
    }

    std::wstring hostName(components.lpszHostName, components.dwHostNameLength);
    std::wstring objectName(components.lpszUrlPath, components.dwUrlPathLength);
    if (objectName.empty()) {
        objectName = L"/";
    }

    session = WinHttpOpen(
        L"NFCAiME AiMEIO DLL/0.1",
        WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0);
    if (session == nullptr) {
        return nullptr;
    }

    connect = WinHttpConnect(session, hostName.c_str(), port, 0);
    if (connect == nullptr) {
        return nullptr;
    }

    HINTERNET request = WinHttpOpenRequest(
        connect,
        L"GET",
        objectName.c_str(),
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        secure ? WINHTTP_FLAG_SECURE : 0);
    if (request == nullptr) {
        return nullptr;
    }

    if (!WinHttpSetOption(request, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, nullptr, 0) ||
        !WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(request, nullptr)) {
        WinHttpCloseHandle(request);
        return nullptr;
    }

    HINTERNET websocket = WinHttpWebSocketCompleteUpgrade(request, 0);
    WinHttpCloseHandle(request);
    return websocket;
}

void websocket_loop(const ClientConfig& config) {
    const std::wstring endpoint = http_upgrade_endpoint(config);
    while (g_clientRunning.load()) {
        HINTERNET session = nullptr;
        HINTERNET connect = nullptr;
        HINTERNET websocket = connect_websocket(endpoint, session, connect);
        if (websocket != nullptr) {
            std::string message;
            std::vector<char> buffer(4096);
            while (g_clientRunning.load()) {
                DWORD bytesRead = 0;
                WINHTTP_WEB_SOCKET_BUFFER_TYPE bufferType;
                const DWORD result = WinHttpWebSocketReceive(
                    websocket,
                    buffer.data(),
                    static_cast<DWORD>(buffer.size()),
                    &bytesRead,
                    &bufferType);
                if (result != ERROR_SUCCESS) {
                    break;
                }
                if (bufferType == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE) {
                    break;
                }
                if (bufferType == WINHTTP_WEB_SOCKET_UTF8_FRAGMENT_BUFFER_TYPE ||
                    bufferType == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE) {
                    message.append(buffer.data(), buffer.data() + bytesRead);
                    if (bufferType == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE) {
                        store_payload(message);
                        message.clear();
                    }
                }
            }
            WinHttpCloseHandle(websocket);
        }
        if (connect != nullptr) {
            WinHttpCloseHandle(connect);
        }
        if (session != nullptr) {
            WinHttpCloseHandle(session);
        }
        for (int i = 0; i < 30 && g_clientRunning.load(); i++) {
            Sleep(100);
        }
    }
}

void start_client() {
    bool expected = false;
    if (!g_clientStarted.compare_exchange_strong(expected, true)) {
        return;
    }

    ClientConfig config = load_config();
    if (config.sessionKey.empty() || config.serverUrl.empty()) {
        g_clientStarted.store(false);
        return;
    }

    g_clientRunning.store(true);
    g_clientThread = std::thread([config]() {
        websocket_loop(config);
    });
    g_clientThread.detach();
}

void clear_presence() {
    g_aimePresent = false;
    g_felicaPresent = false;
    std::memset(g_aimeId, 0, sizeof(g_aimeId));
    std::memset(g_felicaId, 0, sizeof(g_felicaId));
}

} // namespace

extern "C" {

uint16_t aime_io_get_api_version(void) {
    return 0x0100;
}

HRESULT aime_io_init(void) {
    clear_presence();
    start_client();
    return S_OK;
}

HRESULT aime_io_nfc_poll(uint8_t unit_no) {
    if (unit_no != 0) {
        return S_OK;
    }

    clear_presence();
    std::lock_guard<std::mutex> lock(g_cacheMutex);
    if (g_cache.expiresAtTick <= GetTickCount64()) {
        return S_OK;
    }

    if (g_cache.aimePresent) {
        std::memcpy(g_aimeId, g_cache.aimeData, sizeof(g_aimeId));
        g_aimePresent = true;
    }
    if (g_cache.felicaPresent) {
        std::memcpy(g_felicaId, g_cache.felicaData, sizeof(g_felicaId));
        g_felicaPresent = true;
    }
    return S_OK;
}

HRESULT aime_io_nfc_get_aime_id(uint8_t unit_no, uint8_t* luid, size_t luid_size) {
    if (unit_no != 0 || luid == nullptr || luid_size < sizeof(g_aimeId) || !g_aimePresent) {
        return S_FALSE;
    }
    std::memcpy(luid, g_aimeId, sizeof(g_aimeId));
    return S_OK;
}

HRESULT aime_io_nfc_get_felica_id(uint8_t unit_no, uint64_t* IDm) {
    if (unit_no != 0 || IDm == nullptr || !g_felicaPresent) {
        return S_FALSE;
    }

    uint64_t value = 0;
    for (uint8_t byte : g_felicaId) {
        value = (value << 8) | byte;
    }
    *IDm = value;
    return S_OK;
}

void aime_io_led_set_color(uint8_t unit_no, uint8_t r, uint8_t g, uint8_t b) {
    (void)unit_no;
    (void)r;
    (void)g;
    (void)b;
}

} // extern "C"
