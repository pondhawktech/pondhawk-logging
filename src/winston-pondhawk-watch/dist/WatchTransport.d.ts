import Transport from 'winston-transport';
export interface WatchTransportOptions extends Transport.TransportStreamOptions {
    /**
     * The Watch URL — `scheme://[key@]host[:port][/base-path]/<domain>`, e.g. `http://localhost:11000/my-app`
     * or `https://pwk_id_secret@watch.example.com/my-app`. The last path segment is the domain; the optional
     * user-info is a Watch API key, sent as `Authorization: Bearer`.
     */
    url: string;
    /** Category stamped on every event. Defaults to the domain. */
    category?: string;
    batchSize?: number;
    flushInterval?: number;
}
export declare class WatchTransport extends Transport {
    private readonly watchUrl;
    private readonly sinkUrl;
    private readonly category;
    private readonly batchSize;
    private readonly flushIntervalMs;
    private buffer;
    private timer;
    constructor(opts: WatchTransportOptions);
    log(info: Record<string, unknown>, callback: () => void): void;
    flush(): Promise<void>;
    close(): void;
}
