import os
import json
import time
import hashlib
from collections import OrderedDict
import httpx
from typing import AsyncGenerator, List, Dict, Any, Optional, Tuple
from services.supabase_vault import SupabaseKeyVault


class LLMRouter:
    """
    High-performance multi-model LLM router (Noventra Labs) with:
    1. Persistent shared HTTP connection pooling.
    2. Secure Supabase API Key Vault integration (encrypted at rest + TTL memory cache).
    3. In-memory LRU + TTL response cache for instant (<2ms) replay on repeated queries.
    4. Standardized SSE framing (`data: {"delta": ...}\\n\\n` + `data: [DONE]\\n\\n`).
    """

    def __init__(self, cache_size: int = 128, cache_ttl_seconds: int = 3600):
        self.gemini_key = os.getenv("GEMINI_API_KEY", "").strip()
        self.openai_key = os.getenv("OPENAI_API_KEY", "").strip()
        self.anthropic_key = os.getenv("ANTHROPIC_API_KEY", "").strip()
        self.groq_key = os.getenv("GROQ_API_KEY", "").strip()

        if self.gemini_key == "your_gemini_api_key_here":
            self.gemini_key = ""
        if self.openai_key == "your_openai_api_key_here":
            self.openai_key = ""
        if self.anthropic_key == "your_anthropic_api_key_here":
            self.anthropic_key = ""
        if self.groq_key == "your_groq_api_key_here":
            self.groq_key = ""

        self.vault = SupabaseKeyVault()

        self.cache_size = cache_size
        self.cache_ttl = cache_ttl_seconds
        self._cache: OrderedDict[str, Tuple[float, str]] = OrderedDict()
        self.cache_hits = 0
        self.cache_misses = 0

        self._client: Optional[httpx.AsyncClient] = None

    def get_client(self) -> httpx.AsyncClient:
        if self._client is None or self._client.is_closed:
            self._client = httpx.AsyncClient(
                timeout=httpx.Timeout(60.0, connect=10.0),
                limits=httpx.Limits(max_keepalive_connections=25, max_connections=100, keepalive_expiry=30.0),
            )
        return self._client

    async def close(self):
        if self._client is not None and not self._client.is_closed:
            await self._client.aclose()

    async def resolve_api_key(
        self,
        provider: str,
        api_key_override: Optional[str] = None,
        client_id: str = "default",
    ) -> str:
        """
        Resolves an API key in priority order:
        1. Explicit `api_key_override` in request (also syncs to Supabase Vault automatically).
        2. Encrypted key stored in Supabase (`public.beam_api_keys`) via `SupabaseKeyVault`.
        3. Environment variable fallback (`GEMINI_API_KEY`, `OPENAI_API_KEY`, etc.).
        """
        client = self.get_client()
        if api_key_override and api_key_override.strip():
            clean_key = api_key_override.strip()
            # Asynchronously persist override key to Supabase Vault if not already cached
            try:
                await self.vault.upsert_api_key(client, provider, clean_key, client_id)
            except Exception:
                pass
            return clean_key

        vault_key = await self.vault.get_api_key(client, provider, client_id)
        if vault_key:
            return vault_key

        env_map = {
            "gemini": self.gemini_key,
            "openai": self.openai_key,
            "anthropic": self.anthropic_key,
            "groq": self.groq_key,
        }
        return env_map.get(provider, "")

    def _make_cache_key(self, model: str, messages: List[Dict[str, str]]) -> str:
        raw = json.dumps({"model": model, "messages": messages}, sort_keys=True, ensure_ascii=False)
        return hashlib.sha256(raw.encode("utf-8")).hexdigest()

    def _get_cached(self, key: str) -> Optional[str]:
        if key not in self._cache:
            return None
        ts, value = self._cache[key]
        if (time.time() - ts) > self.cache_ttl:
            del self._cache[key]
            return None
        self._cache.move_to_end(key)
        return value

    def _set_cached(self, key: str, value: str):
        if not value or value.startswith("Error:") or value.startswith("Gemini Error") or value.startswith("API Error"):
            return
        self._cache[key] = (time.time(), value)
        self._cache.move_to_end(key)
        while len(self._cache) > self.cache_size:
            self._cache.popitem(last=False)

    def clear_cache(self) -> int:
        count = len(self._cache)
        self._cache.clear()
        return count

    def get_cache_stats(self) -> Dict[str, Any]:
        return {
            "entries": len(self._cache),
            "max_entries": self.cache_size,
            "ttl_seconds": self.cache_ttl,
            "hits": self.cache_hits,
            "misses": self.cache_misses,
        }

    def get_available_models(self) -> List[Dict[str, str]]:
        return [
            {"id": "gemini-2.0-flash", "provider": "gemini", "name": "Gemini 2.0 Flash (Fast & Smart)"},
            {"id": "gemini-1.5-pro", "provider": "gemini", "name": "Gemini 1.5 Pro (Complex Reasoning)"},
            {"id": "gpt-4o", "provider": "openai", "name": "GPT-4o (Omni Flagship)"},
            {"id": "gpt-4o-mini", "provider": "openai", "name": "GPT-4o Mini (Speed Optimized)"},
            {"id": "claude-3-5-sonnet-20241022", "provider": "anthropic", "name": "Claude 3.5 Sonnet"},
            {"id": "llama-3.3-70b-versatile", "provider": "groq", "name": "Llama 3.3 70B (Ultra Fast)"},
        ]

    @staticmethod
    def _sse_delta(text: str) -> str:
        return f"data: {json.dumps({'delta': text}, ensure_ascii=False)}\n\n"

    async def chat_stream(
        self,
        messages: List[Dict[str, str]],
        model: str = "gemini-2.0-flash",
        api_key_override: Optional[str] = None,
        client_id: str = "default",
    ) -> AsyncGenerator[str, None]:
        """Streams LLM tokens using standardized Server-Sent Events (SSE)."""
        cache_key = self._make_cache_key(model, messages)
        cached = self._get_cached(cache_key)
        if cached is not None:
            self.cache_hits += 1
            yield self._sse_delta(cached)
            yield "data: [DONE]\n\n"
            return

        self.cache_misses += 1
        collected_chunks: List[str] = []
        client = self.get_client()

        # 1. Google Gemini
        if "gemini" in model:
            key = await self.resolve_api_key("gemini", api_key_override, client_id)
            if not key:
                yield self._sse_delta("Error: Gemini API key is not configured in Supabase Vault or backend .env.")
                yield "data: [DONE]\n\n"
                return

            endpoint = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse&key={key}"
            contents = []
            for m in messages:
                role = "model" if m.get("role") in ["assistant", "model"] else "user"
                contents.append({"role": role, "parts": [{"text": m.get("content", "")}]})

            async with client.stream("POST", endpoint, json={"contents": contents}) as response:
                if response.status_code != 200:
                    err_text = await response.aread()
                    yield self._sse_delta(f"Gemini Error ({response.status_code}): {err_text.decode('utf-8', errors='ignore')}")
                    yield "data: [DONE]\n\n"
                    return

                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        try:
                            payload = json.loads(line[6:])
                            text = payload["candidates"][0]["content"]["parts"][0].get("text", "")
                            if text:
                                collected_chunks.append(text)
                                yield self._sse_delta(text)
                        except Exception:
                            continue

        # 2. OpenAI / Groq
        elif model.startswith("gpt-") or "llama" in model or "mixtral" in model:
            is_groq = "llama" in model or "mixtral" in model
            provider = "groq" if is_groq else "openai"
            key = await self.resolve_api_key(provider, api_key_override, client_id)
            endpoint = (
                "https://api.groq.com/openai/v1/chat/completions"
                if is_groq
                else "https://api.openai.com/v1/chat/completions"
            )

            if not key:
                yield self._sse_delta(f"Error: API key for {provider} ({model}) is not configured in Supabase Vault or .env.")
                yield "data: [DONE]\n\n"
                return

            headers = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}
            body = {
                "model": model,
                "messages": messages,
                "stream": True,
                "temperature": 0.7,
            }

            async with client.stream("POST", endpoint, headers=headers, json=body) as response:
                if response.status_code != 200:
                    err_text = await response.aread()
                    yield self._sse_delta(f"API Error ({response.status_code}): {err_text.decode('utf-8', errors='ignore')}")
                    yield "data: [DONE]\n\n"
                    return

                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        chunk = line[6:].strip()
                        if chunk == "[DONE]":
                            break
                        try:
                            payload = json.loads(chunk)
                            delta = payload["choices"][0]["delta"].get("content", "")
                            if delta:
                                collected_chunks.append(delta)
                                yield self._sse_delta(delta)
                        except Exception:
                            continue

        # 3. Anthropic Claude
        elif "claude" in model:
            key = await self.resolve_api_key("anthropic", api_key_override, client_id)
            if not key:
                yield self._sse_delta("Error: Anthropic API key is not configured in Supabase Vault or .env.")
                yield "data: [DONE]\n\n"
                return

            system_prompt = next((m["content"] for m in messages if m.get("role") == "system"), None)
            user_msgs = [{"role": m["role"], "content": m["content"]} for m in messages if m.get("role") != "system"]

            headers = {
                "x-api-key": key,
                "anthropic-version": "2023-06-01",
                "content-type": "application/json"
            }
            body = {
                "model": model,
                "max_tokens": 2048,
                "messages": user_msgs,
                "stream": True
            }
            if system_prompt:
                body["system"] = system_prompt

            async with client.stream("POST", "https://api.anthropic.com/v1/messages", headers=headers, json=body) as response:
                if response.status_code != 200:
                    err_text = await response.aread()
                    yield self._sse_delta(f"Anthropic Error: {err_text.decode('utf-8', errors='ignore')}")
                    yield "data: [DONE]\n\n"
                    return

                async for line in response.aiter_lines():
                    if line.startswith("data: "):
                        try:
                            payload = json.loads(line[6:].strip())
                            if payload.get("type") == "content_block_delta":
                                text = payload["delta"].get("text", "")
                                if text:
                                    collected_chunks.append(text)
                                    yield self._sse_delta(text)
                        except Exception:
                            continue
        else:
            yield self._sse_delta(f"Error: Unsupported model '{model}'.")

        if collected_chunks:
            self._set_cached(cache_key, "".join(collected_chunks))

        yield "data: [DONE]\n\n"
