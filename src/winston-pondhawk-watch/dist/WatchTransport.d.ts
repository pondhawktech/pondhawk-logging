import Transport from 'winston-transport';
export interface WatchTransportOptions extends Transport.TransportStreamOptions {
    serverUrl: string;
    domain: string;
    category?: string;
    batchSize?: number;
    flushInterval?: number;
}
export declare class WatchTransport extends Transport {
    private readonly serverUrl;
    private readonly domain;
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
