-- Store the Zoho api_domain per account for multi-datacenter support.
-- This tells us which regional API endpoint to use (e.g., zoho.com vs zoho.com.au).
ALTER TABLE oauth_tokens ADD COLUMN api_domain TEXT;
