// Assertion helpers shared by the TypeScript examples.
//
// Mirrors the C# `Check` class in lib/Durable.cs so a ported example asserts
// exactly the same facts, with the same failure messages, as its original.

function format(value: unknown): string {
  if (value === null || value === undefined) {
    return "(null)";
  }

  if (typeof value === "string") {
    return `"${value}"`;
  }

  return String(value);
}

function fail(message: string): never {
  throw new Error(message);
}

export const Check = {
  that(condition: boolean, because: string): void {
    if (!condition) {
      fail(`Assertion failed: ${because}`);
    }
  },

  equal<T>(expected: T, actual: T, because: string): void {
    if (!Object.is(expected, actual)) {
      fail(
        `Assertion failed: ${because}\n  expected: ${format(expected)}\n  actual:   ${format(actual)}`,
      );
    }
  },

  sequence<T>(expected: readonly T[], actual: readonly T[], because: string): void {
    const same =
      expected.length === actual.length &&
      expected.every((value, index) => Object.is(value, actual[index]));

    if (!same) {
      fail(
        `Assertion failed: ${because}\n  expected: [${expected.map(format).join(", ")}]\n  actual:   [${actual.map(format).join(", ")}]`,
      );
    }
  },

  notNull<T>(value: T | null | undefined, because: string): T {
    if (value === null || value === undefined) {
      fail(`Assertion failed: ${because}\n  expected: not null\n  actual:   null`);
    }

    return value;
  },

  contains(haystack: string, needle: string, because: string): void {
    if (!haystack.toLowerCase().includes(needle.toLowerCase())) {
      fail(
        `Assertion failed: ${because}\n  expected to contain: ${needle}\n  actual:              ${haystack}`,
      );
    }
  },
};
