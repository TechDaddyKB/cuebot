# Reference

Every trigger and sub-action in Cuebot, with what each setting means and which variables you get.

- **[Triggers](triggers/index.md)**: the "when" of an action. *When* someone follows, *when* a scene changes, *when*
  you press a key.
- **[Sub-actions](sub-actions/index.md)**: the "then do this" steps. Send a message, wait, switch a scene.

For people who write code or build their own tools:

- **[C# methods (CPH)](csharp/index.md)**: every `CPH.` method for a **Run C# code** step, with an example.
- **[Script functions](script-functions/index.md)**: Cuebot's functions for C# and Python code, with an example in
  each language.
- **[REST API](rest-api.md)**: every web address other programs can use to control Cuebot.
- **[MCP tools](mcp-tools.md)**: the tools an AI app gets when it connects to Cuebot.

!!! tip "The same text is in the app"
    You do not need this site open while you build. In Cuebot, pick a trigger or sub-action and the same explanation
    shows above its settings, and every setting has a hint under it. When you add one, the list shows a short
    description of whatever you have selected, and search finds words in those descriptions too.

These pages are made from the help text and the code inside Cuebot, so they always match the version you download.
A test checks that nothing is missing and that every C# example compiles.
