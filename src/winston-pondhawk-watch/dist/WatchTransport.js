import Transport from 'winston-transport';
import { Level, PayloadType } from './types.js';
import { ulid } from './ulid.js';
import { parseWatchUrl } from './watchUrl.js';
const winstonToWatchLevel = {
    error: Level.Error,
    warn: Level.Warning,
    info: Level.Information,
    http: Level.Debug,
    verbose: Level.Debug,
    debug: Level.Debug,
    silly: Level.Verbose,
};
export class WatchTransport extends Transport {
    watchUrl;
    sinkUrl;
    category;
    batchSize;
    flushIntervalMs;
    buffer = [];
    timer = null;
    constructor(opts) {
        super(opts);
        this.watchUrl = parseWatchUrl(opts.url);
        this.sinkUrl = new URL('api/sink', this.watchUrl.baseUrl).href;
        this.category = opts.category ?? this.watchUrl.domain;
        this.batchSize = opts.batchSize ?? 50;
        this.flushIntervalMs = opts.flushInterval ?? 2000;
        this.timer = setInterval(() => this.flush(), this.flushIntervalMs);
        this.timer.unref();
    }
    log(info, callback) {
        const level = winstonToWatchLevel[info.level] ?? Level.Information;
        const { level: _level, message, timestamp: _timestamp, [Symbol.for('level')]: _lvl, [Symbol.for('splat')]: _splat, ...metadata } = info;
        const event = {
            Category: this.category,
            CorrelationId: '',
            Title: String(message ?? ''),
            Tenant: '',
            Subject: '',
            Tag: '',
            Level: level,
            Color: 0,
            Nesting: 0,
            Occurred: new Date().toISOString(),
            Type: PayloadType.None,
            Payload: null,
            ErrorType: null,
        };
        // Check for Error instances in metadata
        for (const value of Object.values(metadata)) {
            if (value instanceof Error) {
                event.ErrorType = value.constructor.name;
                break;
            }
        }
        if (Object.keys(metadata).length > 0) {
            event.Payload = JSON.stringify(metadata, (_key, value) => {
                if (value instanceof Error) {
                    return { name: value.name, message: value.message, stack: value.stack };
                }
                return value;
            });
            event.Type = PayloadType.Json;
        }
        this.buffer.push(event);
        if (this.buffer.length >= this.batchSize) {
            this.flush();
        }
        callback();
    }
    async flush() {
        if (this.buffer.length === 0)
            return;
        const events = this.buffer;
        this.buffer = [];
        const batch = {
            Uid: ulid(),
            Domain: this.watchUrl.domain,
            Events: events,
        };
        try {
            const headers = { 'Content-Type': 'application/json' };
            if (this.watchUrl.apiKey !== null) {
                headers.Authorization = `Bearer ${this.watchUrl.apiKey}`;
            }
            const response = await fetch(this.sinkUrl, {
                method: 'POST',
                headers,
                body: JSON.stringify(batch),
            });
            if (!response.ok) {
                console.error(`winston-pondhawk-watch: HTTP ${response.status} posting to ${this.watchUrl.redacted}`);
            }
        }
        catch (err) {
            console.error('winston-pondhawk-watch: Failed to send batch', err);
        }
    }
    close() {
        if (this.timer) {
            clearInterval(this.timer);
            this.timer = null;
        }
        this.flush();
    }
}
