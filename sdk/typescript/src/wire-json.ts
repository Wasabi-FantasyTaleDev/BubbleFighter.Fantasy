/** JSON transport values preserve integer tokens before JavaScript can round them. */
import { parse, stringify } from "lossless-json";

/** Decodes safe integers as number and larger integer tokens as bigint, including nested values. */
export function parseWireJson(text: string): unknown {
  return parse(text, undefined, (token: string) => {
    const value = Number(token);
    if (/^-?\d+$/.test(token) && !Number.isSafeInteger(value)) return BigInt(token);
    if (!Number.isFinite(value)) throw new RangeError("non-finite JSON number");
    return value;
  });
}

/** Writes bigint as an exact JSON integer. Use bigint literals for values beyond the safe number range. */
export function stringifyWireJson(value: unknown): string {
  const text = stringify(value, (_key, item) => {
    if (typeof item === "number" && (!Number.isFinite(item) || (Number.isInteger(item) && !Number.isSafeInteger(item))))
      throw new RangeError("use bigint for integers beyond the safe number range");
    return item;
  });
  if (text === undefined) throw new TypeError("value has no JSON representation");
  return text;
}
