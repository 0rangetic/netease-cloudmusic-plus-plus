import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('./triline_agent.js', import.meta.url), 'utf8');

// Run the complete agent without a client process. The mock models the native
// vector descriptors separately from their storage so two vectors can alias.
function fixture(sharedSecondary) {
  const memory = Buffer.alloc(1024 * 1024);
  const memoryBase = 0x10000;
  let cursor = memoryBase;
  const hooks = new Map();
  const messages = [];
  const exports = new Map();
  const nativeVectors = new Map();
  const offsets = { vectorOf: 0x100, fontHook: 0x200, selectLine: 0x300,
    rendererStart: 0x400, rendererEnd: 0x500 };
  const imageBase = 0x10000000;

  class Pointer {
    constructor(address) { this.address = Number(address); }
    add(value) { return new Pointer(this.address + Number(value)); }
    sub(value) { return new Pointer(this.address - value.address); }
    toInt32() { return this.address | 0; }
    isNull() { return this.address === 0; }
    equals(other) { return this.address === other.address; }
    offset() {
      const offset = this.address - memoryBase;
      assert.ok(offset >= 0 && offset < memory.length, 'native memory access is in the fixture');
      return offset;
    }
    readPointer() { return new Pointer(memory.readBigUInt64LE(this.offset())); }
    writePointer(value) { memory.writeBigUInt64LE(BigInt(value.address), this.offset()); }
    readS32() { return memory.readInt32LE(this.offset()); }
    writeS32(value) { memory.writeInt32LE(value, this.offset()); }
    readU64() {
      const number = Number(memory.readBigUInt64LE(this.offset()));
      return { toNumber: () => number };
    }
    writeU64(value) { memory.writeBigUInt64LE(BigInt(value), this.offset()); }
    readFloat() { return memory.readFloatLE(this.offset()); }
    writeFloat(value) { memory.writeFloatLE(value, this.offset()); }
    readByteArray(length) { return Uint8Array.from(memory.subarray(this.offset(), this.offset() + length)).buffer; }
    readUtf16String(length) {
      const start = this.offset();
      let end = start;
      if (length === undefined) {
        while (memory.readUInt16LE(end) !== 0) end += 2;
      } else {
        end += length * 2;
      }
      return memory.toString('utf16le', start, end);
    }
  }
  const ptr = value => new Pointer(value);
  function alloc(size) {
    const result = ptr(cursor);
    cursor += (size + 15) & ~15;
    assert.ok(cursor - memoryBase < memory.length);
    return result;
  }
  function string(text) {
    const result = alloc((text.length + 1) * 2);
    memory.write(text, result.offset(), 'utf16le');
    return result;
  }
  function vector(rows, sharedBegin = null) {
    const descriptor = alloc(24);
    const begin = sharedBegin ?? alloc(rows.length * 64);
    descriptor.writePointer(begin);
    descriptor.add(8).writePointer(begin.add(rows.length * 64));
    descriptor.add(16).writePointer(begin.add(rows.length * 64));
    if (!sharedBegin) {
      rows.forEach(([start, duration, text], index) => {
        const line = begin.add(index * 64);
        line.writeS32(start);
        line.add(4).writeS32(duration);
        const value = line.add(0x20);
        if (text.length > 7) value.writePointer(string(text));
        else memory.write(text, value.offset(), 'utf16le');
        value.add(0x10).writeU64(text.length);
        value.add(0x18).writeU64(Math.max(7, text.length));
      });
    }
    return { descriptor, begin, count: rows.length };
  }
  const original = vector([
    [37180, 5980, '原文前句'], [43160, 7000, '原文当前'], [50160, 5000, '原文下句'],
  ]);
  const nativeRows = [
    [37230, 3260, 'roma-before'], [40490, 9720, 'roma-current'], [50210, 5000, 'roma-next'],
  ];
  const romanization = vector(nativeRows);
  const secondary = vector(nativeRows, sharedSecondary ? romanization.begin : null);
  const translation = vector([
    [37230, 3260, '上一译文'], [40490, 9720, '当前译文'], [50210, 5000, '下一译文'],
  ]);
  const translationParser = alloc(8);
  const romanizationParser = alloc(8);
  nativeVectors.set(translationParser.address, translation.descriptor);
  nativeVectors.set(romanizationParser.address, romanization.descriptor);
  const list = alloc(16);
  list.writePointer(translationParser);
  list.add(8).writePointer(romanizationParser);
  const manager = alloc(0x28);
  manager.add(0x18).writePointer(list);
  manager.add(0x20).writePointer(list.add(16));
  const parser = alloc(8);
  parser.writePointer(manager);
  const context = alloc(0x18);
  context.add(0x10).writePointer(parser);
  const producer = alloc(0x20);
  producer.add(8).writePointer(context);
  producer.add(0x10).writePointer(original.descriptor);
  producer.add(0x18).writePointer(secondary.descriptor);

  function exportAddress(module, name) {
    const key = `${module}!${name}`;
    if (!exports.has(key)) exports.set(key, ptr(0x20000000 + exports.size * 0x100));
    return exports.get(key);
  }
  function NativeFunction(address) {
    if (address.address === imageBase + offsets.vectorOf) {
      return parserPointer => {
        assert.ok(nativeVectors.has(parserPointer.address), 'vectorOf receives a parser');
        return nativeVectors.get(parserPointer.address);
      };
    }
    // No desktop window is present, so font resizing never runs in this test.
    return () => ptr(0);
  }
  const sandbox = {
    LYRIC_PROFILE: { addresses: offsets, signatures: {} },
    Process: {
      arch: 'x64', id: 123,
      getModuleByName: module => ({
        base: ptr(imageBase), getExportByName: name => exportAddress(module, name),
      }),
      findRangeByAddress: () => ({ protection: 'r-x' }),
    },
    NativeFunction, ptr,
    Memory: { alloc, allocUtf16String: string },
    Interceptor: { attach: (address, callbacks) => hooks.set(address.address, callbacks) },
    Thread: { backtrace: () => [] }, Backtracer: { ACCURATE: 1 },
    setInterval: () => 0, send: message => messages.push(message),
  };
  vm.runInNewContext(source, sandbox, { filename: 'triline_agent.js' });
  assert.equal(messages.find(message => message.type === 'ready')?.pid, 123);
  const select = hooks.get(imageBase + offsets.selectLine);
  assert.ok(select, 'the real agent registered its line-selection hook');
  function times(track) {
    return Array.from({ length: track.count }, (_, index) => {
      const entry = track.begin.add(index * 64);
      return [entry.readS32(), entry.add(4).readS32()];
    });
  }
  function snapshot(track) {
    return Buffer.from(memory.subarray(track.begin.offset(), track.begin.offset() + track.count * 64));
  }
  function draw(module, name, text) {
    const callbacks = hooks.get(exportAddress(module, name).address);
    assert.ok(callbacks, `the real agent registered ${name}`);
    const args = [ptr(0), string(text), ptr(text.length)];
    const invocation = {};
    callbacks.onEnter.call(invocation, args);
    const result = args[1].readUtf16String(args[2].toInt32());
    callbacks.onLeave.call(invocation);
    return result;
  }
  return { select, producer, ptr, original, secondary, romanization, translation,
    times, snapshot, draw, messages };
}

