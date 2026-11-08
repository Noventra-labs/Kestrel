import os
import json
from contextlib import asynccontextmanager
from typing import List, Dict, Any, Optional
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from dotenv import load_dotenv

from services.llm_router import LLMRouter

load_dotenv()

router = LLMRouter()


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Initialize shared connection pool on startup
    router.get_client()
    yield
    # Gracefully close pooled connections on shutdown
    await router.close()


app = FastAPI(
    title="Beam AI Cloud Service — Noventra Labs",
    description="High-performance streaming AI backend with Supabase Encrypted Key Vault, deployable to GCP Cloud Run.",
    version="2.3.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


class ChatMessage(BaseModel):
    role: str
    content: str


class ChatRequest(BaseModel):
    messages: List[ChatMessage]
    model: Optional[str] = "gemini-2.0-flash"
    api_key_override: Optional[str] = None
    client_id: Optional[str] = "default"
    stream: Optional[bool] = True


class SaveKeyRequest(BaseModel):
    provider: str  # "gemini" | "openai" | "anthropic" | "groq"
    api_key: str
    client_id: Optional[str] = "default"


@app.get("/health")
async def health_check():
    return {
        "status": "healthy",
        "service": "beam-backend",
        "publisher": "Noventra Labs",
        "environment": os.getenv("ENVIRONMENT", "production"),
        "version": "2.3.0",
        "supabase_vault_configured": router.vault.is_configured,
        "cache": router.get_cache_stats(),
    }


@app.get("/v1/models")
async def get_models():
    return {
        "models": router.get_available_models()
    }


@app.post("/v1/keys")
async def store_api_key_in_supabase(req: SaveKeyRequest):
    """Encrypts and stores a provider API key in Supabase (`public.beam_api_keys`)."""
    provider = req.provider.lower().strip()
    if provider not in ("gemini", "openai", "anthropic", "groq"):
        raise HTTPException(status_code=400, detail="Provider must be one of: gemini, openai, anthropic, groq")
    if not req.api_key or not req.api_key.strip():
        raise HTTPException(status_code=400, detail="api_key cannot be empty")

    result = await router.vault.upsert_api_key(
        client=router.get_client(),
        provider=provider,
        api_key=req.api_key.strip(),
        client_id=req.client_id or "default",
    )
    return result


@app.get("/v1/keys")
async def list_stored_keys(client_id: str = "default"):
    """Lists masked metadata of API keys stored in Supabase without exposing plaintext keys."""
    return await router.vault.list_stored_keys(router.get_client(), client_id)


@app.delete("/v1/keys/{provider}")
async def delete_stored_key(provider: str, client_id: str = "default"):
    """Deletes a provider API key from Supabase and in-memory cache."""
    return await router.vault.delete_api_key(router.get_client(), provider, client_id)


@app.post("/v1/cache/clear")
async def clear_cache():
    cleared = router.clear_cache()
    return {"status": "cleared", "entries_removed": cleared}


@app.post("/v1/chat")
async def chat_completion(req: ChatRequest):
    if not req.messages:
        raise HTTPException(status_code=400, detail="Messages array cannot be empty")

    msg_dicts = [{"role": m.role, "content": m.content} for m in req.messages]

    if req.stream:
        return StreamingResponse(
            router.chat_stream(
                messages=msg_dicts,
                model=req.model or "gemini-2.0-flash",
                api_key_override=req.api_key_override,
                client_id=req.client_id or "default",
            ),
            media_type="text/event-stream",
            headers={
                "Cache-Control": "no-cache",
                "Connection": "keep-alive",
                "X-Accel-Buffering": "no"
            }
        )
    else:
        full_text = ""
        async for chunk in router.chat_stream(
            messages=msg_dicts,
            model=req.model or "gemini-2.0-flash",
            api_key_override=req.api_key_override,
            client_id=req.client_id or "default",
        ):
            if chunk.startswith("data: ") and chunk.strip() != "data: [DONE]":
                try:
                    payload = json.loads(chunk[6:].strip())
                    full_text += payload.get("delta", "")
                except Exception:
                    pass
        return {
            "model": req.model,
            "choices": [{"message": {"role": "assistant", "content": full_text}}]
        }


if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", "8080"))
    uvicorn.run("main:app", host="0.0.0.0", port=port, reload=True)
