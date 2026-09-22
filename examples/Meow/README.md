# Meow

The least a plugin can be. One class, no view model, no XAML, no strings, no colours. It
compiles against the contract and nothing else, and the shell shows it exactly as written:
description in English whatever the window is set to, the default health line on its card, no
plain name.

What it shows: the four members without a default (`Id`, `DisplayName`, `Description`,
`CreateView`), `Icon` and `Category`, and that everything else on `IMeowsPlugin` is optional.

Guide: [section 3, the entry point](../../PLUGIN-GUIDE.md#3-the-entry-point).
