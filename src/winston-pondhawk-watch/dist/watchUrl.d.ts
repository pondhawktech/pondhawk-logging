/**
 * A parsed Watch URL — the single string that tells a process where to log:
 * `scheme://[key@]host[:port][/base-path]/<domain>`.
 *
 * The last path segment is the domain; everything before it is the base the Watch API lives under.
 * The optional user-info is a Watch API key: it is sent as `Authorization: Bearer <key>`, never in a
 * request URL (Node's `fetch` refuses URLs with credentials), and never appears in `redacted`.
 */
export interface WatchUrl {
    /** Absolute base the Watch API paths resolve against, ending in `/`. */
    baseUrl: string;
    /** The domain log events are delivered under (decoded). */
    domain: string;
    /** The API key, or `null` when the URL carries none. */
    apiKey: string | null;
    /** The URL with any key masked as `***` — safe to log. */
    redacted: string;
}
/** Parses a Watch URL, throwing an `Error` (that never contains the key) when it is invalid. */
export declare function parseWatchUrl(url: string): WatchUrl;
