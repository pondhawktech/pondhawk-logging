import { describe, it, expect } from 'vitest';
import { parseWatchUrl } from '../src/watchUrl.js';

describe('parseWatchUrl', () => {
  it.each([
    ['http://localhost:11000/orders', 'http://localhost:11000/', 'orders'],
    ['http://localhost:11000/orders/', 'http://localhost:11000/', 'orders'],
    ['https://ingest.example.com/watch/orders', 'https://ingest.example.com/watch/', 'orders'],
    ['http://localhost:11000/Orders%20Service', 'http://localhost:11000/', 'Orders Service'],
  ])('splits %s into base and domain', (url, baseUrl, domain) => {
    const parsed = parseWatchUrl(url);
    expect(parsed.baseUrl).toBe(baseUrl);
    expect(parsed.domain).toBe(domain);
    expect(parsed.apiKey).toBeNull();
  });

  it('takes the key from user-info and redacts it', () => {
    const parsed = parseWatchUrl('https://pwk_a1_secret@watch.example.com/orders');
    expect(parsed.apiKey).toBe('pwk_a1_secret');
    expect(parsed.baseUrl).toBe('https://watch.example.com/');
    expect(parsed.redacted).toBe('https://***@watch.example.com/orders');
  });

  it.each(['http://pwk_a_s@localhost:11000/orders', 'http://pwk_a_s@127.0.0.1:11000/orders', 'http://pwk_a_s@[::1]:11000/orders'])(
    'allows a key over http to loopback: %s',
    (url) => expect(parseWatchUrl(url).apiKey).toBe('pwk_a_s'),
  );

  it.each(['http://pwk_a_s@127.evil.example/orders', 'http://pwk_a_s@127.0.0.1.evil.example/orders', 'http://pwk_a_s@localhost.evil.example/orders'])(
    'does not mistake a look-alike host for loopback: %s',
    (url) => expect(() => parseWatchUrl(url)).toThrow(/https/),
  );

  it('refuses a key over http elsewhere, without echoing it', () => {
    expect(() => parseWatchUrl('http://pwk_a_secret@watch.example.com/orders')).toThrow(/https/);
    expect(() => parseWatchUrl('http://pwk_a_secret@watch.example.com/orders')).not.toThrow(/secret/);
  });

  it.each([
    'not a url',
    'ftp://watch.example.com/orders',
    'http://localhost:11000',
    'http://localhost:11000/',
    'http://localhost:11000/orders?x=1',
    'http://localhost:11000/orders#frag',
    'https://user:password@watch.example.com/orders',
  ])('rejects %s', (url) => {
    expect(() => parseWatchUrl(url)).toThrow();
  });
});
