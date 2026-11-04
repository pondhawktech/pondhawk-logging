const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);
/** Parses a Watch URL, throwing an `Error` (that never contains the key) when it is invalid. */
export function parseWatchUrl(url) {
    let parsed;
    try {
        parsed = new URL(url.trim());
    }
    catch {
        throw new Error('winston-pondhawk-watch: url must be an absolute http or https URL, e.g. http://localhost:11000/my-domain');
    }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
        throw new Error('winston-pondhawk-watch: url must be an absolute http or https URL, e.g. http://localhost:11000/my-domain');
    }
    const hasKey = parsed.username !== '' || parsed.password !== '';
    const display = new URL(parsed.href);
    display.username = hasKey ? '***' : '';
    display.password = '';
    const redacted = display.href;
    if (parsed.search || parsed.hash) {
        throw new Error(`winston-pondhawk-watch: a Watch URL has no query string or fragment: ${redacted}`);
    }
    let apiKey = null;
    if (hasKey) {
        if (parsed.password !== '') {
            throw new Error(`winston-pondhawk-watch: a Watch URL's user-info is a single API key, not a user name and password: ${redacted}`);
        }
        apiKey = decodeURIComponent(parsed.username);
        if (parsed.protocol === 'http:' && !isLoopback(parsed.hostname)) {
            throw new Error(`winston-pondhawk-watch: a Watch URL carrying an API key must use https (plain http is allowed only for loopback): ${redacted}`);
        }
    }
    const path = parsed.pathname.replace(/\/+$/, '');
    const lastSlash = path.lastIndexOf('/');
    const domain = decodeURIComponent(path.slice(lastSlash + 1));
    if (!domain.trim()) {
        throw new Error(`winston-pondhawk-watch: a Watch URL must end with the domain to log to, e.g. http://localhost:11000/my-domain: ${redacted}`);
    }
    const baseUrl = `${parsed.protocol}//${parsed.host}${path.slice(0, lastSlash + 1)}`;
    return { baseUrl, domain, apiKey, redacted };
}
// A real loopback address: localhost, [::1], or an IPv4 address in 127.0.0.0/8 — not merely a name that
// starts with "127." (e.g. 127.evil.example), which would let a key go out over plain http.
const IPV4_LOOPBACK = /^127\.(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)\.(25[0-5]|2[0-4]\d|1?\d?\d)$/;
function isLoopback(hostname) {
    return LOOPBACK_HOSTS.has(hostname) || IPV4_LOOPBACK.test(hostname);
}
