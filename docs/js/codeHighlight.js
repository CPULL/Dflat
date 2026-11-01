const languageRules = [
    // 1. Comments
    { type: 'comment', regex: /\/\/.*|\/\*[\s\S]*?\*\// },

    // 2. Preprocessor directives
    { type: 'directive', regex: /##[a-zA-Z_]\w*/ },

    // 3. Labels
    { type: 'label', regex: /#[a-zA-Z][a-zA-Z_\-+!?$%]*#/ },

    // 4. Strings & Characters (uses non-capturing groups internally)
    { type: 'string', regex: /"[^"\\]*(?:\\.[^"\\]*)*"|'[^'\\]*(?:\\.[^'\\]*)*'/ },

    // 5. Numbers (internal groups changed to non-capturing)
    { type: 'number', regex: /\b(?:0x[0-9a-fA-F]+|0b[0-9]+|\d+(?:\.\d+)?)(?:[uUlLsSbBfF]*)?\b/ },

    // 6. Keywords (internal group changed to non-capturing)
    { type: 'keyword', regex: /\b(?:class|trait|enum|bitfield|namespace|import|alias|fun|public|private|internal|asparent|unrestricted|static|const|var|if|else|while|for|switch|default|jump|break|continue|return|enforce|null|is|as|unsafe|extern)\b/ },

    // 7. Built-in Types (internal group changed to non-capturing)
    { type: 'type', regex: /\b(?:i8|i16|i32|i64|i128|i256|u8|u16|u32|u64|u128|u256|r16|r32|r64|r128|dec|byte|int|uns|real|bool|char|date|time|clock|lapse|addr|string|dfoot|arr|vec|list|map|hmap|set|hset)\b/ },

    // 8. StdLib Classes/Namespaces (internal group changed to non-capturing)
    { type: 'stdlib', regex: /\b(?:Root|Console|Math|Math64|Math32|Time|Random|IO|File|FileStream)\b/ },

    // 9. StdLib Methods (internal group changed to non-capturing)
    { type: 'stdlib-method', regex: /\b(?:getArgs|exit|log|logError|print|read|readLine|clear|now|clock|sleep|sin|cos|tan|asin|acos|atan|atan2|sinh|cosh|tanh|sqrt|cbrt|pow|exp|ln|log10|abs|sign|min|max|clamp|floor|ceil|round|trunc|hypot|fmod|lerp|inverseLerp|seed|realRange|getCurrentDir|setCurrentDir|getFullPath|getRelativePath|exists|delete|copy|move|createDirectory|getAllFiles|getTempFile|getTempFileName|getTempDir|combinePaths|open|readAll|readAllText|writeAll|writeAllText|run|exec|getEnv)\b/ },

    // 10. General Methods
    { type: 'method', regex: /\b[a-z][a-zA-Z0-9]*(?=\s*\()/ },

    // 11. Variables
    { type: 'variable', regex: /\b[a-zA-Z][a-zA-Z0-9_]*\b/ }
];

function highlightCodeNode(node) {
    if (!node) return;

    const rawText = node.textContent;
    
    // Escape HTML for safety
    const safeText = rawText
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");

    // Dynamically combine all rules into one regex safely
    const combinedPattern = new RegExp(
        languageRules.map(rule => `(${rule.regex.source})`).join('|'),
        'g'
    );

    // Replace matches using the corresponding rule type
    const highlighted = safeText.replace(combinedPattern, (match, ...args) => {
        // Find which capture group matched
        for (let i = 0; i < languageRules.length; i++) {
            if (args[i] !== undefined) {
                return `<span class="token ${languageRules[i].type}">${match}</span>`;
            }
        }
        return match;
    });

    node.innerHTML = highlighted;
}