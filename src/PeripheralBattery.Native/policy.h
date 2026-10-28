#pragma once
#include <algorithm>
#include <string>

namespace Policy {
inline std::string upper(std::string text) {
    for (char& c : text) if (c >= 'a' && c <= 'z') c -= 'a' - 'A';
    return text;
}
inline std::string hidPath(std::string path) {
    path = upper(path);
    if (path.size() >= 4 && path.compare(path.size() - 4, 4, "\\KBD") == 0) path.resize(path.size() - 4);
    return path;
}
// Windows gives each USB composite interface a different MI/suffix. Remove only
// the matching pair, retaining the physical receiver's port/instance identity.
inline std::string physicalReceiver(std::string key) {
    key = upper(key);
    if (key.compare(0, 4, "USB\\") != 0) return {};
    auto mi = key.find("&MI_");
    if (mi == std::string::npos) return key;
    if (mi + 6 >= key.size() || key[mi + 6] != '\\') return {};
    auto hex = [](char c) { return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'); };
    if (!hex(key[mi + 4]) || !hex(key[mi + 5])) return {};
    auto suffix = "&00" + key.substr(mi + 4, 2);
    if (key.size() < suffix.size() || key.compare(key.size() - suffix.size(), suffix.size(), suffix) != 0) return {};
    key.resize(key.size() - suffix.size()); key.erase(mi, 6); return key;
}
inline unsigned trayRetryMilliseconds(unsigned failures) {
    return std::min(30000u, 1000u << std::min(failures, 5u));
}
struct LowBatteryEpisode {
    bool notified = false;
    int rearmAbove = 25;
    bool observe(int percent, bool explicitWarning, int threshold, bool enabled) {
        if (notified && percent >= 0 && percent <= 100 && percent > rearmAbove) notified = false;
        bool low = (percent >= 0 && percent <= threshold) || (percent < 0 && explicitWarning);
        if (low && !notified) rearmAbove = std::min(100, threshold + 5);
        // Disabling notifications also consumes the current episode; toggling the
        // option or changing a palette must not create another warning.
        if (low && !enabled) notified = true;
        return low && enabled && !notified;
    }
    void acknowledge() { notified = true; }
};
}
