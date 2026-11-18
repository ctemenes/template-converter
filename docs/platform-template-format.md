# Platform template format (schemaVersion 1.0)

This is the target format the converter writes. It is a neutral stand-in for a real platform's native
template schema: the point of the project is the conversion method, and the target can be swapped by
changing the mapper prompt, the validator's structure checks and the renderer.

```
{
  "schemaVersion": "1.0",
  "name": string,
  "source": { "file": string, "format": "docx" },
  "page": { "size": string, "orientation": "portrait" | "landscape", "marginsMm": { "top", "right", "bottom", "left" } },
  "variables": [ Variable ],
  "header": [ Content ],
  "footer": [ Content ],
  "blocks": [ Block ],
  "rules": [ Rule ]
}
```

**Variable**: `{ "name", "type", "sourceNames", "format"?, "description"?, "fields"? }`

- `type` is one of `string`, `number`, `currency`, `date`, `boolean`, `list`.
- `sourceNames` lists every name the value has in the legacy file, exactly as written there. This is the
  audit trail from the new template back to the old one. It is empty only for a variable that an
  instruction made necessary and the legacy file never named.
- `fields` exists only on a `list` and holds the variables of each repeated row.

**Content** is exactly one of:

| Item | Meaning |
| --- | --- |
| `{ "text", "bold"?, "italic"? }` | literal words |
| `{ "var", "format"? }` | a variable, or `list.field` inside a repeating row |
| `{ "sys" }` | `pageNumber`, `pageCount` or `today` |
| `{ "when", "content", "else"? }` | conditional words inside a sentence |

**Block** is one of:

| Block | Shape |
| --- | --- |
| paragraph | `{ "type": "paragraph", "style", "list"?, "content": [Content] }` |
| conditional | `{ "type": "conditional", "when", "blocks": [Block], "else"?: [Block] }` |
| table | `{ "type": "table", "header"?: [cell], "rows"?: [[cell]], "repeat"?: { "over", "cells": [cell] } }` |
| page break | `{ "type": "pageBreak" }` |

**Condition**: `{ "var", "op", "value"? }` with `op` one of `eq ne gt ge lt le in isTrue isFalse exists`,
or a combination: `{ "all": [...] }`, `{ "any": [...] }`, `{ "not": ... }`.

**Rule**: `{ "id", "source", "interpretation", "resolution", "confidence" }`. One per plain-English
instruction found in the legacy file. `resolution` is `conditional`, `repeat`, `removed` or `needs-review`.
