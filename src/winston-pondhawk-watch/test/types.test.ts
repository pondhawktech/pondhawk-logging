import { describe, it, expect } from 'vitest';
import { Level, PayloadType, type LogEvent, type LogEventBatch } from '../src/types.js';
import { ulid } from '../src/ulid.js';

describe('Level enum', () => {
  it('has correct numeric values', () => {
    expect(Level.Verbose).toBe(0);
    expect(Level.Debug).toBe(1);
    expect(Level.Information).toBe(2);
    expect(Level.Warning).toBe(3);
    expect(Level.Error).toBe(4);
    expect(Level.Fatal).toBe(5);
  });
});

describe('PayloadType enum', () => {
  it('has correct numeric values', () => {
    expect(PayloadType.None).toBe(0);
    expect(PayloadType.Json).toBe(1);
    expect(PayloadType.Sql).toBe(2);
    expect(PayloadType.Xml).toBe(3);
    expect(PayloadType.Text).toBe(4);
    expect(PayloadType.Yaml).toBe(5);
  });
});

describe('LogEvent interface', () => {
  it('creates a valid LogEvent with PascalCase properties', () => {
    const event: LogEvent = {
      Category: 'TestApp',
      CorrelationId: '01ARZ3NDEKTSV4RRFFQ69G5FAV',
      Title: 'Test message',
      Tenant: '',
      Subject: '',
      Tag: '',
      Level: Level.Information,
      Color: 0,
      Nesting: 0,
      Occurred: '2025-01-01T00:00:00.000Z',
      Type: PayloadType.None,
      Payload: null,
      ErrorType: null,
    };

    const json = JSON.stringify(event);
    const parsed = JSON.parse(json);

    expect(parsed.Category).toBe('TestApp');
    expect(parsed.CorrelationId).toBe('01ARZ3NDEKTSV4RRFFQ69G5FAV');
    expect(parsed.Title).toBe('Test message');
    expect(parsed.Level).toBe(Level.Information);
    expect(parsed.Type).toBe(PayloadType.None);
    expect(parsed.Occurred).toBe('2025-01-01T00:00:00.000Z');
    // Verify PascalCase - no camelCase keys
    expect(parsed.category).toBeUndefined();
    expect(parsed.correlationId).toBeUndefined();
    expect(parsed.title).toBeUndefined();
  });
});

describe('LogEventBatch interface', () => {
  it('creates a valid batch with PascalCase properties', () => {
    const batch: LogEventBatch = {
      Uid: ulid(),
      Domain: 'TestDomain',
      Events: [],
    };

    const json = JSON.stringify(batch);
    const parsed = JSON.parse(json);

    expect(parsed.Uid).toBeDefined();
    expect(parsed.Domain).toBe('TestDomain');
    expect(parsed.Events).toEqual([]);
    // Verify PascalCase
    expect(parsed.uid).toBeUndefined();
    expect(parsed.domain).toBeUndefined();
    expect(parsed.events).toBeUndefined();
  });
});

describe('ulid', () => {
  it('generates a 26-character string', () => {
    const id = ulid();
    expect(id).toHaveLength(26);
  });

  it('uses only Crockford base32 characters', () => {
    const id = ulid();
    expect(id).toMatch(/^[0-9A-HJKMNP-TV-Z]{26}$/);
  });

  it('generates unique values', () => {
    const ids = new Set(Array.from({ length: 100 }, () => ulid()));
    expect(ids.size).toBe(100);
  });

  it('is lexicographically sortable by time', () => {
    const id1 = ulid();
    // Advance time slightly to guarantee different timestamp
    const start = Date.now();
    while (Date.now() === start) {
      // busy-wait for next ms
    }
    const id2 = ulid();
    expect(id1 < id2).toBe(true);
  });
});
