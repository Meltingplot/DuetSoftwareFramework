# CLAUDE.md

## References

- **Duet3D wiki content**: https://github.com/Duet3D/wiki-content — source for the official Duet3D documentation (G-code dictionary, object model, RepRapFirmware and DSF behaviour).

## G-code rules

Do not guess G-code semantics, parameters, or responses. Before writing, changing, or explaining any G-code handling:

1. Check the DSF source (`src/`) for how the command is actually parsed and handled.
2. Check the Duet3D wiki content (link above) for the documented command, its parameters, and expected output.

If the code and the docs disagree, or neither covers the case, say so explicitly instead of filling the gap with an assumption.
