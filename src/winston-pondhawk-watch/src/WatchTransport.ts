import Transport from 'winston-transport';
import type { LogEvent, LogEventBatch } from './types.js';
import { Level, PayloadType } from './types.js';
import { ulid } from './ulid.js';

export interface WatchTransportOptions extends Transport.TransportStreamOptions {
  serverUrl: string;
  domain: string;
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
  private readonly serverUrl: string;
  private readonly domain: string;
  private readonly category: string;
  private readonly batchSize: number;
  private readonly flushIntervalMs: number;

  private buffer: LogEvent[] = [];
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor(opts: WatchTransportOptions) {
    super(opts);
    this.serverUrl = opts.serverUrl.replace(/\/+$/, '');
    this.domain = opts.domain;
    this.category = opts.category ?? opts.domain;
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
      Domain: this.domain,
      Events: events,
    };

    try {
      const response = await fetch(`${this.serverUrl}/api/sink`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(batch),
      });

      if (!response.ok) {
        console.error(
          `winston-pondhawk-watch: HTTP ${response.status} posting to ${this.serverUrl}/api/sink`
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
