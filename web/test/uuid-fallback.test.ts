import { describe, expect, test } from "bun:test";

import { randomUUID } from "../src/lib/uuid";

describe("randomUUID fallback", () => {
    test("returns valid UUID v4", () => {
        const re = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
        for (let i = 0; i < 200; i++) expect(randomUUID()).toMatch(re);
    });

    test("uniqueness across 200 runs", () => {
        const seen = new Set<string>();
        for (let i = 0; i < 200; i++) seen.add(randomUUID());
        expect(seen.size).toBe(200);
    });
});
