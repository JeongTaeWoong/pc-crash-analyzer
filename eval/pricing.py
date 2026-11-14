"""Price table and cost of one response. USD per million tokens.

Source: the model table of the claude-api reference, cached 2026-11-03. Check the pricing page before a large run -
a wrong table makes the ledger wrong, not the bill. Batch requests cost half; a cache write (5-minute) costs 1.25x
the input rate. Where a cache-read rate is not known here, the full input rate is used (the ledger then overstates).
"""
from __future__ import annotations

PRICES: dict[str, dict] = {
    "claude-opus-5-5": {"in": 4.00, "out": 20.00, "cache_read": 0.20},
    "claude-sonnet-5-5": {"in": 2.00, "out": 10.00, "cache_read": 0.10},
    # Haiku 5.5: the listed rate holds for prompts up to 100K tokens, a higher rate beyond
    "claude-haiku-5-5": {"in": 0.10, "out": 0.50, "cache_read": None, "long": {"over": 100_000, "in": 0.50, "out": 2.50}},
    "claude-fable-5-2": {"in": 8.00, "out": 40.00, "cache_read": 0.20},
}
CACHE_WRITE_FACTOR = 1.25
BATCH_FACTOR = 0.5


def cost_usd(model: str, input_tokens: int, output_tokens: int, cache_write_tokens: int = 0, cache_read_tokens: int = 0, batch: bool = False) -> float:
    """Cost of one request from the token counts the API reported (response.usage)."""
    p = PRICES[model]
    rate_in, rate_out = p["in"], p["out"]
    prompt = input_tokens + cache_write_tokens + cache_read_tokens
    if "long" in p and prompt > p["long"]["over"]:
        rate_in, rate_out = p["long"]["in"], p["long"]["out"]
    rate_read = p["cache_read"] if p.get("cache_read") is not None and rate_in == p["in"] else rate_in
    usd = (input_tokens * rate_in + cache_write_tokens * rate_in * CACHE_WRITE_FACTOR + cache_read_tokens * rate_read + output_tokens * rate_out) / 1_000_000
    return usd * (BATCH_FACTOR if batch else 1.0)


def usage_cost(model: str, usage, batch: bool = False) -> float:
    """usage = response.usage (object or dict)."""
    get = (lambda k: usage.get(k) or 0) if isinstance(usage, dict) else (lambda k: getattr(usage, k, 0) or 0)
    return cost_usd(model, get("input_tokens"), get("output_tokens"), get("cache_creation_input_tokens"), get("cache_read_input_tokens"), batch)
