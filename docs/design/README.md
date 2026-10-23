# ImageGen UI concept

- Tool: built-in ImageGen, `ui-mockup`.
- Concept: [ui-concept-v1.png](ui-concept-v1.png).
- Exact generation prompt: [ui-concept-v1-prompt.txt](ui-concept-v1-prompt.txt).

The generated board establishes charcoal/porcelain surfaces, consistent header controls, colored service initial badges, grouped quota rows, fine rounded meters, and right-aligned numeric values. It is a design reference, not a screenshot of the application or proof of a working layout.

Implementation uses native WPF controls and vector shapes. In v1.4.1 the standard layout follows the board's measured proportions: 360 logical pixels wide, 42-pixel header, 86-pixel single-quota card, 116-pixel double-quota card and 11-pixel card gaps. Its height follows the selected services. Previously saved tiny sizes migrate to this standard layout once; later manual resizing remains available. Service selections are preserved. The user explicitly requested removal of the C/A/G badges, so these are the only intentional visual deviation from the board.

Software tests, application screenshots, and runtime verification were omitted at the user's standing request. Production compilation and launch remain authorized.
