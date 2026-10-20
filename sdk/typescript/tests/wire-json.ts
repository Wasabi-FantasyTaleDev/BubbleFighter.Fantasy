import assert from "node:assert/strict";
import { test } from "node:test";
import { parseWireJson, stringifyWireJson } from "../src/wire-json.js";

test("nested signed and unsigned integer boundaries round trip without changing strings or fractions", () => {
  const text = '{"nested":[9007199254740993,-9223372036854775808,18446744073709551615],"text":"9007199254740993","safe":42,"fraction":1.25}';
  const value = parseWireJson(text);
  assert.deepEqual(value, { nested: [9007199254740993n, -9223372036854775808n, 18446744073709551615n], text: "9007199254740993", safe: 42, fraction: 1.25 });
  assert.equal(stringifyWireJson(value), text);
});

test("unsafe numeric inputs fail instead of writing an already rounded player id", () => {
  assert.throws(() => stringifyWireJson({ id: 9007199254740992 }), RangeError);
  assert.throws(() => stringifyWireJson({ value: Infinity }), RangeError);
  assert.throws(() => parseWireJson('{"value":1e999}'), RangeError);
  assert.throws(() => parseWireJson('{"value":01}'));
});
