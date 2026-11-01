"""Assertion helpers shared by the Python examples.

Mirrors the C# ``Check`` class in ``lib/Durable.cs`` so a ported example asserts
exactly the same facts, with the same failure messages, as its original.
"""

from __future__ import annotations

from typing import Sequence, TypeVar

T = TypeVar("T")


def _format(value: object) -> str:
    if value is None:
        return "(null)"

    if isinstance(value, str):
        return f'"{value}"'

    return str(value)


def _fail(message: str) -> None:
    raise AssertionError(message)


class Check:
    """The five assertion operations the C# examples use."""

    @staticmethod
    def that(condition: bool, because: str) -> None:
        if not condition:
            _fail(f"Assertion failed: {because}")

    @staticmethod
    def equal(expected: T, actual: T, because: str) -> None:
        if expected != actual:
            _fail(
                f"Assertion failed: {because}\n"
                f"  expected: {_format(expected)}\n"
                f"  actual:   {_format(actual)}"
            )

    @staticmethod
    def sequence(expected: Sequence[T], actual: Sequence[T], because: str) -> None:
        expected_list = list(expected)
        actual_list = list(actual)
        if expected_list != actual_list:
            rendered_expected = ", ".join(_format(value) for value in expected_list)
            rendered_actual = ", ".join(_format(value) for value in actual_list)
            _fail(
                f"Assertion failed: {because}\n"
                f"  expected: [{rendered_expected}]\n"
                f"  actual:   [{rendered_actual}]"
            )

    @staticmethod
    def not_none(value: T | None, because: str) -> T:
        if value is None:
            _fail(f"Assertion failed: {because}\n  expected: not null\n  actual:   null")

        return value

    @staticmethod
    def contains(haystack: str, needle: str, because: str) -> None:
        if needle.lower() not in haystack.lower():
            _fail(
                f"Assertion failed: {because}\n"
                f"  expected to contain: {needle}\n"
                f"  actual:              {haystack}"
            )
