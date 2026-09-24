# RelicFrame Discord Webhook Setup

Webhook mode posts the ranked RelicFrame market report into one Discord
channel. It needs a webhook URL rather than a bot token.

A webhook can post and update reports. It cannot receive slash commands,
autocomplete relic names, or provide the interactive controls from `bot.py`.

## Create the webhook

1. Open the destination Discord channel.
2. Open **Edit Channel > Integrations > Webhooks**.
3. Choose **New Webhook**, name it `RelicFrame`, and copy its URL.
4. Treat the URL like a password; anyone who has it can post to the channel.

## Configure PyCharm

Install dependencies from the project folder:

```text
pip install -r requirements.txt
```

Copy `.env.example` to `.env` and set this value in the local file:

```text
DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
```

The `.env` file is ignored, and a normal process environment variable still
takes priority. Do not store the real URL in source code or PyCharm's shared
project files.

## Test it

Use this script parameter first:

```text
--test-webhook
```

Discord should receive a connection message without contacting Warframe
Market. Remove the parameter to post the live top-ten report.

Useful examples:

```text
--refinement radiant --rank-by "Best Overall"
--green-only --vault Unvaulted
--channel-scope "Online only" --max-cost 20 --min-roi 10
--interval-minutes 10
```

Recurring mode edits the same message instead of filling the channel with
new posts. The program prints the message ID; after a restart, use
`--message-id NUMBER` to resume editing it.

If a webhook URL is exposed, delete it in Discord and create a new one.
