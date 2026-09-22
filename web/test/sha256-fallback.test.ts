import { describe, expect, test } from "bun:test";
import { createHash } from "node:crypto";
import { sha256Hex, sha256HexFallback } from "../src/lib/sha256";

describe("sha256 fallback", () => {
    test("fallback matches node crypto across padding boundaries", () => {
        const sizes = [0, 1, 3, 55, 56, 63, 64, 65, 119, 120, 1000, 10000];
        for (const size of sizes) {
            const input = "x".repeat(size);
            const expected = createHash("sha256").update(input).digest("hex");
            expect(sha256HexFallback(new TextEncoder().encode(input))).toBe(expected);
        }
    });

    test("sha256Hex accepts strings and bytes", async () => {
        expect(await sha256Hex("abc")).toBe(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        expect(await sha256Hex(new TextEncoder().encode("abc"))).toBe(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    });
});
