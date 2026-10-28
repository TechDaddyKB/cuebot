import streamDeck from "@elgato/streamdeck";
import { RunActionKey, SetGlobalKey, ToggleActionKey, TriggerKey } from "./actions.js";
import { start } from "./hub.js";

// Entry point (manifest CodePath: bin/plugin.js). Stream Deck starts this with its bundled Node.js runtime.
streamDeck.logger.setLevel("info");
streamDeck.actions.registerAction(new RunActionKey());
streamDeck.actions.registerAction(new ToggleActionKey());
streamDeck.actions.registerAction(new SetGlobalKey());
streamDeck.actions.registerAction(new TriggerKey());

await streamDeck.connect();
await start();
