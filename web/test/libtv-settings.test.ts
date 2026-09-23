import { describe, expect, test } from "bun:test";

import { isAdminLibTVSetting } from "../src/services/api/libtv";

describe("LibTV admin setting response", () => {
    test("accepts the .NET response for an unset setting with a null timestamp", () => {
        expect(isAdminLibTVSetting({ enabled: false, hasToken: false, updatedAt: null })).toBe(true);
    });

    test("accepts saved settings and rejects malformed response fields", () => {
        expect(isAdminLibTVSetting({ enabled: true, hasToken: true, updatedAt: "2026-09-23T02:00:00Z" })).toBe(true);
        expect(isAdminLibTVSetting({ enabled: false, hasToken: false, updatedAt: 0 })).toBe(false);
        expect(isAdminLibTVSetting({ enabled: "false", hasToken: false })).toBe(false);
    });
});
