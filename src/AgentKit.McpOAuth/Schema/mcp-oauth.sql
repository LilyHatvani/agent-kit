-- AgentKit.McpOAuth schema (PostgreSQL). Run it once with the host's own migrator; the package never migrates.
-- Read it from the package with McpOAuthSchema.ReadScript(). Every statement is IF NOT EXISTS, so running it on a
-- database that already has these tables changes nothing.
--
-- The four OpenIddict tables back its EF Core stores. Their shape must match OpenIddict's EF model exactly: the quoted
-- PascalCase names are OpenIddict's. Generated from OpenIddict 7.8.0's model. One deliberate difference: "ClientId" is
-- 400 characters, not 100, because a client id metadata document's URL is the client id and can be long.
-- Keep in sync when the OpenIddict major version changes (the package's CHANGELOG says when).

CREATE TABLE IF NOT EXISTS "OpenIddictApplications" (
    "Id" text NOT NULL,
    "ApplicationType" character varying(50),
    "ClientId" character varying(400),
    "ClientSecret" text,
    "ClientType" character varying(50),
    "ConcurrencyToken" character varying(50),
    "ConsentType" character varying(50),
    "DisplayName" text,
    "DisplayNames" text,
    "JsonWebKeySet" text,
    "Permissions" text,
    "PostLogoutRedirectUris" text,
    "Properties" text,
    "RedirectUris" text,
    "Requirements" text,
    "Settings" text,
    CONSTRAINT "PK_OpenIddictApplications" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictScopes" (
    "Id" text NOT NULL,
    "ConcurrencyToken" character varying(50),
    "Description" text,
    "Descriptions" text,
    "DisplayName" text,
    "DisplayNames" text,
    "Name" character varying(200),
    "Properties" text,
    "Resources" text,
    CONSTRAINT "PK_OpenIddictScopes" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictAuthorizations" (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "ConcurrencyToken" character varying(50),
    "CreationDate" timestamp with time zone,
    "Properties" text,
    "Scopes" text,
    "Status" character varying(50),
    "Subject" character varying(400),
    "Type" character varying(50),
    CONSTRAINT "PK_OpenIddictAuthorizations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id")
);

CREATE TABLE IF NOT EXISTS "OpenIddictTokens" (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "AuthorizationId" text,
    "ConcurrencyToken" character varying(50),
    "CreationDate" timestamp with time zone,
    "ExpirationDate" timestamp with time zone,
    "Payload" text,
    "Properties" text,
    "RedemptionDate" timestamp with time zone,
    "ReferenceId" character varying(100),
    "Status" character varying(50),
    "Subject" character varying(400),
    "Type" character varying(150),
    CONSTRAINT "PK_OpenIddictTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES "OpenIddictApplications" ("Id"),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId" FOREIGN KEY ("AuthorizationId") REFERENCES "OpenIddictAuthorizations" ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OpenIddictApplications_ClientId" ON "OpenIddictApplications" ("ClientId");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type" ON "OpenIddictAuthorizations" ("ApplicationId", "Status", "Subject", "Type");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OpenIddictScopes_Name" ON "OpenIddictScopes" ("Name");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type" ON "OpenIddictTokens" ("ApplicationId", "Status", "Subject", "Type");
CREATE INDEX IF NOT EXISTS "IX_OpenIddictTokens_AuthorizationId" ON "OpenIddictTokens" ("AuthorizationId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OpenIddictTokens_ReferenceId" ON "OpenIddictTokens" ("ReferenceId");

-- The authorization server's signing and encryption keys, created on first use. Kept in the database so a redeploy or
-- a second replica keeps every connection signed in, with nothing for the operator to generate or paste: a reference
-- token's stored payload is encrypted with the encryption key, so ephemeral keys would sign everyone out on each
-- deploy. Holding them here adds little to what a database reader already has: reference tokens are stored by the
-- hash of their id, so a key alone redeems nothing. Rotate by deleting the rows; every connection then signs in again.
CREATE TABLE IF NOT EXISTS oauth_key (
    name       text PRIMARY KEY CHECK (name IN ('signing', 'encryption')),
    pem        text NOT NULL,
    created_at timestamptz NOT NULL
);
