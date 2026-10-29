import type { CreateAccountRequest } from '../hooks/useApi'

// ---------------------------------------------------------------------------
// Provider presets (single source of truth)
// ---------------------------------------------------------------------------

export const PROVIDER_PRESETS: Record<string, Partial<CreateAccountRequest>> = {
  gmail: { imapHost: 'imap.gmail.com', imapPort: 993, smtpHost: 'smtp.gmail.com', smtpPort: 465, smtpUseSsl: true },
  outlook: { imapHost: 'outlook.office365.com', imapPort: 993, smtpHost: 'smtp.office365.com', smtpPort: 587, smtpUseSsl: false },
  yahoo: { imapHost: 'imap.mail.yahoo.com', imapPort: 993, smtpHost: 'smtp.mail.yahoo.com', smtpPort: 465, smtpUseSsl: true },
  icloud: { imapHost: 'imap.mail.me.com', imapPort: 993, smtpHost: 'smtp.mail.me.com', smtpPort: 587, smtpUseSsl: false },
  fastmail: { imapHost: 'imap.fastmail.com', imapPort: 993, smtpHost: 'smtp.fastmail.com', smtpPort: 465, smtpUseSsl: true },
  zoho: { imapHost: 'imap.zoho.com', imapPort: 993, smtpHost: 'smtp.zoho.com', smtpPort: 465, smtpUseSsl: true },
  protonmail: { imapHost: '127.0.0.1', imapPort: 1143, smtpHost: '127.0.0.1', smtpPort: 1025, smtpUseSsl: false },
  generic: {},
}

export const PROVIDER_DISPLAY_NAMES: Record<string, string> = {
  gmail: 'Gmail',
  outlook: 'Outlook',
  yahoo: 'Yahoo',
  icloud: 'iCloud',
  fastmail: 'Fastmail',
  zoho: 'Zoho',
  protonmail: 'ProtonMail',
  generic: 'Other',
}

export const PROVIDER_HELP_LINKS: Record<string, { label: string; url: string }> = {
  gmail: { label: 'Get a Gmail App Password', url: 'https://support.google.com/accounts/answer/185833' },
  outlook: { label: 'Create an Outlook App Password', url: 'https://support.microsoft.com/en-us/account-billing/manage-app-passwords-for-two-step-verification-d6dc8c6d-4bf7-4851-ad95-6d07799205e9' },
  yahoo: { label: 'Generate a Yahoo App Password', url: 'https://help.yahoo.com/kb/generate-manage-third-party-passwords-sln15241.html' },
  icloud: { label: 'Generate an iCloud App-Specific Password', url: 'https://support.apple.com/en-us/102654' },
  fastmail: { label: 'Create a Fastmail App Password', url: 'https://www.fastmail.help/hc/en-us/articles/360058752854-App-passwords' },
  zoho: { label: 'Generate a Zoho App-Specific Password', url: 'https://www.zoho.com/mail/help/adminconsole/two-factor-authentication.html' },
  protonmail: { label: 'Set up ProtonMail Bridge', url: 'https://proton.me/mail/bridge' },
}

export const PROVIDER_NOTES: Record<string, string> = {
  protonmail: 'Requires ProtonMail Bridge running locally.',
}

export const PROVIDER_LIST = Object.keys(PROVIDER_PRESETS)
