import Transport from 'winston-transport';
import type { LogEvent, LogEventBatch } from './types.js';
import { Level, PayloadType } from './types.js';
import { ulid } from './ulid.js';
import { parseWatchUrl, type WatchUrl } from './watchUrl.js';

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

const winstonToWatchLevel: Record<string, Level> = {
  error: Level.Error,
  warn: Level.Warning,
  info: Level.Information,
  http: Level.Debug,
  verbose: Level.Debug,
  debug: Level.Debug,
  silly: Level.Verbose,
};

export class WatchTransport extends Transport {
  private readonly watchUrl: WatchUrl;
  private readonly sinkUrl: string;
  private readonly category: string;
  private readonly batchSize: number;
  private readonly flushIntervalMs: number;

  private buffer: LogEvent[] = [];
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(opts: WatchTransportOptions) {
    super(opts);
    this.watchUrl = parseWatchUrl(opts.url);
    this.sinkUrl = new URL('api/sink', this.watchUrl.baseUrl).href;
    this.category = opts.category ?? this.watchUrl.domain;
    this.batchSize = opts.batchSize ?? 50;
    this.flushIntervalMs = opts.flushInterval ?? 2000;

    this.timer = setInterval(() => this.flush(), this.flushIntervalMs);
    this.timer.unref();
  }

  log(info: Record<string, unknown>, callback: () => void): void {
    const level = winstonToWatchLevel[info.level as string] ?? Level.Information;

    const {
      level: _level,
      message,
      timestamp: _timestamp,
      [Symbol.for('level') as unknown as string]: _lvl,
      [Symbol.for('splat') as unknown as string]: _splat,
      ...metadata
    } = info;

    const event: LogEvent = {
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

  async flush(): Promise<void> {
    if (this.buffer.length === 0) return;

    const events = this.buffer;
    this.buffer = [];

    const batch: LogEventBatch = {
      Uid: ulid(),
      Domain: this.watchUrl.domain,
      Events: events,
    };

    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' };
      if (this.watchUrl.apiKey !== null) {
        headers.Authorization = `Bearer ${this.watchUrl.apiKey}`;
      }

      const response = await fetch(this.sinkUrl, {
        method: 'POST',
        headers,
        body: JSON.stringify(batch),
      });

      if (!response.ok) {
        console.error(
          `winston-pondhawk-watch: HTTP ${response.status} posting to ${this.watchUrl.redacted}`
        );
      }
    } catch (err) {
      console.error('winston-pondhawk-watch: Failed to send batch', err);
    }
  }

  close(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.flush();
  }
}
