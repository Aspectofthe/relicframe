# Implementation Notes from Untitled.txt

The new source notes were reviewed before this clean project was assembled.
The relevant confirmed behavior has been incorporated into production code:

- Full Discord feature parity remains in `bot.py`; webhook mode is an optional one-way publisher, not a replacement for the bot.
- The desktop and Discord interfaces share the same calculation, filtering, and ranking modules.
- The live market layer bootstraps complete order books with bounded concurrency and continuously reconciles them.
- WebSocket connection settings are the confirmed production URL plus the required `wfm` subprotocol.
- WebSocket order events are routed through `itemId`; code no longer expects a nested `item.slug` field that the documented order schema does not provide.
- The global new-order subscription is a speed layer only. It cannot replace REST reconciliation because it does not provide a complete stream of edits and removals.
- Vaulted status is stored directly on each relic, and unknown status remains blank rather than being guessed as unvaulted.
- Channel-specific risk and ranking calculations respect the selected online/offline scope.
- Percentage-style desktop columns sort numerically instead of alphabetically.

Raw conversation history and numbered save iterations were deliberately left
outside this folder. This project contains the active implementation and its
verification suite.
