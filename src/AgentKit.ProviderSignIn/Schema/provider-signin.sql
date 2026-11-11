-- AgentKit.ProviderSignIn schema (PostgreSQL). Run it once with the host's own migrator; the package never migrates.
-- Read it from the package with ProviderSignInSchema.ReadScript(). Every statement is IF NOT EXISTS, so running it on a
-- database that already has the table changes nothing. Needed only when the host stores sign-in state in PostgreSQL
-- (ProviderSignInOptions.ConnectionString); the in-memory store needs no schema.
--
-- One row per sign-in in flight, between the start (the redirect to the provider) and the callback. The callback deletes
-- the row it reads, so a state is good once. Rows the browser never came back for expire (10 minutes by default) and are
-- swept on the next start. The key is the SHA-256 of the state, never the state itself: a database reader cannot complete
-- a sign-in in flight. The PKCE verifier and nonce are useless without the provider's code, which only the browser holds.

CREATE TABLE IF NOT EXISTS provider_signin_state (
    key           text PRIMARY KEY,
    provider      text NOT NULL,
    purpose       text NOT NULL,
    data          text,
    return_to     text,
    code_verifier text NOT NULL,
    nonce         text NOT NULL,
    created_at    timestamptz NOT NULL,
    expires_at    timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS provider_signin_state_expires_at ON provider_signin_state (expires_at);
