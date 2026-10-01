import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { WatchTransport } from '../src/WatchTransport.js';
import { Level, PayloadType } from '../src/types.js';

// Helper to invoke the transport's log method as Winston would
function logMessage(
  transport: WatchTransport,
  level: string,
  message: string,
  meta?: Record<string, unknown>
): void {
  const info: Record<string, unknown> = {
    level,
    message,
    [Symbol.for('level')]: level,
    ...meta,
  };
  transport.log(info, () => {});
}

describe('WatchTransport', () => {
  let transport: WatchTransport;
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn().mockResolvedValue({ ok: true, status: 200 });
    vi.stubGlobal('fetch', fetchSpy);

    transport = new WatchTransport({
      url: 'http://localhost:11000/TestApp',
      batchSize: 10,
      flushInterval: 60000, // Long interval so we control flushing manually
    });
  });

  afterEach(() => {
    transport.close();
    vi.restoreAllMocks();
  });

  describe('level mapping', () => {
    const levelCases: [string, Level][] = [
      ['error', Level.Error],
      ['warn', Level.Warning],
      ['info', Level.Information],
      ['http', Level.Debug],
      ['verbose', Level.Debug],
      ['debug', Level.Debug],
      ['silly', Level.Verbose],
    ];

    it.each(levelCases)(
      'maps Winston "%s" to Watch Level %i',
      async (winstonLevel, expectedWatchLevel) => {
        logMessage(transport, winstonLevel, 'test');
        await transport.flush();

        const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
        expect(body.Events[0].Level).toBe(expectedWatchLevel);
      }
    );
  });

  describe('event construction', () => {
    it('sets Title from message', async () => {
      logMessage(transport, 'info', 'Hello World');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events[0].Title).toBe('Hello World');
    });

    it('sets Category from domain by default', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events[0].Category).toBe('TestApp');
    });

    it('uses custom category when provided', async () => {
      transport.close();
      transport = new WatchTransport({
        url: 'http://localhost:11000/TestApp',
        category: 'CustomCategory',
        batchSize: 10,
        flushInterval: 60000,
      });

      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events[0].Category).toBe('CustomCategory');
    });

    it('sets Occurred as ISO 8601 string', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const occurred = body.Events[0].Occurred;
      expect(new Date(occurred).toISOString()).toBe(occurred);
    });

    it('initializes empty string fields', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const event = body.Events[0];
      expect(event.CorrelationId).toBe('');
      expect(event.Tenant).toBe('');
      expect(event.Subject).toBe('');
      expect(event.Tag).toBe('');
    });
  });

  describe('PascalCase JSON', () => {
    it('serializes batch with PascalCase property names', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      // Batch-level
      expect(body).toHaveProperty('Uid');
      expect(body).toHaveProperty('Domain');
      expect(body).toHaveProperty('Events');
      // Event-level
      const event = body.Events[0];
      expect(event).toHaveProperty('Category');
      expect(event).toHaveProperty('CorrelationId');
      expect(event).toHaveProperty('Title');
      expect(event).toHaveProperty('Level');
      expect(event).toHaveProperty('Occurred');
      expect(event).toHaveProperty('Type');
      expect(event).toHaveProperty('Payload');
      expect(event).toHaveProperty('ErrorType');
    });
  });

  describe('batching by count', () => {
    it('flushes automatically when batchSize is reached', async () => {
      for (let i = 0; i < 10; i++) {
        logMessage(transport, 'info', `message ${i}`);
      }

      // Allow the flush promise to resolve
      await vi.waitFor(() => {
        expect(fetchSpy).toHaveBeenCalledTimes(1);
      });

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events).toHaveLength(10);
    });

    it('does not flush before batchSize is reached', async () => {
      for (let i = 0; i < 9; i++) {
        logMessage(transport, 'info', `message ${i}`);
      }

      // Give a tick for any async flush to occur
      await new Promise((r) => setTimeout(r, 10));
      expect(fetchSpy).not.toHaveBeenCalled();
    });
  });

  describe('batching by interval', () => {
    it('flushes on timer interval', async () => {
      vi.useFakeTimers();

      const timerTransport = new WatchTransport({
        url: 'http://localhost:11000/TestApp',
        batchSize: 100,
        flushInterval: 1000,
      });

      logMessage(timerTransport, 'info', 'delayed message');
      expect(fetchSpy).not.toHaveBeenCalled();

      vi.advanceTimersByTime(1000);

      // Allow the flush promise to resolve
      await vi.waitFor(() => {
        expect(fetchSpy).toHaveBeenCalledTimes(1);
      });

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events).toHaveLength(1);
      expect(body.Events[0].Title).toBe('delayed message');

      timerTransport.close();
      vi.useRealTimers();
    });
  });

  describe('metadata extraction', () => {
    it('extracts extra fields as JSON payload', async () => {
      logMessage(transport, 'info', 'Order processed', {
        orderId: 123,
        amount: 29.99,
      });
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const event = body.Events[0];
      expect(event.Type).toBe(PayloadType.Json);
      const payload = JSON.parse(event.Payload);
      expect(payload.orderId).toBe(123);
      expect(payload.amount).toBe(29.99);
    });

    it('excludes Winston internal symbols from payload', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const event = body.Events[0];
      // No metadata means no payload
      expect(event.Payload).toBeNull();
      expect(event.Type).toBe(PayloadType.None);
    });

    it('sets PayloadType.None when no metadata', async () => {
      logMessage(transport, 'info', 'plain message');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events[0].Type).toBe(PayloadType.None);
    });
  });

  describe('error handling', () => {
    it('sets ErrorType from Error instance in metadata', async () => {
      logMessage(transport, 'error', 'Something failed', {
        error: new TypeError('bad input'),
      });
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const event = body.Events[0];
      expect(event.ErrorType).toBe('TypeError');
    });

    it('serializes Error properties in payload', async () => {
      const err = new Error('test error');
      logMessage(transport, 'error', 'Failure', { error: err });
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      const payload = JSON.parse(body.Events[0].Payload);
      expect(payload.error.name).toBe('Error');
      expect(payload.error.message).toBe('test error');
      expect(payload.error.stack).toBeDefined();
    });

    it('logs HTTP errors to console.error without throwing', async () => {
      fetchSpy.mockResolvedValueOnce({ ok: false, status: 500 });
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(consoleSpy).toHaveBeenCalledWith(
        expect.stringContaining('HTTP 500')
      );
    });

    it('logs network errors to console.error without throwing', async () => {
      fetchSpy.mockRejectedValueOnce(new Error('ECONNREFUSED'));
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(consoleSpy).toHaveBeenCalledWith(
        'winston-pondhawk-watch: Failed to send batch',
        expect.any(Error)
      );
    });
  });

  describe('flush on close', () => {
    it('sends remaining events when close is called', async () => {
      logMessage(transport, 'info', 'final message');
      transport.close();

      await vi.waitFor(() => {
        expect(fetchSpy).toHaveBeenCalledTimes(1);
      });

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Events).toHaveLength(1);
      expect(body.Events[0].Title).toBe('final message');
    });

    it('does not flush when buffer is empty on close', () => {
      transport.close();
      expect(fetchSpy).not.toHaveBeenCalled();
    });
  });

  describe('batch structure', () => {
    it('sets Domain from constructor option', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Domain).toBe('TestApp');
    });

    it('generates a ULID for batch Uid', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      const body = JSON.parse(fetchSpy.mock.calls[0][1].body);
      expect(body.Uid).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
    });

    it('posts to /api/sink endpoint', async () => {
      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(fetchSpy).toHaveBeenCalledWith(
        'http://localhost:11000/api/sink',
        expect.objectContaining({
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
        })
      );
    });

    it('accepts a trailing slash after the domain', async () => {
      transport.close();
      transport = new WatchTransport({
        url: 'http://localhost:11000/TestApp/',
        batchSize: 10,
        flushInterval: 60000,
      });

      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(fetchSpy).toHaveBeenCalledWith('http://localhost:11000/api/sink', expect.any(Object));
      expect(JSON.parse(fetchSpy.mock.calls[0][1].body).Domain).toBe('TestApp');
    });

    it('posts under a base path', async () => {
      transport.close();
      transport = new WatchTransport({ url: 'https://ingest.example.com/watch/TestApp', batchSize: 10, flushInterval: 60000 });

      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(fetchSpy).toHaveBeenCalledWith('https://ingest.example.com/watch/api/sink', expect.any(Object));
    });

    it('sends the API key as a Bearer header, never in the URL', async () => {
      transport.close();
      transport = new WatchTransport({ url: 'https://pwk_a1_secret@watch.example.com/TestApp', batchSize: 10, flushInterval: 60000 });

      logMessage(transport, 'info', 'test');
      await transport.flush();

      const [url, init] = fetchSpy.mock.calls[0];
      expect(url).toBe('https://watch.example.com/api/sink');
      expect(init.headers.Authorization).toBe('Bearer pwk_a1_secret');
    });

    it('never logs the key when a post fails', async () => {
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      fetchSpy.mockResolvedValue({ ok: false, status: 401 });
      transport.close();
      transport = new WatchTransport({ url: 'https://pwk_a1_secret@watch.example.com/TestApp', batchSize: 10, flushInterval: 60000 });

      logMessage(transport, 'info', 'test');
      await transport.flush();

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('https://***@watch.example.com/TestApp'));
      expect(errorSpy.mock.calls.flat().join(' ')).not.toContain('secret');
    });
  });
});
