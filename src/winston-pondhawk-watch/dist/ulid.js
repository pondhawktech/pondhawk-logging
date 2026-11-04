import { randomBytes } from 'node:crypto';
const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
function encodeTime(time, length) {
    let result = '';
    for (let i = length - 1; i >= 0; i--) {
        result = CROCKFORD[time % 32] + result;
        time = Math.floor(time / 32);
    }
    return result;
}
function encodeRandom(length) {
    const bytes = randomBytes(length);
    let result = '';
    for (let i = 0; i < length; i++) {
        result += CROCKFORD[bytes[i] % 32];
    }
    return result;
}
export function ulid() {
    const time = Date.now();
    return encodeTime(time, 10) + encodeRandom(16);
}