for (const sharedSecondary of [true, false]) {
  test(`pairs native timestamps before alignment (${sharedSecondary ? 'shared' : 'separate'} vector storage)`, () => {
    const f = fixture(sharedSecondary);
    assert.equal(f.secondary.begin.equals(f.romanization.begin), sharedSecondary);
    const originalBefore = f.snapshot(f.original);
    const translationBefore = f.snapshot(f.translation);
    const romanizationBefore = f.snapshot(f.romanization);
    const secondaryBefore = f.snapshot(f.secondary);
    const invocation = {};
    f.select.onEnter.call(invocation, [f.producer, f.ptr(44000)]);
    try {
      assert.deepEqual(f.times(f.secondary), [[37180, 5980], [43160, 7000], [50160, 5000]],
        'native line selection sees aligned starts and durations');
      assert.deepEqual(f.snapshot(f.original), originalBefore, 'the original track is untouched');
      assert.deepEqual(f.snapshot(f.translation), translationBefore, 'the translation parser is untouched');
      if (!sharedSecondary) {
        assert.deepEqual(f.snapshot(f.romanization), romanizationBefore, 'a separate romanization parser is untouched');
      }
      for (const [module, name] of [
        ['gdiplus.dll', 'GdipAddPathString'], ['gdiplus.dll', 'GdipMeasureString'],
        ['gdi32.dll', 'GetTextExtentPoint32W'],
      ]) {
        assert.equal(f.draw(module, name, 'roma-current'), 'roma-current\n当前译文',
          `${name} still pairs the current romanization when timestamps differ by 2670 ms after alignment`);
      }
    } finally {
      f.select.onLeave.call(invocation);
    }
    assert.deepEqual(f.snapshot(f.secondary), secondaryBefore, 'selection return restores every auxiliary start and duration');
    assert.deepEqual(f.snapshot(f.romanization), romanizationBefore, 'aliased parser storage is restored');
    assert.deepEqual(f.snapshot(f.translation), translationBefore, 'translation remains unchanged after return');
    assert.equal(f.draw('gdiplus.dll', 'GdipAddPathString', 'roma-current'), 'roma-current\n当前译文',
      'cached rendering remains paired after the native selection returns');
    assert.equal(f.messages.filter(message => message.type === 'error').length, 0);
  });
}
