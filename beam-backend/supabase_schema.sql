-- =============================================================================
-- Beam AI Launcher: Supabase Vault Schema
-- Table: public.beam_api_keys
-- Description: Authenticated encrypted API key storage at rest for multi-tenant / client-level keys.
-- =============================================================================

CREATE TABLE IF NOT EXISTS public.beam_api_keys (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    client_id TEXT NOT NULL DEFAULT 'default',
    provider TEXT NOT NULL,
    encrypted_key TEXT NOT NULL,
    key_hint TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::text, now()),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT timezone('utc'::text, now()),
    CONSTRAINT beam_api_keys_client_provider_unique UNIQUE (client_id, provider)
);

-- Index on lookup columns
CREATE INDEX IF NOT EXISTS idx_beam_api_keys_client_provider 
ON public.beam_api_keys (client_id, provider);

-- Enable Row Level Security (RLS)
ALTER TABLE public.beam_api_keys ENABLE ROW LEVEL SECURITY;

-- Policy: Allow service role complete access
CREATE POLICY "Service Role Full Access"
ON public.beam_api_keys
FOR ALL
TO service_role
USING (true)
WITH CHECK (true);
